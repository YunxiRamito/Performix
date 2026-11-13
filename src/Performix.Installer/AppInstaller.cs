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

    private async Task<(string? Tag, GitHubAsset? Asset, ExpectedHash? Hash, string? Error)> ResolveReleaseAsync(
        string? requestedTag, CancellationToken cancellationToken)
    {
        _ui.Info(requestedTag is null
            ? "查询最新 GitHub release：api.github.com/…/releases/latest"
            : $"查询 GitHub release：{requestedTag}");

        (string? body, string? fetchError) = await TryGetStringAsync(
            GitHubRelease.ApiUrl(requestedTag), cancellationToken).ConfigureAwait(false);

        GitHubReleaseParseResult parsed = GitHubRelease.Parse(body);
        if (parsed.Ok && parsed.Release is not null)
        {
            GitHubReleaseInfo release = parsed.Release;
            GitHubAsset? asset = release.Package;
            if (asset is null)
            {
                string names = release.Assets.Count == 0
                    ? "（没有资产）"
                    : string.Join(", ", release.Assets.Select(a => a.Name));
                return (null, null, null, $"release {release.Tag} 里没有 {GitHubRelease.PackageAssetName}；实际资产：{names}");
            }

            ExpectedHash? hash = await ResolveDigestAsync(release, asset, cancellationToken).ConfigureAwait(false);
            _ui.Ok($"找到 release {release.Tag}");
            return (release.Tag, asset, hash, null);
        }

        // GitHub's unauthenticated API is rate limited (60/h per IP). Rather than dying there,
        // fall back to what needs no API: the release page redirect names the latest tag, and the
        // download URL is derivable from the tag.
        _ui.Warn($"GitHub API 不可用：{fetchError ?? parsed.Error ?? "未知原因"}");
        _ui.Info("改用 release 页面重定向解析版本号");

        string tag;
        if (requestedTag is not null)
        {
            tag = requestedTag.Trim();
        }
        else
        {
            (string? location, string? redirectError) = await TryGetRedirectAsync(
                GitHubRelease.LatestReleasePageUrl(), cancellationToken).ConfigureAwait(false);
            string? parsedTag = GitHubRelease.TagFromLocation(location);
            if (parsedTag is null)
            {
                return (null, null, null,
                    $"无法确定最新版本：{redirectError ?? "release 页面没有重定向到 /releases/tag/（仓库可能还没有发布任何 release）"}。" +
                    "可以用 --version <tag> 指定版本，或用 --offline <zip> 装本地包。");
            }

            tag = parsedTag.Trim();
        }

        string url = GitHubRelease.AssetDownloadUrl(tag);
        var fallbackAsset = new GitHubAsset(GitHubRelease.PackageAssetName, url, null, null);
        ExpectedHash? fallbackHash = await TryFetchChecksumAsync(url, cancellationToken).ConfigureAwait(false);
        _ui.Ok($"解析到 release {tag}");
        return (tag, fallbackAsset, fallbackHash, null);
    }

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

    private async Task<ExpectedHash?> TryFetchChecksumAsync(string packageUrl, CancellationToken cancellationToken)
    {
        (string? content, _) = await TryGetStringAsync(packageUrl + GitHubRelease.ChecksumAssetSuffix, cancellationToken)
            .ConfigureAwait(false);
        string? hex = GitHubRelease.ParseChecksumFile(content);
        if (hex is null)
        {
            _ui.Warn("该 release 没有发布 SHA-256，跳过校验");
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

    private async Task<(string? Body, string? Error)> TryGetStringAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            return (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Follows exactly one redirect by hand, to read the <c>Location</c> header.</summary>
    private async Task<(string? Location, string? Error)> TryGetRedirectAsync(string url, CancellationToken cancellationToken)
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
            return location is null
                ? (null, $"HTTP {(int)response.StatusCode}，没有 Location 头")
                : (location, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            return (null, ex.Message);
        }
    }
}
