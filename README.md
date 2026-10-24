# LightDl

LightDl is a lightweight multi-threaded HTTP downloader for .NET with resume support and Native AOT compatibility.

## Installation

NuGet:

```bash
dotnet add package LightDl
```

Or add it to your `.csproj`:

```xml
<PackageReference Include="LightDl" Version="0.4.5" />
```

The package includes `net10.0` and `net11.0` assemblies. The `net11.0` assembly enables Runtime Async (Async2) and is built with the .NET 11 RC2 SDK. Applications targeting `net11.0` select it automatically when rebuilt; installing a newer runtime alone does not replace the assembly already bundled with an application.

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

## MsQuic deployment and HTTP/3

LightDl defaults to **not copying MsQuic shared libraries into build/publish output**. Existing system/runtime installations may still provide QUIC. HTTP/1.1 and HTTP/2 do not require MsQuic. Choose one deployment mode in the **application** project:

| Mode | MSBuild properties | Result |
| --- | --- | --- |
| Default | None | No bundled `msquic.dll` / `libmsquic.*` |
| Shared library | `LightDlIncludeMsQuic=true` | Retains the runtime pack's MsQuic library, or copies the file supplied below |
| Static Native AOT | `PublishAot=true`, `LightDlStaticQuic=true` | Links the selected RID's archive; no separate MsQuic shared library |

For Windows self-contained deployments, the .NET runtime pack provides `msquic.dll`:

```xml
<PropertyGroup>
  <LightDlIncludeMsQuic>true</LightDlIncludeMsQuic>
</PropertyGroup>
```

For framework-dependent applications or Unix platforms, also provide a compatible target-platform shared library:

```xml
<PropertyGroup>
  <LightDlIncludeMsQuic>true</LightDlIncludeMsQuic>
  <LightDlMsQuicLibrary>$(MSBuildProjectDirectory)/native/libmsquic.so.2</LightDlMsQuicLibrary>
</PropertyGroup>
```

Use `msquic.dll` on Windows or `libmsquic.dylib` on macOS. The shared library's own dependencies must also be available. Publishing with `LightDlIncludeMsQuic=true` fails if no library is available. Build/publish into a clean output directory when changing modes; previously copied files are not deleted from arbitrary output directories.

For static Native AOT on the build host's platform:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <LightDlStaticQuic>true</LightDlStaticQuic>
</PropertyGroup>
```

```bash
dotnet publish -c Release -r osx-arm64
```

Static integration currently accepts **net10.0 with runtime 10.0.12 / 10.0.13**, and **net11.0 with runtime 11.0.0-rc.2.26504.105**. It validates and patches a project-local copy of `System.Net.Quic` before Native AOT compilation, preserving runtime checks and certificate validation. Unknown runtime builds are rejected; no runtime version is silently pinned or downgraded. Ordinary managed builds retain the original QUIC assembly.

Archives are provided for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, and `osx-arm64`. `win-x86` is unsupported. macOS archives require macOS 14 or later. The NuGet package carries static archives, but only the selected archive enters a static AOT executable; normal publish output does not contain them.

Windows static mode uses the normal Native AOT **MSVC toolchain on a Windows build host**. No Zig/AotAnywhere package is required. Cross-publishing Windows static binaries from Unix is rejected: the preview's mixed MinGW/MSVC startup was found to crash before managed code. Use the Windows GitHub Actions jobs to build Windows executables.

The package includes a matching .NET 11 static crypto shim for each Linux RID, built from the pinned .NET 11 RC2 source. Static MsQuic does not mean a fully static operating-system executable: macOS frameworks, Windows system DLLs, and glibc on Linux remain external. A fully static musl executable additionally requires `StaticExecutable=true`, compatible native build tools, and usually `InvariantGlobalization=true`.

To actually require HTTP/3, configure the downloader:

```csharp
using var downloader = new LightDownloader(new LightDownloadConfig { ForceHttp3 = true });
```

The CLI supports `-h3` / `--http3-only`. This requires HTTPS on every request and redirect, and rejects fallback to HTTP/1.1 or HTTP/2. With `ForceHttp3=false` (the default), `EnableHttp3=true` enables automatic selection:

- HTTPS metadata requests negotiate H2, with H1.1 fallback. An H3-only endpoint can be discovered if the TCP probe fails.
- When MsQuic is available and a missing contiguous range is at least 256 KB, the downloader saves two equal-sized real ranges over H2 and H3. Their data counts toward the file and resume metadata; it is not downloaded again for benchmarking.
- The faster completed sample wins for the remaining ranges. A failed or timed-out candidate does not consume the download's retry budget. An unavailable H3 candidate leaves H2/H1.1 available.
- Sampling uses up to `Http3ProbeSize` bytes per protocol (256 KB by default, clamped to 64 KB–4 MB). Each sample has a `Http3ProbeTimeout` budget (3 seconds by default, at most 30 seconds). Measurements include connection setup, transfer and file writes, so they describe this download rather than a guarantee about future throughput.
- Two samples run concurrently within the configured worker count, or sequentially with `ChunkCount=1`. HTTP proxies skip H3 probing. Small files, fragmented resumes without a sufficiently large missing range, and non-range transfers do not perform the speed comparison.
- The choice is per download and origin. A selected H3 transport that fails mid-transfer switches to H2 and requeues only bytes that have not been written. Changing origins clears the H3 preference. Authentication, entity-change and local storage failures keep their normal handling.

Inspect the choice and measured bytes/sec:

```csharp
var config = new LightDownloadConfig
{
    ProtocolSelectionHandler = selection => Console.WriteLine(
        $"Selected HTTP/{selection.SelectedVersion}: H2={selection.Http2Speed}, H3={selection.Http3Speed}")
};
using var downloader = new LightDownloader(config);
// downloader.SelectedHttpVersion also exposes the selected preference.
```

Set `EnableHttp3=false` (CLI: `--no-http3`) to disable automatic H3 probing. H2 can still negotiate H1.1. `ForceHttp3=true` overrides this opt-out and never falls back. The CLI's `--verbose` output reports protocol measurements and fallback decisions.

Native archive provenance, validation results, and local package instructions are in [eng/STATIC-QUIC.md](eng/STATIC-QUIC.md).

## Releases

Pushing a version tag such as `v0.4.3` runs `.github/workflows/release.yml`. The workflow builds all Native AOT CLI archives, creates `LightDl.0.4.3.nupkg`, generates SHA-256 checksums, and attaches everything to the GitHub Release.

## License

MIT
