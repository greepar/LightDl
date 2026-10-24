# Static MsQuic integration

## Native input provenance

The archives and bridge functions were imported byte-for-byte from the user-supplied
`LightDl.0.4.4-h3-static.3.nupkg`.

Input package SHA-256:
`91659183cf593abe1c214ac18cb7b1b7cd2fb23513c2a871ea8180cb8e226d84`.

This workspace did not contain that preview's native build scripts, source commits,
or original `eng/STATIC-QUIC.md`. The archives have not been rebuilt from source in
this change. Before a production release, recover the native build recipe and
check the embedded MsQuic/OpenSSL versions against your update policy. The package
preserves the supplied MsQuic, OpenSSL and .NET notices and includes Mono.Cecil's notice.

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

## Validation on 2026-10-24

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
  runtime 10.0.13. .NET 11 and unknown runtime versions are rejected in static mode.
- Linux and Windows arm64 execution have not been validated in this workspace.
  Cross-link success is not proof of execution or successful HTTP/3 traffic.

## Archive SHA-256 values

- `linux-arm64/libmsquic.a`: `46cc7d1c33befaa7fead0503d7abbf74aa48657242b7f7e7e7526debdef2b279`
- `linux-musl-arm64/libmsquic.a`: `0ad7acabf4ce3aa2585c66be97bf6b955e22f99271d12d7ed107770365c14838`
- `linux-musl-x64/libmsquic.a`: `5c56e1207eceefa990a2048789382cf971c6a0fb46b2c2ef0e7a8ee3f6777fd4`
- `linux-x64/libmsquic.a`: `e5258d3efaf9ce100985778e822b8379b5db72094d3ff10cad8292002569afa5`
- `osx-arm64/libmsquic.a`: `6e95cc2b93bbb4b87031e6e58ed2ce1030eab29a6016408c41b877564e67926e`
- `osx-x64/libmsquic.a`: `949acee9753ac8ad034b2cc17f210c401267930b6c338f14bf3fb2964411ba58`
- `win-arm64/libmsquic.a`: `a11c853a9c7df534fbb68a2b3a1f660ccfba48c0986a19ea39c2b51a01f02469`
- `win-arm64/secur32-extra.a`: `8b3bed3593b141c8de667210efdf87a257f18568f9f2a20d81a6caa2edbfb74e`
- `win-x64/libmsquic.a`: `0134f8b56886c15a03ced1dd72792172401742ff852498601d8cdec88b9d454c`
- `win-x64/secur32-extra.a`: `ab10e4e025ab93d8861d51875da802270860b37b25acb9c811b15bbe9e3ac27d`
