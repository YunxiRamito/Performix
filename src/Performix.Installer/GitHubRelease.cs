using System.Globalization;
using System.Text.Json;

namespace Performix.Installer;

/// <summary>A release asset as the GitHub API describes it.</summary>
/// <param name="Digest">
/// <c>sha256:hex</c> when GitHub published one for this asset (newer releases do), otherwise
/// <c>null</c>.
/// </param>
public sealed record GitHubAsset(string Name, string Url, long? Size, string? Digest);

/// <summary>What we need out of a GitHub release.</summary>
/// <param name="PublishedAt">
/// <c>published_at</c> when the response carries one. Only used to break ties between releases
/// whose version numbers are equal (a re-published tag), never as the primary ordering.
/// </param>
public sealed record GitHubReleaseInfo(
    string Tag,
    IReadOnlyList<GitHubAsset> Assets,
    string? Notes = null,
    bool Prerelease = false,
    bool Draft = false,
    DateTimeOffset? PublishedAt = null)
{
    public GitHubAsset? Package => GitHubRelease.SelectPackage(Assets);
}

/// <summary>Result wrapper so failures carry a message instead of an exception.</summary>
public sealed record GitHubReleaseParseResult(GitHubReleaseInfo? Release, string? Error)
{
    public bool Ok => Release is not null;
}

/// <summary>Result of parsing <c>/releases</c> (the list endpoint), which is a JSON array.</summary>
public sealed record GitHubReleaseListParseResult(IReadOnlyList<GitHubReleaseInfo> Releases, string? Error)
{
    public bool Ok => Error is null;

    public static GitHubReleaseListParseResult Failure(string error)
        => new(Array.Empty<GitHubReleaseInfo>(), error);
}

/// <summary>
/// Release channel, ordered by preference: a stable release outranks a beta, a beta outranks an
/// alpha. The numeric value IS the priority (lower wins), so <c>SelectLatest</c> can compare it.
/// </summary>
public enum ReleaseChannel
{
    Stable = 0,
    Beta = 1,
    Alpha = 2,
}

/// <summary>
/// The Performix release contract: which asset we install, where it lives, and how to verify it.
///
/// Everything here is pure — URL construction, JSON parsing, checksum-file parsing, redirect
/// target parsing, "which release is newest" — so the tests can pin it without touching
/// api.github.com. That matters, because the API is rate limited (60 requests/hour per IP, which
/// a CI runner can and does exhaust); when it is, the installer falls back to the release page's
/// redirect and the <c>/releases/download/&lt;tag&gt;/&lt;asset&gt;</c> URL shape, which needs no
/// API at all.
///
/// Why <see cref="SelectLatest"/> is not <c>/releases/latest</c>: that endpoint answers 404 for
/// a repository whose releases are ALL prereleases, because GitHub defines "latest" as the newest
/// non-prerelease. Performix publishes alpha/beta builds with <c>--prerelease</c>, so relying on
/// it made the installer unable to find anything at all. The list endpoint plus
/// <see cref="SelectLatest"/> is the version that actually works.
/// </summary>
public static class GitHubRelease
{
    public const string Owner = "YunxiRamito";
    public const string Repository = "Performix";
    public const string PackageAssetName = "Performix-win-x64.zip";
    public const string ChecksumAssetSuffix = ".sha256";

    /// <summary>
    /// <c>https://api.github.com/repos/owner/repo/releases/latest</c> (or <c>/tags/&lt;tag&gt;</c>).
    /// The tag is passed through verbatim: GitHub's tag names are case- and prefix-sensitive
    /// (<c>v0.1.0-alpha.2</c> and <c>0.1.0-alpha.2</c> are different tags), so stripping the "v"
    /// here — however tempting — turns a working URL into a 404. Only the directory name drops it
    /// (see <see cref="InstallerPaths.SanitizeVersion"/>).
    /// </summary>
    public static string ApiUrl(string? tag = null, string owner = Owner, string repository = Repository)
        => string.IsNullOrWhiteSpace(tag)
            ? $"https://api.github.com/repos/{owner}/{repository}/releases/latest"
            : $"https://api.github.com/repos/{owner}/{repository}/releases/tags/{Uri.EscapeDataString(tag.Trim())}";

    /// <summary>
    /// <c>https://api.github.com/repos/owner/repo/releases?per_page=20</c> — the release LIST.
    ///
    /// This is the endpoint that saves the installer: <c>/releases/latest</c> only ever answers
    /// with a non-prerelease, so a repository whose every release was published with
    /// <c>--prerelease</c> (which is exactly how Performix ships alpha/beta builds) gets a hard
    /// 404 there and no latest version at all. The list endpoint returns drafts and prereleases
    /// too, and it is ordered by <c>created_at</c> descending, so the newest ones are in the
    /// first page.
    /// </summary>
    public static string ListApiUrl(int perPage = 20, string owner = Owner, string repository = Repository)
        => $"https://api.github.com/repos/{owner}/{repository}/releases?per_page={Math.Clamp(perPage, 1, 100)}";

    /// <summary>
    /// <c>https://github.com/owner/repo/releases/latest</c> — a plain HTML redirect whose
    /// <c>Location</c> ends in <c>/releases/tag/&lt;tag&gt;</c>. Used when the API is rate limited.
    /// Note that it only redirects when a NON-prerelease exists: for a repository that has only
    /// published alphas it answers 404 with no <c>Location</c> at all, which is why the feed
    /// below exists as well.
    /// </summary>
    public static string LatestReleasePageUrl(string owner = Owner, string repository = Repository)
        => $"https://github.com/{owner}/{repository}/releases/latest";

    /// <summary>
    /// <c>https://github.com/owner/repo/releases.atom</c> — the public release feed. No API, no
    /// rate limit, drafts excluded and prereleases included, newest entry first. This is the
    /// fallback that still works when api.github.com has answered 403 for the hour: without it a
    /// prerelease-only repository cannot be resolved at all once the 60 requests/hour are gone.
    /// </summary>
    public static string ReleasesFeedUrl(string owner = Owner, string repository = Repository)
        => $"https://github.com/{owner}/{repository}/releases.atom";

    /// <summary>
    /// <c>https://github.com/owner/repo/releases/download/&lt;tag&gt;/&lt;asset&gt;</c>.
    /// The tag is verbatim, for the same reason as <see cref="ApiUrl"/>: the asset URL GitHub
    /// publishes contains the tag exactly as it was created.
    /// </summary>
    public static string AssetDownloadUrl(string tag, string assetName = PackageAssetName,
        string owner = Owner, string repository = Repository)
        => $"https://github.com/{owner}/{repository}/releases/download/{Uri.EscapeDataString(tag.Trim())}/{Uri.EscapeDataString(assetName)}";

    /// <summary>Parses <c>/releases/latest</c> or <c>/releases/tags/&lt;tag&gt;</c> JSON.</summary>
    public static GitHubReleaseParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new GitHubReleaseParseResult(null, "GitHub release 响应为空");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new GitHubReleaseParseResult(null, "GitHub release 响应不是对象");
            }

            // GitHub answers rate limiting and errors with an object carrying "message".
            if (root.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
            {
                return new GitHubReleaseParseResult(null, $"GitHub API：{message.GetString()}");
            }

            GitHubReleaseInfo? release = ReadRelease(root);
            return release is null
                ? new GitHubReleaseParseResult(null, "GitHub release 里没有 tag_name")
                : new GitHubReleaseParseResult(release, null);
        }
        catch (JsonException ex)
        {
            return new GitHubReleaseParseResult(null, $"GitHub release JSON 解析失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Parses the <c>/releases</c> list (a JSON array, drafts and prereleases included).
    ///
    /// An empty array is NOT an error: it means the repository has published nothing, and the
    /// caller turns that into "还没有发布任何版本" rather than "GitHub 挂了".
    /// </summary>
    public static GitHubReleaseListParseResult ParseReleaseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return GitHubReleaseListParseResult.Failure("GitHub release 列表响应为空");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.String)
            {
                return GitHubReleaseListParseResult.Failure($"GitHub API：{message.GetString()}");
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                return GitHubReleaseListParseResult.Failure("GitHub release 列表不是数组");
            }

            var releases = new List<GitHubReleaseInfo>();
            foreach (JsonElement element in root.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                GitHubReleaseInfo? release = ReadRelease(element);
                if (release is not null)
                {
                    releases.Add(release);
                }
            }

            return new GitHubReleaseListParseResult(releases, null);
        }
        catch (JsonException ex)
        {
            return GitHubReleaseListParseResult.Failure($"GitHub release 列表 JSON 解析失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Picks the release the installer should install when <c>--version</c> was not given.
    ///
    /// The rules, in order:
    /// <list type="number">
    ///   <item>drafts are never candidates — they are not downloadable at all;</item>
    ///   <item>the most stable channel wins: stable &gt; beta (beta/rc) &gt; alpha, so a
    ///         published stable release is not silently skipped in favour of a prerelease;</item>
    ///   <item>inside that channel, the highest version number wins (numeric, prerelease labels
    ///         compared segment-wise, so <c>alpha.10</c> beats <c>alpha.9</c>);</item>
    ///   <item>equal versions: the later <c>published_at</c> wins;</item>
    ///   <item>when no tag parses as a version at all, the newest published non-draft wins —
    ///         installing something readable beats refusing to install anything.</item>
    /// </list>
    /// </summary>
    public static GitHubReleaseInfo? SelectLatest(IReadOnlyList<GitHubReleaseInfo>? releases)
    {
        if (releases is null || releases.Count == 0)
        {
            return null;
        }

        List<GitHubReleaseInfo> candidates = releases.Where(release => !release.Draft).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        List<GitHubReleaseInfo> versioned = candidates
            .Where(release => VersionText.TryParse(release.Tag, out _, out _))
            .ToList();

        if (versioned.Count == 0)
        {
            return Newest(candidates);
        }

        ReleaseChannel bestChannel = versioned.Min(ChannelOf);
        GitHubReleaseInfo? best = null;
        foreach (GitHubReleaseInfo release in versioned)
        {
            if (ChannelOf(release) != bestChannel)
            {
                continue;
            }

            if (best is null)
            {
                best = release;
                continue;
            }

            int comparison = VersionText.Compare(release.Tag, best.Tag);
            if (comparison > 0 || (comparison == 0 && IsNewer(release, best)))
            {
                best = release;
            }
        }

        return best;
    }

    /// <summary>
    /// Which channel a release belongs to, read off its tag: <c>v0.1.0</c> is stable,
    /// <c>v0.1.0-beta.1</c>/<c>-rc.1</c> is beta, anything else with a suffix
    /// (<c>-alpha.2</c>, <c>-preview.3</c>) is alpha.
    /// </summary>
    public static ReleaseChannel ChannelOf(GitHubReleaseInfo release)
    {
        if (!VersionText.TryParse(release.Tag, out _, out string? prerelease) || prerelease is null)
        {
            return ReleaseChannel.Stable;
        }

        if (HasLabel(prerelease, "beta") || HasLabel(prerelease, "rc"))
        {
            return ReleaseChannel.Beta;
        }

        return ReleaseChannel.Alpha;
    }

    /// <summary>
    /// Tag spellings to try for a <c>--version</c> value, most likely first.
    ///
    /// GitHub tags are prefix-sensitive, but a user typing <c>--version 0.1.0-alpha.2</c> means
    /// the same release as <c>v0.1.0-alpha.2</c>. Trying the literal value first keeps an
    /// existing tag authoritative (a repository could legitimately have both), and the
    /// <c>v</c>-toggled spelling is what rescues the common case.
    /// </summary>
    public static IReadOnlyList<string> TagCandidates(string? requestedTag)
    {
        if (string.IsNullOrWhiteSpace(requestedTag))
        {
            return Array.Empty<string>();
        }

        string trimmed = requestedTag.Trim();
        string toggled = trimmed.Length > 1 && (trimmed[0] == 'v' || trimmed[0] == 'V')
            ? trimmed[1..]
            : "v" + trimmed;

        return string.Equals(toggled, trimmed, StringComparison.Ordinal)
            ? new[] { trimmed }
            : new[] { trimmed, toggled };
    }

    private static bool HasLabel(string prerelease, string label)
        => prerelease.Equals(label, StringComparison.OrdinalIgnoreCase)
           || prerelease.StartsWith(label + ".", StringComparison.OrdinalIgnoreCase)
           || prerelease.StartsWith(label + "-", StringComparison.OrdinalIgnoreCase);

    private static GitHubReleaseInfo? Newest(IEnumerable<GitHubReleaseInfo> releases)
        => releases
            .OrderByDescending(release => release.PublishedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

    private static bool IsNewer(GitHubReleaseInfo candidate, GitHubReleaseInfo current)
        => (candidate.PublishedAt ?? DateTimeOffset.MinValue) > (current.PublishedAt ?? DateTimeOffset.MinValue);

    /// <summary>One release object → <see cref="GitHubReleaseInfo"/>, or <c>null</c> without a tag.</summary>
    private static GitHubReleaseInfo? ReadRelease(JsonElement root)
    {
        string? tag = GetString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var assets = new List<GitHubAsset>();
        if (root.TryGetProperty("assets", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in array.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? name = GetString(asset, "name");
                string? url = GetString(asset, "browser_download_url") ?? GetString(asset, "url");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
                {
                    continue;
                }

                long? size = asset.TryGetProperty("size", out JsonElement sizeElement)
                             && sizeElement.ValueKind == JsonValueKind.Number
                             && sizeElement.TryGetInt64(out long parsed)
                    ? parsed
                    : null;

                assets.Add(new GitHubAsset(name, url, size, GetString(asset, "digest")));
            }
        }

        bool prerelease = root.TryGetProperty("prerelease", out JsonElement pre)
                          && pre.ValueKind == JsonValueKind.True;
        bool draft = root.TryGetProperty("draft", out JsonElement draftElement)
                     && draftElement.ValueKind == JsonValueKind.True;

        return new GitHubReleaseInfo(tag, assets, GetString(root, "body"), prerelease, draft,
            GetTimestamp(root, "published_at"));
    }

    private static DateTimeOffset? GetTimestamp(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed
            : null;

    /// <summary>The <c>Performix-win-x64.zip</c> asset, or <c>null</c>.</summary>
    public static GitHubAsset? SelectPackage(IReadOnlyList<GitHubAsset>? assets)
    {
        if (assets is null)
        {
            return null;
        }

        return assets.FirstOrDefault(asset =>
                   string.Equals(asset.Name, PackageAssetName, StringComparison.OrdinalIgnoreCase))
               ?? assets.FirstOrDefault(asset =>
                   asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                   && asset.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The published checksum: the asset's own <c>digest</c> field if GitHub filled it in,
    /// otherwise the sibling <c>Performix-win-x64.zip.sha256</c> asset's Digest (its <em>content</em>
    /// is fetched separately by the caller).
    /// </summary>
    public static string? DigestSha256(GitHubAsset? asset)
        => asset is not null && Checksums.TryParse(asset.Digest, out ExpectedHash? hash)
           && hash is { Kind: HashKind.Sha256 }
            ? Checksums.ToHex(hash.Bytes)
            : null;

    /// <summary>The sibling checksum file for a package: <c>Performix-win-x64.zip.sha256</c>.</summary>
    public static GitHubAsset? SelectChecksumAsset(IReadOnlyList<GitHubAsset>? assets, string? packageName = PackageAssetName)
    {
        if (assets is null || string.IsNullOrEmpty(packageName))
        {
            return null;
        }

        return assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, packageName + ChecksumAssetSuffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads a <c>sha256sum</c>-style file: <c>"&lt;64 hex&gt;  Performix-win-x64.zip"</c>,
    /// <c>"SHA256 (Performix-win-x64.zip) = &lt;64 hex&gt;"</c>, or a bare hex digest.
    /// Returns the lowercase hex digest, or <c>null</c>.
    /// </summary>
    public static string? ParseChecksumFile(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        foreach (string rawLine in content.Split('\n'))
        {
            foreach (string token in rawLine.Split(new[] { ' ', '\t', '\r', '(', ')', '=' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length == 64 && Checksums.IsHex(token))
                {
                    return token.ToLowerInvariant();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Pulls the tag out of a GitHub redirect target:
    /// <c>https://github.com/o/r/releases/tag/v0.1.0</c> → <c>v0.1.0</c>. Handles query strings and
    /// trailing slashes; returns <c>null</c> when the URL is not a tag URL.
    /// </summary>
    public static string? TagFromLocation(string? location)
    {        if (string.IsNullOrWhiteSpace(location)
            || !Uri.TryCreate(location, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < segments.Length; i++)
        {
            if (string.Equals(segments[i], "tag", StringComparison.OrdinalIgnoreCase))
            {
                string tag = Uri.UnescapeDataString(segments[i + 1]);
                return string.IsNullOrWhiteSpace(tag) ? null : tag;
            }
        }

        return null;
    }

    /// <summary>
    /// Tags from a <c>releases.atom</c> feed, in feed order (newest first), deduped.
    ///
    /// Deliberately a scan for <c>/releases/tag/&lt;tag&gt;</c> rather than an XML parser: the
    /// installer is trimmed and this needs no schema, no namespaces and no XDocument. An empty or
    /// unreadable feed yields an empty list, which the caller reports as "feed gave nothing".
    /// </summary>
    public static IReadOnlyList<string> ParseReleasesFeed(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return Array.Empty<string>();
        }

        const string Marker = "/releases/tag/";
        var tags = new List<string>();
        int index = 0;

        while (index < xml.Length)
        {
            int found = xml.IndexOf(Marker, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                break;
            }

            int start = found + Marker.Length;
            int end = start;
            while (end < xml.Length
                   && xml[end] is not ('"' or '\'' or '<' or '>' or '&' or '?' or '/')
                   && !char.IsWhiteSpace(xml[end]))
            {
                end++;
            }

            if (end > start)
            {
                string tag = Uri.UnescapeDataString(xml[start..end]).Trim();
                if (tag.Length > 0 && !tags.Contains(tag, StringComparer.Ordinal))
                {
                    tags.Add(tag);
                }
            }

            index = end > start ? end : start;
        }

        return tags;
    }

    /// <summary>
    /// The tag in <paramref name="tags"/> that a user's <c>--version</c> value means, or
    /// <c>null</c>. Matching ignores the <c>v</c> prefix and letter case (see
    /// <see cref="VersionText.AreSame"/>) but never invents a tag that is not in the list — that
    /// is what lets the installer resolve "0.1.0-alpha.2" to the real tag "v0.1.0-alpha.2" when
    /// the API is rate limited and only the releases feed is reachable.
    /// </summary>
    public static string? MatchTag(IReadOnlyList<string>? tags, string? requested)
    {
        if (tags is null || tags.Count == 0 || string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        foreach (string tag in tags)
        {
            if (VersionText.AreSame(tag, requested))
            {
                return tag;
            }
        }

        return null;
    }

    /// <summary>
    /// The version an <c>--offline</c> package should install as, when <c>--version</c> was not
    /// given: the zip's own name (<c>Performix-win-x64.zip</c> → <c>local</c>), because inventing
    /// a version number would make the install directory lie about what it holds.
    /// </summary>
    public static string VersionFromFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "local";
        }

        string stem = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(stem)
            || string.Equals(stem, "Performix-win-x64", StringComparison.OrdinalIgnoreCase))
        {
            return "local";
        }

        return InstallerPaths.SanitizeVersion(stem);
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
