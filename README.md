# LightDl

LightDl is a lightweight multi-threaded HTTP downloader for .NET with resume support and Native AOT compatibility.

## Installation

NuGet:

```bash
dotnet add package LightDl
```

Or add it to your `.csproj`:

```xml
<PackageReference Include="LightDl" Version="0.3.0" />
```

## Usage

```csharp
using LightDl;

var url = new Uri("https://example.com/file.zip");
var request = LightDownloadRequest.ToDirectory(url, AppContext.BaseDirectory)
    .OnFileInfo(info => Console.WriteLine($"Downloading {info.FileName} ({info.Size} bytes)"))
    .OnProgress(p => Console.Write($"\r{p.ProgressPercentage:F1}%  {p.Speed / 1024 / 1024:F1} MB/s"));

using var downloader = new LightDownloader();
var result = await downloader.DownloadAsync(request);

Console.WriteLine($"\nSaved to: {result.FilePath}");
```

Pass a directory to use the remote file name, or pass a full file path to choose the output name yourself:

```csharp
using var downloader = new LightDownloader();
await downloader.DownloadAsync(LightDownloadRequest.ToDirectory(url, @"C:\Downloads\"));
await downloader.DownloadAsync(LightDownloadRequest.ToFile(url, @"C:\Downloads\custom-name.zip"));
```

String URL overloads are also available for simple scripts:

```csharp
using var downloader = new LightDownloader();
await downloader.DownloadAsync(LightDownloadRequest.ToDirectory("https://example.com/file.zip", @"C:\Downloads\"));
```

With options:

```csharp
var config = new LightDownloadConfig
{
    ChunkCount = 24,
    Proxy = new WebProxy("http://127.0.0.1:8080"),
    UseProxy = true
};

using var downloader = new LightDownloader(config);
await downloader.DownloadAsync(LightDownloadRequest.ToDirectory(url, path));
```

If the destination file already exists, LightDl renames the new download by default, for example `file (1).zip`. Set `FileConflictPolicy` to `Overwrite`, `Fail`, or `Skip` if you want different behavior.

With request headers:

```csharp
var headers = new Dictionary<string, string>
{
    ["Authorization"] = "Bearer token",
    ["Referer"] = "https://example.com/"
};

using var downloader = new LightDownloader();
await downloader.DownloadAsync(LightDownloadRequest.ToDirectory(url, path, headers));
```

Parallel downloads:

```csharp
static async Task<LightDownloadResult> DownloadAsync(Uri url)
{
    using var downloader = new LightDownloader();
    return await downloader.DownloadAsync(
        LightDownloadRequest.ToDirectory(url, @"C:\Downloads\"));
}

var results = await Task.WhenAll(urls.Select(DownloadAsync));
```

Use one `LightDownloader` instance per active download; concurrent calls on the same instance are rejected.

Resume support is enabled by default. If the download is interrupted, call `DownloadAsync` again with the same request to continue.

## CLI

Download the Native AOT executable for your platform from [GitHub Releases](https://github.com/greepar/LightDl/releases). No .NET runtime installation is required.

Available release targets:

- Windows: `win-x64`, `win-arm64`, `win-x86`
- Linux glibc: `linux-x64`, `linux-arm64`
- Linux musl/Alpine: `linux-musl-x64`, `linux-musl-arm64`
- macOS: `osx-x64`, `osx-arm64`

Download into a directory:

```bash
lightdl https://example.com/file.zip ./downloads
```

Download to an exact file path:

```bash
lightdl https://example.com/file.zip ./downloads/custom.zip --file
```

Configure workers, conflict handling, or request headers:

```bash
lightdl https://example.com/file.zip ./downloads \
  --chunks 8 \
  --conflict skip \
  --header "Authorization: Bearer token"
```

On Windows, use `lightdl.exe`. Run `lightdl --help` to see all options. The default conflict policy is `rename`, so an existing file is not overwritten.

## Releases

Pushing a version tag such as `v0.3.0` runs `.github/workflows/release.yml`. The workflow builds all Native AOT CLI archives, creates `LightDl.0.3.0.nupkg`, generates SHA-256 checksums, and attaches everything to the GitHub Release.

## License

MIT
