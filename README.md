# LightDl

LightDl is a lightweight multi-threaded HTTP downloader for .NET with resume support and Native AOT compatibility.

## Installation

NuGet:

```bash
dotnet add package LightDl
```

Or add it to your `.csproj`:

```xml
<PackageReference Include="LightDl" Version="0.2.1" />
```

## Usage

```csharp
using LightDl;

var request = LightDownloadRequest.ToDirectory("https://example.com/file.zip", AppContext.BaseDirectory)
    .OnFileInfo(info => Console.WriteLine($"Downloading {info.FileName} ({info.Size} bytes)"))
    .OnProgress(p => Console.Write($"\r{p.ProgressPercentage:F1}%  {p.Speed / 1024 / 1024:F1} MB/s"));

var result = await LightDownload.DownloadAsync(request);

Console.WriteLine($"\nSaved to: {result.FilePath}");
```

Pass a directory to use the remote file name, or pass a full file path to choose the output name yourself:

```csharp
await LightDownload.DownloadAsync(LightDownloadRequest.ToDirectory(url, @"C:\Downloads\"));
await LightDownload.DownloadAsync(LightDownloadRequest.ToFile(url, @"C:\Downloads\custom-name.zip"));
```

With options:

```csharp
var config = new LightDownloadConfig
{
    ChunkCount = 24,
    Proxy = new WebProxy("http://127.0.0.1:8080"),
    FileConflictPolicy = LightDownloadFileConflictPolicy.Rename
};

await LightDownload.DownloadAsync(LightDownloadRequest.ToDirectory(url, path), config);
```

With request headers:

```csharp
var headers = new Dictionary<string, string>
{
    ["Authorization"] = "Bearer token",
    ["Referer"] = "https://example.com/"
};

await LightDownload.DownloadAsync(LightDownloadRequest.ToDirectory(url, path, headers));
```

Parallel downloads:

```csharp
var results = await Task.WhenAll(urls.Select(url =>
    LightDownload.DownloadAsync(LightDownloadRequest.ToDirectory(url, @"C:\Downloads\"))));
```

For advanced scenarios, create a `LightDownloader` instance yourself. Use one instance per active download; concurrent calls on the same instance are rejected.

Resume support is enabled by default. If the download is interrupted, call `DownloadAsync` again with the same request to continue.

## License

MIT
