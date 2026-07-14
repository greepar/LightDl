using LightDl;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    CliOptions options;
    try
    {
        options = ParseArguments(args);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Console.Error.WriteLine("Run 'lightdl --help' for usage.");
        return 2;
    }

    if (options.ShowHelp)
    {
        PrintHelp();
        return 0;
    }

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
        Console.Error.WriteLine("\nCancelling download...");
    };

    var config = new LightDownloadConfig
    {
        EnableResume = options.EnableResume,
        IgnoreSslErrors = options.IgnoreSslErrors
    };

    if (options.ChunkCount is { } chunkCount)
        config.ChunkCount = chunkCount;

    if (options.ConflictPolicy is { } conflictPolicy)
        config.FileConflictPolicy = conflictPolicy;

    var destination = Path.GetFullPath(options.DestinationPath!);
    var request = options.DestinationKind == LightDownloadDestinationKind.File
        ? LightDownloadRequest.ToFile(options.Url!, destination, options.Headers)
        : LightDownloadRequest.ToDirectory(options.Url!, destination, options.Headers);

    var lastProgressLength = 0;
    request.OnFileInfo(info =>
    {
        Console.WriteLine($"File:  {info.FileName}");
        Console.WriteLine($"Size:  {FormatBytes(info.Size)}");
        Console.WriteLine($"Range: {(info.SupportsRange ? "supported" : "not supported")}");
    });

    if (!Console.IsOutputRedirected)
    {
        request.OnProgress(progress =>
        {
            var line = $"{progress.ProgressPercentage,6:F1}%  " +
                       $"{FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes)}  " +
                       $"{FormatBytes(progress.Speed)}/s";
            var padding = Math.Max(0, lastProgressLength - line.Length);
            Console.Write($"\r{line}{new string(' ', padding)}");
            lastProgressLength = line.Length;
        });
    }

    try
    {
        var result = await LightDownload.DownloadAsync(request, config, cancellation.Token);
        if (lastProgressLength > 0)
            Console.WriteLine();

        Console.WriteLine(result.Skipped ? $"Skipped: {result.FilePath}" : $"Saved: {result.FilePath}");
        return 0;
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        return 130;
    }
    catch (Exception ex)
    {
        if (lastProgressLength > 0)
            Console.Error.WriteLine();

        Console.Error.WriteLine($"Download failed: {ex.Message}");
        return 1;
    }
}

static CliOptions ParseArguments(string[] args)
{
    if (args.Length == 0)
        return new CliOptions { ShowHelp = true };

    string? url = null;
    string? destination = null;
    var destinationKind = LightDownloadDestinationKind.Directory;
    int? chunkCount = null;
    LightDownloadFileConflictPolicy? conflictPolicy = null;
    var enableResume = true;
    var ignoreSslErrors = false;
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < args.Length; i++)
    {
        var argument = args[i];
        switch (argument)
        {
            case "-h":
            case "--help":
                return new CliOptions { ShowHelp = true };

            case "-o":
            case "--output":
                destination = ReadValue(args, ref i, argument);
                break;

            case "--file":
                destinationKind = LightDownloadDestinationKind.File;
                break;

            case "--directory":
                destinationKind = LightDownloadDestinationKind.Directory;
                break;

            case "-c":
            case "--chunks":
                var chunkValue = ReadValue(args, ref i, argument);
                if (!int.TryParse(chunkValue, out var parsedChunkCount) || parsedChunkCount < 1)
                    throw new ArgumentException("Chunk count must be a positive integer.");

                chunkCount = parsedChunkCount;
                break;

            case "--conflict":
                conflictPolicy = ParseConflictPolicy(ReadValue(args, ref i, argument));
                break;

            case "--no-resume":
                enableResume = false;
                break;

            case "--ignore-ssl-errors":
                ignoreSslErrors = true;
                break;

            case "-H":
            case "--header":
                var header = ReadValue(args, ref i, argument);
                var separator = header.IndexOf(':');
                if (separator <= 0)
                    throw new ArgumentException($"Invalid header '{header}'. Expected 'Name: Value'.");

                headers[header[..separator].Trim()] = header[(separator + 1)..].Trim();
                break;

            default:
                if (argument.StartsWith('-'))
                    throw new ArgumentException($"Unknown option '{argument}'.");

                if (url is null)
                    url = argument;
                else if (destination is null)
                    destination = argument;
                else
                    throw new ArgumentException($"Unexpected argument '{argument}'.");
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
        throw new ArgumentException("A valid absolute HTTP or HTTPS URL is required.");
    }

    if (destinationKind == LightDownloadDestinationKind.File && string.IsNullOrWhiteSpace(destination))
        throw new ArgumentException("--file requires an output path, either as the second argument or with --output.");

    destination ??= Directory.GetCurrentDirectory();

    return new CliOptions
    {
        Url = uri.AbsoluteUri,
        DestinationPath = destination,
        DestinationKind = destinationKind,
        ChunkCount = chunkCount,
        ConflictPolicy = conflictPolicy,
        EnableResume = enableResume,
        IgnoreSslErrors = ignoreSslErrors,
        Headers = headers.Count == 0 ? null : headers
    };
}

static string ReadValue(string[] args, ref int index, string option)
{
    if (++index >= args.Length)
        throw new ArgumentException($"Option '{option}' requires a value.");

    return args[index];
}

static LightDownloadFileConflictPolicy ParseConflictPolicy(string value)
{
    return value.ToLowerInvariant() switch
    {
        "rename" => LightDownloadFileConflictPolicy.Rename,
        "overwrite" => LightDownloadFileConflictPolicy.Overwrite,
        "fail" => LightDownloadFileConflictPolicy.Fail,
        "skip" => LightDownloadFileConflictPolicy.Skip,
        _ => throw new ArgumentException("Conflict policy must be rename, overwrite, fail, or skip.")
    };
}

static string FormatBytes(double bytes)
{
    string[] units = ["B", "KB", "MB", "GB", "TB"];
    var unit = 0;
    while (bytes >= 1024 && unit < units.Length - 1)
    {
        bytes /= 1024;
        unit++;
    }

    return $"{bytes:F1} {units[unit]}";
}

static void PrintHelp()
{
    Console.WriteLine("""
        LightDl.Cli - command-line example for LightDl

        Usage:
          lightdl <url> [output-directory] [options]
          lightdl <url> <output-file> --file [options]

        Options:
          -o, --output <path>       Output directory or file path
              --file                Treat the output path as an exact file path
              --directory           Treat the output path as a directory (default)
          -c, --chunks <count>      Number of download workers
              --conflict <policy>   rename, overwrite, fail, or skip (default: rename)
              --no-resume           Disable resume support
              --ignore-ssl-errors   Ignore TLS certificate validation errors
          -H, --header <header>     Add a request header, for example "Authorization: Bearer token"
          -h, --help                Show help

        Examples:
          lightdl https://example.com/file.zip
          lightdl https://example.com/file.zip ./downloads
          lightdl https://example.com/file.zip ./downloads/custom.zip --file
          lightdl https://example.com/file.zip --chunks 8 --conflict skip
        """);
}

file sealed class CliOptions
{
    public bool ShowHelp { get; init; }
    public string? Url { get; init; }
    public string? DestinationPath { get; init; }
    public LightDownloadDestinationKind DestinationKind { get; init; }
    public int? ChunkCount { get; init; }
    public LightDownloadFileConflictPolicy? ConflictPolicy { get; init; }
    public bool EnableResume { get; init; } = true;
    public bool IgnoreSslErrors { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
