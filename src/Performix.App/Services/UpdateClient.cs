// The update client: fetch the manifest from the two source families, download the package,
// prove it. No UI, no WinUI, no static state — the HTTP handler and the temp directory are
// injectable, which is what makes it unit-testable without touching the network (the tests feed
// it a fake handler and a temp directory).
//
// ── Source strategy (user-specified, docs\UPDATE-DESIGN.md §1.1.3/§1.1.4) ────────────────
//   group 1  OFFICIAL      GitHub Releases API + registry.npmjs.org, queried IN PARALLEL
//   group 2  ACCELERATED   gh-proxy.com / ghproxy.net / cdn.jsdelivr.net/gh + npmmirror
// Groups run SERIALLY: the official group gets 4 s; if it produces no complete manifest and
// the 8 s total budget has not run out, the accelerated group gets the remainder. Inside a
// group all sources are queried at once and the highest version with a COMPLETE manifest wins;
// the source that supplied it is recorded and shown on the page.
// A single source failing never fails the page: it becomes a recorded failure ("降级").
//
// ── Hard gates ───────────────────────────────────────────────────────────────────────────
//   • manifest-level: schema/version/channel/publishedAt readable, at least one asset that
//     carries BOTH sizeBytes and sha256 (UpdateManifestParser).
//   • download-level: size first, then SHA-256; either mismatch deletes the file and reports
//     "校验失败". Neither value can be skipped or defaulted.
//
// ── Transfer discipline ──────────────────────────────────────────────────────────────────
//   • stream to %TEMP%\Performix\update\<name>.part, resume with HTTP Range when the server
//     honours it, restart from zero when it answers 200 to a Range request, restart when the
//     sidecar says the .part belongs to another asset/URL.
//   • a 20 s stall (no new bytes) aborts the attempt and retries (resuming); at most 2 retries
//     per URL, transient failures only (network error, 5xx, 408, 429, stall, timeout).
//   • every request carries User-Agent: Performix/<version>.
//   • progress reports received bytes, total bytes (-1 = the server did NOT tell us), EMA
//     smoothed speed and remaining time; the page shows an indeterminate bar when the total is
//     unknown rather than inventing a percentage.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Predex.WinUI.Services.Update
{
    // ── Progress / results ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// One progress sample. <see cref="TotalBytes"/> is -1 when the transfer did not declare a
    /// length (chunked response, resume without Content-Range); a percentage is then unavailable
    /// and the UI must show an indeterminate bar plus the received-byte count.
    /// </summary>
    public readonly record struct UpdateDownloadProgress(long ReceivedBytes, long TotalBytes,
        double BytesPerSecond, TimeSpan? Remaining)
    {
        public static UpdateDownloadProgress Empty => new(0, -1, 0, null);

        public bool IsTotalKnown => TotalBytes > 0;

        /// <summary>0..100, or -1 when the total is unknown (never fake a percentage).</summary>
        public double Percent => TotalBytes > 0
            ? Math.Clamp(ReceivedBytes * 100.0 / TotalBytes, 0, 100)
            : -1;
    }

    /// <summary>How one source answered a check (drives the "no release" vs "error" verdict).</summary>
    public enum UpdateAttemptKind
    {
        /// <summary>A complete manifest with a usable asset.</summary>
        Succeeded = 0,

        /// <summary>The source answered "nothing published" (404 / empty release list).</summary>
        Missing = 1,

        /// <summary>The source could not be reached or refused us (timeout, 403, 5xx).</summary>
        Unavailable = 2,

        /// <summary>The source answered with content this build refused (bad channel, no checksum).</summary>
        Invalid = 3,
    }

    /// <summary>One source's answer during a check (kept for the UI and for --check-update).</summary>
    public sealed class UpdateAttemptInfo
    {
        internal UpdateAttemptInfo(UpdateSource source, UpdateAttemptKind kind, string? reason)
        {
            Source = source;
            Kind = kind;
            Reason = reason;
        }

        public UpdateSource Source { get; }
        public string SourceId => Source.Id;
        public string Label => Source.Label;
        public UpdateAttemptKind Kind { get; }
        public bool Succeeded => Kind == UpdateAttemptKind.Succeeded;
        public string? Reason { get; }

        /// <summary>The source exists but has nothing published (404 / empty release list).</summary>
        public bool Missing => Kind == UpdateAttemptKind.Missing;

        /// <summary>The source answered with a manifest this build refused (a real problem).</summary>
        public bool Invalid => Kind == UpdateAttemptKind.Invalid;

        /// <summary>Set only when <see cref="Succeeded"/>.</summary>
        public UpdateManifest? Manifest { get; internal init; }

        /// <summary>Set only when <see cref="Succeeded"/>.</summary>
        public UpdateAsset? Asset { get; internal init; }

        public IReadOnlyList<UpdateAssetRejection> RejectedAssets { get; internal init; } = Array.Empty<UpdateAssetRejection>();
    }

    /// <summary>Result of a check. A failed check is still a result — the page shows the reason.</summary>
    public sealed class UpdateCheckResult
    {
        internal UpdateCheckResult(bool hasRelease, UpdateManifest? manifest, UpdateAsset? asset,
            UpdateSource? source, bool noRelease, string? failureReason,
            IReadOnlyList<UpdateAttemptInfo> attempts, IReadOnlyList<UpdateAssetRejection> rejectedAssets,
            long elapsedMilliseconds)
        {
            HasRelease = hasRelease;
            Manifest = manifest;
            Asset = asset;
            Source = source;
            NoRelease = noRelease;
            FailureReason = failureReason;
            Attempts = attempts;
            RejectedAssets = rejectedAssets;
            ElapsedMilliseconds = elapsedMilliseconds;
        }

        /// <summary>True when a complete manifest was obtained (does not mean "there is an update").</summary>
        public bool HasRelease { get; }

        public UpdateManifest? Manifest { get; }

        /// <summary>The asset the downloader should take (already passed the size/sha gate).</summary>
        public UpdateAsset? Asset { get; }

        /// <summary>The source that actually answered ("hit source", shown on the page).</summary>
        public UpdateSource? Source { get; }

        /// <summary>Every source answered "there is no such release" — not an error.</summary>
        public bool NoRelease { get; }

        /// <summary>English source sentence explaining a failure; null when <see cref="HasRelease"/>.</summary>
        public string? FailureReason { get; }

        public IReadOnlyList<UpdateAttemptInfo> Attempts { get; }

        public IReadOnlyList<UpdateAssetRejection> RejectedAssets { get; }

        public long ElapsedMilliseconds { get; }

        /// <summary>"github-releases ok · npm-official HTTP 404" — one line for the log/console.</summary>
        public string DescribeAttempts()
        {
            if (Attempts.Count == 0) return "no source was queried";
            var parts = new List<string>(Attempts.Count);
            foreach (UpdateAttemptInfo attempt in Attempts)
            {
                parts.Add(attempt.Succeeded
                    ? attempt.SourceId + " ok"
                    : attempt.SourceId + " " + (attempt.Missing ? "no release" : attempt.Reason ?? "failed"));
            }
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Outcome of a completed transfer (path is inside the update temp directory).</summary>
    public sealed record UpdateDownloadResult(string FilePath, long Bytes, string Url);

    /// <summary>Result of the size + SHA-256 verification step.</summary>
    public sealed record UpdateVerifyOutcome(bool Ok, string Sha256, string? Reason, string? Detail)
    {
        public static UpdateVerifyOutcome Pass(string sha256) => new(true, sha256, null, null);
        public static UpdateVerifyOutcome Fail(string reason, string? detail = null) => new(false, string.Empty, reason, detail);
    }

    /// <summary>Download failure with a transient flag (drives the retry decision).</summary>
    public sealed class UpdateDownloadException : Exception
    {
        public UpdateDownloadException(string message, bool transient) : base(message) => Transient = transient;

        public bool Transient { get; }
    }

    /// <summary>HTTP-level failure from a source (carries the "nothing published" bit).</summary>
    public sealed class UpdateHttpException : Exception
    {
        public UpdateHttpException(string message, bool transient, bool missing = false) : base(message)
        {
            Transient = transient;
            Missing = missing;
        }

        public bool Transient { get; }

        /// <summary>404: the source exists but has nothing published.</summary>
        public bool Missing { get; }
    }

    // ── Client ──────────────────────────────────────────────────────────────────────────────

    public sealed class UpdateClient : IDisposable
    {
        private const long MaximumManifestBytes = 1L * 1024 * 1024;
        private const long MaximumMetadataBytes = 4L * 1024 * 1024;
        private const long MaximumTarballBytes = 16L * 1024 * 1024;

        private readonly HttpClient _http;
        private readonly SemaphoreSlim _downloadGate = new(1, 1);
        private int _sourceOffset;

        public UpdateClient(UpdateOptions options, HttpMessageHandler? handler = null)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));

            HttpMessageHandler effectiveHandler = handler ?? new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
            };
            _http = new HttpClient(effectiveHandler, disposeHandler: true)
            {
                // Per-request budgets are applied with CancellationTokenSource, so a slow source
                // cannot hold the whole check; HttpClient.Timeout would also apply to a download's
                // body copy, which is the wrong place for it.
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(Options.UserAgent);
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public UpdateOptions Options { get; }

        /// <summary>Directory downloads land in (created on demand).</summary>
        public string TempRoot => Options.ResolveTempRoot();

        // ── Check ────────────────────────────────────────────────────────────────────────

        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
            => CheckAsync(Options.Channel, cancellationToken);

        /// <summary>
        /// Queries the source groups and returns the newest complete manifest. Never throws for
        /// network reasons; only a caller cancellation propagates.
        /// </summary>
        public async Task<UpdateCheckResult> CheckAsync(string channel, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(channel)) channel = UpdateOptions.DefaultChannel;
            string normalizedChannel = channel.Trim().ToLowerInvariant();

            var attempts = new List<UpdateAttemptInfo>();
            var rejected = new List<UpdateAssetRejection>();
            var clock = Stopwatch.StartNew();

            (List<UpdateSource> official, List<UpdateSource> accelerated) = ResolveGroups(normalizedChannel);

            UpdateAttemptInfo? best = await RunGroupAsync(official, normalizedChannel, attempts, rejected,
                Options.GroupBudget, cancellationToken).ConfigureAwait(false);

            if (best is null)
            {
                TimeSpan remaining = Options.TotalBudget - clock.Elapsed;
                if (remaining > TimeSpan.FromMilliseconds(200))
                {
                    TimeSpan budget = remaining < Options.GroupBudget ? remaining : Options.GroupBudget;
                    best = await RunGroupAsync(accelerated, normalizedChannel, attempts, rejected,
                        budget, cancellationToken).ConfigureAwait(false);
                }
            }

            clock.Stop();

            if (best is not null && best.Manifest is not null && best.Asset is not null)
            {
                return new UpdateCheckResult(true, best.Manifest, best.Asset, best.Source, false, null,
                    attempts, rejected, clock.ElapsedMilliseconds);
            }

            // "No release published anywhere" is a normal answer, not a failure: at least one
            // authoritative source answered "this does not exist yet" (404 / empty release
            // list / no version on the channel) and no source handed us a manifest it then
            // refused (that would be a structural problem, not an empty feed). A source that
            // merely timed out or hit a rate limit does not turn the answer into an error as
            // long as another reached source has the authoritative "nothing there" answer.
            bool anyMissing = false;
            bool anyInvalid = false;
            foreach (UpdateAttemptInfo attempt in attempts)
            {
                if (attempt.Missing) anyMissing = true;
                else if (attempt.Invalid) anyInvalid = true;
            }
            bool noRelease = attempts.Count > 0 && anyMissing && !anyInvalid;

            string reason;
            if (noRelease)
            {
                reason = "No release has been published on the update feed yet.";
            }
            else
            {
                // A source that answered with a manifest we refused (bad channel, missing
                // checksum) is the most useful reason to show; otherwise the sources are simply
                // unavailable and the per-source detail stays in Attempts/log.
                string? invalidReason = null;
                foreach (UpdateAttemptInfo attempt in attempts)
                {
                    if (attempt.Invalid && attempt.Reason is { } candidate)
                    {
                        invalidReason = candidate;
                        break;
                    }
                }
                reason = invalidReason ?? "The update source is unavailable.";
            }

            return new UpdateCheckResult(false, null, null, null, noRelease, reason, attempts, rejected,
                clock.ElapsedMilliseconds);
        }

        private (List<UpdateSource> Official, List<UpdateSource> Accelerated) ResolveGroups(string channel)
        {
            if (!string.IsNullOrWhiteSpace(Options.FeedOverride))
            {
                return (new List<UpdateSource> { UpdateSourceCatalog.Override(channel, Options.FeedOverride!) },
                        new List<UpdateSource>());
            }

            return (new List<UpdateSource>(UpdateSourceCatalog.Official(channel)),
                    new List<UpdateSource>(UpdateSourceCatalog.Accelerated(channel)));
        }

        /// <summary>
        /// Queries every source in <paramref name="sources"/> in parallel under a shared budget
        /// and returns the newest attempt that produced a complete manifest (or null).
        /// </summary>
        private async Task<UpdateAttemptInfo?> RunGroupAsync(List<UpdateSource> sources, string channel,
            List<UpdateAttemptInfo> attempts, List<UpdateAssetRejection> rejected, TimeSpan budget,
            CancellationToken cancellationToken)
        {
            if (sources.Count == 0) return null;

            using var groupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            groupCts.CancelAfter(budget);

            var tasks = new List<Task<UpdateAttemptInfo>>(sources.Count);
            foreach (UpdateSource source in sources)
                tasks.Add(FetchSourceAsync(source, channel, groupCts.Token, cancellationToken));

            UpdateAttemptInfo[] results;
            try
            {
                results = await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                results = Array.Empty<UpdateAttemptInfo>();
            }
            catch (Exception)
            {
                results = Array.Empty<UpdateAttemptInfo>();
            }

            UpdateAttemptInfo? best = null;
            foreach (UpdateAttemptInfo result in results)
            {
                attempts.Add(result);
                rejected.AddRange(result.RejectedAssets);

                if (result.Manifest is null || result.Asset is null) continue;
                if (best?.Manifest is null || result.Manifest.Version.CompareTo(best.Manifest.Version) > 0)
                    best = result;
            }

            // Every source should have produced a result; if the group budget killed one before
            // it could answer, say so instead of silently dropping it.
            if (results.Length < sources.Count)
            {
                foreach (UpdateSource source in sources)
                {
                    bool seen = false;
                    foreach (UpdateAttemptInfo result in results)
                    {
                        if (ReferenceEquals(result.Source, source)) { seen = true; break; }
                    }
                    if (seen) continue;
                    attempts.Add(new UpdateAttemptInfo(source, UpdateAttemptKind.Unavailable,
                        FormattableString.Invariant($"no answer within {budget.TotalSeconds:0.#} s")));
                }
            }

            return best;
        }

        private async Task<UpdateAttemptInfo> FetchSourceAsync(UpdateSource source, string channel,
            CancellationToken groupToken, CancellationToken outerToken)
        {
            try
            {
                return source.Kind switch
                {
                    UpdateSourceKind.GitHubReleases => await FetchGitHubReleaseAsync(source, channel, groupToken).ConfigureAwait(false),
                    UpdateSourceKind.NpmRegistry => await FetchNpmAsync(source, channel, groupToken).ConfigureAwait(false),
                    _ => await FetchManifestAsync(source, channel, groupToken).ConfigureAwait(false),
                };
            }
            catch (OperationCanceledException) when (!outerToken.IsCancellationRequested)
            {
                return new UpdateAttemptInfo(source, UpdateAttemptKind.Unavailable, "timed out");
            }
            catch (UpdateHttpException ex)
            {
                return new UpdateAttemptInfo(source, ex.Missing ? UpdateAttemptKind.Missing : UpdateAttemptKind.Unavailable, ex.Message);
            }
            catch (Exception ex)
            {
                return new UpdateAttemptInfo(source, UpdateAttemptKind.Unavailable, ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ── Manifest acquisition ────────────────────────────────────────────────────────

        private async Task<UpdateAttemptInfo> FetchManifestAsync(UpdateSource source, string channel, CancellationToken ct)
        {
            byte[] bytes = await ReadSourceAsync(source.Url, MaximumManifestBytes, source.Label + " manifest", ct)
                .ConfigureAwait(false);
            return BuildAttempt(source, channel, bytes);
        }

        private UpdateAttemptInfo BuildAttempt(UpdateSource source, string channel, byte[] jsonBytes)
        {
            string json = DecodeText(jsonBytes);
            UpdateManifestParseResult parsed = UpdateManifestParser.Parse(json, channel, Options.AllowLocalUrls);
            if (!parsed.Succeeded || parsed.Manifest is null)
            {
                return new UpdateAttemptInfo(source, UpdateAttemptKind.Invalid,
                    parsed.Failure ?? "The source returned an unreadable manifest.")
                {
                    RejectedAssets = parsed.RejectedAssets,
                };
            }

            UpdateAsset? asset = parsed.Manifest.SelectAsset();
            if (asset is null)
            {
                return new UpdateAttemptInfo(source, UpdateAttemptKind.Invalid,
                    "The update manifest is incomplete: no asset carries both sizeBytes and sha256.")
                {
                    RejectedAssets = parsed.RejectedAssets,
                };
            }

            return new UpdateAttemptInfo(source, UpdateAttemptKind.Succeeded, null)
            {
                Manifest = parsed.Manifest,
                Asset = asset,
                RejectedAssets = parsed.RejectedAssets,
            };
        }

        /// <summary>
        /// GitHub Releases: read the release list, pick the newest release for the channel, then
        /// fetch the update.json asset from it. The asset is mandatory — the API's tag alone is
        /// never treated as an installable release (docs\UPDATE-DESIGN.md §1.1.6 pitfall 17).
        /// </summary>
        private async Task<UpdateAttemptInfo> FetchGitHubReleaseAsync(UpdateSource source, string channel, CancellationToken ct)
        {
            byte[] releaseBytes = await ReadSourceAsync(source.Url, MaximumMetadataBytes, source.Label + " releases", ct)
                .ConfigureAwait(false);

            using JsonDocument document = ParseJson(releaseBytes, source.Label + " releases");
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return new UpdateAttemptInfo(source, UpdateAttemptKind.Invalid, "The releases endpoint did not return a list.");

            bool anyRelease = false;
            SemanticVersion? bestVersion = null;
            string? tag = null;
            JsonElement best = default;

            foreach (JsonElement release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object) continue;
                anyRelease = true;
                if (release.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True) continue;

                string? tagName = release.TryGetProperty("tag_name", out JsonElement tagElement) &&
                                  tagElement.ValueKind == JsonValueKind.String
                    ? tagElement.GetString()
                    : null;
                SemanticVersion? version = SemanticVersion.Parse(tagName);
                if (version is null || !MatchesChannel(version, channel)) continue;
                if (bestVersion is not null && version.CompareTo(bestVersion) <= 0) continue;

                bestVersion = version;
                tag = tagName;
                best = release;
            }

            if (!anyRelease)
                return new UpdateAttemptInfo(source, UpdateAttemptKind.Missing,
                    "No release has been published on the update feed yet.");

            if (bestVersion is null)
                return new UpdateAttemptInfo(source, UpdateAttemptKind.Missing,
                    $"No release on channel '{channel}' has been published yet.");

            string manifestName = UpdateSourceCatalog.ManifestFileName(channel);
            string? assetUrl = FindReleaseAssetUrl(best, manifestName);
            if (assetUrl is null && !string.Equals(manifestName, "update.json", StringComparison.OrdinalIgnoreCase))
                assetUrl = FindReleaseAssetUrl(best, "update.json");

            if (assetUrl is null)
                return new UpdateAttemptInfo(source, UpdateAttemptKind.Invalid,
                    $"Release {tag} does not carry a {manifestName} asset.");

            byte[] manifestBytes = await ReadSourceAsync(assetUrl, MaximumManifestBytes,
                source.Label + " " + manifestName, ct).ConfigureAwait(false);
            return BuildAttempt(source, channel, manifestBytes);
        }

        private static string? FindReleaseAssetUrl(JsonElement release, string fileName)
        {
            if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;
                string? name = asset.TryGetProperty("name", out JsonElement nameElement) &&
                               nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;
                if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (asset.TryGetProperty("browser_download_url", out JsonElement urlElement) &&
                    urlElement.ValueKind == JsonValueKind.String)
                    return urlElement.GetString();
            }
            return null;
        }

        /// <summary>
        /// npm registry: read the metadata, take the newest version on the channel, download that
        /// version's tarball and read the manifest out of it (it rides inside the package, as the
        /// brief specifies).
        /// </summary>
        private async Task<UpdateAttemptInfo> FetchNpmAsync(UpdateSource source, string channel, CancellationToken ct)
        {
            byte[] metadataBytes = await ReadSourceAsync(source.Url, MaximumMetadataBytes, source.Label + " metadata", ct)
                .ConfigureAwait(false);

            using JsonDocument document = ParseJson(metadataBytes, source.Label + " metadata");
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new UpdateAttemptInfo(source, UpdateAttemptKind.Invalid, "The npm registry did not return a package document.");

            if (!document.RootElement.TryGetProperty("versions", out JsonElement versions) ||
                versions.ValueKind != JsonValueKind.Object)
                return new UpdateAttemptInfo(source, UpdateAttemptKind.Missing, "The npm package has no published versions.");

            SemanticVersion? bestVersion = null;
            string? bestKey = null;
            string? tarball = null;

            foreach (JsonProperty property in versions.EnumerateObject())
            {
                SemanticVersion? version = SemanticVersion.Parse(property.Name);
                if (version is null || !MatchesChannel(version, channel)) continue;
                if (bestVersion is not null && version.CompareTo(bestVersion) <= 0) continue;
                if (!property.Value.TryGetProperty("dist", out JsonElement dist) ||
                    !dist.TryGetProperty("tarball", out JsonElement tarballElement) ||
                    tarballElement.ValueKind != JsonValueKind.String) continue;

                bestVersion = version;
                bestKey = property.Name;
                tarball = tarballElement.GetString();
            }

            if (bestKey is null || tarball is null)
                return new UpdateAttemptInfo(source, UpdateAttemptKind.Missing,
                    $"No npm version on channel '{channel}' has been published yet.");

            byte[] tarballBytes = await ReadSourceAsync(tarball, MaximumTarballBytes,
                source.Label + " tarball", ct).ConfigureAwait(false);

            string manifestName = UpdateSourceCatalog.ManifestFileName(channel);
            string? json = ExtractTarEntryText(tarballBytes, "package/" + manifestName)
                           ?? ExtractTarEntryText(tarballBytes, "package/update.json");
            if (json is null)
                return new UpdateAttemptInfo(source, UpdateAttemptKind.Invalid,
                    $"npm version {bestKey} does not carry {manifestName}.");

            return BuildAttempt(source, channel, Encoding.UTF8.GetBytes(json));
        }

        /// <summary>Channel membership: alpha accepts any pre-release, beta accepts beta/rc, stable none.</summary>
        public static bool MatchesChannel(SemanticVersion? version, string channel)
        {
            if (version is null) return false;
            switch ((channel ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "stable":
                    return !version.IsPrerelease;
                case "beta":
                    return version.IsPrerelease && StartsWithLabel(version.Prerelease, "beta", "rc");
                case "alpha":
                    return version.IsPrerelease && StartsWithLabel(version.Prerelease, "alpha", "beta", "rc");
                default:
                    return true;
            }
        }

        private static bool StartsWithLabel(string prerelease, params string[] labels)
        {
            foreach (string label in labels)
            {
                if (prerelease.Equals(label, StringComparison.OrdinalIgnoreCase)) return true;
                if (prerelease.StartsWith(label + ".", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ── Download ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Streams <paramref name="asset"/> into the update temp directory. Returns the completed
        /// file; verification is a separate step (<see cref="VerifyAsync"/>) so the page can show
        /// "校验中". <paramref name="rotateSource"/> makes "换源重试" start from the next
        /// candidate address instead of the one that just failed.
        /// </summary>
        public async Task<UpdateDownloadResult> DownloadAsync(UpdateAsset asset, UpdateSource? source,
            IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken, bool rotateSource = false)
        {
            if (asset is null) throw new ArgumentNullException(nameof(asset));

            // ONE TRANSFER AT A TIME. Two concurrent calls would both write the same
            // <name>.part, interleave their Range resume offsets and share one HttpClient — the
            // file that reaches verification then belongs to neither transfer. A rotation that
            // arrives while a download is running is refused here too, and the caller (the
            // session) cancels the running one first, so _sourceOffset is only ever advanced by
            // a transfer that actually starts.
            if (!_downloadGate.Wait(0))
                throw new UpdateDownloadException(
                    "Another update download is already running; the new request was ignored.", transient: false);

            try
            {
                if (rotateSource) _sourceOffset++;

                var candidates = BuildDownloadCandidates(asset, source);
                if (candidates.Count == 0)
                    throw new UpdateDownloadException("The update package has no usable download address.", transient: false);

                string directory = TempRoot;
                Directory.CreateDirectory(directory);
                string finalPath = Path.Combine(directory, SanitizeFileName(asset.Name));
                string partPath = finalPath + ".part";
                string metaPath = finalPath + ".part.meta";

                string lastReason = "The download failed.";
                for (int i = 0; i < candidates.Count; i++)
                {
                    string url = candidates[((_sourceOffset + i) % candidates.Count + candidates.Count) % candidates.Count];
                    int attempts = Options.MaxRetries + 1;
                    for (int attempt = 0; attempt < attempts; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            long written = await DownloadFromUrlAsync(url, asset, partPath, metaPath, progress, cancellationToken)
                                .ConfigureAwait(false);
                            if (File.Exists(finalPath)) TryDelete(finalPath);
                            File.Move(partPath, finalPath, overwrite: true);
                            TryDelete(metaPath);
                            return new UpdateDownloadResult(finalPath, written, url);
                        }
                        catch (UpdateDownloadException ex)
                        {
                            lastReason = ex.Message;
                            if (!ex.Transient) break;          // permanent for this address: next one
                        }

                        if (attempt + 1 < attempts)
                            await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                    }
                }

                throw new UpdateDownloadException(lastReason, transient: false);
            }
            finally
            {
                _downloadGate.Release();
            }
        }

        private async Task<long> DownloadFromUrlAsync(string url, UpdateAsset asset, string partPath,
            string metaPath, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

            // A .part that belongs to another asset or another URL must not be resumed.
            if (existing > 0 && (!File.Exists(metaPath) || !PartMatches(metaPath, asset, url)))
            {
                TryDelete(partPath);
                TryDelete(metaPath);
                existing = 0;
            }

            if (existing > asset.SizeBytes)
            {
                TryDelete(partPath);
                TryDelete(metaPath);
                existing = 0;
            }

            if (asset.SizeBytes > 0 && existing == asset.SizeBytes)
            {
                // Already complete: nothing to transfer, verification decides.
                progress?.Report(new UpdateDownloadProgress(existing, asset.SizeBytes, 0, TimeSpan.Zero));
                return existing;
            }

            Uri.TryCreate(url, UriKind.Absolute, out Uri? uri);
            if (uri is not null && uri.IsFile)
                return await CopyLocalFileAsync(uri.LocalPath, partPath, existing, progress, cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

            using var headerBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headerBudget.CancelAfter(Options.GroupBudget);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerBudget.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new UpdateDownloadException(
                    FormattableString.Invariant($"The download did not start within {Options.GroupBudget.TotalSeconds:0} s."),
                    transient: true);
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateDownloadException("Download failed: " + ex.Message, transient: true);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    // Resume accepted: append to the .part.
                }
                else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
                {
                    TryDelete(partPath);
                    TryDelete(metaPath);
                    throw new UpdateDownloadException(
                        "The update server rejected the resume range; retrying from scratch.", transient: true);
                }
                else if (response.StatusCode == HttpStatusCode.OK)
                {
                    // Server ignored Range (or we asked for the whole file): start over.
                    existing = 0;
                }
                else if (IsTransientStatus(response.StatusCode))
                {
                    throw new UpdateDownloadException(
                        $"Download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.", transient: true);
                }
                else
                {
                    throw new UpdateDownloadException(
                        $"Download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.", transient: false);
                }

                long total = ResolveTotalBytes(response, existing);
                WritePartMeta(metaPath, asset, url);

                using var fileStream = new FileStream(partPath,
                    existing > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 81920, useAsync: true);

                using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await PumpAsync(responseStream, fileStream, existing, total, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Copies the response body into the .part file, enforcing the 20 s stall watchdog and
        /// reporting smoothed progress. Returns the final length of the file.
        /// </summary>
        private async Task<long> PumpAsync(Stream source, FileStream destination, long startOffset, long total,
            IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            long received = startOffset;
            double speed = 0;
            long windowStartBytes = received;
            var windowClock = Stopwatch.StartNew();

            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                while (true)
                {
                    // Restart the stall timer for every read: 20 s without a single new byte is
                    // the failure mode that otherwise hangs on a dead mirror for minutes.
                    stall.CancelAfter(Options.StallTimeout);

                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new UpdateDownloadException(
                            FormattableString.Invariant($"The download stalled for {Options.StallTimeout.TotalSeconds:0} s."),
                            transient: true);
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException)
                    {
                        // A reset connection mid-body: transient, the .part is resumed on retry.
                        throw new UpdateDownloadException("The download was interrupted: " + ex.Message, transient: true);
                    }

                    if (read <= 0) break;

                    try
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException)
                    {
                        throw new UpdateDownloadException("The update package could not be written: " + ex.Message,
                            transient: false);
                    }

                    received += read;

                    double windowSeconds = windowClock.Elapsed.TotalSeconds;
                    if (windowSeconds >= 0.4)
                    {
                        double instant = (received - windowStartBytes) / windowSeconds;
                        speed = speed <= 0 ? instant : (speed * 0.55) + (instant * 0.45);
                        windowStartBytes = received;
                        windowClock.Restart();
                    }

                    TimeSpan? remaining = null;
                    if (total > 0 && speed > 1)
                    {
                        double seconds = (total - received) / speed;
                        if (seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds)
                            remaining = TimeSpan.FromSeconds(seconds);
                    }

                    progress?.Report(new UpdateDownloadProgress(received, total, speed, remaining));
                }
            }
            finally
            {
                stall.Dispose();
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

            long length = destination.Length;
            TimeSpan? finalRemaining = total > 0 && speed > 1 && total > length
                ? TimeSpan.FromSeconds((total - length) / speed)
                : TimeSpan.Zero;
            progress?.Report(new UpdateDownloadProgress(length, total, speed, finalRemaining));
            return length;
        }

        private async Task<long> CopyLocalFileAsync(string path, string partPath, long existing,
            IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            if (!File.Exists(path))
                throw new UpdateDownloadException("The local update package does not exist: " + path, transient: false);

            var info = new FileInfo(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            if (existing > 0) input.Seek(existing, SeekOrigin.Begin);

            using var output = new FileStream(partPath, existing > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 81920, useAsync: true);

            return await PumpAsync(input, output, existing, info.Length, progress, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Size first, then SHA-256. A mismatch deletes the file (a bad package must never survive
        /// to the next attempt) and returns a reason the page can show.
        /// </summary>
        public static async Task<UpdateVerifyOutcome> VerifyAsync(string path, UpdateAsset asset,
            CancellationToken cancellationToken = default)
        {
            if (asset is null) throw new ArgumentNullException(nameof(asset));
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return UpdateVerifyOutcome.Fail("The downloaded update package is missing.");

            long length = new FileInfo(path).Length;
            if (length != asset.SizeBytes)
            {
                DiscardDownload(path);
                return UpdateVerifyOutcome.Fail(
                    "Verification failed: the file size does not match the manifest.",
                    FormattableString.Invariant($"manifest {asset.SizeBytes} bytes, file {length} bytes"));
            }

            string actual;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, useAsync: true);
                byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                actual = Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return UpdateVerifyOutcome.Fail("Verification failed: the file could not be read.", ex.Message);
            }

            if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                DiscardDownload(path);
                return UpdateVerifyOutcome.Fail(
                    "Verification failed: the SHA-256 does not match the manifest.",
                    "expected " + asset.Sha256 + ", got " + actual);
            }

            return UpdateVerifyOutcome.Pass(actual);
        }

        /// <summary>
        /// Candidate addresses for an asset. A GitHub release URL is also reachable through the
        /// two proxy prefixes the brief names; when the manifest itself came from a proxy the
        /// proxied address is tried first, otherwise the direct one is.
        /// </summary>
        public List<string> BuildDownloadCandidates(UpdateAsset asset, UpdateSource? source)
        {
            var candidates = new List<string>();
            var proxies = new List<string>();

            void Add(List<string> target, string? url)
            {
                if (string.IsNullOrWhiteSpace(url)) return;
                foreach (string existing in candidates)
                {
                    if (string.Equals(existing, url, StringComparison.OrdinalIgnoreCase)) return;
                }
                foreach (string existing in proxies)
                {
                    if (string.Equals(existing, url, StringComparison.OrdinalIgnoreCase)) return;
                }
                target.Add(url!);
            }

            if (Uri.TryCreate(asset.Url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps &&
                (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            {
                Add(proxies, "https://gh-proxy.com/" + asset.Url);
                Add(proxies, "https://ghproxy.net/" + asset.Url);
            }

            bool proxyFirst = source is not null && source.Id is "gh-proxy" or "ghproxy-net" or "jsdelivr-gh";
            if (proxyFirst)
            {
                candidates.AddRange(proxies);
                Add(candidates, asset.Url);
            }
            else
            {
                Add(candidates, asset.Url);
                candidates.AddRange(proxies);
            }

            return candidates;
        }

        // ── Low-level HTTP / parsing helpers ────────────────────────────────────────────

        private async Task<byte[]> ReadSourceAsync(string location, long maximumBytes, string what, CancellationToken cancellationToken)
        {
            if (TryResolveLocalFile(location, out string? localPath))
            {
                var info = new FileInfo(localPath!);
                if (info.Length > maximumBytes)
                    throw new UpdateHttpException($"{what} is larger than {maximumBytes} bytes.", transient: false);
                return await File.ReadAllBytesAsync(localPath!, cancellationToken).ConfigureAwait(false);
            }

            UpdateHttpException? last = null;
            for (int attempt = 0; attempt <= Options.MaxRetries; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken).ConfigureAwait(false);

                try
                {
                    return await ReadOnceAsync(location, maximumBytes, what, cancellationToken).ConfigureAwait(false);
                }
                catch (UpdateHttpException ex)
                {
                    last = ex;
                    if (!ex.Transient || attempt >= Options.MaxRetries) throw;
                }
            }

            throw last ?? new UpdateHttpException(what + " could not be read.", transient: false);
        }

        private async Task<byte[]> ReadOnceAsync(string location, long maximumBytes, string what, CancellationToken cancellationToken)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(Options.GroupBudget);

            using var request = new HttpRequestMessage(HttpMethod.Get, location);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new UpdateHttpException(
                    FormattableString.Invariant($"no answer within {Options.GroupBudget.TotalSeconds:0.#} s"), transient: true);
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateHttpException(ex.Message, transient: true);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new UpdateHttpException("HTTP 404 (nothing published)", transient: false, missing: true);

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateHttpException(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", IsTransientStatus(response.StatusCode));
            }

            long? declared = response.Content.Headers.ContentLength;
            if (declared is long size && size > maximumBytes)
                throw new UpdateHttpException($"{what} is larger than {maximumBytes} bytes.", transient: false);

            using Stream stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            long total = 0;
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(chunk, budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new UpdateHttpException("timed out reading " + what, transient: true);
                }

                if (read <= 0) break;
                total += read;
                if (total > maximumBytes)
                    throw new UpdateHttpException($"{what} is larger than {maximumBytes} bytes.", transient: false);
                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        private static bool TryResolveLocalFile(string location, out string? path)
        {
            path = null;
            if (string.IsNullOrWhiteSpace(location)) return false;
            if (Uri.TryCreate(location, UriKind.Absolute, out Uri? uri))
            {
                if (uri.IsFile)
                {
                    path = uri.LocalPath;
                    return File.Exists(path);
                }
                if (uri.Scheme is "http" or "https") return false;
            }
            if (File.Exists(location))
            {
                path = Path.GetFullPath(location);
                return true;
            }
            return false;
        }

        private static JsonDocument ParseJson(byte[] bytes, string what)
        {
            try
            {
                return JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 32,
                });
            }
            catch (JsonException)
            {
                throw new UpdateHttpException(what + " is not valid JSON.", transient: false);
            }
        }

        private static string DecodeText(byte[] bytes)
        {
            // Feed servers do not always flag UTF-8; a BOM would break JsonDocument.Parse.
            int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        }

        /// <summary>Reads one text entry out of an npm tarball; null when it is not there.</summary>
        private static string? ExtractTarEntryText(byte[] tarballBytes, string entryName)
        {
            try
            {
                using var compressed = new MemoryStream(tarballBytes, writable: false);
                using var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new System.Formats.Tar.TarReader(gzip, leaveOpen: false);
                System.Formats.Tar.TarEntry? entry;
                while ((entry = reader.GetNextEntry()) is not null)
                {
                    if (entry.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile
                        or System.Formats.Tar.TarEntryType.V7RegularFile)) continue;
                    string name = entry.Name.Replace('\\', '/');
                    if (!string.Equals(name, entryName, StringComparison.Ordinal) &&
                        !name.EndsWith("/" + entryName, StringComparison.Ordinal)) continue;
                    if (entry.Length > MaximumManifestBytes)
                        throw new UpdateHttpException("The npm update.json is unreasonably large.", transient: false);

                    using Stream? stream = entry.DataStream;
                    if (stream is null) return null;
                    using var text = new StreamReader(stream, Encoding.UTF8);
                    return text.ReadToEnd();
                }
            }
            catch (InvalidDataException)
            {
                throw new UpdateHttpException("The npm tarball could not be read.", transient: false);
            }
            return null;
        }

        private static bool IsTransientStatus(HttpStatusCode status)
        {
            int code = (int)status;
            if (code == 408 || code == 429) return true;
            return code >= 500 && code <= 599;
        }

        private static long ResolveTotalBytes(HttpResponseMessage response, long startOffset)
        {
            if (response.Content.Headers.ContentRange?.Length is long rangeLength && rangeLength > 0)
                return rangeLength;

            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength is long length && length >= 0) return startOffset + length;

            // No declared length (chunked): the UI shows an indeterminate bar, never a guess.
            return -1;
        }

        private static string MetaText(UpdateAsset asset, string url)
            => asset.Sha256 + "|" + asset.SizeBytes.ToString(CultureInfo.InvariantCulture) + "|" + url;

        private static bool PartMatches(string metaPath, UpdateAsset asset, string url)
        {
            try
            {
                return string.Equals(File.ReadAllText(metaPath).Trim(), MetaText(asset, url), StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static void WritePartMeta(string metaPath, UpdateAsset asset, string url)
        {
            try { File.WriteAllText(metaPath, MetaText(asset, url)); } catch { /* resume identity is best effort */ }
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "package.zip";
            string file = Path.GetFileName(name.Replace('\\', '/'));
            foreach (char invalid in Path.GetInvalidFileNameChars())
                file = file.Replace(invalid, '_');
            return file.Length == 0 ? "package.zip" : file;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }

        /// <summary>
        /// Removes a package together with the resume fragments beside it (<c>.part</c> and
        /// <c>.part.meta</c>). Called when verification refuses the file: a package whose size or
        /// SHA-256 does not match the manifest must never be resumed from, otherwise the next
        /// attempt appends at an offset of a body that was already wrong.
        /// </summary>
        internal static void DiscardDownload(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            TryDelete(path);
            TryDelete(path + ".part");
            TryDelete(path + ".part.meta");
        }

        public void Dispose()
        {
            _http.Dispose();
            _downloadGate.Dispose();
        }
    }
}
