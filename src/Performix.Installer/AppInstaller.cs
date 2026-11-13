using System.Diagnostics;
using System.Text.Json;

namespace Performix.Installer;

/// <summary>Result of the app half of the install.</summary>
public sealed record AppInstallResult(
    int ExitCode,
    string? Version,
    string? Directory,
    string? Executable,
    string? Error)
{
    public bool Ok => ExitCode == InstallerExitCodes.Success;
}

/// <summary>
/// Downloads and installs Performix itself.
///
/// Where it goes and why: <c>%LOCALAPPDATA%\Performix\app\&lt;version&gt;\</c>, one directory per
/// version, never Program Files. Performix needs administrator rights at *runtime*, but the
/// installer should not need elevation just to write files, and keeping the previous version
/// around is what makes a rollback a matter of pointing the shortcut at the old folder.
///
/// How it gets there: the package comes from a GitHub release (
/// <c>Performix-win-x64.zip</c>), verified with the asset's <c>digest</c> or its sibling
/// <c>.sha256</c> when the release publishes one; with <c>--offline</c> the same pipeline runs
/// on a local file, which is how CI tests the installer without a network.
/// </summary>
public sealed class AppInstaller
{
    private readonly HttpClient _http;
    private readonly ConsoleUi _ui;
    private readonly SegmentedDownloader _downloader;

    public AppInstaller(HttpClient http, ConsoleUi ui, SegmentedDownloader? downloader = null)
    {
        _http = http;
        _ui = ui;
        _downloader = downloader ?? new SegmentedDownloader(log: ui.Detail);
    }

    public async Task<AppInstallResult> InstallAsync(InstallerCommand command, CancellationToken cancellationToken)
    {
        string appRoot = string.IsNullOrWhiteSpace(command.InstallDirectory)
            ? InstallerPaths.DefaultAppRoot
            : Path.GetFullPath(command.InstallDirectory);

        _ui.Step("2/4 获取 Performix 程序包");
        _ui.Info($"安装根目录：{appRoot}");

        // The tag is kept exactly as typed (GitHub tags are prefix-sensitive); the *directory*
        // name is the sanitized form, so v0.1.0 and 0.1.0 land in the same place.
        string? version = string.IsNullOrWhiteSpace(command.VersionTag)
            ? null
            : command.VersionTag.Trim();

        string? packagePath;
        ExpectedHash? expected = null;

        if (!string.IsNullOrWhiteSpace(command.OfflinePackage))
        {
            packagePath = Path.GetFullPath(command.OfflinePackage);
            if (!File.Exists(packagePath))
            {
                return Fail(InstallerExitCodes.ReleaseLookupFailed, $"离线包不存在：{packagePath}");
            }

            version ??= GitHubRelease.VersionFromFileName(packagePath);
            _ui.Info($"离线安装包：{packagePath}");
            expected = ReadSiblingChecksum(packagePath);
            if (expected is null)
            {
                _ui.Warn("离线包旁边没有 .sha256 文件，跳过完整性校验");
            }
        }
        else
        {
            (string? tag, GitHubAsset? asset, ExpectedHash? digest, string? error) =
                await ResolveReleaseAsync(version, cancellationToken).ConfigureAwait(false);
            if (error is not null || tag is null || asset is null)
            {
                return Fail(InstallerExitCodes.ReleaseLookupFailed, error ?? "无法确定要安装的版本");
            }

            version = tag;
            expected = digest;

            string cachePath = Path.Combine(
                InstallerPaths.CacheRoot, "app", InstallerPaths.SanitizeVersion(tag),
                GitHubRelease.PackageAssetName);

            if (await ReuseCachedAsync(cachePath, expected, cancellationToken).ConfigureAwait(false))
            {
                _ui.Ok("复用已下载的程序包");
                packagePath = cachePath;
            }
            else
            {
                _ui.Info($"下载 {asset.Name}（{version}）");
                _ui.Detail(asset.Url);

                try
                {
                    await _downloader.DownloadAsync(
                        _http, asset.Url, cachePath, asset.Size,
                        progress => _ui.Progress(progress), cancellationToken).ConfigureAwait(false);
                    _ui.CloseProgressLine();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
                {
                    return Fail(InstallerExitCodes.PackageDownloadFailed, $"下载程序包失败：{ex.Message}");
                }

                packagePath = cachePath;

                if (!await VerifyPackageAsync(packagePath, expected, cancellationToken).ConfigureAwait(false))
                {
                    return Fail(InstallerExitCodes.PackageDownloadFailed, "程序包校验失败（已删除，请重试）");
                }
            }
        }

        return InstallFromPackage(command, appRoot, version!, packagePath!);
    }

    // ── release lookup ──────────────────────────────────────────────────────────────────
    //
    // Finding "the version to install" is the part that broke in the field, so the order is
    // explicit and every step records why it failed:
    //
    //   --version given   /releases/tags/<tag>, trying the v-toggled spelling too
    //                     → releases.atom to confirm the spelling when the API is unusable
    //   --version absent  1. /releases/latest        (stable only — 404 is normal for us)
    //                     2. /releases?per_page=20   (drafts + prereleases, channel-preferred)
    //                     3. release page redirect    (no API at all, survives rate limiting)
    //                     4. releases.atom feed       (no API, includes prereleases)
    //
    // Step 1 exists because it is the cheapest correct answer when a stable release does exist.
    // Step 2 is the one that matters for Performix today: every published tag so far was created
    // with --prerelease, and GitHub's "latest" means "newest non-prerelease", so step 1 answers
    // 404 forever. Relying on step 1 alone made the installer unable to install anything at all.
    // Steps 3/4 exist because the anonymous API allows 60 requests/hour per IP: once that is
    // spent, steps 1 and 2 both answer 403 and only a no-API path can still name a version.

    private async Task<(string? Tag, GitHubAsset? Asset, ExpectedHash? Hash, string? Error)> ResolveReleaseAsync(
        string? requestedTag, CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(requestedTag)
            ? await ResolveLatestAsync(cancellationToken).ConfigureAwait(false)
            : await ResolveRequestedTagAsync(requestedTag.Trim(), cancellationToken).ConfigureAwait(false);

    private async Task<(string? Tag, GitHubAsset? Asset, ExpectedHash? Hash, string? Error)> ResolveLatestAsync(
        CancellationToken cancellationToken)
    {
        var trail = new List<string>();

        _ui.Info("查询最新 GitHub release：api.github.com/…/releases/latest");
        (GitHubReleaseInfo? stable, string? stableError, _) =
            await TryGetReleaseByApiAsync(null, cancellationToken).ConfigureAwait(false);
        if (stable is not null)
        {
            return await FinishReleaseAsync(stable, cancellationToken).ConfigureAwait(false);
        }

        trail.Add("releases/latest：" + (stableError ?? "未知原因"));
        _ui.Detail("releases/latest 只返回正式版；预发布（alpha/beta）不计入，404 属正常");

        _ui.Info("改查 release 列表：api.github.com/…/releases?per_page=20（含预发布）");
        (GitHubReleaseInfo? listed, string? listError) =
            await TryGetLatestFromListAsync(cancellationToken).ConfigureAwait(false);
        if (listed is not null)
        {
            _ui.Ok($"列表里选到 {listed.Tag}（{DescribeChannel(listed)}）");
            return await FinishReleaseAsync(listed, cancellationToken).ConfigureAwait(false);
        }

        trail.Add("release 列表：" + (listError ?? "未知原因"));

        // Last resort: the release page's redirect names the latest tag and needs no API, which
        // is what keeps a rate-limited (403) or blocked api.github.com from killing the install.
        _ui.Warn("GitHub API 不可用，改用 release 页面重定向解析版本号");
        (string? location, string? redirectError) = await TryGetRedirectAsync(
            GitHubRelease.LatestReleasePageUrl(), cancellationToken).ConfigureAwait(false);
        string? parsedTag = GitHubRelease.TagFromLocation(location);
        if (parsedTag is not null)
        {
            return await InstallFromTagAsync(parsedTag.Trim(), cancellationToken).ConfigureAwait(false);
        }

        trail.Add("release 页面：" + (redirectError ?? "没有重定向到 /releases/tag/"));

        // Extra net, and the only one that still works for a prerelease-only repository while
        // api.github.com is rate limited: the public releases feed needs no API and lists
        // prereleases. Without it, "60 requests/hour per IP" (shared NAT, a CI runner, a proxy)
        // turns into "the installer cannot install anything" all over again.
        _ui.Info("改查 releases.atom 订阅源（无需 API，含预发布）");
        (string? feedTag, string? feedError) = await TryGetLatestFromFeedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (feedTag is not null)
        {
            _ui.Ok($"订阅源里选到 {feedTag}");
            return await InstallFromTagAsync(feedTag, cancellationToken).ConfigureAwait(false);
        }

        trail.Add("releases.atom：" + (feedError ?? "没有解析到 tag"));

        return (null, null, null,
            $"无法确定最新版本（{string.Join("；", trail)}）。" +
            "GitHub 上可能还没有发布任何可安装的 release，或者网络/API 暂时不可用。" +
            "可以用 --version <tag> 指定版本（例如 v0.1.0-alpha.2，开头的 v 可省略），" +
            "或用 --offline <zip> 装本地包。");
    }

    /// <summary>The releases feed fallback: newest entry of <c>releases.atom</c>, channel-preferred.</summary>
    private async Task<(string? Tag, string? Error)> TryGetLatestFromFeedAsync(CancellationToken cancellationToken)
    {
        (IReadOnlyList<string> tags, string? error) = await GetFeedTagsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (tags.Count == 0)
        {
            return (null, error ?? "订阅源里没有 release");
        }

        // The feed carries no assets and no timestamps, only tags — enough for SelectLatest,
        // whose tie-break then keeps the first (newest) entry, which is the feed's own order.
        GitHubReleaseInfo? chosen = GitHubRelease.SelectLatest(
            tags.Select(tag => new GitHubReleaseInfo(tag, Array.Empty<GitHubAsset>())).ToList());

        return chosen is null ? (null, "订阅源里没有可用的 tag") : (chosen.Tag, null);
    }

    /// <summary>Every tag in <c>releases.atom</c>, newest first (empty when it cannot be read).</summary>
    private async Task<(IReadOnlyList<string> Tags, string? Error)> GetFeedTagsAsync(
        CancellationToken cancellationToken)
    {
        TextResponse response = await GetTextAsync(GitHubRelease.ReleasesFeedUrl(), cancellationToken)
            .ConfigureAwait(false);
        if (response.Body is null)
        {
            return (Array.Empty<string>(), response.Error);
        }

        return (GitHubRelease.ParseReleasesFeed(response.Body), null);
    }

    /// <summary>
    /// An explicit <c>--version</c>. Users type <c>0.1.0-alpha.2</c> as often as
    /// <c>v0.1.0-alpha.2</c>, and GitHub tags are prefix-sensitive, so both spellings are tried
    /// (see <see cref="GitHubRelease.TagCandidates"/>) instead of failing on a missing "v".
    /// </summary>
    private async Task<(string? Tag, GitHubAsset? Asset, ExpectedHash? Hash, string? Error)> ResolveRequestedTagAsync(
        string requestedTag, CancellationToken cancellationToken)
    {
        var trail = new List<string>();
        bool apiUnavailable = false;

        foreach (string candidate in GitHubRelease.TagCandidates(requestedTag))
        {
            _ui.Info($"查询 GitHub release：{candidate}");

            (GitHubReleaseInfo? release, string? error, bool notFound) =
                await TryGetReleaseByApiAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (release is not null)
            {
                if (!string.Equals(candidate, requestedTag, StringComparison.Ordinal))
                {
                    _ui.Detail($"tag 自动补全为 {release.Tag}");
                }

                return await FinishReleaseAsync(release, cancellationToken).ConfigureAwait(false);
            }

            trail.Add($"{candidate}：{error ?? "未找到"}");
            if (!notFound)
            {
                // Not "this tag does not exist" but "the API is unusable" — the other spelling
                // would hit the same wall, so stop asking and go the no-API way.
                apiUnavailable = true;
                _ui.Warn($"GitHub API 不可用：{error ?? "未知原因"}");
                break;
            }
        }

        // No API answer. Before guessing at the download URL, ask the public feed which spelling
        // actually exists — it lists every tag and needs no API, so "0.1.0-alpha.2" still resolves
        // to "v0.1.0-alpha.2" while api.github.com is rate limited.
        (IReadOnlyList<string> feedTags, string? feedError) = await GetFeedTagsAsync(cancellationToken)
            .ConfigureAwait(false);
        string? matched = GitHubRelease.MatchTag(feedTags, requestedTag);
        if (matched is not null)
        {
            _ui.Warn(apiUnavailable
                ? "GitHub API 不可用，改用 releases.atom 核对 tag"
                : "GitHub API 里查不到，改用 releases.atom 核对 tag");
            if (!string.Equals(matched, requestedTag, StringComparison.Ordinal))
            {
                _ui.Detail($"tag 自动补全为 {matched}");
            }

            return await InstallFromTagAsync(matched, cancellationToken).ConfigureAwait(false);
        }

        trail.Add(feedTags.Count > 0
            ? $"releases.atom：没有与 {requestedTag} 匹配的 tag（共 {feedTags.Count} 个）"
            : "releases.atom：" + (feedError ?? "没有解析到 tag"));

        // Still nothing: the download URL is derivable from the tag, so try it anyway. Which
        // spelling exists is decided by probing the sibling checksum file — a cheap request that
        // 404s for a tag that is not there, instead of downloading a package that never was.
        foreach (string candidate in GitHubRelease.TagCandidates(requestedTag))
        {
            string url = GitHubRelease.AssetDownloadUrl(candidate);
            ExpectedHash? probed = await TryFetchChecksumAsync(url, cancellationToken, quiet: true)
                .ConfigureAwait(false);
            if (probed is null)
            {
                continue;
            }

            _ui.Warn("无法通过 API 确认版本，改用 release 下载地址");
            _ui.Ok($"解析到 release {candidate}");
            return (candidate, new GitHubAsset(GitHubRelease.PackageAssetName, url, null, null), probed, null);
        }

        // Nothing could be verified: fall back to the spelling as typed and let the download
        // report the truth. An explicit --version is still better served by trying than by
        // refusing, and the checksum step still runs when the release publishes one.
        string fallbackTag = GitHubRelease.TagCandidates(requestedTag)[0];
        string fallbackUrl = GitHubRelease.AssetDownloadUrl(fallbackTag);
        ExpectedHash? fallbackHash = await TryFetchChecksumAsync(fallbackUrl, cancellationToken, quiet: true)
            .ConfigureAwait(false);
        _ui.Warn($"无法确认 {requestedTag}（{string.Join("；", trail)}），直接尝试下载 {fallbackTag}");
        _ui.Ok($"解析到 release {fallbackTag}");
        return (fallbackTag, new GitHubAsset(GitHubRelease.PackageAssetName, fallbackUrl, null, null),
            fallbackHash, null);
    }

    /// <summary>The release-list fallback: newest non-draft, preferring stable &gt; beta &gt; alpha.</summary>
    private async Task<(GitHubReleaseInfo? Release, string? Error)> TryGetLatestFromListAsync(
        CancellationToken cancellationToken)
    {
        TextResponse response = await GetTextAsync(GitHubRelease.ListApiUrl(), cancellationToken)
            .ConfigureAwait(false);
        if (response.Body is null)
        {
            return (null, response.Error);
        }

        GitHubReleaseListParseResult parsed = GitHubRelease.ParseReleaseList(response.Body);
        if (!parsed.Ok)
        {
            return (null, parsed.Error);
        }

        if (parsed.Releases.Count == 0)
        {
            return (null, "仓库还没有发布任何 release");
        }

        GitHubReleaseInfo? chosen = GitHubRelease.SelectLatest(parsed.Releases);
        return chosen is null
            ? (null, $"列表里 {parsed.Releases.Count} 个 release 全部是 draft，没有可安装的")
            : (chosen, null);
    }

    private async Task<(GitHubReleaseInfo? Release, string? Error, bool NotFound)> TryGetReleaseByApiAsync(
        string? tag, CancellationToken cancellationToken)
    {
        TextResponse response = await GetTextAsync(GitHubRelease.ApiUrl(tag), cancellationToken)
            .ConfigureAwait(false);
        if (response.Body is null)
        {
            return (null, response.Error, response.NotFound);
        }

        GitHubReleaseParseResult parsed = GitHubRelease.Parse(response.Body);
        return parsed.Ok && parsed.Release is not null
            ? (parsed.Release, null, false)
            : (null, parsed.Error ?? "无法解析 GitHub release 响应", false);
    }

    /// <summary>Turns a chosen release into "tag + package asset + expected hash".</summary>
    private async Task<(string? Tag, GitHubAsset? Asset, ExpectedHash? Hash, string? Error)> FinishReleaseAsync(
        GitHubReleaseInfo release, CancellationToken cancellationToken)
    {
        GitHubAsset? asset = release.Package;
        if (asset is null)
        {
            string names = release.Assets.Count == 0
                ? "（没有资产）"
                : string.Join(", ", release.Assets.Select(a => a.Name));
            return (null, null, null,
                $"release {release.Tag} 里没有 {GitHubRelease.PackageAssetName}；实际资产：{names}");
        }

        ExpectedHash? hash = await ResolveDigestAsync(release, asset, cancellationToken).ConfigureAwait(false);
        _ui.Ok($"找到 release {release.Tag}");
        return (release.Tag, asset, hash, null);
    }

    /// <summary>Downloads-from-tag path (the redirect fallback): no API, URL derived from the tag.</summary>
    private async Task<(string? Tag, GitHubAsset? Asset, ExpectedHash? Hash, string? Error)> InstallFromTagAsync(
        string tag, CancellationToken cancellationToken)
    {
        string url = GitHubRelease.AssetDownloadUrl(tag);
        var asset = new GitHubAsset(GitHubRelease.PackageAssetName, url, null, null);
        ExpectedHash? hash = await TryFetchChecksumAsync(url, cancellationToken).ConfigureAwait(false);
        _ui.Ok($"解析到 release {tag}");
        return (tag, asset, hash, null);
    }

    private static string DescribeChannel(GitHubReleaseInfo release)
        => GitHubRelease.ChannelOf(release) switch
        {
            ReleaseChannel.Stable => "正式版",
            ReleaseChannel.Beta => "beta 通道",
            _ => "alpha 通道",
        };

    /// <summary>
    /// The package checksum: asset <c>digest</c> first (GitHub computes it), then the sibling
    /// <c>.sha256</c> asset's content. Neither exists on older releases — then we say so out loud
    /// instead of pretending the download was verified.
    /// </summary>
    private async Task<ExpectedHash?> ResolveDigestAsync(
        GitHubReleaseInfo release, GitHubAsset asset, CancellationToken cancellationToken)
    {
        string? digest = GitHubRelease.DigestSha256(asset);
        if (digest is not null)
        {
            Checksums.TryParse(digest, out ExpectedHash? hash);
            _ui.Detail($"使用 release 的资产 digest：SHA-256 {digest}");
            return hash;
        }

        GitHubAsset? checksumAsset = GitHubRelease.SelectChecksumAsset(release.Assets, asset.Name);
        if (checksumAsset is null)
        {
            _ui.Warn("该 release 没有发布 SHA-256，跳过校验");
            return null;
        }

        (string? content, string? error) = await TryGetStringAsync(checksumAsset.Url, cancellationToken)
            .ConfigureAwait(false);
        string? hex = GitHubRelease.ParseChecksumFile(content);
        if (hex is null)
        {
            _ui.Warn($"读取 {checksumAsset.Name} 失败（{error ?? "内容里没有 sha256"}），跳过校验");
            return null;
        }

        Checksums.TryParse(hex, out ExpectedHash? parsed);
        _ui.Detail($"使用 {checksumAsset.Name}：SHA-256 {hex}");
        return parsed;
    }

    private async Task<ExpectedHash?> TryFetchChecksumAsync(string packageUrl, CancellationToken cancellationToken,
        bool quiet = false)
    {
        (string? content, _) = await TryGetStringAsync(packageUrl + GitHubRelease.ChecksumAssetSuffix, cancellationToken)
            .ConfigureAwait(false);
        string? hex = GitHubRelease.ParseChecksumFile(content);
        if (hex is null)
        {
            if (!quiet)
            {
                _ui.Warn("该 release 没有发布 SHA-256，跳过校验");
            }

            return null;
        }

        Checksums.TryParse(hex, out ExpectedHash? hash);
        return hash;
    }

    /// <summary>A <c>&lt;zip&gt;.sha256</c> file next to an <c>--offline</c> package.</summary>
    private ExpectedHash? ReadSiblingChecksum(string packagePath)
    {
        string checksumPath = packagePath + GitHubRelease.ChecksumAssetSuffix;
        if (!File.Exists(checksumPath))
        {
            return null;
        }

        string? hex = GitHubRelease.ParseChecksumFile(File.ReadAllText(checksumPath));
        if (hex is null)
        {
            _ui.Warn($"{Path.GetFileName(checksumPath)} 里没有可用的 sha256");
            return null;
        }

        Checksums.TryParse(hex, out ExpectedHash? hash);
        _ui.Detail($"使用 {Path.GetFileName(checksumPath)}：SHA-256 {hex}");
        return hash;
    }

    // ── download / verify ───────────────────────────────────────────────────────────────

    private async Task<bool> ReuseCachedAsync(string path, ExpectedHash? expected, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (expected is null)
        {
            return new FileInfo(path).Length > 0;
        }

        return await Task.Run(() => Checksums.Matches(Checksums.HashFile(path, expected.Kind), expected),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> VerifyPackageAsync(string path, ExpectedHash? expected, CancellationToken cancellationToken)
    {
        if (expected is null)
        {
            _ui.Warn("没有可用的 SHA-256，跳过校验（包完整性未经验证）");
            return true;
        }

        byte[] digest = await Task.Run(() => Checksums.HashFile(path, expected.Kind), cancellationToken)
            .ConfigureAwait(false);
        _ui.Info($"{Checksums.Describe(expected.Kind)}：{Checksums.ToHex(digest)}");

        if (Checksums.Matches(digest, expected))
        {
            _ui.Ok("校验通过");
            return true;
        }

        _ui.Error($"{Checksums.Describe(expected.Kind)} 不符（期望 {Checksums.ToHex(expected.Bytes)}）");
        SegmentedDownloader.DeleteArtifacts(path);
        return false;
    }

    // ── extract / install ──────────────────────────────────────────────────────────────

    private AppInstallResult InstallFromPackage(
        InstallerCommand command, string appRoot, string version, string packagePath)
    {
        _ui.Step("3/4 解压并安装");

        string versionDirectory = InstallerPaths.VersionDirectory(appRoot, version);
        _ui.Info($"目标目录：{versionDirectory}");

        try
        {
            if (Directory.Exists(versionDirectory))
            {
                _ui.Detail("该版本已存在，先删除后重装");
                Directory.Delete(versionDirectory, recursive: true);
            }

            Directory.CreateDirectory(appRoot);
            PackageExtractor.ExtractTo(packagePath, versionDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail(InstallerExitCodes.InstallFailed,
                $"解压失败：{ex.Message}（若 Performix 正在运行，请先退出）");
        }

        // The check that stops "installed successfully, nothing to launch".
        string? executable = PackageExtractor.FindExecutable(versionDirectory);
        if (executable is null)
        {
            return Fail(InstallerExitCodes.InstallFailed,
                $"解压完成但目录里没有 {InstallerPaths.ExecutableName}：{versionDirectory}");
        }

        long bytes = Directory.EnumerateFiles(versionDirectory, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
        _ui.Ok($"已安装 {version}（{ConsoleUi.FormatBytes(bytes)}）");

        PruneOldVersions(appRoot, versionDirectory);

        _ui.Step("4/4 创建快捷方式");
        try
        {
            ShortcutService.CreateStartMenuShortcut(executable, versionDirectory);
            if (command.DesktopShortcut)
            {
                ShortcutService.CreateDesktopShortcut(executable, versionDirectory);
            }
        }
        catch (Exception ex)
        {
            // A missing shortcut is annoying but the app is installed; do not fail the run.
            _ui.Warn($"快捷方式创建失败：{ex.Message}");
        }

        if (!command.NoLaunch)
        {
            TryLaunch(executable, versionDirectory);
        }

        return new AppInstallResult(InstallerExitCodes.Success, version, versionDirectory, executable, null);
    }

    /// <summary>Keeps the two newest version directories (see <see cref="InstallerPaths.VersionsToKeep"/>).</summary>
    private void PruneOldVersions(string appRoot, string keepPath)
    {
        try
        {
            var folders = Directory.GetDirectories(appRoot)
                .Select(path => new VersionFolder(path, Path.GetFileName(path), Directory.GetLastWriteTimeUtc(path)))
                .ToArray();

            IReadOnlyList<VersionFolder> doomed =
                InstallerPaths.SelectVersionsToPrune(folders, InstallerPaths.VersionsToKeep, keepPath);

            foreach (VersionFolder folder in doomed)
            {
                try
                {
                    Directory.Delete(folder.Path, recursive: true);
                    _ui.Detail($"清理旧版本：{folder.Name}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _ui.Warn($"清理旧版本 {folder.Name} 失败：{ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ui.Warn($"检查旧版本失败：{ex.Message}");
        }
    }

    private void TryLaunch(string executable, string workingDirectory)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
            };
            Process.Start(startInfo);
            _ui.Ok("已启动 Performix，安装器退出");
        }
        catch (Exception ex)
        {
            _ui.Warn($"启动失败：{ex.Message}（可以从开始菜单打开 Performix）");
        }
    }

    private AppInstallResult Fail(int exitCode, string error)
    {
        _ui.Error(error);
        return new AppInstallResult(exitCode, null, null, null, error);
    }

    // ── tiny HTTP helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One GET, with 2 retries and exponential backoff for TRANSIENT failures only (network
    /// error, timeout, 5xx, 408, 429). A 404 is an answer — "this tag/release does not exist" —
    /// and retrying it would just triple the wait before the fallback runs.
    /// </summary>
    private async Task<TextResponse> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        const int Attempts = 3; // the first try + 2 retries
        TextResponse last = default;

        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            last = await GetTextOnceAsync(url, cancellationToken).ConfigureAwait(false);
            if (!last.Transient || attempt == Attempts)
            {
                return last;
            }

            TimeSpan delay = TimeSpan.FromMilliseconds(400 * (1 << (attempt - 1)));
            _ui.Detail($"请求失败（{last.Error}），{delay.TotalMilliseconds:0} ms 后重试 {attempt}/{Attempts - 1}");
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return last;
            }
        }

        return last;
    }

    private async Task<TextResponse> GetTextOnceAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            int status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return new TextResponse(null, $"HTTP {status} {response.ReasonPhrase}", status, IsTransient(status));
            }

            return new TextResponse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), null, status, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            return new TextResponse(null, ex.Message, 0, true);
        }
    }

    /// <summary>Worth another attempt: the server or the connection hiccuped, not a real answer.</summary>
    private static bool IsTransient(int status)
        => status >= 500 || status is 408 or 429;

    /// <summary>A body, or an error and a status worth acting on (404 vs everything else).</summary>
    private readonly record struct TextResponse(string? Body, string? Error, int Status, bool Transient)
    {
        public bool NotFound => Status == 404;
    }

    private async Task<(string? Body, string? Error)> TryGetStringAsync(string url, CancellationToken cancellationToken)
    {
        TextResponse response = await GetTextAsync(url, cancellationToken).ConfigureAwait(false);
        return (response.Body, response.Error);
    }

    /// <summary>Follows exactly one redirect by hand, to read the <c>Location</c> header.</summary>
    private async Task<(string? Location, string? Error)> TryGetRedirectAsync(string url, CancellationToken cancellationToken)
    {
        string? lastError = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(Program.UserAgent);
                using HttpResponseMessage response = await client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                string? location = response.Headers.Location?.ToString();
                if (location is not null)
                {
                    return (location, null);
                }

                // A page that answers without a redirect will not redirect on a retry either.
                return (null, $"HTTP {(int)response.StatusCode}，没有 Location 头");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
            {
                lastError = ex.Message;
                if (attempt < 3)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(400 * (1 << (attempt - 1))), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return (null, lastError);
                    }
                }
            }
        }

        return (null, lastError);
    }
}
