# Static MsQuic integration

## Native input provenance

The MsQuic archives and their two address-returning bridge functions were imported byte-for-byte from the user-supplied
`LightDl.0.4.4-h3-static.3.nupkg`.

Input package SHA-256:
`91659183cf593abe1c214ac18cb7b1b7cd2fb23513c2a871ea8180cb8e226d84`.

This workspace did not contain that preview's native build scripts, source commits,
or original `eng/STATIC-QUIC.md`. The archives have not been rebuilt from source in
this change. Before a production release, recover the native build recipe and
check the embedded MsQuic/OpenSSL versions against your update policy. The package
preserves the supplied MsQuic, OpenSSL and .NET notices and includes Mono.Cecil's notice.

The preview's tiny `secur32-extra.a` import libraries have been replaced. They
required `Secur32.dll!QueryContextAttributesExW`, which does not resolve on the
Windows x64/ARM64 runners. `eng/msquic-sspi.c` instead calls the corresponding
entry in the OS-provided `InitSecurityInterfaceW` function table, preserving the
extended API and its buffer-length parameter. Rebuild with
`sh eng/build-windows-sspi.sh /path/to/zig src/LightDl/buildTransitive/native`
using Zig toolset 0.17.0.1. Windows now uses the normal Native AOT MSVC linker, including .NET 11's SDK
`Synchronization.lib`. The only x64 ABI alias needed by the supplied C archive is
`___chkstk_ms=__chkstk`. Zig remains a way to reproduce the small SSPI archive;
it is not a dependency for Windows application builds.

Runtime testing found that the preview's mixed MinGW/MSVC startup crashes before
entering managed code even after all DLL imports resolve. Windows static publishing
therefore requires a Windows host with the Native AOT C++ build tools; Unix-to-Windows
static cross-publishing is explicitly rejected instead of producing a broken EXE.

`eng/PatchQuic` restores the preview patcher's narrow rewrite as reviewable source.
It checks the implementation assembly's runtime version and loader call sites,
replaces only three `TryLoad` and two `GetExport` calls, and uses the archive's
`StaticMsQuicGetOpen` / `StaticMsQuicGetClose` bridges. It leaves protocol support,
MsQuic version, OS/TLS and certificate checks in place. It writes to `obj`, never
to a globally installed runtime. Updating the accepted runtime versions requires
reviewing the new runtime loader and rerunning end-to-end validation.

## Local build

```bash
# This machine has .NET 10 only. The project retains its net11.0 target for CI.
dotnet pack src/LightDl/LightDl.csproj -c Release \
  -p:TargetFrameworks=net10.0 -p:PackageVersion=0.4.5-quic.4 -o artifacts
python3 eng/verify-quic-package.py artifacts/LightDl.0.4.5-quic.4.nupkg
```

Add `artifacts` as a NuGet source in a consuming application and reference the
preview version. Configure deployment properties in the application as shown in
README.md. For a source CLI build, its project imports the same targets and builds
the patcher from source automatically.

## Initial local validation on 2026-10-24

- SDK 10.0.402, runtime 10.0.13, macOS arm64.
- 57 unit tests passed, including strict HTTP/3 requests and rejection of HTTP
  redirects. With the local SDK, explicit `--test-adapter-path` to
  `xunit.runner.visualstudio/3.1.5/build/net8.0` was needed for discovery when
  overriding `TargetFrameworks=net10.0`.
- An external NuGet consumer published a Native AOT `osx-arm64` executable. It
  reported `QuicConnection.IsSupported == true` and downloaded
  `https://www.cloudflare.com/cdn-cgi/trace` using LightDl `ForceHttp3=true`.
  `otool -L` showed no MsQuic/OpenSSL dylib dependency. macOS system libraries and
  frameworks remain dynamic.
- The local command-line-tools macOS 27 SDK and Xcode linker were mismatched.
  Validation selected Xcode's installed macOS 26.5 SDK through process-local
  `SDKROOT` / `DEVELOPER_DIR`; no system configuration was changed.
- A Windows x64 self-contained managed publish omitted `msquic.dll` by default
  and included it with `LightDlIncludeMsQuic=true`.
- Windows x64 Native AOT cross-linking succeeded with explicit
  `StuDev.AotAnywhere` 1.1.0 and `Vezel.Zig.Toolsets.osx-arm64` 0.17.0.1.
  PE import inspection found Windows system DLLs and no `msquic.dll` dependency.
  Windows execution was not performed.
- Runtime 10.0.12 support is inherited from the supplied preview; this run checks
  runtime 10.0.13. The initial package did not yet enable .NET 11.
- Linux and Windows arm64 execution have not been validated in this workspace.
  Cross-link success is not proof of execution or successful HTTP/3 traffic.

## .NET 11 RC2 and GitHub Actions

Static mode also accepts the exact runtime `11.0.0-rc.2.26504.105`. The public
QUIC loader was inspected at tag `v11.0.0-rc.2.26504.105` and the actual runtime
assembly passed the patcher's version and IL checks. Other .NET 11 builds remain
rejected until reviewed. Runtime Async in LightDl's net11.0 assembly is retained.

Linux's .NET 11 crypto interop uses new native entry points. The .NET 10 shim
inside the original combined archive cannot provide those. The package therefore
links a separate, matching .NET 11 static crypto archive **before** the combined
MsQuic/OpenSSL archive. The linker resolves the new shim first and does not extract
the old embedded shim. These additional archives are built from public
`dotnet/runtime` commit `474ed963699fc29809015392738a15231aaae2de` (the RC2
release tag), using the official standalone `LOCAL_BUILD` CMake configuration with
`FEATURE_DISTRO_AGNOSTIC_SSL=OFF` and `HAVE_OPENSSL_ENGINE=OFF` to match the
combined archive's no-engine OpenSSL build. This binds OpenSSL directly instead of loading a
shared OpenSSL library. `eng/build-net11-crypto.sh` contains the build command;
`.github/workflows/quic.yml` contains the glibc and Alpine build environments.

The workflow builds all four Linux shims from source, packs both target frameworks,
runs unit tests for both, and validates the resulting NuGet package with an external
consumer. Its 16 static AOT jobs cover both frameworks on all eight RIDs. Each job:

- Starts an HTTP/3-only Kestrel origin inside the published Native AOT executable.
- Confirms that default certificate validation rejects its untrusted certificate.
- Explicitly permits that test certificate, downloads a 1 MB payload with multiple
  HTTP/3 range requests, and validates SHA-256.
- Rejects MsQuic sidecars and inspects the executable's native dependencies.
- For musl, also requires no ELF interpreter or dynamic dependencies.

The patcher can run on a host with only the .NET 11 SDK/runtime, including the
RC2 Alpine image. Roll-forward applies only to this build tool; the target runtime
assembly still has to match the explicit allowlist.

CI runs and downloadable packages/executables are available under
[MsQuic integration](https://github.com/greepar/LightDl/actions/workflows/quic.yml).

## Archive SHA-256 values

- `linux-arm64/libmsquic.a`: `46cc7d1c33befaa7fead0503d7abbf74aa48657242b7f7e7e7526debdef2b279`
- `linux-musl-arm64/libmsquic.a`: `0ad7acabf4ce3aa2585c66be97bf6b955e22f99271d12d7ed107770365c14838`
- `linux-musl-x64/libmsquic.a`: `5c56e1207eceefa990a2048789382cf971c6a0fb46b2c2ef0e7a8ee3f6777fd4`
- `linux-x64/libmsquic.a`: `e5258d3efaf9ce100985778e822b8379b5db72094d3ff10cad8292002569afa5`
- `osx-arm64/libmsquic.a`: `6e95cc2b93bbb4b87031e6e58ed2ce1030eab29a6016408c41b877564e67926e`
- `osx-x64/libmsquic.a`: `949acee9753ac8ad034b2cc17f210c401267930b6c338f14bf3fb2964411ba58`
- `win-arm64/libmsquic.a`: `a11c853a9c7df534fbb68a2b3a1f660ccfba48c0986a19ea39c2b51a01f02469`
- `win-arm64/secur32-extra.a`: `0ed53720c4b3e3a5998b1294454fb9cc35102e4ecca6cfa6b3b4636448dcc2f7`
- `win-x64/libmsquic.a`: `0134f8b56886c15a03ced1dd72792172401742ff852498601d8cdec88b9d454c`
- `win-x64/secur32-extra.a`: `a88fe400e4c0a3e56709839833e8ad90aa7c51230ba8325a506dcdc204fee760`

Additional .NET 11 Linux crypto shims:

- `net11/linux-arm64/libSystem.Security.Cryptography.Native.OpenSsl.a`: `31044e2f719599d4b620b13616eec24152ec0857e6b623a951525d8e84771d57`
- `net11/linux-musl-arm64/libSystem.Security.Cryptography.Native.OpenSsl.a`: `41b06ee30d96b3282242a46977bf8c825fa1fd7b725d4a8a88b896619a990a9b`
- `net11/linux-musl-x64/libSystem.Security.Cryptography.Native.OpenSsl.a`: `632cebc99c530028f22c3099c37170c8aec47182fd652cb4ab25a28e853cb1cd`
- `net11/linux-x64/libSystem.Security.Cryptography.Native.OpenSsl.a`: `24fd7336d2accd9bb1a0be1b1237c6688151bc49dd70939d2ccc75dab200c965`
