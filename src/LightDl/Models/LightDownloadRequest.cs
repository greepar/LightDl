namespace LightDl;

/// <summary>
/// Describes a single HTTP download request.
/// </summary>
public sealed class LightDownloadRequest
{
    public LightDownloadRequest(string url, string destinationPath, LightDownloadDestinationKind destinationKind)
        : this(CreateAbsoluteUri(url), destinationPath, destinationKind)
    {
    }

    public LightDownloadRequest(Uri url, string destinationPath, LightDownloadDestinationKind destinationKind)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (!url.IsAbsoluteUri)
            throw new ArgumentException("The URL must be absolute.", nameof(url));

        RequestUri = url;
        Url = url.AbsoluteUri;
        DestinationPath = destinationPath;
        DestinationKind = destinationKind;
    }

    /// <summary>Remote URL to download.</summary>
    public string Url { get; }

    /// <summary>Remote URI to download.</summary>
    public Uri RequestUri { get; }

    /// <summary>Destination file path or directory path, depending on <see cref="DestinationKind" />.</summary>
    public string DestinationPath { get; }

    /// <summary>How <see cref="DestinationPath" /> should be interpreted.</summary>
    public LightDownloadDestinationKind DestinationKind { get; }

    /// <summary>Optional request headers used for probing and downloading.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    internal Action<LightDownloadProgress>? ProgressHandler { get; private set; }

    internal Action<LightDownloadFileInfo>? FileInfoHandler { get; private set; }

    /// <summary>Adds a progress callback to this request.</summary>
    public LightDownloadRequest OnProgress(Action<LightDownloadProgress> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ProgressHandler += handler;
        return this;
    }

    /// <summary>Adds a remote file information callback to this request.</summary>
    public LightDownloadRequest OnFileInfo(Action<LightDownloadFileInfo> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        FileInfoHandler += handler;
        return this;
    }

    /// <summary>Create a request that writes to an exact file path.</summary>
    public static LightDownloadRequest ToFile(string url, string filePath, IReadOnlyDictionary<string, string>? headers = null)
    {
        return ToFile(CreateAbsoluteUri(url), filePath, headers);
    }

    /// <summary>Create a request that writes to an exact file path.</summary>
    public static LightDownloadRequest ToFile(Uri url, string filePath, IReadOnlyDictionary<string, string>? headers = null)
    {
        return new LightDownloadRequest(url, filePath, LightDownloadDestinationKind.File)
        {
            Headers = headers
        };
    }

    /// <summary>Create a request that writes into a directory using the remote file name.</summary>
    public static LightDownloadRequest ToDirectory(string url, string directoryPath, IReadOnlyDictionary<string, string>? headers = null)
    {
        return ToDirectory(CreateAbsoluteUri(url), directoryPath, headers);
    }

    /// <summary>Create a request that writes into a directory using the remote file name.</summary>
    public static LightDownloadRequest ToDirectory(Uri url, string directoryPath, IReadOnlyDictionary<string, string>? headers = null)
    {
        return new LightDownloadRequest(url, directoryPath, LightDownloadDestinationKind.Directory)
        {
            Headers = headers
        };
    }

    private static Uri CreateAbsoluteUri(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException("The URL must be absolute.", nameof(url));

        return uri;
    }
}
