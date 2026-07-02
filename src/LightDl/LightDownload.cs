namespace LightDl;

/// <summary>
/// Convenience entry point for one-off downloads.
/// </summary>
public static class LightDownload
{
    /// <summary>
    /// Downloads a request using default configuration.
    /// </summary>
    public static Task<LightDownloadResult> DownloadAsync(
        LightDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        return DownloadAsync(request, config: null, cancellationToken);
    }

    /// <summary>
    /// Downloads a request using the specified configuration.
    /// </summary>
    public static async Task<LightDownloadResult> DownloadAsync(
        LightDownloadRequest request,
        LightDownloadConfig? config,
        CancellationToken cancellationToken = default)
    {
        using var downloader = new LightDownloader(config);
        return await downloader.DownloadAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
