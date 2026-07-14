namespace LightDl;

/// <summary>
/// Downloader configuration.
/// </summary>
public class LightDownloadConfig
{
    /// <summary>Number of download workers. Default is 24.</summary>
    public int ChunkCount { get; set; } = 24;

    /// <summary>Minimum worker count when dynamic concurrency is enabled. Default is 4.</summary>
    public int MinChunkCount { get; set; } = 4;

    /// <summary>Maximum worker count when dynamic concurrency is enabled. Default is 32.</summary>
    public int MaxChunkCount { get; set; } = 32;

    /// <summary>Enables dynamic concurrency. Disabled by default.</summary>
    public bool EnableDynamicConcurrency { get; set; }

    /// <summary>Download segment size. Default is 16 MB.</summary>
    public long SegmentSize { get; set; } = 16L * 1024 * 1024;

    /// <summary>Minimum segment size when dynamic segment sizing is enabled. Default is 1 MB.</summary>
    public long MinSegmentSize { get; set; } = 1L * 1024 * 1024;

    /// <summary>Maximum segment size when dynamic segment sizing is enabled. Default is 16 MB.</summary>
    public long MaxSegmentSize { get; set; } = 16L * 1024 * 1024;

    /// <summary>Enables dynamic segment sizing. Disabled by default.</summary>
    public bool EnableDynamicSegmentSize { get; set; }

    /// <summary>Interval for dynamic concurrency and segment-size adaptation. Default is 5 seconds.</summary>
    public TimeSpan AdaptInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>HTTP request timeout. Default is 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>HTTP proxy. Null means direct connection.</summary>
    public System.Net.IWebProxy? Proxy { get; set; }

    /// <summary>User-Agent header. Defaults to a Chrome-like UA.</summary>
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36 Edg/149.0.0.0";

    /// <summary>Network read buffer size per worker. Default is 128 KB.</summary>
    public int BufferSize { get; set; } = 128 * 1024;

    /// <summary>Maximum retry count per segment.</summary>
    public int MaxRetry { get; set; } = 20;

    /// <summary>Minimum segment runtime before slow-connection detection starts. Default is 15 seconds.</summary>
    public TimeSpan SlowSegmentMinDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Abort and requeue a segment if no data is received for this duration. Default is 15 seconds.</summary>
    public TimeSpan NoDataTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Marks a segment as slow when its average speed is below this ratio of the global average speed.</summary>
    public double SlowSpeedRatio { get; set; } = 0.05;

    /// <summary>Minimum remaining bytes required to requeue a slow segment. Default is 256 KB.</summary>
    public long MinRemainingBytesForRequeue { get; set; } = 256 * 1024;

    /// <summary>Progress report interval in milliseconds. Default is 500 ms.</summary>
    public int ProgressIntervalMs { get; set; } = 500;

    /// <summary>Optional dynamic speed limit provider in bytes per second. Null or non-positive means unlimited.</summary>
    public Func<double?>? SpeedLimitProvider { get; set; }

    /// <summary>Enables resume support. Enabled by default.</summary>
    public bool EnableResume { get; set; } = true;

    /// <summary>How to handle an existing destination file. Default is rename.</summary>
    public LightDownloadFileConflictPolicy FileConflictPolicy { get; set; } = LightDownloadFileConflictPolicy.Rename;

    /// <summary>Temporary data file extension used for resume support.</summary>
    public string TempFileExtension { get; set; } = ".lightdl";

    /// <summary>Metadata file extension used for resume support.</summary>
    public string MetadataFileExtension { get; set; } = ".lightdl.meta";

    /// <summary>Ignores SSL certificate validation errors.</summary>
    public bool IgnoreSslErrors { get; set; }

    internal LightDownloadConfig Clone()
    {
        return new LightDownloadConfig
        {
            ChunkCount = ChunkCount,
            MinChunkCount = MinChunkCount,
            MaxChunkCount = MaxChunkCount,
            EnableDynamicConcurrency = EnableDynamicConcurrency,
            SegmentSize = SegmentSize,
            MinSegmentSize = MinSegmentSize,
            MaxSegmentSize = MaxSegmentSize,
            EnableDynamicSegmentSize = EnableDynamicSegmentSize,
            AdaptInterval = AdaptInterval,
            Timeout = Timeout,
            Proxy = Proxy,
            UserAgent = UserAgent,
            BufferSize = BufferSize,
            MaxRetry = MaxRetry,
            SlowSegmentMinDuration = SlowSegmentMinDuration,
            NoDataTimeout = NoDataTimeout,
            SlowSpeedRatio = SlowSpeedRatio,
            MinRemainingBytesForRequeue = MinRemainingBytesForRequeue,
            ProgressIntervalMs = ProgressIntervalMs,
            SpeedLimitProvider = SpeedLimitProvider,
            EnableResume = EnableResume,
            FileConflictPolicy = FileConflictPolicy,
            TempFileExtension = TempFileExtension,
            MetadataFileExtension = MetadataFileExtension,
            IgnoreSslErrors = IgnoreSslErrors
        };
    }
}
