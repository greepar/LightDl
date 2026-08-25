namespace LightDl;

/// <summary>
/// Describes a single retry attempt. Reported through <see cref="LightDownloadConfig.RetryHandler" />.
/// </summary>
public sealed class LightDownloadRetry
{
    /// <summary>First byte of the range being retried.</summary>
    public required long Start { get; init; }

    /// <summary>Last byte of the range being retried, or -1 for a whole-file retry.</summary>
    public required long End { get; init; }

    /// <summary>Retry attempt number, starting at 1.</summary>
    public required int Attempt { get; init; }

    /// <summary>Delay applied before the next attempt.</summary>
    public required TimeSpan Delay { get; init; }

    /// <summary>Error that caused the retry.</summary>
    public required Exception Error { get; init; }
}
