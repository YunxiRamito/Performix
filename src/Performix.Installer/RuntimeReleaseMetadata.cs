using System.Text.Json;

namespace Performix.Installer;

/// <summary>
/// The runtime installer package picked out of a release metadata document.
/// <paramref name="Size"/> is the published size when the feed has one (it usually does not for
/// the Windows desktop bootstrapper, so the downloader falls back to the server's
/// <c>Content-Length</c>).
/// </summary>
public sealed record RuntimePackage(string Version, string FileName, string Url, string? Hash, long? Size)
{
    /// <summary>The parsed digest, or <c>null</c> when the feed published something unparseable.</summary>
    public ExpectedHash? ExpectedHash => Checksums.TryParse(Hash, out ExpectedHash? hash) ? hash : null;
}

/// <summary>Result of parsing a <c>releases.json</c> document.</summary>
public sealed record RuntimeMetadataResult(RuntimePackage? Package, string? Error, string? ChannelVersion = null)
{
    public bool Ok => Package is not null;

    public static RuntimeMetadataResult Failed(string error) => new(null, error);
}

/// <summary>
/// Parses the .NET release metadata (<c>release-metadata/10.0/releases.json</c>) into the one
/// package the installer needs: the Windows x64 desktop runtime bootstrapper, with its version,
/// URL and SHA-512.
///
/// Version numbers and hashes are NOT hardcoded anywhere in this installer — they are read from
/// this document at run time, so a new 10.0.x patch ships without rebuilding the setup.
///
/// Shape (verified 2026-11 against builds.dotnet.microsoft.com, 10.0/releases.json):
/// <code>
/// {
///   "channel-version": "10.0",
///   "latest-runtime": "10.0.15",
///   "releases": [
///     {
///       "release-version": "10.0.15",
///       "runtime": { "version": "10.0.15", "files": [ ... ] },
///       "windowsdesktop": {
///         "version": "10.0.15",
///         "files": [
///           { "name": "windowsdesktop-runtime-win-x64.exe",
///             "rid": "win-x64",
///             "url": "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.15/windowsdesktop-runtime-10.0.15-win-x64.exe",
///             "hash": "f838…" }   // 128 hex chars = SHA-512
///         ]
///       }
///     }
///   ]
/// }
/// </code>
/// The desktop files live under <c>windowsdesktop</c> in the current feed; older feeds put them
/// under <c>runtime.files</c> and some list them only inside <c>sdks[]</c>, so every file list in
/// the chosen release is searched. The file name in the current feed does not carry the version
/// (<c>windowsdesktop-runtime-win-x64.exe</c>); the version lives in the URL. Both spellings are
/// accepted.
/// </summary>
public static class RuntimeReleaseMetadata
{
    public const string DefaultUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json";
    public const string DefaultRid = "win-x64";
    public const string FileNamePrefix = "windowsdesktop-runtime";

    private static readonly string[] CandidateSections =
    {
        "windowsdesktop", "runtime", "aspnetcore-runtime", "sdk",
    };

    /// <summary>Parses the document. Never throws on bad input; returns an error string instead.</summary>
    public static RuntimeMetadataResult Parse(string? json, string rid = DefaultRid, string fileNamePrefix = FileNamePrefix)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return RuntimeMetadataResult.Failed("releases.json 为空");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return RuntimeMetadataResult.Failed($"releases.json 不是合法 JSON：{ex.Message}");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return RuntimeMetadataResult.Failed("releases.json 的根节点不是对象");
            }

            string? channelVersion = GetString(root, "channel-version");
            string? latestRuntime = GetString(root, "latest-runtime") ?? GetString(root, "latest-release");

            if (!root.TryGetProperty("releases", out JsonElement releases)
                || releases.ValueKind != JsonValueKind.Array
                || releases.GetArrayLength() == 0)
            {
                return RuntimeMetadataResult.Failed("releases.json 里没有 releases 数组");
            }

            JsonElement chosen = default;
            bool chosenFound = false;

            // Prefer the release that latest-runtime names; otherwise the highest version present.
            foreach (JsonElement release in releases.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? releaseVersion = GetString(release, "release-version");
                if (latestRuntime is not null && VersionText.AreSame(releaseVersion, latestRuntime))
                {
                    chosen = release;
                    chosenFound = true;
                    break;
                }
            }

            if (!chosenFound)
            {
                foreach (JsonElement release in releases.EnumerateArray())
                {
                    if (release.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!chosenFound || VersionText.Compare(GetString(release, "release-version"),
                            GetString(chosen, "release-version")) > 0)
                    {
                        chosen = release;
                        chosenFound = true;
                    }
                }
            }

            if (!chosenFound)
            {
                return RuntimeMetadataResult.Failed("releases.json 里没有可用的 release 条目");
            }

            string? version = null;
            JsonElement bestFile = default;
            string? bestUrl = null;
            string? bestName = null;
            int bestScore = 0;

            foreach ((string section, JsonElement element) in EnumerateSections(chosen))
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("files", out JsonElement files)
                    || files.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement file in files.EnumerateArray())
                {
                    if (file.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    int score = ScoreFile(file, rid, fileNamePrefix);
                    if (score <= bestScore)
                    {
                        continue;
                    }

                    string? url = GetString(file, "url");
                    string? name = GetString(file, "name");
                    if (url is null || name is null)
                    {
                        continue;
                    }

                    bestScore = score;
                    bestFile = file;
                    bestUrl = url;
                    bestName = name;
                    version = GetString(element, "version") ?? GetString(chosen, "release-version") ?? latestRuntime;
                }
            }

            if (bestUrl is null || bestName is null)
            {
                return RuntimeMetadataResult.Failed(
                    $"releases.json 里没有找到 {fileNamePrefix}*-{rid}.exe（尝试过 windowsdesktop/runtime/aspnetcore-runtime/sdk 的文件列表）");
            }

            string? hash = GetString(bestFile, "hash")
                           ?? GetString(bestFile, "hash-sha512")
                           ?? GetString(bestFile, "sha512");

            long? size = GetLong(bestFile, "size") ?? GetLong(bestFile, "sizeBytes") ?? GetLong(bestFile, "fileSize");

            return new RuntimeMetadataResult(
                new RuntimePackage(version ?? "unknown", bestName, bestUrl, hash, size),
                null,
                channelVersion);
        }
    }

    private static IEnumerable<(string Section, JsonElement Element)> EnumerateSections(JsonElement release)
    {
        foreach (string section in CandidateSections)
        {
            if (release.TryGetProperty(section, out JsonElement element))
            {
                yield return (section, element);
            }
        }

        // Some feeds only carry the desktop runtime inside the sdks[] entries.
        if (release.TryGetProperty("sdks", out JsonElement sdks) && sdks.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement sdk in sdks.EnumerateArray())
            {
                yield return ($"sdks[{index++}]", sdk);
            }
        }
    }

    /// <summary>
    /// Scores a file entry: 2 for an explicit <c>rid</c> match, 1 for the rid appearing in the
    /// name, 0 for "not the file we want". Requires the name to start with the desktop-runtime
    /// prefix and end in <c>.exe</c> so the <c>.zip</c> variants in the same list never win.
    /// </summary>
    private static int ScoreFile(JsonElement file, string rid, string fileNamePrefix)
    {
        string? name = GetString(file, "name");
        if (string.IsNullOrEmpty(name)
            || !name.StartsWith(fileNamePrefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        string? fileRid = GetString(file, "rid");
        if (string.Equals(fileRid, rid, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (name.Contains(rid, StringComparison.OrdinalIgnoreCase)
            && !name.Contains("arm64", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 0;
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out long parsed) => parsed,
            _ => null,
        };
    }
}
