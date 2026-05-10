# 認証とクレデンシャル

[← README に戻る](../README.ja.md) · [English](./credentials.md)

OSVFS が AWS / Azure のクレデンシャルをどう解決するか。OSVFS の
DPAPI 暗号化クレデンシャルストア、AWS IAM Identity Center (SSO)、
AWS CLI 2.32+ の `aws login`、Azure の 4 認証ブランチ (connection
string / SAS / Managed Identity / DefaultAzureCredential) を扱います。

---

## Azure Blob の設定

`provider = "azureblob"` を選ぶと Azure バックエンドに切り替わります。
README の他のセクション (帯域幅の制限、マルチパートのチューニング、変更
ソースの選択、doctor、lost-and-found …) はそのまま適用されます。マウント
設定の差分は最小限です。

```toml
# ./osvfs.toml — Azure Blob、connection-string 認証
provider          = "azureblob"
bucket            = "my-container"             # Azure コンテナ名
root-folder       = "C:/Users/you/OSVFS-azure"
connection-string = "DefaultEndpointsProtocol=https;AccountName=…;AccountKey=…;EndpointSuffix=core.windows.net"
```

Azure Blob はマウントごとに以下 4 つの認証ブランチのうち**ちょうど 1 つ**
を受け付けます。複数指定はあいまいさを避けるため起動時に拒否されます。

| TOML キー | Azure SDK の経路 | 備考 |
| --- | --- | --- |
| `connection-string` | shared key 内蔵 | Azurite が出力する形式、Portal の "アクセスキー" ブレードからコピーできる形式 |
| `account-name` + `sas` | `AzureSasCredential` | サービス / アカウントレベル SAS |
| `account-name` + `managed-identity = true` | `ManagedIdentityCredential` | Azure 上で動くワークロード用 (VM / App Service / Functions / AKS) |
| `account-name` + `default-azure-credential = true` | `DefaultAzureCredential` チェーン | env / Visual Studio サインイン / Azure CLI / Managed Identity 等を SDK 公式の順で評価 |

`endpoint-url` はソブリンクラウドや Azure Stack 用で、デフォルトの
`*.blob.core.windows.net` を `https://{account}.blob.{suffix}` で上書き
できます。

#### ストレージアカウントの安全性要件

OSVFS はストレージアカウントで **Blob Soft Delete** が有効でない限り起動を
拒否します。安全性ガードはコピペ可能な
`az storage account blob-service-properties update` コマンドを表示します。
**Versioning** は上書きパスの保護として併用が推奨され、ガードのメッセージ
にも両方の有効化フラグを含めています。Versioning の状態は OSVFS が使う
データプレーン SDK では取得できないため自動検証はしませんが、ガードの
出力する `az` コマンドを実行すれば両方が同時に有効になります。

#### サーバーサイド変更通知 (Event Grid → Storage Queue)

`change-source = "events"` で Watcher を Azure Storage Queue に接続できま
す。Storage Account に Event Grid サブスクリプションを設定し、
`Microsoft.Storage.BlobCreated` / `Microsoft.Storage.BlobDeleted` を
Storage Queue に送るようにします。`event-queue` には connection-string 利
用時はキュー名を、SAS / Managed Identity / DefaultAzureCredential 利用時は
完全なキュー URL を指定します。サブスクリプションの構築 (Storage Account
→ トピック → サブスクリプション → Storage Queue) は Azure ポータルまたは
`az eventgrid event-subscription create` で行います。


## AWS 認証情報の管理

OSVFS は AWS SDK 標準の認証情報チェーン (環境変数 / 共有プロファイル
`~/.aws/credentials` / IAM ロール / IMDS) を利用できるほか、**Windows
Credential Manager 上に独自のユーザー単位の暗号化ストア**を持つことができま
す。シークレットアクセスキー (および任意の STS セッショントークン) は
**DPAPI** の `CurrentUser` スコープで暗号化されたうえで credential blob に
書き込まれるため、**保存したユーザー本人だけが同一マシン上で復号できる**
設計です。

```powershell
# 対話入力で保存 (シークレット入力はマスク表示)
osvfs credentials set --profile prod

# コマンドラインで全部渡す場合 (対話プロンプトなし)
osvfs credentials set --profile prod `
  --access-key AKIA... `
  --secret-key ... `
  --session-token ...   # 一時認証情報のときだけ

# 保存済みプロファイルのメタデータ表示 (シークレットは絶対に表示されません)
osvfs credentials get --profile prod

# OSVFS 管理下のプロファイル一覧
osvfs credentials list

# 削除
osvfs credentials remove --profile prod
```

その上でマウント設定にプロファイル名を記述します:

```toml
provider    = "s3"
bucket      = "my-bucket"
root-folder = "C:/Users/you/OSVFS"
aws-profile = "prod"
```

エントリは generic credential として `OSVFS:AWS:<profile>` という target
name で保存され、`LocalMachine` スコープで永続化されます (ログアウト後も
維持)。一方で blob は保存したユーザーの DPAPI 鍵で暗号化されているため、
**別ユーザーや別マシンにエントリをコピーしても復号できません**。OSVFS の
このストアはあくまで「ユーザー単位のローカルキャッシュ」として扱い、AWS
認証情報のバックアップ用途には使わないでください。

#### AWS IAM Identity Center (SSO) でサインイン

AWS IAM Identity Center (旧 AWS SSO) を利用する環境では、AWS CLI 標準の
`aws configure sso` をそのまま使ってください。`~/.aws/config` に
`sso_session` プロファイルが書き込まれ、ベアラトークンは
`~/.aws/sso/cache/` にキャッシュされ、AWS SDK がリクエスト毎にロール
クレデンシャルを自動でリフレッシュします。OSVFS は SDK 共有プロファイル
チェーン経由でこのプロファイルを拾うため、OSVFS 専用の SSO コマンドは
ありません。

1. **SDK 推奨ウィザードを実行** (`sso-session` ブロックとそれを参照する
   プロファイルが書き込まれます。詳細は
   [SDK ドキュメント](https://docs.aws.amazon.com/sdkref/latest/guide/feature-sso-credentials.html#sso-token-config) 参照):
   ```powershell
   aws configure sso --profile prod
   ```
   ウィザードが start URL / region / アカウント / ロールを対話で聞いた
   うえで、以下のような設定を生成します:
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
2. **ベアラトークンが期限切れになったら再認可** (既定で約 8 時間):
   ```powershell
   aws sso login --sso-session my-org
   ```
3. **`osvfs.toml` から参照**。OSVFS DPAPI ストアに無いプロファイル名は
   SDK 共有プロファイルチェーンへフォールバックして SSO エントリを
   拾います:
   ```toml
   provider    = "s3"
   bucket      = "my-bucket"
   root-folder = "C:/Users/you/OSVFS"
   aws-profile = "prod"
   ```

`osvfs doctor --profile prod` で解決経路 (例: `shared profile 'prod' (sso)`)
を確認できます。SDK チェーンが SSO 経由で配っていることがログから
判別できます。

##### マウント中の自動再認証

SDK が提供する `SSOAWSCredentials` (および `credential_process` /
`AssumeRole` 等の同系統ラッパー) は、リクエスト毎に短期クレデンシャルを
期限切れ前にローテーションします。OSVFS は通常パスでは何もする必要が
ありません。安全網として、SDK のプリエンプトウィンドウをすり抜けて
発生する稀な `ExpiredToken` レスポンス (スリープ復帰や時刻の大きな
ずれなど) は OSVFS が捕捉し、SDK のリフレッシングラッパーに対して
`ClearCredentials()` を呼び出し、リクエストを 1 度だけ再送します。

- **再試行成功**: 新しい有効期限が Information ログに 1 行残るだけで、
  マウントは継続します。
- **再試行失敗** (上流のベアラトークン / リフレッシュトークン自体が
  失効している場合): Windows のバルーン通知 "OSVFS: AWS credentials
  expired" が表示され、`aws sso login` (または `aws login` /
  `osvfs credentials set`) を再実行して再認証するよう促します。失敗
  リクエストの例外はそのまま呼び出し元に伝播するため、エディタ / シェル
  でも検知できます。

#### `aws login` (AWS CLI 2.32+) でサインイン

IAM Identity Center を使わない環境では、AWS CLI v2.32.0 で追加された
`aws login` (OAuth 2.0 + PKCE フロー) でコンソールサインインを最大 12 時間の
自動更新付き一時クレデンシャルに変換できます。OAuth クライアントとエンド
ポイントは AWS CLI 専用に予約されているため、OSVFS は **AWS 公式推奨の
`credential_process` パターンを介して `~/.aws/config` のプロファイルを SDK
共有プロファイルチェーン経由で参照します**。

1. **AWS CLI v2.32.0 以降をインストール** し、IAM プリンシパルに
   [`SignInLocalDevelopmentAccess`](https://docs.aws.amazon.com/signin/latest/userguide/security-iam-awsmanpol.html)
   マネージドポリシーをアタッチ。
2. **CLI でサインイン**。`login_session` プロファイルが書き込まれ、リフレッシュ
   トークンは `%USERPROFILE%\.aws\login\cache` にキャッシュされます:
   ```powershell
   aws login --profile signin
   ```
3. **`~/.aws/config` に `credential_process` プロファイルを追加**。任意の
   AWS SDK が消費できる形にします (将来的に SDK が `login_session` を
   ネイティブサポートする可能性はありますが、現時点では credential_process
   が確実です):
   ```ini
   [profile signin]
   login_session = arn:aws:iam::123456789012:user/you
   region = us-east-1

   [profile osvfs-login]
   credential_process = aws configure export-credentials --profile signin --format process
   region = us-east-1
   ```
4. **`osvfs.toml` から参照**。指定した名前が OSVFS DPAPI ストアにない場合、
   OSVFS は SDK 共有プロファイルチェーンにフォールバックして
   `credential_process` エントリを拾います:
   ```toml
   provider    = "s3"
   bucket      = "my-bucket"
   root-folder = "C:/Users/you/OSVFS"
   aws-profile = "osvfs-login"
   ```

`osvfs doctor --profile osvfs-login` は解決経路 (例:
`shared profile 'osvfs-login' (credential_process)`) を表示するため、SDK
デフォルトチェーンではなく共有ファイルから取得できているかを確認できます。

