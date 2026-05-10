# 設定

[← README に戻る](../README.ja.md) · [English](./configuration.md)

OSVFS の必要環境、マウントの起動方法、CLI の最小表面、TOML 設定ファイルの
全キーリファレンス (マルチマウント含む) をまとめます。Azure 固有のキー
(`account-name` / `sas` / `managed-identity` / `default-azure-credential` /
`connection-string`) は [認証とクレデンシャル](./credentials.ja.md) を
参照してください。

---

## 必要環境

- Windows 10 1809 (ビルド 17763) 以降、または Windows 11
- Windows オプション機能 **`Client-ProjFS`** が有効化されていること
- AWS SDK の標準的な認証情報チェーン (環境変数 / 共有プロファイル / IAM ロール
  など) で解決できる AWS 認証情報、または下記
  [AWS 認証情報の管理](#aws-認証情報の管理)で説明する OSVFS 内蔵の暗号化スト
  アに保存した認証情報
- 読み書き可能な S3 バケット
- 対象バケットで **バージョニングが有効化されていること**。バージョニングが
  Enabled でない場合 `osvfs` は起動を拒否します — 詳しい理由と
  `allow-unversioned` によるバイパス方法は
  [バージョニングが必要な理由](#バージョニングが必要な理由) を参照してくだ
  さい。認証情報は併せて `s3:GetBucketVersioning` を許可している必要があり
  ます。

バージョニングは AWS CLI で 1 回だけ有効化します:

```powershell
aws s3api put-bucket-versioning `
  --bucket my-bucket `
  --versioning-configuration Status=Enabled
```

#### バージョニングが必要な理由

仮想化ルート内でのローカルファイルの編集・削除は、S3 上では上書き
`PutObject` および `DeleteObject` 呼び出しとして伝播されます。バケット
バージョニングが無効な場合、これらの操作は **破壊的かつ取り消し不可能** で
す。削除されたオブジェクトは消滅し、上書きされた内容は復元できません。
バージョニングを有効化すると、各操作が新しいバージョンと削除マーカーの組と
して保存されるため、エクスプローラー上の誤操作や暴走スクリプトからも復旧
できます。

`osvfs` は起動時に対象バケットがバージョニング無効 (もしくは Suspended) の
状態であると判定すると、コピー&ペースト可能な
`aws s3api put-bucket-versioning` コマンド・バケット名・このセクションへの
リンクを含むエラーメッセージを出して起動を拒否します。

バケットをジョブ毎に再作成する CI 用途や使い捨てバケットなど、復旧性の議論
が当てはまらないシナリオでは `osvfs.toml` で `allow-unversioned = true`
を設定すると安全チェックをバイパスできます。

ProjFS は管理者権限の PowerShell で 1 回だけ有効化します:

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -All
```

## 起動

OSVFS はマウントごとの設定を **TOML 設定ファイル** から読み込みます (キー
の詳細と複数マウント形式は [設定ファイル](#設定ファイル) 参照)。最小構成
は次の 2 行です:

```toml
# ./osvfs.toml
bucket      = "my-bucket"
root-folder = "C:/Users/you/OSVFS"
```

このファイルをカレントディレクトリ (もしくは `%APPDATA%\OSVFS\config.toml`)
に置けば:

```powershell
osvfs                            # 設定済みマウントを起動
osvfs mount-all                  # [[mount]] 配列の全マウントをこのプロセスで起動
osvfs mount --name personal      # 名前指定で 1 つだけ起動
```

仮想化ルートのフォルダーをエクスプローラーで開くと、バケットの内容が表示

## コマンドラインの構成

OSVFS は意図的に**設定ファイル駆動**です。各マウントの設定 (`bucket` /
`root-folder` / `region` / `aws-profile` / `bandwidth-up` /
`retry-max-attempts` …) はすべて `osvfs.toml` のみで管理します。CLI が
受け付けるのは次の 3 つだけです。

| 構成要素 | 用途 |
| --- | --- |
| サブコマンド (`mount` / `mount-all` / `credentials` / `doctor` / `lost-and-found`) | どのマウントを起動するか、暗号化された認証情報ストアの管理、環境セルフチェック、または隔離された退避ファイルの確認・復元 |
| `--name <mount>` | `osvfs mount` で `[[mount]]` 配列の中から 1 つ選ぶ |
| `--verbose` / `--log-format` | デバッグ用のプロセスレベル一時上書き。TOML の `verbose` / `log-format` も引き続き有効で、両方ある場合は CLI が勝ちます |

バケット内の特定のサブツリーだけを投影したい場合 (例えば
`s3://my-bucket/team-a/`) はマウントエントリで `prefix = "team-a/"` を
指定します。仮想化ルートからはこのプレフィックスが論理ルートに見えるよ
うになり、列挙・hydrate・書き込み・削除・リネームすべてがプレフィックス
配下のオブジェクトにスコープされます。バケット内のそれ以外のオブジェク
トは見えなくなります。

## 設定ファイル

マウント設定は TOML 設定ファイルでのみ管理します。最大 3 つのソースを
**優先度の低い順**でマージし、後のソースが先のソースを**キー単位で上書き**
します。

1. **`osvfs.exe` と同階層の `osvfs.toml`** (最低優先度。配布物に同梱する
   ベースライン)。`AppContext.BaseDirectory` で解決するため、カレント
   ディレクトリに依存せず常に exe 隣を見ます。
2. **`%APPDATA%\OSVFS\config.toml`** (ユーザー単位 / マシン共通)。認証情報
   やログ設定など、ユーザー個別の値を置く場所として推奨します。
3. **`--config <path>`** (最高優先度)。標準の保存場所を編集せず、複数の
   設定ファイルを切り替えたい場合に便利。1, 2 と異なり、指定したパスが
   存在しないと**起動時エラー**になります (黙ってフォールバックしない)。

プロセスレベルの CLI フラグ (`--verbose` / `--log-format`) は最終マージ
結果より優先されます。

`credentials` サブコマンドは設定ファイルの影響を受けません。常に CLI 引数
と対話プロンプトのみが入力源です。

```toml
# ./osvfs.toml もしくは %APPDATA%\OSVFS\config.toml
provider             = "s3"
bucket               = "my-bucket"
root-folder          = "C:/Users/you/OSVFS"
endpoint-url         = "http://localhost:4566"   # 任意
region               = "ap-northeast-1"          # 任意
prefix               = "team-a/"                 # 任意
aws-profile          = "prod"                    # 任意
bandwidth-up         = "5M"                      # 任意。"0" / 省略で無制限
bandwidth-down       = "10M"                     # 任意。"0" / 省略で無制限
multipart-threshold  = "16M"                     # 任意。既定 16 MiB (AWS SDK v4 既定値)
multipart-part-size  = "16M"                     # 任意。5M〜5G
retry-max-attempts   = 3                         # 任意。1 でリトライ無効
max-concurrent-uploads   = 4                     # 任意。同時 UploadAsync 呼び出し数
max-concurrent-downloads = 8                     # 任意。同時 ReadRangeAsync 呼び出し数
max-multipart-parts      = 10                    # 任意。1 アップロードあたりの並列パート数
log-format           = "text"                    # 任意。"text" または "json"
allow-unversioned    = false                     # DANGER: バージョニング安全チェックをスキップ
verbose              = false
sync-interval-seconds = 30
change-source        = "polling"                 # "polling" | "events"
sync-mode            = "on-demand"               # "on-demand" | "full" — polling 時のみ有効
event-queue          = ""                        # SQS URL/名。events で必須

[telemetry]                                       # 任意。省略で OTel 無効
otlp-endpoint        = "http://localhost:4317"   # OTLP コレクター URL
otlp-protocol        = "grpc"                    # "grpc" | "http-protobuf"
service-name         = "osvfs"                   # service.name リソース属性
metrics-listen       = "127.0.0.1:9999"          # ローカル Prometheus /metrics リスナー (host:port)
```

編集用のサンプル [`osvfs.toml.example`](./osvfs.toml.example) をリポジトリ
ルートに同梱しています。`dotnet publish` 時には `osvfs.exe` と同じ階層にも
コピーされるので、`osvfs.toml` (または `%APPDATA%\OSVFS\config.toml`) に
リネームして必要なキーをコメントアウト解除するだけで使えます。

キーはケバブケース (`root-folder`) とスネークケース (`root_folder`) のど
ちらも受け付けます。ケバブケースが推奨です。設定ファイルを置けば、起動は
次のように短く済みます。

```powershell
osvfs                       # オプションはすべて osvfs.toml から取得
```

#### 1 ファイルで複数マウント

`[[mount]]` のテーブル配列構文で 1 ファイルに複数のマウント定義を持たせ
られます。各マウントごとに bucket / root-folder / region / etc を別々
に書けるため、個人用バケットと業務用バケットを 1 つの設定ファイルで管
理できます。プロセスレベルのキー (`verbose` / `log-format`) はファイル
ルートに置き、すべてのマウントに適用されます。

```toml
# ./osvfs.toml — 複数マウント
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

`[[mount]]` エントリには従来の単一マウント形式と同じキー (`verbose` /
`log-format` を除く) が指定可能です。`name` はファイル内で一意である必
要があり、明示しないエントリには `mount[0]` / `mount[1]` … が自動付与さ
れます。1 つのファイル内で `[[mount]]` 配列とルート直下のマウントキー
を混在させると優先順位があいまいになるため、混在は明示的なエラーで拒否
します。

ファイルが 2 つ以上のマウントを宣言している場合、引数なしの `osvfs` は
どのマウントを起動すべきか判断できないので、次のいずれかを使います。

```powershell
osvfs mount-all                 # 全 [[mount]] をこのプロセスで起動
osvfs mount --name personal     # 名前指定で 1 つだけ起動
osvfs mount --name work         # 同じ設定ファイル内の別マウントを起動
```

各マウントは独自の `ProjFsProvider` で動作します。ログは
`OSVFS.Mount.<name>` カテゴリに分類されるので text / JSON フォーマット
のいずれでもマウント名で識別できます。ホストプロセスで Enter を押すと
逐次逆順 (最後に起動したマウントから先に) シャットダウンします。
