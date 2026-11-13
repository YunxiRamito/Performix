using System.Text.Json;

namespace Performix.Installer;

/// <summary>A release asset as the GitHub API describes it.</summary>
/// <param name="Digest">
/// <c>sha256:hex</c> when GitHub published one for this asset (newer releases do), otherwise
/// <c>null</c>.
/// </param>
public sealed record GitHubAsset(string Name, string Url, long? Size, string? Digest);

/// <summary>What we need out of a GitHub release.</summary>
public sealed record GitHubReleaseInfo(
    string Tag,
    IReadOnlyList<GitHubAsset> Assets,
    string? Notes = null,
    bool Prerelease = false)
{
    public GitHubAsset? Package => GitHubRelease.SelectPackage(Assets);
}

/// <summary>Result wrapper so failures carry a message instead of an exception.</summary>
public sealed record GitHubReleaseParseResult(GitHubReleaseInfo? Release, string? Error)
{
    public bool Ok => Release is not null;
}

/// <summary>
/// The Performix release contract: which asset we install, where it lives, and how to verify it.
///
/// Everything here is pure — URL construction, JSON parsing, checksum-file parsing, redirect
/// target parsing — so the tests can pin it without touching api.github.com. That matters,
/// because the API is rate limited (60 requests/hour per IP, which a CI runner can and does
/// exhaust); when it is, the installer falls back to the release page's redirect and the
/// <c>/releases/download/&lt;tag&gt;/&lt;asset&gt;</c> URL shape, which needs no API at all.
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
    /// <c>https://github.com/owner/repo/releases/latest</c> — a plain HTML redirect whose
    /// <c>Location</c> ends in <c>/releases/tag/&lt;tag&gt;</c>. Used when the API is rate limited.
    /// </summary>
    public static string LatestReleasePageUrl(string owner = Owner, string repository = Repository)
        => $"https://github.com/{owner}/{repository}/releases/latest";

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

            string? tag = GetString(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tag))
            {
                return new GitHubReleaseParseResult(null, "GitHub release 里没有 tag_name");
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

            return new GitHubReleaseParseResult(
                new GitHubReleaseInfo(tag, assets, GetString(root, "body"), prerelease), null);
        }
        catch (JsonException ex)
        {
            return new GitHubReleaseParseResult(null, $"GitHub release JSON 解析失败：{ex.Message}");
        }
    }

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
    {
        if (string.IsNullOrWhiteSpace(location)
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
