namespace Performix.Installer;

/// <summary>
/// One byte range of a segmented download, inclusive on both ends (the HTTP <c>Range</c> header
/// is inclusive, so modelling it any other way only invents off-by-one bugs).
/// </summary>
public readonly record struct DownloadSegment(int Index, long Start, long End)
{
    /// <summary>Bytes this segment must produce.</summary>
    public long Length => End - Start + 1;

    public override string ToString() => $"#{Index} [{Start}-{End}] ({Length} B)";
}

/// <summary>
/// The arithmetic of segmented downloading, with no I/O in sight: how to cut a known length into
/// ranges, whether a server can serve ranges at all, and how far a resumed <c>.part.N</c> file
/// lets a segment skip ahead.
///
/// Kept pure so the awkward cases (zero length, unknown length, more segments than bytes, a part
/// file that is longer than its range) are testable without a network or a socket.
/// </summary>
public static class SegmentMath
{
    public const int DefaultSegmentCount = 8;

    /// <summary>
    /// Cuts <paramref name="contentLength"/> bytes into at most <paramref name="segmentCount"/>
    /// contiguous, non-overlapping, inclusive ranges that together cover every byte exactly once.
    ///
    /// The remainder always lands on the last segment: with 1001 bytes and 8 segments the first
    /// seven carry 125 bytes and the eighth carries 126. Sizing the first segment instead would
    /// be equally correct, but "the tail is one byte longer" is the shape every resumable
    /// downloader uses and the one the tests pin.
    /// </summary>
    public static DownloadSegment[] Plan(long contentLength, int segmentCount = DefaultSegmentCount)
    {
        if (contentLength <= 0)
        {
            return Array.Empty<DownloadSegment>(); // nothing to download: no ranges, not one empty range
        }

        int count = (int)Math.Min(segmentCount < 1 ? 1 : segmentCount, contentLength);
        long chunk = contentLength / count;
        var segments = new DownloadSegment[count];

        for (int i = 0; i < count; i++)
        {
            long start = i * chunk;
            long end = i == count - 1 ? contentLength - 1 : start + chunk - 1;
            segments[i] = new DownloadSegment(i, start, end);
        }

        return segments;
    }

    /// <summary>Total bytes a plan covers (should equal the content length).</summary>
    public static long TotalBytes(IReadOnlyList<DownloadSegment>? segments)
    {
        if (segments is null)
        {
            return 0;
        }

        long total = 0;
        foreach (DownloadSegment segment in segments)
        {
            total += segment.Length;
        }

        return total;
    }

    /// <summary>
    /// Whether the download may be split: the server advertised byte ranges AND the length is
    /// known. A <c>null</c>/zero length falls back to a single stream, because ranges without a
    /// total are unverifiable.
    /// </summary>
    public static bool IsRangeSupported(bool acceptRangesAdvertised, long? contentLength)
        => acceptRangesAdvertised && contentLength is > 0;

    /// <summary>
    /// How many bytes of this segment are already on disk, given the current <c>.part</c> length.
    /// Clamped to the segment's own length: a stale part file from a different total length must
    /// not push the resume offset past the end (that produced a negative <c>Range</c> before).
    /// </summary>
    public static long ResumedLength(DownloadSegment segment, long partFileLength)
    {
        if (partFileLength <= 0)
        {
            return 0;
        }

        return Math.Min(partFileLength, Math.Max(0, segment.Length));
    }

    /// <summary>True when the part file already holds the whole segment.</summary>
    public static bool IsSegmentComplete(DownloadSegment segment, long partFileLength)
        => ResumedLength(segment, partFileLength) >= segment.Length;

    /// <summary>Bytes still missing across all segments, i.e. what the progress bar must cover.</summary>
    public static long RemainingBytes(IReadOnlyList<DownloadSegment>? segments, IReadOnlyList<long>? partLengths)
    {
        if (segments is null || partLengths is null || segments.Count != partLengths.Count)
        {
            return 0;
        }

        long remaining = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            remaining += segments[i].Length - ResumedLength(segments[i], partLengths[i]);
        }

        return remaining;
    }

    /// <summary>The <c>Range</c> header value for the remaining bytes of a segment.</summary>
    public static string RangeHeader(DownloadSegment segment, long partFileLength)
    {
        // Clamped to the last byte: a part file that already holds more than this range must not
        // produce an inverted "bytes=750-749" header.
        long from = Math.Min(segment.Start + ResumedLength(segment, partFileLength), segment.End);
        return $"bytes={from}-{segment.End}";
    }

    /// <summary>Part file for a destination: <c>foo.exe.part.3</c>.</summary>
    public static string PartPath(string destinationPath, int index) => $"{destinationPath}.part.{index}";

    /// <summary>
    /// Parses a <c>Content-Range: bytes 0-1023/2048</c> header into the total size.
    /// Returns false for <c>*</c> (unknown total) and for anything malformed.
    /// </summary>
    public static bool TryParseContentRangeTotal(string? contentRange, out long total)
    {
        total = 0;
        if (string.IsNullOrWhiteSpace(contentRange))
        {
            return false;
        }

        int slash = contentRange.LastIndexOf('/');
        if (slash < 0 || slash == contentRange.Length - 1)
        {
            return false;
        }

        string tail = contentRange[(slash + 1)..].Trim();
        return long.TryParse(tail, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out total) && total > 0;
    }
}
