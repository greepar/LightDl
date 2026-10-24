namespace LightDl;

/// <summary>A per-download protocol measurement or fallback decision.</summary>
public sealed class LightDownloadProtocolSelection
{
    public required Uri Origin { get; init; }
    public required Version SelectedVersion { get; init; }
    /// <summary>Measured bytes/sec; null if the HTTP/2 sample did not complete.</summary>
    public double? Http2Speed { get; init; }
    /// <summary>Measured bytes/sec; null if the HTTP/3 sample did not complete.</summary>
    public double? Http3Speed { get; init; }
    public bool IsFallback { get; init; }
}
