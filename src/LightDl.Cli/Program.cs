using System.Reflection;
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

    if (options.ShowVersion)
    {
        Console.WriteLine($"lightdl {GetVersion()}");
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

    if (options.Verbose)
    {
        config.RetryHandler = retry =>
        {
            // The probe has no range yet, so it reports End as -1 rather than a byte offset.
            var scope = retry.End < 0 ? "probe" : $"{retry.Start}-{retry.End}";
            Console.Error.WriteLine(
                $"\nretry #{retry.Attempt} [{scope}] in {retry.Delay.TotalSeconds:F1}s: {retry.Error.Message}");
        };
    }

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
        using var downloader = new LightDownloader(config);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await downloader.DownloadAsync(request, cancellation.Token);
        stopwatch.Stop();
        if (lastProgressLength > 0)
            Console.WriteLine();

        if (result.Skipped)
        {
            Console.WriteLine($"Skipped: {result.FilePath}");
            return 0;
        }

        var seconds = stopwatch.Elapsed.TotalSeconds;
        var average = seconds > 0 ? result.Size / seconds : 0;
        Console.WriteLine($"Saved: {result.FilePath}");
        Console.WriteLine($"Time:  {FormatDuration(stopwatch.Elapsed)}  (avg {FormatBytes(average)}/s)");
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

        // A LightDownloadException already reads as a full sentence about the download.
        Console.Error.WriteLine(ex is LightDownloadException ? ex.Message : $"Download failed: {ex.Message}");
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
    var verbose = false;
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < args.Length; i++)
    {
        var argument = args[i];
        switch (argument)
        {
            case "-h":
            case "--help":
                return new CliOptions { ShowHelp = true };

            case "-v":
            case "--version":
                return new CliOptions { ShowVersion = true };

            case "--verbose":
                verbose = true;
                break;

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
        Verbose = verbose,
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

static string GetVersion()
{
    var assembly = Assembly.GetExecutingAssembly();
    var informational = assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if (!string.IsNullOrWhiteSpace(informational))
    {
        // Strip the "+<commit sha>" source-link suffix.
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }

    return assembly.GetName().Version?.ToString(3) ?? "unknown";
}

static string FormatDuration(TimeSpan elapsed)
{
    return elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m {elapsed.Seconds}s"
        : elapsed.TotalMinutes >= 1
            ? $"{elapsed.Minutes}m {elapsed.Seconds}s"
            : $"{elapsed.TotalSeconds:F1}s";
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
              --verbose             Report retries and stalls to stderr
          -h, --help                Show help
          -v, --version             Show version

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
    public bool ShowVersion { get; init; }
    public bool Verbose { get; init; }
    public string? Url { get; init; }
    public string? DestinationPath { get; init; }
    public LightDownloadDestinationKind DestinationKind { get; init; }
    public int? ChunkCount { get; init; }
    public LightDownloadFileConflictPolicy? ConflictPolicy { get; init; }
    public bool EnableResume { get; init; } = true;
    public bool IgnoreSslErrors { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
