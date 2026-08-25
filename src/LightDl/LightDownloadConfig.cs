namespace LightDl;

/// <summary>
/// Downloader configuration.
/// </summary>
public class LightDownloadConfig
{
    /// <summary>Number of download workers. Default is 24.</summary>
    public int ChunkCount { get; set; } = 24;

    /// <summary>
    /// Lower bound the worker count may drop to while <see cref="EnableDynamicConcurrency" /> is on.
    /// Ignored when dynamic concurrency is off, and never raises <see cref="ChunkCount" />. Default is 4.
    /// </summary>
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

    /// <summary>
    /// Timeout for obtaining a response. It does not bound the time spent streaming the body -
    /// stalled transfers are handled by <see cref="NoDataTimeout" />. Default is 30 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for establishing a TCP connection. Default is 15 seconds.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Explicit HTTP proxy. Null uses the system proxy when <see cref="UseProxy" /> is enabled.</summary>
    public System.Net.IWebProxy? Proxy { get; set; }

    /// <summary>Enables proxy resolution. When enabled with a null proxy, the system proxy is used.</summary>
    public bool UseProxy { get; set; }

    /// <summary>
    /// Optional platform-specific HTTP message handler factory. The handler must have automatic
    /// redirects and automatic decompression disabled; both break byte-range accounting.
    /// </summary>
    public Func<HttpMessageHandler>? HttpMessageHandlerFactory { get; set; }

    /// <summary>User-Agent header. Defaults to a Chrome-like UA.</summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36 Edg/149.0.0.0";

    /// <summary>Network read buffer size per worker. Default is 128 KB.</summary>
    public int BufferSize { get; set; } = 128 * 1024;

    /// <summary>Maximum retry count per segment. Only transient failures are retried.</summary>
    public int MaxRetry { get; set; } = 20;

    /// <summary>Base delay for exponential retry backoff. Default is 500 ms.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound for retry backoff, including a server-supplied Retry-After. Default is 10 seconds.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Optional callback invoked before each retry. Exceptions thrown by it are ignored.</summary>
    public Action<LightDownloadRetry>? RetryHandler { get; set; }

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

    /// <summary>Minimum interval between resume-metadata writes. Default is 1 second.</summary>
    public TimeSpan MetadataFlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Forces written data to physical media (fsync) before a range is recorded as complete.
    /// Protects resume state against power loss at a noticeable throughput cost. Disabled by default;
    /// resume state is always consistent across a process crash regardless of this setting.
    /// </summary>
    public bool DurableFlush { get; set; }

    /// <summary>Fails early when the target volume does not have room for the download. Enabled by default.</summary>
    public bool CheckFreeSpace { get; set; } = true;

    /// <summary>Hash algorithm used to verify the completed file. Default is none.</summary>
    public LightDownloadChecksumAlgorithm ChecksumAlgorithm { get; set; } = LightDownloadChecksumAlgorithm.None;

    /// <summary>Expected checksum as a hex string. Verified when <see cref="ChecksumAlgorithm" /> is set.</summary>
    public string? ExpectedChecksum { get; set; }

    /// <summary>How to handle an existing destination file. Default is rename.</summary>
    public LightDownloadFileConflictPolicy FileConflictPolicy { get; set; } = LightDownloadFileConflictPolicy.Rename;

    /// <summary>Temporary data file extension used while downloading.</summary>
    public string TempFileExtension { get; set; } = ".lightdl";

    /// <summary>Metadata file extension used for resume support.</summary>
    public string MetadataFileExtension { get; set; } = ".lightdl.meta";

    /// <summary>
    /// Keeps the resume metadata file out of the way: a leading dot on Unix, the Hidden attribute
    /// on Windows. Metadata written by an earlier version under the visible name is picked up and
    /// renamed, so an in-flight download is not restarted. Enabled by default.
    /// </summary>
    public bool HideMetadataFile { get; set; } = true;

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
            ConnectTimeout = ConnectTimeout,
            Proxy = Proxy,
            UseProxy = UseProxy,
            HttpMessageHandlerFactory = HttpMessageHandlerFactory,
            UserAgent = UserAgent,
            BufferSize = BufferSize,
            MaxRetry = MaxRetry,
            RetryBaseDelay = RetryBaseDelay,
            MaxRetryDelay = MaxRetryDelay,
            RetryHandler = RetryHandler,
            SlowSegmentMinDuration = SlowSegmentMinDuration,
            NoDataTimeout = NoDataTimeout,
            SlowSpeedRatio = SlowSpeedRatio,
            MinRemainingBytesForRequeue = MinRemainingBytesForRequeue,
            ProgressIntervalMs = ProgressIntervalMs,
            SpeedLimitProvider = SpeedLimitProvider,
            EnableResume = EnableResume,
            MetadataFlushInterval = MetadataFlushInterval,
            DurableFlush = DurableFlush,
            CheckFreeSpace = CheckFreeSpace,
            ChecksumAlgorithm = ChecksumAlgorithm,
            ExpectedChecksum = ExpectedChecksum,
            FileConflictPolicy = FileConflictPolicy,
            TempFileExtension = TempFileExtension,
            MetadataFileExtension = MetadataFileExtension,
            HideMetadataFile = HideMetadataFile,
            IgnoreSslErrors = IgnoreSslErrors
        };
    }
}
