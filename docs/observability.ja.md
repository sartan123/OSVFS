# 可観測性

[← README に戻る](../README.ja.md) · [English](./observability.md)

構造化ログ、OpenTelemetry トレース / メトリクス (OTLP + ローカル
Prometheus リスナー)、ユーザー定義メタデータの往復保持。

---

## 構造化ログ

既定では `osvfs` は人間可読な 1 行形式のテキストログをコンソールに出力
します。[`osvfs.toml`](#設定ファイル) で `log-format = "json"` を指定する
(あるいは一時的に `--log-format json` で起動する) と、Datadog / Loki などの
ログ集約基盤が正規表現なしでパースできる構造化ストリームに切り替わります。

```powershell
osvfs --log-format json    # ファイル設定があっても CLI が一時的に上書きする
```

各ログエントリは 1 つの JSON オブジェクトを 1 行として書き出します
(プラットフォームの改行コードで終端)。フィールド名は
`Microsoft.Extensions.Logging.Console` の JSON フォーマッタが出力する
キーに準拠します:

| フィールド | 説明 |
| --- | --- |
| `Timestamp` | UTC タイムスタンプ。`yyyy-MM-ddTHH:mm:ss.fffZ` 形式 |
| `EventId` | ログエントリの `EventId.Id` (未指定時は `0`) |
| `LogLevel` | `Trace` / `Debug` / `Information` / `Warning` / `Error` / `Critical` |
| `Category` | ロガーのカテゴリ。通常はソース型のフルネーム (`OSVFS`、`OSVFS.ProjFs.ProjFsProvider` など) |
| `Message` | プレースホルダー置換後の最終的なメッセージ文字列 |
| `State` | 元のメッセージテンプレートと、`{Bucket}` などの名前付きプレースホルダーをそれぞれ独立したプロパティとして保持するオブジェクト。下流で構造化フィルタリングに使える |
| `Exception` | 例外が添付されている場合のみ存在。フォーマット済みの例外文字列 |

サンプル (可読性のため整形しているが、実際は 1 行で出力される):

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

## OpenTelemetry トレーシングとメトリクス

OSVFS は S3 バックエンドの全操作 **と** 主要な ProjFS コールバックを、
2 つの
[`ActivitySource`](https://learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing)
と対応する `Meter` で計装しています。OTLP exporter とコレクターを
組み合わせれば、Windows 側の仮想化処理時間がどこで使われ、どの S3
呼び出しに依存しているか、転送バイト量・エラー率がどう推移しているか
をバイナリに手を入れずに観測できます。

#### S3 バックエンドのシグナル (source `osvfs.s3`)

| シグナル | 種別 | タグ / 計測対象 |
| --- | --- | --- |
| `S3.List` / `S3.ListAll` / `S3.ListRecursive` / `S3.Head` / `S3.Get` / `S3.Put` / `S3.Delete` / `S3.DeletePrefix` / `S3.Rename` / `S3.RenamePrefix` / `S3.Copy` | Activity (`Client`) | `relative.path`, `relative.directory`, `byte.offset`, `byte.length` ほか |
| `osvfs.s3.bytes_uploaded` | Counter (`By`) | なし |
| `osvfs.s3.bytes_downloaded` | Counter (`By`) | なし |
| `osvfs.s3.errors_total` | Counter (`{error}`) | `operation` |
| `osvfs.s3.duration` | Histogram (`ms`) | `operation` |

`S3.Copy` は `S3.Rename` の子スパンになるので、Rename のうち実際の
CopyObject に要した時間と前後処理の時間がフレームグラフで分かれて見え
ます。

#### ProjFS コールバックのシグナル (source `osvfs.projfs`)

| シグナル | 種別 | タグ |
| --- | --- | --- |
| `ProjFS.StartDirectoryEnumeration` | Activity (`Internal`) | `relative.path` |
| `ProjFS.GetPlaceholderInfo` | Activity (`Internal`) | `relative.path`, `projfs.result` (404 時は `not_found`) |
| `ProjFS.GetFileData` | Activity (`Internal`) | `relative.path`, `byte.offset`, `byte.length` |
| `ProjFS.PreDelete` / `ProjFS.PreRename` / `ProjFS.PreCreateHardlink` / `ProjFS.PreConvertToFull` | Activity (`Internal`) | `relative.path` (rename / hardlink は `destination.path` も), `projfs.allowed` |
| `ProjFS.FileRenamed` / `ProjFS.FileHandleClosedFileModifiedOrDeleted` | Activity (`Internal`) | `relative.path`, `is.directory`, close ハンドラは `file.modified` / `file.deleted` も |
| `ProjFS.HandleFileModified` / `ProjFS.HandleFileDeleted` / `ProjFS.HandleFileRenamed` | Activity (`Internal`) | 該当 notification の子として provider 側の処理を記録 |
| `osvfs.projfs.errors_total` | Counter (`{error}`) | `operation` |
| `osvfs.projfs.duration` | Histogram (`ms`) | `operation` |

ユーザー操作 1 回につきスパンが 1 本のツリーになります。例として、
変更したファイルを保存すると次のような階層構造になります。

```
ProjFS.FileHandleClosedFileModifiedOrDeleted
  └─ ProjFS.HandleFileModified
       └─ S3.Put
```

これにより Jaeger の 1 トレースで「仮想化レイヤ → バックエンド」の
全経路が見え、各セグメントが全体時間にどれだけ寄与しているかが分かり
ます。

非常に高頻度で副作用のない notification (`FileOpened`,
`NewFileCreated`, `FileOverwritten`, `HardlinkCreated`,
`FileHandleClosedNoModification`) と、エントリ毎に呼ばれる
`GetDirectoryEnumeration` / `EndDirectoryEnumeration` は **意図的に
計装していません** — UI でフォルダを 1 回開くだけで十数〜数十回呼ば
れるうえ S3 を伴う処理ではないので、計装するとトレース量だけが膨ら
み価値が薄いためです。

#### OTLP exporter を有効化する

ワンショット起動なら `--otlp-endpoint`、永続的に有効化したい場合は
`osvfs.toml` に `[telemetry]` セクションを追加します。

```powershell
osvfs --otlp-endpoint http://localhost:4317   # gRPC (デフォルト)
osvfs --otlp-endpoint http://localhost:4318   # HTTP/Protobuf — 後述の [telemetry] で protocol を指定
```

```toml
# osvfs.toml — テレメトリの永続設定
[telemetry]
otlp-endpoint = "http://localhost:4317"
otlp-protocol = "grpc"          # "grpc" (デフォルト) または "http-protobuf"
service-name  = "osvfs"         # service.name リソース属性 (デフォルト "osvfs")
```

`--otlp-endpoint` を指定すると `[telemetry] otlp-endpoint` を上書きし、
`otlp-protocol` と `service-name` は TOML の値がそのまま採用されます。
両方ともエンドポイントが未設定ならテレメトリは無効のままです。

#### ローカル Prometheus 互換 `/metrics` エンドポイント

OTLP コレクターを併設しづらい単一ホスト / スクラッチ環境 / CI ワーカー
向けに、OSVFS ホストは Prometheus テキスト形式の `/metrics` を直接
HTTP で公開できます。`metrics-listen` を設定するか、ワンショット起動なら
`--metrics-listen host:port` を渡してください。`otlp-endpoint` とは独立
しているため、片方だけ・両方どちらでも動作します:

```powershell
osvfs --metrics-listen 127.0.0.1:9999            # /metrics のみ
osvfs --metrics-listen 127.0.0.1:9999 `
      --otlp-endpoint http://localhost:4317      # 両方
```

```toml
# osvfs.toml — Prometheus でスクレイプする pull 型メトリクス
[telemetry]
metrics-listen = "127.0.0.1:9999"
```

同一ポートに 3 つのエンドポイントを mount しています:

| パス        | 用途                                                                                                    |
| ----------- | ------------------------------------------------------------------------------------------------------- |
| `/metrics`  | Prometheus テキスト形式 v0.0.4。スクレイプ毎に `MetricReader.Collect` で最新スナップショットを再構築。  |
| `/healthz`  | フラットテキストの liveness プローブ (常に `200 OK\nok`)。                                              |
| `/version`  | アセンブリの informational version (OTel リソース version と同値)。                                     |

ループバック (`127.0.0.1` / `[::1]` / `localhost`) を推奨します。ワイルドカード
ホスト (`0.0.0.0` / `+` / `[::]`) にバインドすると内部カウンタを全インター
フェイス上で公開することになり、起動時に警告が出力されます。Windows では
非ループバックの prefix は管理者権限なし実行時に
`netsh http add urlacl` での予約も必要です。

利用可能な Prometheus スクレイプ設定は
[`examples/otel/prometheus.yml`](./examples/otel/prometheus.yml)
として同梱しています。OTel Collector 経由の push 経路 (`otelcol:9464`)
とホスト直 `/metrics` の pull 経路 (`host.docker.internal:9999`) の
2 ジョブを定義しているので、どちらの経路でもこの 1 ファイルで賄えます。
リスナーを既定以外の host:port にバインドした場合は `targets:` を
書き換えてください。

メトリクス名は OTLP 経路と完全一致するため (例:
`osvfs_s3_bytes_uploaded_bytes_total`、
`osvfs_s3_duration_milliseconds_bucket`)、後述の OTLP コレクター経路で
紹介しているクエリがそのまま流用できます。

#### ローカル Collector + Jaeger + Prometheus の例

OpenTelemetry Collector contrib + Jaeger v2 + Prometheus を組み合わせ
たサンプル一式を [`examples/otel/`](./examples/otel) に同梱しています。

| ファイル | 役割 |
| --- | --- |
| [`examples/otel/docker-compose.yml`](./examples/otel/docker-compose.yml) | `jaeger` / `prometheus` / `otelcol` を起動。4317 (OTLP gRPC) / 4318 (OTLP HTTP) / 16686 (Jaeger UI) / 9090 (Prometheus UI) を公開。 |
| [`examples/otel/otelcol.yaml`](./examples/otel/otelcol.yaml) | Collector パイプライン設定。OTLP receiver → Jaeger (traces) / Prometheus exporter on 9464 (metrics)。 |
| [`examples/otel/prometheus.yml`](./examples/otel/prometheus.yml) | Prometheus のスクレイプ設定。`otelcol:9464` (push 経路) と `host.docker.internal:9999` (`/metrics` 直接 pull 経路) の 2 ジョブを定義。 |

スタックを起動して OSVFS をマウント:

```powershell
cd examples/otel
docker compose up -d
osvfs --otlp-endpoint http://localhost:4317
```

- Jaeger UI → <http://localhost:16686>。`osvfs` サービスを選ぶと操作別の
  フレームグラフが見られます (`osvfs.s3` と `osvfs.projfs` 両方の source
  が同じ service.name 配下に表示されます)。
- Prometheus UI → <http://localhost:9090>。よく使うクエリ:
  - `histogram_quantile(0.95, sum by (le, operation) (rate(osvfs_s3_duration_milliseconds_bucket[5m])))`
    — S3 バックエンドの操作別 p95 レイテンシ。
  - `histogram_quantile(0.95, sum by (le, operation) (rate(osvfs_projfs_duration_milliseconds_bucket[5m])))`
    — ProjFS コールバックの操作別 p95 レイテンシ。同名 operation の
    S3 側の数値を引けば、コールバック内のオーバーヘッドが切り出せ
    ます。
  - `rate(osvfs_s3_errors_total[5m])` / `rate(osvfs_projfs_errors_total[5m])`
    — パイプライン別のエラー率。
  - `rate(osvfs_s3_bytes_uploaded_bytes_total[5m])` /
    `rate(osvfs_s3_bytes_downloaded_bytes_total[5m])` — スループット。

## ユーザー定義オブジェクトメタデータの往復保持

S3 のオブジェクトはユーザー定義ヘッダー (`x-amz-meta-*`) を任意個持てます。
タグや作者名、アプリ固有のマーカーなどがその例です。OSVFS はこれらの
ヘッダーを hydrate と再アップロードを跨いで保持し、ローカル編集によって
失われないようにします。

プレースホルダー作成時、OSVFS は `HeadObject` でバケット側のユーザー
メタデータを取得し、各エントリを `:osvfs-user-meta` という Windows NTFS の
**代替データストリーム (ADS)** にミラーします。フォーマットは UTF-8 で
1 行あたり `key=value` のプレーンテキストです。キーは S3 の wire と同じく
小文字に正規化されます。

ローカル編集をアップロードする際、OSVFS は同じ ADS を読み戻し、
`PutObject` (またはマルチパート) リクエストに `x-amz-meta-*` として
そのまま添付します。これによりローカル編集サイクル前後でヘッダーが
ビット単位で保たれます。新規作成のローカルファイルにはストリームが
存在しないため、従来どおりユーザーメタデータなしでアップロードされます。

ミラーされたメタデータは PowerShell から確認できます。

```powershell
# プレースホルダーに付与されたストリームを一覧
Get-Item C:\Users\you\OSVFS\meta\file.txt -Stream *

# メタデータを表示 (UTF-8、1 行 1 key=value)
Get-Content C:\Users\you\OSVFS\meta\file.txt -Stream osvfs-user-meta
```

AWS は `x-amz-meta-*` の合計サイズを **1 オブジェクトあたり 2 KiB** に
制限しています。OSVFS は同じ上限でアップロード前に検証を行い、ネットワーク
往復を待たず即座にエラーを返します。S3 が不透明な 400 を返すのを待つ
必要はありません。

