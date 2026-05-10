# 同期と変更イベント

[← README に戻る](../README.ja.md) · [English](./sync-and-events.md)

OSVFS のリモート変更検出 (ポーリング / イベント駆動)、整合性チェックの
間隔、読み取り専用マウントへの切替方法。AWS S3 は EventBridge → SQS、
Azure Blob は Event Grid → Storage Queue を使います (キュー側の認証は
[認証とクレデンシャル](./credentials.ja.md) を参照)。

---

## 変更検出モード

OSVFS は、他クライアント (AWS マネジメントコンソール、別マシンの
`aws s3 cp`、チームメンバーの作業など) がバケットに加えた変更を検出する
方式を 2 つから選べます。バケット規模、要求するレイテンシ、サーバ側の
設定権限に応じて選択してください。

| モード | レイテンシ | バケット側のセットアップ | 想定用途 |
| --- | --- | --- | --- |
| `polling` (既定) | `sync-interval-seconds` 以下 (既定 30 秒) | 不要。AWS 認証情報がバケットを list できれば動く | 小規模バケットや変更頻度の低いバケット。EventBridge / SQS を追加する権限がない環境 |
| `events` | 数秒 (long-poll の覚醒 + SQS 往復) | バケット → EventBridge → SQS のパイプライン (下記手順) | 再列挙コストが高い大規模バケット、リモート編集を準リアルタイムで反映したい場合 |

`events` には、`Object Created` / `Object Deleted` 通知を EventBridge
経由で受け取る SQS キューが必要です。レガシーの S3 直接 SQS 通知形式
(`Records[]`) は **パースしません** — EventBridge 経由で構成してください。

#### SQS キュー / EventBridge ルール / バケット通知のセットアップ

下の 4 ステップで最小構成のパイプラインを作成します。アカウント ID、
リージョン、バケット名は環境に合わせて置き換えてください。各ステップは
AWS CLI コマンドを示していますが、SQS / EventBridge / S3 のマネジメント
コンソールからも同等の操作が可能です。

1. **SQS キューを作成。**

   ```bash
   aws sqs create-queue --queue-name osvfs-changes \
     --attributes ReceiveMessageWaitTimeSeconds=20
   ```

   キュー側でも long-polling を有効にしておくと空受信が減ります。

2. **EventBridge からの SendMessage を許可する。** プレースホルダーを
   置き換えて `queue-policy.json` として保存します。

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

   キューにアタッチ:

   ```bash
   aws sqs set-queue-attributes \
     --queue-url QUEUE_URL \
     --attributes Policy=file://queue-policy.json
   ```

3. **バケットの EventBridge 通知を有効化** (S3 → 対象バケット → プロパティ
   → "Amazon EventBridge"、もしくは)

   ```bash
   aws s3api put-bucket-notification-configuration \
     --bucket YOUR_BUCKET \
     --notification-configuration '{"EventBridgeConfiguration":{}}'
   ```

4. **キューをターゲットにする EventBridge ルールを作成。** イベント
   パターンを `event-pattern.json` として保存:

   ```json
   {
     "source": ["aws.s3"],
     "detail-type": ["Object Created", "Object Deleted"],
     "detail": { "bucket": { "name": ["YOUR_BUCKET"] } }
   }
   ```

   ルールとターゲットを作成:

   ```bash
   aws events put-rule \
     --name osvfs-bucket-changes \
     --event-pattern file://event-pattern.json

   aws events put-targets \
     --rule osvfs-bucket-changes \
     --targets 'Id=osvfs-sqs,Arn=arn:aws:sqs:REGION:ACCOUNT_ID:osvfs-changes'
   ```

OSVFS が動作する IAM 主体には、対象キューに対して
`sqs:ReceiveMessage`、`sqs:DeleteMessage`、(`event-queue` にキュー名のみを
渡す場合は) `sqs:GetQueueUrl` が必要です。

設定ファイルでマウントをイベント連携に切り替えます:

```toml
bucket        = "my-bucket"
root-folder   = "C:/Users/you/OSVFS"
change-source = "events"
event-queue   = "https://sqs.ap-northeast-1.amazonaws.com/123456789012/osvfs-changes"
```

> 仮想化ルート 1 つにつき 1 キュー。同じキューを複数の `osvfs` インスタンス
> で共有すると、メッセージが消費者間で分配されてしまい、それぞれが半分しか
> 受け取らなくなります。

## オンデマンド同期

`polling` モードには `sync-mode` で切り替え可能な 2 種類の再列挙戦略
があります。

| モード | tick ごとに再列挙する範囲 | API コスト | 適した用途 |
| --- | --- | --- | --- |
| `on-demand` (既定) | ProjFS 経由でユーザーが実際に訪問したディレクトリと、その祖先チェーンのみ | **訪問済みディレクトリ数** に比例 (バケットサイズに非依存) | 既定値。ProjFS の本来のオンデマンド設計と AWS S3 Files の[同期設計](https://docs.aws.amazon.com/AmazonS3/latest/userguide/s3-files-synchronization.html)に揃えた挙動 |
| `full` | バケット全体 (`prefix` 指定時はそのサブツリー) | **総オブジェクト数** に比例 (1000 キーごとに `ListObjectsV2` 1 ページ × tick) | バケット全域での "remote = source of truth" 保証が必要な場合や、十分小さい / 静かなバケットでコストが無視できる場合。Phase&nbsp;1 当初の挙動を保持 |

## 大規模バケット運用時の同期間隔

`osvfs` は外部オブジェクトストアの変更を `sync-interval-seconds`
(既定 `30` 秒) ごとのバケット再列挙で検出します。`sync-mode = "full"` では
各ポーリングが `ListObjectsV2` の全ページ (設定された prefix 以下) を
走査します。`sync-mode = "on-demand"` (既定) では訪問済みディレクトリごとに
`Delimiter='/'` 付きの 1 リクエスト (内部でページング) が走ります。

`full` モードでは S3 の ListObjectsV2 が 1 ページあたり 1000 キーで
打ち切られるため、ポーリング 1 回の所要時間は監視対象プレフィックス配下の
オブジェクト数にほぼ比例して増えます。目安として、近接リージョンの本番
S3 に対して 1 ページの ListObjectsV2 は十数〜数百ミリ秒程度で返るので、
10 万オブジェクトのバケットなら ~100 ラウンドトリップ・数秒分の listing
が tick ごとに発生します。listing 時間が `sync-interval-seconds` に
近づいたら、ポーリングが重ならないように間隔を伸ばすか、`prefix` で
対象範囲を絞ってください。

`on-demand` モードのコストは訪問済みディレクトリ数にスケールするため、
100 ディレクトリ × 各 1 万件のバケットでも、ユーザーが開いたディレクトリ
1 つあたり ~1 件の `ListObjectsV2` 呼び出しに収まります (バケット全体の
1000 キーごとに 1 ページ、ではありません)。

## 読み取り専用マウント

仮想化ルートを「バケットからは読み込むが書き戻しはしない」一方向モード
に切り替えたい場合は、[`osvfs.toml`](#設定ファイル) で `read-only = true`
を指定します:

```toml
bucket      = "my-bucket"
root-folder = "C:/Users/you/OSVFS"
read-only   = true
```

`read-only` を有効化したマウントの挙動は以下の通りです:

- 削除 / リネーム / ハードリンク作成 / プレースホルダーから実体ファイル
  への変換 (placeholder-to-full) に対する ProjFS の事前通知はすべて
  `false` を返します。エクスプローラーや他のプロセスからの書き込み試行は
  S3 呼び出しが発生する前にファイルシステム層で失敗します。新規ファイル
  作成 / 上書き / ハンドルクローズ時の修正通知自体は受信しますが、その後の
  アップロード経路は短絡されます。
- オブジェクトストア変更ウォッチャーは無効化されます。`ListObjectsV2`
  のポーリングも SQS の receive も走らず、`.osvfs-lost+found` 隔離
  ディレクトリも作成されません。したがって読み取り専用マウントは
  各ディレクトリを初めて列挙した時点の **凍結スナップショット** に
  なり、マウント中に他クライアントが行ったリモート編集は反映されません。
- ディレクトリの列挙、プレースホルダーの作成、初回オープン時のオン
  デマンド hydrate (ファイル本体のダウンロード) は読み書きモードと
  まったく同じ挙動です。読み取り経路には一切影響ありません。

