# Troubleshooting

[← Back to README](../README.md) · [日本語版](./troubleshooting.ja.md)

The `osvfs doctor` self-check and the `osvfs lost-and-found` recovery CLI.

---

## Doctor self-check

When a mount refuses to start — `StartVirtualizing failed`, "bucket not
found", "AccessDenied", credentials expired — **run `osvfs doctor`
first**. The doctor performs a fixed sequence of read-only environment
checks and prints a colored summary:

```powershell
# Use the first [[mount]] in osvfs.toml as the bucket / region / profile context
osvfs doctor

# Override the context entirely (handy when you have no config yet)
osvfs doctor --bucket my-bucket --region eu-central-1 --profile prod

# LocalStack / MinIO style
osvfs doctor --bucket my-bucket --endpoint-url http://localhost:4566
```

The doctor verifies, in order:

1. **Windows ProjFS feature (`Client-ProjFS`)** — registry check that the
   PrjFlt minifilter is registered and the user-mode `ProjectedFSLib.dll`
   is present. Equivalent to
   `Get-WindowsOptionalFeature -FeatureName Client-ProjFS`.
2. **`StartVirtualizing` smoke test** — creates a throwaway directory,
   marks it as a virtualization root, calls `StartVirtualizing`, then
   tears everything down. Catches "feature installed but PrjFlt service
   stopped" and EDR / antivirus interference that the registry check
   cannot see.
3. **AWS credentials resolution** — resolves credentials from the OSVFS
   profile (`--profile`) or the SDK chain, reports the source and the
   last 4 chars of the access key id, and flags whether the credentials
   are temporary (session-token bearing).
4. **Bucket access (`HeadBucket`)** — calls `GetBucketLocation`. A 403
   means the principal can't list the bucket; a 404 typically means the
   region is wrong.
5. **Bucket versioning** — required by OSVFS for safe conflict
   resolution. Reports the exact `aws s3api put-bucket-versioning`
   command when the bucket has versioning suspended or never enabled.

Each row is prefixed by `[OK]`, `[!!]`, `[XX]`, or `[--]` (skipped). The
process exits **0** when every check passes (skips and warnings do not
count) and **2** when any check needs operator action, so the doctor is
also safe to wire into start-up scripts:

```powershell
osvfs doctor --bucket $env:OSVFS_BUCKET; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
osvfs mount-all
```

`NO_COLOR=1` and redirected stdout disable the ANSI escape codes so the
output stays clean for log shippers and CI.

## Recovering quarantined files (`lost-and-found`)

When a remote change collides with an unsynced local edit, the watcher
copies the dirty local file into the mount's `.osvfs-lost+found`
directory before overwriting it with the remote (authoritative) version.
The `osvfs lost-and-found` sub-command lets you inspect and recover
those copies without leaving the shell:

```powershell
# Show every quarantined file (newest first), with the original path and size
osvfs lost-and-found list

# When osvfs.toml defines several mounts, pick one by --name
osvfs lost-and-found list --name docs

# Diff a quarantined copy against the current remote object.
# Text files: external `git diff --no-index --color`.
# Binary files (NUL byte in the first 8 KiB): SHA-256 + size summary.
osvfs lost-and-found diff 20260510T123456789Z_docs%2Fnotes.md

# Copy a quarantined file back out to a chosen path
# (default: ./<original-basename> in the current working directory)
osvfs lost-and-found restore 20260510T123456789Z_docs%2Fnotes.md `
  --target C:\Users\you\Desktop\notes-recovered.md
```

The first column from `list` (`FILENAME`) is the identifier consumed by
`diff` and `restore`; copy-paste it verbatim. The filename encoding is
`<UTC timestamp>_<URL-escaped original path>`, so `list` always prints
the decoded `ORIGINAL-PATH` alongside it. `restore` refuses to clobber an
existing destination unless you pass `--force`. `diff` requires `git` on
`PATH`; without it the command falls back to the binary summary.

