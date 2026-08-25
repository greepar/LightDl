namespace LightDl;

/// <summary>
/// Remote file information.
/// </summary>
public sealed class LightDownloadFileInfo
{
    public required string FileName { get; init; }

    /// <summary>Full path the file will be written to.</summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// File size in bytes, or <see cref="LightDownloader.UnknownSize" /> (-1) when the server does
    /// not declare one. Such downloads run as a single stream and cannot be resumed.
    /// </summary>
    public required long Size { get; init; }

    /// <summary>True when <see cref="Size" /> is a real length rather than <see cref="LightDownloader.UnknownSize" />.</summary>
    public bool HasKnownSize => Size >= 0;

    public string? ContentType { get; init; }

    public bool SupportsRange { get; init; }
}
