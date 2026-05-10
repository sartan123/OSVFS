# Performance tuning

[← Back to README](../README.md) · [日本語版](./tuning.ja.md)

Bandwidth ceilings, multipart upload thresholds, request concurrency, the
underlying HTTP transport, and the retry policy. All knobs map to keys in
[`osvfs.toml`](./configuration.md).

---

## Bandwidth limits

`osvfs` runs as a long-lived background process, so a single large hydration
or upload can saturate the link and starve other applications. Set
`bandwidth-up` / `bandwidth-down` in
[`osvfs.toml`](#configuration-file) to cap each direction independently:

```toml
bucket         = "my-bucket"
root-folder    = "C:/Users/you/OSVFS"
bandwidth-up   = "5M"       # cap uploads at 5 MiB/s
bandwidth-down = "10M"      # cap downloads at 10 MiB/s
```

Values follow the rclone `--bwlimit` convention: a plain number is bytes per
second, and the `K` / `M` / `G` suffixes mean KiB/s, MiB/s, and GiB/s
respectively (`5M` = 5 MiB/s). Omitting the key — or setting it to `0` —
leaves that direction unlimited. The limit is enforced through a token
bucket on the upload payload stream and the download response stream, so
`TransferUtility`'s multipart workers and the on-demand hydration path are
both paced by the same ceiling.

## Tuning multipart uploads

`osvfs` routes any upload at or above `multipart-threshold` through the
S3 multipart path, splitting the payload into `multipart-part-size`
chunks that `TransferUtility` uploads in parallel. The defaults (16 MiB
threshold, 5 MiB parts) match the AWS SDK v4 default for
`MinSizeBeforePartUpload`, but two common scenarios benefit from
explicit tuning:

| Scenario | Suggested settings | Why |
| --- | --- | --- |
| Fat links / large files | `multipart-threshold = "64M"`, `multipart-part-size = "64M"` | Larger parts amortize per-request overhead and cut the part count on multi-GiB files. |
| Many tiny edits | `multipart-threshold = "16M"` (keep 5M parts) | Skips multipart for small files where a single PUT is faster than negotiating an upload session. |
| Constrained networks | Keep defaults | Smaller parts mean a network blip retries less data. |

S3 enforces three hard limits on the part size — you must stay inside
all of them or `osvfs` refuses to start, and the service rejects the
upload at completion time:

- `multipart-part-size` must be **≥ 5 MiB** (`5M`). Smaller parts are
  rejected by S3 except for the last part of an upload.
- `multipart-part-size` must be **≤ 5 GiB** (`5G`). Larger parts
  exceed the per-part ceiling.
- A single multipart upload is capped at **10 000 parts**, so the
  largest object you can upload is `part-size × 10 000` (16 MiB parts
  → 160 GiB max; 64 MiB parts → 640 GiB max). Pick a part size large
  enough to fit your largest expected file.

## Tuning request concurrency

`osvfs` caps the number of in-flight S3 calls per direction so a burst of
hydrations or background uploads cannot saturate the SDK's HTTP pool or
overwhelm the bucket. Three independent knobs in `osvfs.toml` control the
ceiling:

| Key | Default | What it bounds |
| --- | --- | --- |
| `max-concurrent-uploads` | `4` | Distinct `UploadAsync` calls in flight. One save = one permit, regardless of how many multipart parts the SDK fans the call out into. |
| `max-concurrent-downloads` | `8` | Distinct `ReadRangeAsync` calls in flight (one per ProjFS hydration request). |
| `max-multipart-parts` | `10` | Multipart parts uploaded **inside a single `UploadAsync` call**, threaded through to `TransferUtilityConfig.ConcurrentServiceRequests`. |

The two ceilings are orthogonal: the *outer* gate (`max-concurrent-uploads`)
limits how many uploads start at once, and the *inner* gate
(`max-multipart-parts`) limits how many of one upload's parts ride the
network in parallel. The peak in-flight S3 part PUTs at any instant is at
most `max-concurrent-uploads × max-multipart-parts`. The HTTP connection
pool is sized as `max(max-concurrent-uploads, max-concurrent-downloads) × 2`
so connection exhaustion is not the binding constraint.

| Scenario | Suggested settings | Why |
| --- | --- | --- |
| Fat link, multi-GiB files | `max-concurrent-uploads = 2`, `max-multipart-parts = 16` | One upload at a time, but each upload pushes many parts in parallel — fastest single-file throughput. |
| Many small files (build artifacts, photos) | `max-concurrent-uploads = 8`, `max-multipart-parts = 4` | Lots of tiny PUTs in flight; per-upload parallelism is wasted on small files. |
| Flaky upstream / 5xx storms | `max-concurrent-uploads = 2`, `max-concurrent-downloads = 4` | Smaller bursts give the SDK's adaptive retry token bucket room to back off. |
| Bucket with low TPS quotas | Halve all three values | Caps total requests/sec so you stay below `RequestLimitExceeded` thresholds. |

```toml
bucket                    = "my-bucket"
root-folder               = "C:/Users/you/OSVFS"
max-concurrent-uploads    = 4
max-concurrent-downloads  = 8
max-multipart-parts       = 10
```

All three values must be ≥ 1; OSVFS rejects zero or negative values at
startup.

## HTTP transport tuning

OSVFS hands the AWS SDK a custom `HttpClientFactory` so the underlying
`SocketsHttpHandler` is pinned to operationally-safe defaults instead of
the framework's "infinite lifetime, unbounded pool" defaults. The factory
is built once per backend and shared with the SDK for the lifetime of the
mount, so a single `AmazonS3Client` can sustain long-lived high-throughput
sessions without leaking sockets or pinning a stale DNS answer.

| Setting | Value | Why |
| --- | --- | --- |
| `PooledConnectionLifetime` | `5 min` | Caps how long a pooled TCP connection lives so DNS changes (S3 endpoint rotation, VPC endpoint failover) are picked up without restarting the process. |
| `PooledConnectionIdleTimeout` | `2 min` | Closes connections that have been idle past this window so the host releases sockets promptly when a burst of traffic ends. |
| `MaxConnectionsPerServer` | `max(uploads, downloads) × 2` | Same value as the SDK's `AmazonS3Config.MaxConnectionsPerServer`; sized off the configured concurrency so the per-direction gates remain the binding constraint, not connection exhaustion. |
| `EnableMultipleHttp2Connections` | `true` | Lets the pool open additional HTTP/2 connections when a single one runs out of `SETTINGS_MAX_CONCURRENT_STREAMS`. |
| HTTP/2 promotion | enabled for AWS endpoints | Outbound requests are issued with `HttpVersion.Version20` and `RequestVersionOrLower` policy, so endpoints that only speak HTTP/1.1 (LocalStack, MinIO) negotiate down transparently. Disabled when `endpoint-url` is set. |

These knobs are not surfaced in `osvfs.toml` — the values above are
appropriate for every supported deployment and have no operational reason
to be tuned per mount. Override them in code if you fork the project for
a non-AWS object store with materially different connection semantics.

## Retry policy

Transient object-store failures are retried by the AWS SDK pipeline. OSVFS
configures the client with `RetryMode.Adaptive` (the SDK's adaptive
client-side throttling, which combines the standard exponential backoff with
a token bucket that suppresses request bursts when the service signals
overload) and `MaxErrorRetry = retry-max-attempts − 1`. The SDK's built-in
retry classifier decides which failures are eligible:

| Failure | Retried? | Notes |
| --- | --- | --- |
| HTTP 5xx (`500`, `502`, `503`, `504`, …) | Yes | Server-side / load-balancer errors. Treated as transient by the SDK. |
| HTTP 408 `Request Timeout` | Yes | Server-side timeout; the SDK retries with backoff. |
| `Throttling` / `ThrottlingException` / `RequestThrottled*` / `TooManyRequestsException` / `ProvisionedThroughputExceededException` / `RequestLimitExceeded` / `SlowDown` | Yes | AWS throttling family. Adaptive mode also slows the next request via the token bucket. |
| `RequestTimeout` / network errors / connection resets | Yes | Local socket / connection errors. |
| HTTP 4xx other than 408 (`400`, `401`, `403`, `404`, `409`, `412`, …) | No | Caller-side errors (bad request, missing object, permissions). Surfaced immediately. |
| `OperationCanceledException` / `TaskCanceledException` | No | Cancellation propagates without retry. |

The schedule is owned by the SDK: it uses exponential backoff with jitter
inside `MaxErrorRetry` retries. When `retry-max-attempts` is `1` the SDK
performs zero retries (the first attempt is the only one). The SDK's
`TransferUtility` retries individual multipart parts on its own — under
`retry-max-attempts = 3` a single failing part can be re-uploaded up to
three times without restarting the whole multi-GiB upload.

```toml
bucket             = "my-bucket"
root-folder        = "C:/Users/you/OSVFS"
retry-max-attempts = 5         # 5 total attempts (1 initial + 4 retries)
```

