namespace Performix.Installer;

/// <summary>
/// One progress sample handed from a transfer to whatever draws it (the console bar, a log file,
/// a test).
///
/// Lives on its own so the download path never depends on the UI: <see cref="SegmentedDownloader"/>
/// reports these, <see cref="ConsoleUi"/> renders them, and either could be replaced without
/// touching the other.
/// </summary>
/// <param name="Received">Bytes on disk so far.</param>
/// <param name="Total">Expected total, or 0 when the server never said.</param>
/// <param name="BytesPerSecond">Smoothed transfer rate.</param>
public readonly record struct DownloadProgress(long Received, long Total, double BytesPerSecond)
{
    /// <summary>Percent complete, clamped to 0..100; 0 while the total is unknown.</summary>
    public double Percent => Total > 0 ? Math.Clamp(Received * 100.0 / Total, 0, 100) : 0;

    /// <summary>Estimated time left, or <c>null</c> while the rate or the total is unknown.</summary>
    public TimeSpan? Remaining
        => BytesPerSecond > 1 && Total > Received
            ? TimeSpan.FromSeconds((Total - Received) / BytesPerSecond)
            : null;

    public static DownloadProgress Empty { get; } = new(0, 0, 0);
}
