# HTTP/3 range throughput

## Local measurements, 2026-10-25

macOS x64, .NET 10.0.13, static MsQuic, direct connections (`UseProxy=false`).
The supplied `https://patch.qwq.lu/kiss` redirects to a 1,522,314,091-byte R2 object.
Observed responses used Cloudflare LAX and returned valid 206 Content-Range headers.
One earlier H3 metadata request returned a non-range response; Range support was
not consistent across all initial attempts.

Each run used 24 workers, no resume, and a fresh process. Runs were sequential.
The time budget includes metadata, redirects, handshakes, worker ramp-up and writes.
Bytes below are the last progress observation, so they exclude up to one reporting
interval before cancellation. These are bounded partial downloads, not full-file
integrity checks. CDN caching and network conditions were not controlled.

| Protocol | Pools | Budget | Observed MiB | Retries |
| --- | ---: | ---: | ---: | ---: |
| H3, diagnostic transport | 1 | 25 s | 2.01 | 2 |
| H2, exact protocol diagnostic transport | 1 | 25 s | 49.81 | 0 |
| H3, diagnostic transport | 4 | 25 s | 15.57 | 7 |
| H3, diagnostic transport | 24 | 25 s | 232.62 | 1 |
| H3, diagnostic transport | 8 | 25 s | 8.18 | 3 |
| H3, diagnostic transport, repeated control | 1 | 25 s | 8.42 | 1 |
| H3, production transport with Http3ConnectionCount=24 | 24 | 40 s | 272.35 | 1 |
| H3, production automatic pools, 24 fixed workers | automatic | 25 s | 70.02 | 0 |

The production run reached roughly 16 MiB/s in several one-second observations,
but still fluctuated. The evidence supports exposing independent H3 connection
pools for this path; it does not establish a universally optimal connection count.
The default now follows worker concurrency automatically (`Http3ConnectionCount=0`),
including dynamic adaptation and throttle backoff, up to 32 pools. Positive values
set a pool cap. Existing streams are not aborted when the limit decreases.
For this resource, 24 pools performed better than the tested smaller counts.
H2 behavior is unchanged. Throughput-based worker adaptation still requires the
existing `EnableDynamicConcurrency=true`; automatic H3 pools do not change that setting.
The automatic-pool run confirms operation without an explicit pool count, but its
lower throughput also illustrates the remaining network/CDN variability. It used
fixed workers and does not measure the throughput adaptation policy itself.

`SocketsHttpHandler` normally multiplexes these requests onto one H3 connection.
Adding worker streams does not add independent QUIC congestion windows.
`EnableMultipleHttp3Connections` allows overflow connections when stream capacity
is exhausted; it does not guarantee one connection per download worker. LightDl
uses separate handler pools for explicitly selected H3 range requests instead.
The [runtime connection-pool implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/ConnectionPool/HttpConnectionPool.Http3.cs)
documents the underlying stream-capacity behavior.

## Reproduce

Build from a machine with .NET 10 and Native AOT tools:

```bash
dotnet publish eng/LightDl.Benchmark/LightDl.Benchmark.csproj \
  -f net10.0 -c Release -r osx-x64 -p:TargetFrameworks=net10.0 \
  -p:LightDlStaticQuic=true -o /tmp/lightdl-bench
```

Change the RID for your platform. Run one benchmark at a time:

```bash
# Diagnostic transport: exact H2/H3, log response protocols and Content-Range.
/tmp/lightdl-bench/LightDl.Benchmark https://patch.qwq.lu/kiss 2 24 25 1
/tmp/lightdl-bench/LightDl.Benchmark https://patch.qwq.lu/kiss 3 24 25 1
/tmp/lightdl-bench/LightDl.Benchmark https://patch.qwq.lu/kiss 3 24 25 24

# Exercise the production downloader's pool selection.
/tmp/lightdl-bench/LightDl.Benchmark https://patch.qwq.lu/kiss 3 24 40 24 native
# Let the production transport choose the pool count from worker concurrency.
/tmp/lightdl-bench/LightDl.Benchmark https://patch.qwq.lu/kiss 3 24 25 0 native
```

Arguments are URL, protocol, workers, time budget in seconds, pool count and
optional `native`. Diagnostic mode uses a custom transport to report actual
response versions and round-robins all requests, including metadata; production
mode retains metadata on its primary pool. Native H2 permits H1 fallback, so use
diagnostic mode for exact H2 measurements. All modes write into a temporary
directory and remove partial data on exit. `time_budget_reached=true` is an
expected result, not a completed download. Protocol 3 never silently falls back.
