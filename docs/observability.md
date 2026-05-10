# Observability

[← Back to README](../README.md) · [日本語版](./observability.ja.md)

Structured logging, OpenTelemetry traces / metrics (OTLP + local
Prometheus listener), and user-defined metadata round-trip.

---

## Structured logging

By default `osvfs` writes single-line, human-readable log entries to the
console. Set `log-format = "json"` in
[`osvfs.toml`](#configuration-file) (or pass `--log-format json` for an
ad-hoc override) to switch to a structured stream that log shippers such
as Datadog or Loki can parse without regex:

```powershell
osvfs --log-format json    # one-off override; the file value applies otherwise
```

Each log entry is written as a single line (terminated with the platform
line separator) carrying one JSON object. Field names follow the keys
produced by `Microsoft.Extensions.Logging.Console`'s JSON formatter:

| Field | Description |
| --- | --- |
| `Timestamp` | UTC timestamp in `yyyy-MM-ddTHH:mm:ss.fffZ` format. |
| `EventId` | The `EventId.Id` of the log entry (`0` when not set). |
| `LogLevel` | `Trace`, `Debug`, `Information`, `Warning`, `Error`, or `Critical`. |
| `Category` | Logger category — typically the source type's full name (`OSVFS`, `OSVFS.ProjFs.ProjFsProvider`, ...). |
| `Message` | Final formatted message after structured-template substitution. |
| `State` | Object containing the original message template and each named placeholder (e.g. `{Bucket}`) as a separate property — preserved as structured data for downstream filtering. |
| `Exception` | Present only when an exception was attached; carries the formatted exception text. |

Sample line (pretty-printed here for readability — on the wire it is one
line):

```json
{
  "Timestamp": "2026-05-09T11:22:33.456Z",
  "EventId": 0,
  "LogLevel": "Information",
  "Category": "OSVFS",
  "Message": "Virtualizing s3://my-bucket at C:\\Users\\you\\OSVFS",
  "State": {
    "Message": "Virtualizing s3://my-bucket at C:\\Users\\you\\OSVFS",
    "Bucket": "my-bucket",
    "Root": "C:\\Users\\you\\OSVFS",
    "{OriginalFormat}": "Virtualizing s3://{Bucket} at {Root}"
  }
}
```

## OpenTelemetry tracing and metrics

OSVFS instruments every S3 backend operation **and** every meaningful
ProjFS callback with two
[`ActivitySource`](https://learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing)s
and matching `Meter`s. Wire up the OTLP exporter and a collector to see
where Windows-side virtualization time is spent, which S3 calls back it,
and how the byte volume / error rate breaks down — all without touching
the binary.

#### S3 backend signals (source `osvfs.s3`)

| Signal | Type | Tags / instruments |
| --- | --- | --- |
| `S3.List`, `S3.ListAll`, `S3.ListRecursive`, `S3.Head`, `S3.Get`, `S3.Put`, `S3.Delete`, `S3.DeletePrefix`, `S3.Rename`, `S3.RenamePrefix`, `S3.Copy` | Activity (`Client`) | `relative.path`, `relative.directory`, `byte.offset`, `byte.length`, etc. |
| `osvfs.s3.bytes_uploaded` | Counter (`By`) | none |
| `osvfs.s3.bytes_downloaded` | Counter (`By`) | none |
| `osvfs.s3.errors_total` | Counter (`{error}`) | `operation` |
| `osvfs.s3.duration` | Histogram (`ms`) | `operation` |

`S3.Copy` runs as a child of `S3.Rename` so a Rename's wire-level
CopyObject latency shows up next to the bookkeeping cost in a flame
graph.

#### ProjFS callback signals (source `osvfs.projfs`)

| Signal | Type | Tags |
| --- | --- | --- |
| `ProjFS.StartDirectoryEnumeration` | Activity (`Internal`) | `relative.path` |
| `ProjFS.GetPlaceholderInfo` | Activity (`Internal`) | `relative.path`, `projfs.result` (`not_found` when 404) |
| `ProjFS.GetFileData` | Activity (`Internal`) | `relative.path`, `byte.offset`, `byte.length` |
| `ProjFS.PreDelete`, `ProjFS.PreRename`, `ProjFS.PreCreateHardlink`, `ProjFS.PreConvertToFull` | Activity (`Internal`) | `relative.path` (+ `destination.path` for rename / hardlink), `projfs.allowed` |
| `ProjFS.FileRenamed`, `ProjFS.FileHandleClosedFileModifiedOrDeleted` | Activity (`Internal`) | `relative.path`, `is.directory`, plus `file.modified` / `file.deleted` for the close-handler |
| `ProjFS.HandleFileModified`, `ProjFS.HandleFileDeleted`, `ProjFS.HandleFileRenamed` | Activity (`Internal`) | provider-side handler running inside the matching notification span |
| `osvfs.projfs.errors_total` | Counter (`{error}`) | `operation` |
| `osvfs.projfs.duration` | Histogram (`ms`) | `operation` |

Spans are nested into a single tree per user action — for example,
saving a modified file produces:

```
ProjFS.FileHandleClosedFileModifiedOrDeleted
  └─ ProjFS.HandleFileModified
       └─ S3.Put
```

so a single Jaeger trace shows the full virtualization → backend chain
with each segment's contribution to total latency.

Very high-frequency, side-effect-free notifications (`FileOpened`,
`NewFileCreated`, `FileOverwritten`, `HardlinkCreated`,
`FileHandleClosedNoModification`) and per-entry
`GetDirectoryEnumeration` / `EndDirectoryEnumeration` are
intentionally **not** instrumented — they fire dozens of times per UI
folder open and have no S3-backed work, so capturing them would inflate
trace volume without adding signal.

#### Enable the OTLP exporter

Pass the collector endpoint with `--otlp-endpoint` for an ad-hoc run, or
add a `[telemetry]` section to `osvfs.toml` for persistent configuration:

```powershell
osvfs --otlp-endpoint http://localhost:4317   # gRPC, default protocol
osvfs --otlp-endpoint http://localhost:4318   # HTTP/Protobuf, see [telemetry] block below
```

```toml
# osvfs.toml — persistent telemetry
[telemetry]
otlp-endpoint = "http://localhost:4317"
otlp-protocol = "grpc"          # "grpc" (default) or "http-protobuf"
service-name  = "osvfs"         # service.name resource attribute (default "osvfs")
```

When `--otlp-endpoint` is supplied, it overrides `[telemetry] otlp-endpoint`
while preserving the file's `otlp-protocol` and `service-name` keys.
Telemetry stays disabled when neither source supplies an endpoint.

#### Local Prometheus `/metrics` endpoint

For environments where running an OTLP collector is impractical (single
host, scratch box, CI worker), the OSVFS host can expose metrics
directly over HTTP in Prometheus text exposition format. Configure
`metrics-listen` (or pass `--metrics-listen host:port` for a one-off
run); the listener is independent of `otlp-endpoint`, so you can run
either or both:

```powershell
osvfs --metrics-listen 127.0.0.1:9999            # /metrics only
osvfs --metrics-listen 127.0.0.1:9999 `
      --otlp-endpoint http://localhost:4317      # both pipelines
```

```toml
# osvfs.toml — pull-style metrics via Prometheus scrape
[telemetry]
metrics-listen = "127.0.0.1:9999"
```

Three endpoints are mounted on the same port:

| Path        | Purpose                                                                                             |
| ----------- | --------------------------------------------------------------------------------------------------- |
| `/metrics`  | Prometheus text v0.0.4 exposition; pulls a fresh metric snapshot per scrape via `MetricReader.Collect`. |
| `/healthz`  | Flat-text liveness probe (always `200 OK\nok`).                                                     |
| `/version`  | Assembly informational version (mirrors the OTel resource version).                                 |

Loopback (`127.0.0.1`, `[::1]`, `localhost`) is recommended; binding to a
wildcard host (`0.0.0.0`, `+`, `[::]`) exposes internal counters on every
interface and emits a startup warning. On Windows, non-loopback prefixes
also require an `netsh http add urlacl` reservation when running as a
non-admin user.

A ready-to-use scrape config is checked in at
[`examples/otel/prometheus.yml`](./examples/otel/prometheus.yml). It
defines two jobs — one for the OTel Collector push path
(`otelcol:9464`) and one for the direct `/metrics` pull path
(`host.docker.internal:9999`) — so the same file works for both. Adjust
the `targets:` entry if you bound the listener to a non-default
host:port.

The metric names match the OTLP path exactly (e.g.
`osvfs_s3_bytes_uploaded_bytes_total`,
`osvfs_s3_duration_milliseconds_bucket`), so the queries listed below for
the OTLP collector path apply unchanged.

#### Run a local collector + Jaeger + Prometheus

A ready-to-run sample stack (OpenTelemetry Collector contrib + Jaeger
v2 + Prometheus) is checked in under
[`examples/otel/`](./examples/otel):

| File | Role |
| --- | --- |
| [`examples/otel/docker-compose.yml`](./examples/otel/docker-compose.yml) | Brings up `jaeger`, `prometheus`, and `otelcol`. Exposes 4317 (OTLP gRPC), 4318 (OTLP HTTP), 16686 (Jaeger UI), 9090 (Prometheus UI). |
| [`examples/otel/otelcol.yaml`](./examples/otel/otelcol.yaml) | Collector pipeline: OTLP receiver → Jaeger (traces) + Prometheus exporter on 9464 (metrics). |
| [`examples/otel/prometheus.yml`](./examples/otel/prometheus.yml) | Prometheus scrape config with two jobs: `otelcol:9464` (push path) and `host.docker.internal:9999` (direct `/metrics` pull path). |

Start the stack and the OSVFS host:

```powershell
cd examples/otel
docker compose up -d
osvfs --otlp-endpoint http://localhost:4317
```

- Jaeger UI → <http://localhost:16686> — pick the `osvfs` service to see
  the per-operation flame graph (both `osvfs.s3` and `osvfs.projfs`
  sources land under the same service.name).
- Prometheus UI → <http://localhost:9090> — useful queries:
  - `histogram_quantile(0.95, sum by (le, operation) (rate(osvfs_s3_duration_milliseconds_bucket[5m])))`
    — p95 S3 backend latency by operation.
  - `histogram_quantile(0.95, sum by (le, operation) (rate(osvfs_projfs_duration_milliseconds_bucket[5m])))`
    — p95 ProjFS callback latency by operation. Subtract the S3 quantile
    of the same operation to find the in-process overhead.
  - `rate(osvfs_s3_errors_total[5m])` and
    `rate(osvfs_projfs_errors_total[5m])` — per-pipeline error rates.
  - `rate(osvfs_s3_bytes_uploaded_bytes_total[5m])` /
    `rate(osvfs_s3_bytes_downloaded_bytes_total[5m])` — throughput.

## User-defined object metadata round-trip

S3 lets every object carry an arbitrary number of user-defined headers (the
`x-amz-meta-*` family) — for example a `tag`, an `author`, or any
application-specific marker the upload tool wrote. OSVFS preserves these
headers across hydration and re-upload so a local edit doesn't strip them.

When a placeholder is created, OSVFS reads the bucket-side user metadata via
`HeadObject` and mirrors every entry into a Windows NTFS **alternate data
stream** named `:osvfs-user-meta` attached to the placeholder. The stream is
plain UTF-8 with one `key=value` pair per line. Names are normalized to
lowercase (matching the case S3 uses on the wire).

When a local edit is uploaded, OSVFS reads the same ADS back out and
reattaches every entry as `x-amz-meta-*` on the `PutObject` (or multipart)
request, so the headers survive the local edit cycle bit-for-bit. Newly
created local files have no stream attached and upload with no user
metadata, exactly as before.

You can inspect the mirrored metadata from PowerShell:

```powershell
# List streams attached to a hydrated placeholder
Get-Item C:\Users\you\OSVFS\meta\file.txt -Stream *

# Dump the metadata (UTF-8, one key=value per line)
Get-Content C:\Users\you\OSVFS\meta\file.txt -Stream osvfs-user-meta
```

AWS limits the combined `x-amz-meta-*` name+value byte count to **2 KiB per
object**. OSVFS pre-validates uploads against the same limit and surfaces a
clear error before the network round-trip, instead of forwarding an
oversized request that S3 would reject with an opaque 400.

