# Configuration

[← Back to README](../README.md) · [日本語版](./configuration.ja.md)

This page covers OSVFS prerequisites, how to start a mount, the small CLI
surface, and the full TOML configuration reference (including the
multi-mount layout). For Azure-specific keys (account-name, sas,
managed-identity, default-azure-credential, connection-string) see
[Authentication and credentials](./credentials.md).

---

## Prerequisites

- Windows 10 1809 (build 17763) or later, or Windows 11
- The Windows optional feature **`Client-ProjFS`** must be enabled
- AWS credentials reachable via the standard AWS SDK chain (environment
  variables, shared profile, IAM role, etc.) — or saved into the OSVFS
  built-in encrypted store described in
  [Managing AWS credentials](#managing-aws-credentials)
- An S3 bucket you have read/write access to
- **Bucket versioning must be Enabled** on the target bucket. `osvfs`
  refuses to start otherwise — see [Why versioning matters](#why-versioning-matters)
  for the rationale and the `allow-unversioned` escape hatch. The
  credentials must also allow `s3:GetBucketVersioning`.

Enable versioning once with the AWS CLI:

```powershell
aws s3api put-bucket-versioning `
  --bucket my-bucket `
  --versioning-configuration Status=Enabled
```

#### Why versioning matters

Local file edits and deletes inside the virtualization root propagate to S3
as overwrite `PutObject` and `DeleteObject` calls. Without bucket versioning
those operations are **destructive and irreversible**: a deleted object is
gone, an overwrite leaves no prior copy. Versioning turns each of those
calls into a new version + delete-marker pair, so a misclick in Explorer or
a runaway script remains recoverable.

If `osvfs` detects that the configured bucket has versioning disabled (or
suspended) it refuses to start with a copy-pasteable `aws s3api
put-bucket-versioning` command, the bucket name, and a link back to this
section.

For CI runs or disposable buckets where the bucket is recreated per-job
and the recoverability story does not apply, set `allow-unversioned = true`
in `osvfs.toml` to bypass the safety check.

Enable ProjFS once, in an elevated PowerShell session:

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -All
```

## Run

OSVFS reads every per-mount setting from a TOML configuration file (see
[Configuration file](#configuration-file) for the full key reference and the
multi-mount layout). The shortest possible config is:

```toml
# ./osvfs.toml
bucket      = "my-bucket"
root-folder = "C:/Users/you/OSVFS"
```

With that file in the current directory (or at
`%APPDATA%\OSVFS\config.toml`):

```powershell
osvfs                            # start the configured mount
osvfs mount-all                  # start every [[mount]] in the config (multi-mount form)
osvfs mount --name personal      # start one mount by name
```

Open the configured root folder in Explorer and the bucket contents appear.

## Command-line surface

OSVFS is intentionally configuration-driven: every per-mount setting
(`bucket`, `root-folder`, `region`, `aws-profile`, `bandwidth-up`,
`retry-max-attempts`, …) lives only in `osvfs.toml`. The command line
exposes just three things:

| Surface | Purpose |
| --- | --- |
| Sub-commands (`mount`, `mount-all`, `credentials`, `doctor`, `lost-and-found`) | Pick which mount(s) to start, manage the encrypted credential store, run the environment self-check, or recover quarantined files. |
| `--name <mount>` | Selects an entry from the `[[mount]]` array on `osvfs mount`. |
| `--verbose`, `--log-format` | Process-level overrides for one-off debugging. The TOML keys (`verbose`, `log-format`) are still honoured; the CLI flags simply win when both are present. |

To project only a sub-tree of a bucket — for example
`s3://my-bucket/team-a/` — set `prefix = "team-a/"` in the mount entry.
The virtualization root then mirrors that prefix as its own logical root:
listings, hydration, writes, deletes, and renames all stay scoped to objects
under the prefix, and the rest of the bucket is invisible.


## Configuration file

Mount settings live exclusively in a TOML configuration file. Up to three
sources are merged in **increasing-priority order** — later sources override
earlier ones on a per-key basis:

1. **`osvfs.toml` next to `osvfs.exe`** (lowest priority — acts as the
   packaged baseline). Resolved via `AppContext.BaseDirectory`, so the lookup
   is independent of the current working directory.
2. **`%APPDATA%\OSVFS\config.toml`** (per-user, machine-global). Operators
   typically keep credentials / log preferences here.
3. **`--config <path>`** (highest priority). Useful when an operator wants to
   pick between several profile files without editing the standard locations.
   Unlike sources #1 and #2, a missing path here is a hard error rather than
   a silent skip.

Process-level CLI flags (`--verbose`, `--log-format`) override the merged
config file values when supplied.

`credentials` sub-commands are not affected by the config file; they always
take their inputs from CLI arguments and interactive prompts.

```toml
# ./osvfs.toml or %APPDATA%\OSVFS\config.toml
provider             = "s3"
bucket               = "my-bucket"
root-folder          = "C:/Users/you/OSVFS"
endpoint-url         = "http://localhost:4566"   # optional
region               = "ap-northeast-1"          # optional
prefix               = "team-a/"                 # optional
aws-profile          = "prod"                    # optional
bandwidth-up         = "5M"                      # optional, "0" / omit = unlimited
bandwidth-down       = "10M"                     # optional, "0" / omit = unlimited
multipart-threshold  = "16M"                     # optional, default 16 MiB (AWS SDK v4 default)
multipart-part-size  = "16M"                     # optional, 5M..5G
retry-max-attempts   = 3                         # optional, 1 disables retries
max-concurrent-uploads   = 4                     # optional, in-flight UploadAsync calls
max-concurrent-downloads = 8                     # optional, in-flight ReadRangeAsync calls
max-multipart-parts      = 10                    # optional, parallel parts per upload
log-format           = "text"                    # optional, "text" or "json"
allow-unversioned    = false                     # DANGER: skip the bucket-versioning safety check
verbose              = false
sync-interval-seconds = 30
change-source        = "polling"                 # "polling" | "events"
sync-mode            = "on-demand"               # "on-demand" | "full" — only used by polling
event-queue          = ""                        # SQS URL/name, required for events

[telemetry]                                       # optional, omitted = OTel disabled
otlp-endpoint        = "http://localhost:4317"   # OTLP collector URL
otlp-protocol        = "grpc"                    # "grpc" | "http-protobuf"
service-name         = "osvfs"                   # service.name resource attribute
metrics-listen       = "127.0.0.1:9999"          # local Prometheus /metrics listener (host:port)
```

A ready-to-edit sample is shipped as
[`osvfs.toml.example`](./osvfs.toml.example) at the repo root and is also
copied next to `osvfs.exe` on `dotnet publish`, so you can rename it to
`osvfs.toml` (or `%APPDATA%\OSVFS\config.toml`) and uncomment the keys you
need.

Both kebab-case (`root-folder`) and snake_case (`root_folder`) keys are
accepted; kebab is preferred. With a config file in place, a typical mount
is just:

```powershell
osvfs                       # all options sourced from osvfs.toml
```

#### Multiple mounts in a single config

A configuration file can declare more than one mount under the `[[mount]]`
table-array syntax, each with its own bucket / root-folder / region / etc.
The process-level keys (`verbose`, `log-format`) stay at the top level and
apply to every mount:

```toml
# ./osvfs.toml — multiple mounts
verbose   = false
log-format = "json"

[[mount]]
name        = "personal"
bucket      = "my-personal"
root-folder = "C:/Users/you/OSVFS-personal"

[[mount]]
name        = "work"
bucket      = "my-work"
root-folder = "C:/Users/you/OSVFS-work"
prefix      = "team-a/"
aws-profile = "prod-readonly"
```

Each `[[mount]]` entry takes the same per-mount keys as the legacy single-
mount form (everything except `verbose` and `log-format`). The `name` is
required to be unique within the file; entries without an explicit `name`
are tagged `mount[0]`, `mount[1]`, etc. Mixing top-level mount keys with
`[[mount]]` entries in the same file is rejected so the precedence stays
unambiguous — pick one form per file.

When a file declares 2+ mounts, the bare root command refuses to guess
which one the operator wants. Pick one of:

```powershell
osvfs mount-all                 # start every [[mount]] in this process
osvfs mount --name personal     # start a single named mount
osvfs mount --name work         # start a different mount from the same config
```

Each mount runs its own `ProjFsProvider`; logs from a given mount land in
the `OSVFS.Mount.<name>` category so text / JSON formatters surface which
mount each line came from. Pressing Enter on the host process disposes
every mount in reverse start order.

