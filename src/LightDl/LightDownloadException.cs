namespace LightDl;

/// <summary>
/// Thrown when a download cannot be completed. The original cause, when there is one,
/// is available through <see cref="Exception.InnerException" />.
/// </summary>
public sealed class LightDownloadException : Exception
{
    public LightDownloadException(string message)
        : base(message)
    {
    }

    public LightDownloadException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
