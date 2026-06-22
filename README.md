# LightDl

LightDl is a lightweight multi-threaded HTTP downloader for .NET with resume support and Native AOT compatibility.

## Installation

NuGet:

```bash
dotnet add package LightDl
```

Or add it to your `.csproj`:

```xml
<PackageReference Include="LightDl" Version="0.1.0" />
```

## Usage

```csharp
using LightDl;

using var dl = new LightDownloader();
dl.ProgressChanged += p => Console.Write($"\r{p.ProgressPercentage:F1}%  {p.Speed / 1024 / 1024:F1} MB/s");

await dl.DownloadAsync("https://example.com/file.zip", Path.Combine(AppContext.BaseDirectory, "file.zip"));
```

With options:

```csharp
var config = new LightDownloadConfig { ChunkCount = 24, Proxy = new WebProxy("http://127.0.0.1:8080") };
using var dl = new LightDownloader(config);
await dl.DownloadAsync(url, path);
```

Resume support is enabled by default. If the download is interrupted, call `DownloadAsync` again with the same URL and path to continue.

## License

MIT
