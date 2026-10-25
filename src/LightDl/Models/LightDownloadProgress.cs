namespace LightDl;

/// <summary>
/// Download progress information.
/// </summary>
public class LightDownloadProgress
{
    /// <summary>Downloaded bytes.</summary>
    public long DownloadedBytes { get; init; }

    /// <summary>Total file size in bytes.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Current speed in bytes per second.</summary>
    public double Speed { get; init; }

    /// <summary>Number of download workers currently processing a range or stream. Zero when finished.</summary>
    public int ActiveWorkers { get; init; }

    /// <summary>Current concurrency limit after dynamic adaptation and throttling.</summary>
    public int ConcurrencyLimit { get; init; }

    /// <summary>Progress percentage from 0 to 100.</summary>
    public double ProgressPercentage => TotalBytes > 0 ? DownloadedBytes * 100.0 / TotalBytes : 0;
}
