using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Performix.Installer;

/// <summary>Outcome of a transfer.</summary>
public sealed record DownloadResult(string Path, long Bytes, bool UsedRanges, int Segments);

/// <summary>
/// Range-based, multi-threaded, resumable file download.
///
/// Shape of the transfer:
/// 1. Probe: <c>HEAD</c> for <c>Content-Length</c> and <c>Accept-Ranges</c>. If HEAD refuses or is
///    ambiguous, one <c>GET Range: bytes=0-0</c> probe decides (a 206 with a Content-Range total
///    is the definitive "ranges work" answer, and some proxies drop Accept-Ranges from HEAD).
/// 2. If ranges are usable, cut the length into <see cref="SegmentMath.Plan"/> and fetch the pieces
///    concurrently — each segment writes its own <c>&lt;file&gt;.part.&lt;n&gt;</c> file at its own
///    offset, so an interrupted run resumes exactly where it stopped, per segment.
/// 3. Otherwise fall back to a single streamed response (some CDNs, and every range-hostile
///    corporate proxy, end up here).
/// 4. Concatenate the parts into the destination and delete them.
///
/// Each segment retries up to <c>maxRetries</c> times with exponential backoff and resumes inside
/// the retry (the part file is authoritative), which is what makes a flaky 55 MB runtime download
/// survive instead of starting over.
/// </summary>
public sealed class SegmentedDownloader
{
    /// <summary>Below this, one request beats eight: connection setup costs more than parallelism saves.</summary>
    public const long RangeThresholdBytes = 1L << 20;

    private readonly int _segmentCount;
    private readonly int _maxRetries;
    private readonly TimeSpan _retryDelay;
    private readonly long _rangeThreshold;
    private readonly Action<string>? _log;

    public SegmentedDownloader(
        int segmentCount = SegmentMath.DefaultSegmentCount,
        int maxRetries = 3,
        TimeSpan? retryDelay = null,
        long rangeThreshold = RangeThresholdBytes,
        Action<string>? log = null)
    {
        _segmentCount = segmentCount < 1 ? 1 : segmentCount;
        _maxRetries = maxRetries < 0 ? 0 : maxRetries;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(800);
        _rangeThreshold = rangeThreshold;
        _log = log;
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destination"/>. Safe to call twice:
    /// the second call resumes from the <c>.part</c> files the first one left behind.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        HttpClient http,
        string url,
        string destination,
        long? expectedSize = null,
        Action<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        string fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);

        ProbeOutcome probe = await ProbeAsync(http, url, cancellationToken).ConfigureAwait(false);
        long? total = probe.Length ?? (expectedSize is > 0 ? expectedSize : null);
        bool segmented = SegmentMath.IsRangeSupported(probe.AcceptRanges, total)
                         && (total ?? 0) >= _rangeThreshold
                         && _segmentCount > 1;

        var aggregator = new ProgressAggregator(total ?? 0);
        int segmentCount = 1;

        if (segmented)
        {
            DownloadSegment[] plan = SegmentMath.Plan(total!.Value, _segmentCount);
            segmentCount = plan.Length;

            // Bytes already on disk from a previous run count as progress: seeding the counter
            // keeps the bar honest (it resumes at 28%, not at 0%) and keeps the rate estimate from
            // briefly claiming the whole resumed size went through the wire.
            long resumed = plan.Sum(segment => ResumeLength(SegmentMath.PartPath(fullDestination, segment.Index), segment));
            aggregator.Seed(resumed);

            _log?.Invoke($"分段下载：{plan.Length} 段 / {total} 字节" + (resumed > 0 ? $"（续传：已有 {resumed} 字节）" : string.Empty));
            await DownloadSegmentedAsync(http, url, fullDestination, plan, aggregator, progress, cancellationToken)
                .ConfigureAwait(false);
            MergeParts(fullDestination, plan);
        }
        else
        {
            _log?.Invoke(probe.AcceptRanges
                ? "单线程下载（文件太小或长度未知）"
                : "单线程下载（服务器不支持 Range）");
            await DownloadSingleAsync(http, url, fullDestination, total, aggregator, progress, cancellationToken)
                .ConfigureAwait(false);

            // A previous run may have been segmented (or the server changed its mind about
            // ranges); its .part files are dead weight once the single stream has the whole file.
            DeleteParts(fullDestination);
        }

        long size = new FileInfo(fullDestination).Length;
        if (total is > 0 && size != total.Value)
        {
            throw new IOException($"下载不完整：期望 {total} 字节，实际 {size} 字节");
        }

        progress?.Invoke(new DownloadProgress(size, size, 0));

        return new DownloadResult(fullDestination, size, segmented, segmentCount);
    }

    // ── probe ────────────────────────────────────────────────────────────────────────────

    private readonly record struct ProbeOutcome(long? Length, bool AcceptRanges);

    private static async Task<ProbeOutcome> ProbeAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using HttpResponseMessage response =
                await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                long? length = response.Content.Headers.ContentLength;
                bool advertised = AcceptsBytes(response);
                if (length is > 0 && advertised)
                {
                    return new ProbeOutcome(length, true);
                }

                if (length is > 0)
                {
                    // Length known but no Accept-Ranges: ask once rather than assume.
                    return new ProbeOutcome(length, await SupportsRangesAsync(http, url, length, cancellationToken)
                        .ConfigureAwait(false));
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            // HEAD blocked by a proxy: fall through to the ranged GET probe.
        }

        return await ProbeByRangeAsync(http, url, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> SupportsRangesAsync(HttpClient http, string url, long? length, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using HttpResponseMessage response =
                await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.PartialContent;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task<ProbeOutcome> ProbeByRangeAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using HttpResponseMessage response =
                await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                long? length = SegmentMath.TryParseContentRangeTotal(
                    response.Content.Headers.ContentRange?.ToString(), out long total)
                    ? total
                    : response.Content.Headers.ContentLength;
                return new ProbeOutcome(length, length is > 0);
            }

            return new ProbeOutcome(response.Content.Headers.ContentLength, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return new ProbeOutcome(null, false);
        }
    }

    private static bool AcceptsBytes(HttpResponseMessage response)
        => response.Headers.AcceptRanges.Any(value => value.Contains("bytes", StringComparison.OrdinalIgnoreCase));

    // ── single stream ───────────────────────────────────────────────────────────────────

    private async Task DownloadSingleAsync(
        HttpClient http, string url, string destination, long? total,
        ProgressAggregator aggregator, Action<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using HttpResponseMessage response =
                    await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                long? length = response.Content.Headers.ContentLength ?? total;
                if (length is > 0)
                {
                    aggregator.SetTotal(length.Value);
                }

                await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

                byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int read;
                    while ((read = await body.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        aggregator.Add(read);
                        aggregator.TryReport(progress);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                return;
            }
            catch (Exception ex) when (attempt <= _maxRetries && !cancellationToken.IsCancellationRequested
                                       && ex is HttpRequestException or IOException)
            {
                _log?.Invoke($"下载第 {attempt} 次失败：{ex.Message}");
                await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // ── segmented ───────────────────────────────────────────────────────────────────────

    private async Task DownloadSegmentedAsync(
        HttpClient http, string url, string destination, DownloadSegment[] plan,
        ProgressAggregator aggregator, Action<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var tasks = new Task[plan.Length];
        for (int i = 0; i < plan.Length; i++)
        {
            DownloadSegment segment = plan[i];
            tasks[i] = DownloadSegmentAsync(http, url, destination, segment, aggregator, progress, cancellationToken);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task DownloadSegmentAsync(
        HttpClient http, string url, string destination, DownloadSegment segment,
        ProgressAggregator aggregator, Action<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        string partPath = SegmentMath.PartPath(destination, segment.Index);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await FetchSegmentAsync(http, url, partPath, segment, aggregator, progress, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt <= _maxRetries && ex is HttpRequestException or IOException)
            {
                _log?.Invoke($"分段 #{segment.Index} 第 {attempt} 次失败：{ex.Message}");
                await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task FetchSegmentAsync(
        HttpClient http, string url, string partPath, DownloadSegment segment,
        ProgressAggregator aggregator, Action<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        long have = ResumeLength(partPath, segment);
        if (have >= segment.Length)
        {
            return; // this segment is already complete on disk
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(segment.Start + have, segment.End);

        using HttpResponseMessage response =
            await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new IOException(
                $"分段 #{segment.Index}：服务器没有按 Range 响应（HTTP {(int)response.StatusCode}，期望 206）");
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        // A stale .part that is longer than the range (different file, different length): drop it.
        file.SetLength(have);
        file.Seek(have, SeekOrigin.Begin);

        long written = have;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (written + read > segment.Length)
                {
                    throw new IOException($"分段 #{segment.Index}：响应超出请求范围");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                aggregator.Add(read);
                aggregator.TryReport(progress);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (written != segment.Length)
        {
            throw new IOException(
                $"分段 #{segment.Index} 不完整：期望 {segment.Length} 字节，收到 {written} 字节");
        }
    }

    private static long ResumeLength(string partPath, DownloadSegment segment)
    {
        try
        {
            return File.Exists(partPath)
                ? SegmentMath.ResumedLength(segment, new FileInfo(partPath).Length)
                : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void MergeParts(string destination, DownloadSegment[] plan)
    {
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
            FileShare.None, 262144, FileOptions.SequentialScan);

        foreach (DownloadSegment segment in plan)
        {
            string partPath = SegmentMath.PartPath(destination, segment.Index);
            if (!File.Exists(partPath))
            {
                throw new IOException($"分段 #{segment.Index} 的分片文件不见了：{partPath}");
            }

            using (var input = new FileStream(partPath, FileMode.Open, FileAccess.Read,
                       FileShare.Read, 262144, FileOptions.SequentialScan))
            {
                input.CopyTo(output, 262144);
            }

            // The pieces are only an intermediate form; leaving 8 x N/8 bytes of them behind next
            // to every download doubles the footprint of the runtime cache for no reason.
            output.Flush();
            TryDelete(partPath);
        }
    }

    /// <summary>
    /// Removes the destination and every <c>.part.N</c> next to it. Called when a checksum
    /// mismatch means the bytes are useless — resuming onto them would only fail again.
    /// </summary>
    public static void DeleteArtifacts(string destination)
    {
        TryDelete(destination);
        DeleteParts(destination);
    }

    /// <summary>Removes only the <c>.part.N</c> files next to <paramref name="destination"/>.</summary>
    private static void DeleteParts(string destination)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        string prefix = Path.GetFileName(destination) + ".part.";
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, prefix + "*"))
            {
                TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to clean up.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked file is the caller's problem to report, not a crash here.
        }
    }

    private TimeSpan Backoff(int attempt)
        => TimeSpan.FromMilliseconds(_retryDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1)));

    /// <summary>
    /// Thread-safe progress accumulation for the workers. Speed is an exponential moving average
    /// (a raw "bytes / total elapsed" number starts low, jumps after a resume and never settles).
    /// </summary>
    private sealed class ProgressAggregator
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private long _received;
        private long _total;
        private double _speed;
        private TimeSpan _lastSample;

        public ProgressAggregator(long total) => _total = total;

        public void SetTotal(long total) => Interlocked.Exchange(ref _total, total);

        /// <summary>Counts bytes that were already on disk (a resume) as progress.</summary>
        public void Seed(long bytes)
        {
            if (bytes <= 0)
            {
                return;
            }

            lock (_gate)
            {
                _received += bytes;
                _sampledBytes += bytes; // not transferred by this run: must not become "speed"
            }
        }

        public void Add(long bytes) => Interlocked.Add(ref _received, bytes);

        /// <summary>
        /// Invokes <paramref name="sink"/> at most ~8 times a second. The console cannot refresh
        /// faster than that anyway, and a per-read callback would burn more time than the download.
        /// </summary>
        public void TryReport(Action<DownloadProgress>? sink)
        {
            if (sink is null)
            {
                return;
            }

            lock (_gate)
            {
                TimeSpan now = _clock.Elapsed;
                TimeSpan elapsed = now - _lastSample;
                if (elapsed < TimeSpan.FromMilliseconds(125))
                {
                    return;
                }

                long received = Interlocked.Read(ref _received);
                long total = Interlocked.Read(ref _total);

                // Bytes moved since the last sample drive the instantaneous rate; a 0.3 weight on
                // the new sample keeps the readout from flickering while still reacting quickly.
                long delta = received - _sampledBytes;
                _sampledBytes = received;
                double instant = elapsed.TotalSeconds > 0 ? delta / elapsed.TotalSeconds : 0;
                _speed = _speed <= 0 ? instant : (_speed * 0.7) + (instant * 0.3);
                _lastSample = now;

                sink(new DownloadProgress(received, total, _speed));
            }
        }

        private long _sampledBytes;
    }
}
