// Update manifest (update.json) model, parser and hard gates.
//
// The manifest is the only thing that tells the client "there is a release, here is the
// file, here is how to prove it". Every field below is validated here, once, so the rest of
// the update path can assume the values are sane:
//
//   {
//     "schema": 1,
//     "channel": "alpha",
//     "version": "0.1.0-alpha.2",
//     "publishedAt": "2026-11-14T00:00:00Z",
//     "notes": "...",
//     "minVersion": "0.1.0-alpha.1",
//     "assets": [
//       { "name": "Performix-win-x64.zip", "url": "https://...", "sizeBytes": 123456, "sha256": "..." }
//     ]
//   }
//
// HARD GATE (docs\UPDATE-DESIGN.md §1.3.4 / §5.1): an asset without a positive `sizeBytes`
// AND a 64-hex `sha256` is rejected — the reason is recorded so the page can say why, and
// the checker keeps looking (other asset, other source). No hash, no download; never
// "download and hope". Size is checked before the hash after the transfer because it is
// cheap (a file length) and it catches truncated downloads before hashing 80 MB.
//
// URLs: https only, from the release hosts we actually publish on. A local feed
// (--update-feed) may additionally use http/file on the loopback address, because that is
// how the state machine is verified without a published release. That relaxation is tied to
// the override switch, not to a build flag, so a shipping build cannot be talked into
// reading a file:// manifest from the command line by accident.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Predex.WinUI.Services.Update
{
    /// <summary>One downloadable artifact described by the manifest.</summary>
    public sealed class UpdateAsset
    {
        public UpdateAsset(string name, string url, long sizeBytes, string sha256)
        {
            Name = name;
            Url = url;
            SizeBytes = sizeBytes;
            Sha256 = sha256;
        }

        /// <summary>File name only (no path); the download keeps this name in %TEMP%.</summary>
        public string Name { get; }

        public string Url { get; }

        public long SizeBytes { get; }

        public string Sha256 { get; }

        public override string ToString() => $"{Name} ({SizeBytes} bytes, sha256 {Sha256[..Math.Min(8, Sha256.Length)]}…)";
    }

    /// <summary>An asset the hard gate refused, plus the reason (kept for the UI/log).</summary>
    public sealed record UpdateAssetRejection(string Name, string Reason);

    /// <summary>A manifest that passed validation, with the assets that passed the gate.</summary>
    public sealed class UpdateManifest
    {
        internal UpdateManifest(int schema, string channel, SemanticVersion version, DateTimeOffset publishedAt,
            string notes, SemanticVersion? minVersion, IReadOnlyList<UpdateAsset> assets,
            IReadOnlyList<UpdateAssetRejection> rejectedAssets)
        {
            Schema = schema;
            Channel = channel;
            Version = version;
            PublishedAt = publishedAt;
            Notes = notes;
            MinVersion = minVersion;
            Assets = assets;
            RejectedAssets = rejectedAssets;
        }

        public int Schema { get; }
        public string Channel { get; }

        /// <summary>Parsed version — never compare the raw string (see SemanticVersion).</summary>
        public SemanticVersion Version { get; }

        public DateTimeOffset PublishedAt { get; }

        /// <summary>Release notes, plain text; may be empty ("no notes").</summary>
        public string Notes { get; }

        /// <summary>Lowest version this release can be applied over, when the feed declares one.</summary>
        public SemanticVersion? MinVersion { get; }

        /// <summary>Assets that passed the hard gate (non-empty in any accepted manifest).</summary>
        public IReadOnlyList<UpdateAsset> Assets { get; }

        /// <summary>Assets refused by the gate, with reasons. Never downloaded.</summary>
        public IReadOnlyList<UpdateAssetRejection> RejectedAssets { get; }

        /// <summary>
        /// The asset an x64 desktop build should take: a zip that names win-x64 wins, then any
        /// zip, then whatever is left. Keeps a multi-asset release (symbols, arm64) from being
        /// downloaded into the updater by accident.
        /// </summary>
        public UpdateAsset? SelectAsset()
        {
            UpdateAsset? best = null;
            foreach (UpdateAsset asset in Assets)
            {
                bool zip = asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                bool x64 = asset.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase);

                if (x64 && zip) return asset;
                best ??= zip ? asset : null;
                best ??= asset;
            }
            return best;
        }
    }

    /// <summary>Result of parsing one manifest: exactly one of the two fields is set.</summary>
    public sealed class UpdateManifestParseResult
    {
        private UpdateManifestParseResult(UpdateManifest? manifest, string? failure,
            IReadOnlyList<UpdateAssetRejection> rejections)
        {
            Manifest = manifest;
            Failure = failure;
            RejectedAssets = rejections;
        }

        public UpdateManifest? Manifest { get; }

        /// <summary>English source sentence (goes through Loc.T; unknown keys stay English).</summary>
        public string? Failure { get; }

        public IReadOnlyList<UpdateAssetRejection> RejectedAssets { get; }

        public bool Succeeded => Manifest is not null;

        internal static UpdateManifestParseResult Ok(UpdateManifest manifest, IReadOnlyList<UpdateAssetRejection> rejections)
            => new(manifest, null, rejections);

        internal static UpdateManifestParseResult Fail(string reason, IReadOnlyList<UpdateAssetRejection>? rejections = null)
            => new(null, reason, rejections ?? Array.Empty<UpdateAssetRejection>());
    }

    /// <summary>Parses and validates update.json. Pure: no network, no files, unit-tested.</summary>
    public static class UpdateManifestParser
    {
        /// <summary>Upper bound for the size field (1 GiB) — a larger value is a typo or an attack.</summary>
        public const long MaximumAssetBytes = 1024L * 1024 * 1024;

        /// <summary>Schema version this build understands.</summary>
        public const int SupportedSchema = 1;

        /// <summary>
        /// Parses <paramref name="json"/>. <paramref name="expectedChannel"/> is the channel the
        /// user asked for and must equal the manifest's own channel (a stable client must never
        /// be pushed onto a pre-release by a mistyped feed). <paramref name="allowLocalUrls"/>
        /// is set only for the --update-feed test override.
        /// </summary>
        public static UpdateManifestParseResult Parse(string? json, string expectedChannel, bool allowLocalUrls = false)
        {
            if (string.IsNullOrWhiteSpace(json))
                return UpdateManifestParseResult.Fail("The update manifest is empty.");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 32,
                });
            }
            catch (JsonException)
            {
                return UpdateManifestParseResult.Fail("The update manifest is not valid JSON.");
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return UpdateManifestParseResult.Fail("The update manifest is not a JSON object.");

                if (!root.TryGetProperty("schema", out JsonElement schemaElement) ||
                    schemaElement.ValueKind != JsonValueKind.Number ||
                    !schemaElement.TryGetInt32(out int schema))
                    return UpdateManifestParseResult.Fail("The update manifest is missing a readable schema number.");

                if (schema != SupportedSchema)
                    return UpdateManifestParseResult.Fail(
                        $"The update manifest uses schema {schema}, but this build only understands schema {SupportedSchema}.");

                if (!root.TryGetProperty("channel", out JsonElement channelElement) ||
                    channelElement.ValueKind != JsonValueKind.String)
                    return UpdateManifestParseResult.Fail("The update manifest is missing a channel.");

                string channel = channelElement.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
                if (channel.Length == 0)
                    return UpdateManifestParseResult.Fail("The update manifest is missing a channel.");
                if (!string.Equals(channel, expectedChannel, StringComparison.Ordinal))
                    return UpdateManifestParseResult.Fail(
                        $"The update manifest is for channel '{channel}', not '{expectedChannel}'.");

                if (!root.TryGetProperty("version", out JsonElement versionElement) ||
                    versionElement.ValueKind != JsonValueKind.String ||
                    !SemanticVersion.TryParse(versionElement.GetString(), out SemanticVersion? version) ||
                    version is null)
                    return UpdateManifestParseResult.Fail("The update manifest carries a version number this build cannot read.");

                DateTimeOffset publishedAt;
                if (!root.TryGetProperty("publishedAt", out JsonElement publishedElement) ||
                    publishedElement.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(publishedElement.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out publishedAt))
                    return UpdateManifestParseResult.Fail("The update manifest is missing a readable publishedAt timestamp.");

                string notes = string.Empty;
                if (root.TryGetProperty("notes", out JsonElement notesElement) && notesElement.ValueKind == JsonValueKind.String)
                    notes = notesElement.GetString() ?? string.Empty;

                SemanticVersion? minVersion = null;
                if (root.TryGetProperty("minVersion", out JsonElement minElement))
                {
                    // A present-but-unreadable minVersion must reject the manifest: silently
                    // treating it as "no minimum" would install over a build the publisher
                    // explicitly declared too old. A number/object/null is as unreadable as a
                    // malformed string, so it fails with the same sentence.
                    if (minElement.ValueKind != JsonValueKind.String)
                        return UpdateManifestParseResult.Fail(
                            $"The update manifest carries a minVersion that is not a version string (found {minElement.ValueKind}).");
                    if (!SemanticVersion.TryParse(minElement.GetString(), out SemanticVersion? parsedMin) || parsedMin is null)
                        return UpdateManifestParseResult.Fail("The update manifest carries a minVersion this build cannot read.");
                    minVersion = parsedMin;
                }

                if (!root.TryGetProperty("assets", out JsonElement assetsElement) ||
                    assetsElement.ValueKind != JsonValueKind.Array)
                    return UpdateManifestParseResult.Fail("The update manifest has no assets array.");

                var assets = new List<UpdateAsset>();
                var rejections = new List<UpdateAssetRejection>();

                foreach (JsonElement assetElement in assetsElement.EnumerateArray())
                {
                    ReadAsset(assetElement, allowLocalUrls, assets, rejections);
                }

                if (assets.Count == 0)
                {
                    // Canonical sentence (the page/console rely on it); per-asset details live in
                    // the rejection list so the log can still say exactly what was wrong.
                    return UpdateManifestParseResult.Fail(
                        "The update manifest is incomplete: no asset carries both sizeBytes and sha256.",
                        rejections);
                }

                var manifest = new UpdateManifest(schema, channel, version, publishedAt, notes, minVersion, assets, rejections);
                return UpdateManifestParseResult.Ok(manifest, rejections);
            }
        }

        private static void ReadAsset(JsonElement element, bool allowLocalUrls,
            List<UpdateAsset> assets, List<UpdateAssetRejection> rejections)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                rejections.Add(new UpdateAssetRejection("?", "an asset entry is not an object"));
                return;
            }

            string name = element.TryGetProperty("name", out JsonElement nameElement) &&
                          nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;

            string label = name.Length == 0 ? "(unnamed asset)" : name;

            if (name.Length == 0)
            {
                rejections.Add(new UpdateAssetRejection(label, "an asset is missing its name"));
                return;
            }

            if (!IsPlainFileName(name))
            {
                rejections.Add(new UpdateAssetRejection(label, $"asset name '{name}' is not a plain file name"));
                return;
            }

            string url = element.TryGetProperty("url", out JsonElement urlElement) &&
                         urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;

            string? urlFailure = null;
            bool urlAccepted = url.Length > 0 && TryAcceptUrl(url, allowLocalUrls, out urlFailure);
            if (!urlAccepted)
            {
                rejections.Add(new UpdateAssetRejection(label,
                    $"asset '{name}' has an unusable url ({urlFailure ?? "empty"})"));
                return;
            }

            // HARD GATE part 1: sizeBytes must be present and plausible.
            if (!element.TryGetProperty("sizeBytes", out JsonElement sizeElement) ||
                sizeElement.ValueKind != JsonValueKind.Number ||
                !sizeElement.TryGetInt64(out long sizeBytes) ||
                sizeBytes <= 0 ||
                sizeBytes > MaximumAssetBytes)
            {
                rejections.Add(new UpdateAssetRejection(label,
                    $"asset '{name}' is missing a valid sizeBytes (1..{MaximumAssetBytes})"));
                return;
            }

            // HARD GATE part 2: sha256 must be present and 64 hex characters.
            string sha256 = element.TryGetProperty("sha256", out JsonElement hashElement) &&
                            hashElement.ValueKind == JsonValueKind.String
                ? hashElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;

            if (!IsValidSha256(sha256))
            {
                rejections.Add(new UpdateAssetRejection(label, $"asset '{name}' is missing a valid sha256"));
                return;
            }

            assets.Add(new UpdateAsset(name, url, sizeBytes, sha256.ToLowerInvariant()));
        }

        /// <summary>
        /// Windows device names, with or without an extension: a file called CON or COM1.zip does
        /// not name a regular file, it names the device, so it can neither be written to %TEMP%
        /// nor extracted. COM0/LPT0 and COM10+ are ordinary names on NT, hence the 1..9 list.
        /// </summary>
        private static readonly string[] ReservedDeviceNames =
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// A manifest asset name must be one plain file name: no directory parts, no "..", no
        /// characters Windows rejects in a path component, no trailing dot/space (Windows
        /// silently strips those, which would make the written path differ from the validated
        /// one) and no reserved device name. The downloader combines it with the temp directory
        /// using Path.Combine, so "../../evil.zip" would otherwise escape that directory.
        /// </summary>
        private static bool IsPlainFileName(string name)
        {
            if (name.Length == 0 || name.Length > 120) return false;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (name.Contains('/') || name.Contains('\\')) return false;
            if (name.Contains("..")) return false;
            if (name.EndsWith(' ') || name.EndsWith('.')) return false;

            // "CON", "con.txt", "COM1.tar.gz" — Windows ignores the extension for device names.
            string stem = name;
            int dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem[..dot];
            stem = stem.TrimEnd(' ');
            foreach (string reserved in ReservedDeviceNames)
            {
                if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        /// <summary>64 hex characters, nothing else (the manifest never says "sha512:").</summary>
        public static bool IsValidSha256(string? value)
        {
            if (value is null || value.Length != 64) return false;
            foreach (char c in value)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        /// <summary>
        /// https from the hosts Performix actually publishes on; a local override feed may also
        /// use http/file on loopback (that is the only way to verify the state machine without
        /// a public release, and it cannot be reached from a normal build).
        /// </summary>
        private static bool TryAcceptUrl(string url, bool allowLocalUrls, out string? failure)
        {
            failure = null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                failure = "not an absolute url";
                return false;
            }

            if (allowLocalUrls && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ||
                                   uri.Scheme == Uri.UriSchemeFile))
            {
                if (uri.Scheme == Uri.UriSchemeFile || uri.IsLoopback) return true;
            }

            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                failure = "only https is accepted";
                return false;
            }

            if (IsKnownReleaseHost(uri.Host)) return true;

            failure = $"host '{uri.Host}' is not a release host";
            return false;
        }

        private static bool IsKnownReleaseHost(string host)
        {
            switch (host.ToLowerInvariant())
            {
                case "github.com":
                case "api.github.com":
                case "objects.githubusercontent.com":
                case "codeload.github.com":
                case "raw.githubusercontent.com":
                case "registry.npmjs.org":
                case "registry.npmmirror.com":
                case "cdn.jsdelivr.net":
                case "gh-proxy.com":
                case "ghproxy.net":
                    return true;
                default:
                    return host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
            }
        }
    }
}
