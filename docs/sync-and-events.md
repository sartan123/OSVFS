# Synchronization and change events

[← Back to README](../README.md) · [日本語版](./sync-and-events.ja.md)

How OSVFS picks up remote changes (polling vs. event-driven), how often it
reconciles, and how to flip a mount into read-only mode. AWS S3 uses
EventBridge → SQS for push-mode notifications; Azure Blob uses Event Grid
→ Storage Queue (see [Authentication and credentials](./credentials.md)
for queue auth).

---

## Change detection modes

OSVFS supports two strategies for detecting changes that other clients (the
AWS console, another `aws s3 cp`, a teammate's machine) make to the bucket.
Pick the one that matches your bucket size, latency budget, and how much
server-side configuration you can do.

| Mode | Latency | Bucket-side setup | When to use |
| --- | --- | --- | --- |
| `polling` (default) | Up to `sync-interval-seconds` (default 30 s) | None — works on any bucket the AWS credentials can list. | Small or quiet buckets; environments where you don't have permission to add EventBridge / SQS. |
| `events` | Seconds (long-poll wakeup + SQS round-trip) | Bucket → EventBridge → SQS pipeline (steps below). | Large buckets where re-listing is expensive, or when you need near-real-time visibility on remote edits. |

`events` needs an SQS queue that receives `Object Created` and `Object Deleted`
notifications produced by EventBridge. The legacy direct S3-to-SQS
notification format (`Records[]`) is **not** parsed; configure EventBridge
instead.

#### Setting up the SQS queue, EventBridge rule, and bucket notifications

The four steps below create the minimal pipeline needed. Substitute your
account ID, region, and bucket name. Each step shows the AWS CLI command;
the same actions are available in the console under SQS / EventBridge / S3.

1. **Create the SQS queue.**

   ```bash
   aws sqs create-queue --queue-name osvfs-changes \
     --attributes ReceiveMessageWaitTimeSeconds=20
   ```

   Long-polling on the queue side reduces empty receives.

2. **Allow EventBridge to publish to the queue.** Save this policy (replacing
   the placeholders) as `queue-policy.json`:

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [{
       "Effect": "Allow",
       "Principal": { "Service": "events.amazonaws.com" },
       "Action": "sqs:SendMessage",
       "Resource": "arn:aws:sqs:REGION:ACCOUNT_ID:osvfs-changes"
     }]
   }
   ```

   Then attach it to the queue:

   ```bash
   aws sqs set-queue-attributes \
     --queue-url QUEUE_URL \
     --attributes Policy=file://queue-policy.json
   ```

3. **Enable EventBridge notifications on the bucket** (S3 → bucket → Properties
   → "Amazon EventBridge", or:)

   ```bash
   aws s3api put-bucket-notification-configuration \
     --bucket YOUR_BUCKET \
     --notification-configuration '{"EventBridgeConfiguration":{}}'
   ```

4. **Create the EventBridge rule that targets the queue.** Save the pattern as
   `event-pattern.json`:

   ```json
   {
     "source": ["aws.s3"],
     "detail-type": ["Object Created", "Object Deleted"],
     "detail": { "bucket": { "name": ["YOUR_BUCKET"] } }
   }
   ```

   Then create the rule and target:

   ```bash
   aws events put-rule \
     --name osvfs-bucket-changes \
     --event-pattern file://event-pattern.json

   aws events put-targets \
     --rule osvfs-bucket-changes \
     --targets 'Id=osvfs-sqs,Arn=arn:aws:sqs:REGION:ACCOUNT_ID:osvfs-changes'
   ```

The IAM identity that OSVFS runs as needs `sqs:ReceiveMessage`,
`sqs:DeleteMessage`, and (when `event-queue` is a bare name)
`sqs:GetQueueUrl` on the queue.

Then point the mount at the queue in your config:

```toml
bucket        = "my-bucket"
root-folder   = "C:/Users/you/OSVFS"
change-source = "events"
event-queue   = "https://sqs.ap-northeast-1.amazonaws.com/123456789012/osvfs-changes"
```

> One queue per virtualization root. Two `osvfs` instances sharing a queue
> would each see only half of the messages.

## On-demand sync

`polling` mode supports two reconciliation strategies via `sync-mode`:

| Mode | What gets re-listed each tick | API cost | When to use |
| --- | --- | --- | --- |
| `on-demand` (default) | Only the directories the user has actually visited through ProjFS, plus their ancestor chain | Scales with the **visited-directory count**, independent of bucket size | The default — matches ProjFS's on-demand model and the AWS S3 Files [synchronization design](https://docs.aws.amazon.com/AmazonS3/latest/userguide/s3-files-synchronization.html). |
| `full` | The entire bucket (or `prefix` subtree) | Scales with **total object count** (one `ListObjectsV2` page per 1000 keys, every tick) | When you need the bucket-wide source-of-truth guarantee, or for small/quiet buckets where the cost is negligible. Preserves the original Phase&nbsp;1 behavior. |

## Sync interval for large buckets

`osvfs` detects external object-store changes by re-listing the bucket every
`sync-interval-seconds` (default `30`). Under `sync-mode = "full"` each poll
walks every page of `ListObjectsV2` for the configured prefix; under
`sync-mode = "on-demand"` each poll re-lists every visited directory once with
`Delimiter='/'` (one paged request per directory).

Under `full` mode the wall time of a poll grows roughly linearly with the
number of objects under the watched prefix because S3 caps a single
`ListObjectsV2` page at 1000 keys. As a rough planning guide, a single
`ListObjectsV2` page typically returns in tens to low-hundreds of
milliseconds against AWS S3 from a nearby region, so a 100k-object bucket
needs ~100 round-trips and a few seconds of listing per tick. If the listing
time approaches `sync-interval-seconds`, raise the interval (or scope the
projection with `prefix`) so polls do not overlap and starve other I/O.

Under `on-demand` mode the cost instead scales with the number of visited
directories, so a bucket with 100 directories each containing 10k files
costs roughly one paged `ListObjectsV2` per directory the user has opened,
not one per 1000 keys in the bucket.

## Read-only mounts

A virtualization root can be flipped into a one-way "read from the bucket,
never write back" mode by setting `read-only = true` in
[`osvfs.toml`](#configuration-file):

```toml
bucket      = "my-bucket"
root-folder = "C:/Users/you/OSVFS"
read-only   = true
```

When `read-only` is on:

- ProjFS pre-notifications for delete, rename, hardlink creation, and
  placeholder-to-full conversion all return `false`, so Explorer (and any
  other process) sees the operation fail at the filesystem layer before
  any S3 call would happen. New-file create / overwrite / modified-handle
  notifications are still received but the upload path is short-circuited.
- The object-store change watcher is disabled. No `ListObjectsV2` polling
  and no SQS receive runs, and the `.osvfs-lost+found` quarantine
  directory is never created. A read-only mount is therefore a **frozen
  snapshot** as of the moment each directory was first enumerated —
  remote edits made by other clients are not picked up while the mount is
  live.
- Directory listings, placeholder creation, and on-demand hydration
  (downloading file bodies on first open) work exactly as in read-write
  mode: the read path is unaffected.

