# Authentication and credentials

[← Back to README](../README.md) · [日本語版](./credentials.ja.md)

How OSVFS resolves AWS and Azure credentials. Covers the OSVFS DPAPI-
encrypted credential store, AWS IAM Identity Center (SSO), AWS CLI 2.32+
`aws login`, and the four Azure auth branches (connection string, SAS,
Managed Identity, DefaultAzureCredential).

---

## Azure Blob configuration

Selecting `provider = "azureblob"` swaps in the Azure backend; everything
else in the README (bandwidth limits, multipart tuning, the change-source
selector, the doctor, lost-and-found, …) applies the same way. The
mount-config delta is small:

```toml
# ./osvfs.toml — Azure Blob, connection-string auth
provider          = "azureblob"
bucket            = "my-container"             # Azure container name
root-folder       = "C:/Users/you/OSVFS-azure"
connection-string = "DefaultEndpointsProtocol=https;AccountName=…;AccountKey=…;EndpointSuffix=core.windows.net"
```

Azure Blob accepts exactly **one** of four credential branches per mount —
specifying more than one is rejected at startup so the precedence stays
unambiguous:

| TOML keys | Azure SDK path | Notes |
| --- | --- | --- |
| `connection-string` | embedded shared key | What Azurite hands out and what the Portal "Access keys" blade copies. |
| `account-name` + `sas` | `AzureSasCredential` | Service or account-level SAS. |
| `account-name` + `managed-identity = true` | `ManagedIdentityCredential` | For Azure-hosted workloads (VM, App Service, Functions, AKS). |
| `account-name` + `default-azure-credential = true` | `DefaultAzureCredential` chain | Picks env vars, Visual Studio sign-in, Azure CLI, Managed Identity, etc. in the SDK's documented order. |

`endpoint-url` is honoured for sovereign clouds and Azure Stack — point it
at `https://{account}.blob.{suffix}` to override the default
`*.blob.core.windows.net`.

#### Storage-account safety bar

OSVFS refuses to start unless the Storage Account has **Blob Soft Delete**
turned on; the safety guard surfaces a copy-pasteable `az storage account
blob-service-properties update` line when it does not. Versioning protects
against the *overwrite* path and is recommended alongside Soft Delete; the
remediation message asks for both. Versioning state is not exposed by the
data-plane SDK we ship, so it is not auto-verified — run the same `az` line
the guard prints to enable both flags in one shot.

#### Push-mode change notifications (Event Grid → Storage Queue)

`change-source = "events"` wires the watcher to an Azure Storage Queue
populated by an Event Grid subscription on the Storage Account
(`Microsoft.Storage.BlobCreated`, `Microsoft.Storage.BlobDeleted`). Set
`event-queue` to the queue name when a connection string is in play, or to
the full queue URL when SAS / Managed Identity / DefaultAzureCredential
authenticate the queue client. The exact subscription wiring (storage
account → topic → subscription → Storage Queue) lives in the Azure portal
or in `az eventgrid event-subscription create` — see the Microsoft docs
linked from the change-source description above.


## Managing AWS credentials

OSVFS can resolve AWS credentials through the standard AWS SDK chain
(environment variables, the shared `~/.aws/credentials` profile, IAM role,
IMDS), **or** through its own per-user encrypted store backed by Windows
Credential Manager. The secret access key — and any STS session token — is
encrypted with **DPAPI** at `CurrentUser` scope before it is written into the
credential blob, so the entry can only be decrypted by the user that saved
it on the same machine.

```powershell
# Save a profile (the secret prompt is masked)
osvfs credentials set --profile prod

# Or pass everything on the command line (skip the prompts)
osvfs credentials set --profile prod `
  --access-key AKIA... `
  --secret-key ... `
  --session-token ...   # optional, for temporary credentials

# Inspect a profile (the secret is never echoed)
osvfs credentials get --profile prod

# List every profile owned by OSVFS
osvfs credentials list

# Delete a profile
osvfs credentials remove --profile prod
```

Then reference the profile in your mount config:

```toml
provider    = "s3"
bucket      = "my-bucket"
root-folder = "C:/Users/you/OSVFS"
aws-profile = "prod"
```

Each entry is stored as a Windows generic credential under the target name
`OSVFS:AWS:<profile>`. The credential persists at `LocalMachine` scope
(it survives logout) but the DPAPI envelope is bound to the saving user, so
copying the entry to another user — or to another machine — will fail to
decrypt. Treat the OSVFS store as a per-user convenience cache, not as a
backup of your AWS credentials.

#### Sign in via AWS IAM Identity Center (SSO)

For environments that use AWS IAM Identity Center (formerly AWS SSO), use
the AWS CLI's built-in `aws configure sso` flow — it writes an
`sso_session` profile to `~/.aws/config`, caches the bearer token under
`~/.aws/sso/cache/`, and the AWS SDK auto-refreshes the role credentials
on every request. OSVFS picks the profile up through the SDK shared-profile
chain, so there is no OSVFS-specific SSO command to learn.

1. **Run the SDK's wizard** (writes an `sso-session` block + a profile
   referencing it; see
   [SDK docs](https://docs.aws.amazon.com/sdkref/latest/guide/feature-sso-credentials.html#sso-token-config)):
   ```powershell
   aws configure sso --profile prod
   ```
   The wizard prompts for the start URL, region, account, and role and
   produces something like:
   ```ini
   [sso-session my-org]
   sso_start_url = https://my-org.awsapps.com/start
   sso_region    = us-east-1
   sso_registration_scopes = sso:account:access

   [profile prod]
   sso_session   = my-org
   sso_account_id = 123456789012
   sso_role_name  = ReadOnly
   region         = us-east-1
   ```
2. **(Re-)authorize the bearer token** any time it expires (~8 h by default):
   ```powershell
   aws sso login --sso-session my-org
   ```
3. **Reference the profile from `osvfs.toml`** exactly like any other
   profile name. The OSVFS DPAPI store is consulted first; on a miss OSVFS
   falls back to the shared profile chain and picks up the SSO entry:
   ```toml
   provider    = "s3"
   bucket      = "my-bucket"
   root-folder = "C:/Users/you/OSVFS"
   aws-profile = "prod"
   ```

`osvfs doctor --profile prod` reports the resolution path
(e.g. `shared profile 'prod' (sso)`) so you can confirm the SDK chain is
serving the credentials.

##### Automatic refresh while mounted

The SDK's `SSOAWSCredentials` (and the matching wrappers for
`credential_process`, `AssumeRole`, …) all roll their short-term
credentials over before expiry on every signed request — OSVFS does not
need to do anything for the happy path. As an additional safety net,
OSVFS catches the rare on-the-wire `ExpiredToken` response that slips
past the SDK's preempt window (machine sleep / resume, large clock skew),
calls `ClearCredentials()` on the SDK's refreshing wrapper, and retries
the request once.

- **Retry succeeds**: a single Information log line records the new
  expiration; the mount keeps running.
- **Retry fails** (the upstream bearer / refresh token has itself
  expired): a Windows balloon-tip notification "OSVFS: AWS credentials
  expired" tells the operator to re-run `aws sso login` (or
  `aws login` / `osvfs credentials set`) to re-authenticate, and the
  failed request's exception propagates back to the caller.

#### Sign in via `aws login` (AWS CLI 2.32+)

For environments **not** using IAM Identity Center, AWS CLI v2.32.0 introduced
`aws login` — an OAuth 2.0 + PKCE flow that converts your AWS Management Console
sign-in into auto-refreshing temporary credentials (up to 12 hours). The OAuth
client and endpoints are reserved for the AWS CLI itself, so OSVFS integrates by
**referencing the resulting `~/.aws/config` profile through the SDK
shared-profile chain** (the AWS-recommended `credential_process` pattern).

1. **Install AWS CLI v2.32.0 or later** and attach the
   [`SignInLocalDevelopmentAccess`](https://docs.aws.amazon.com/signin/latest/userguide/security-iam-awsmanpol.html)
   managed policy to your IAM principal.
2. **Sign in** with the CLI; this writes a `login_session` profile and caches
   the refresh token under `%USERPROFILE%\.aws\login\cache`:
   ```powershell
   aws login --profile signin
   ```
3. **Wire it into `~/.aws/config`** as a `credential_process` profile so any
   AWS SDK can consume it (newer SDKs may eventually support `login_session`
   natively, but `credential_process` works today):
   ```ini
   [profile signin]
   login_session = arn:aws:iam::123456789012:user/you
   region = us-east-1

   [profile osvfs-login]
   credential_process = aws configure export-credentials --profile signin --format process
   region = us-east-1
   ```
4. **Reference it from `osvfs.toml`** like any other profile — when the name is
   absent from the OSVFS DPAPI store, OSVFS falls back to the SDK shared-profile
   chain and picks up the `credential_process` entry:
   ```toml
   provider    = "s3"
   bucket      = "my-bucket"
   root-folder = "C:/Users/you/OSVFS"
   aws-profile = "osvfs-login"
   ```

`osvfs doctor --profile osvfs-login` reports the resolution path so you can
confirm the credentials came from the shared file (e.g.
`shared profile 'osvfs-login' (credential_process)`) rather than the SDK
default chain.

