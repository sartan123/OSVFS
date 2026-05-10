# パフォーマンスチューニング

[← README に戻る](../README.ja.md) · [English](./tuning.md)

帯域制御、multipart アップロードのしきい値、リクエスト並列度、HTTP
トランスポート、リトライポリシー。各ノブは
[`osvfs.toml`](./configuration.ja.md) のキーと対応します。

---

## 帯域制御

`osvfs` は長時間バックグラウンドで動くプロセスのため、大きな hydrate や
アップロードが回線を食い潰すと同居アプリケーションのレスポンスを劣化させ
ます。[`osvfs.toml`](#設定ファイル) の `bandwidth-up` / `bandwidth-down`
で双方向独立に上限を指定できます。

```toml
bucket         = "my-bucket"
root-folder    = "C:/Users/you/OSVFS"
bandwidth-up   = "5M"       # アップロードを 5 MiB/s に制限
bandwidth-down = "10M"      # ダウンロードを 10 MiB/s に制限
```

書式は rclone の `--bwlimit` に倣っています。サフィックス無しはバイト/秒、
`K` / `M` / `G` はそれぞれ KiB/s / MiB/s / GiB/s を意味します
(`5M` = 5 MiB/s)。キーを指定しないか `0` を指定すると、その方向は無制限
です。アップロードペイロードのストリームとダウンロードレスポンスストリー
ムの両方をトークンバケットで律速するため、`TransferUtility` の multipart
ワーカーもオンデマンド hydrate も同じ上限の下で動きます。

## multipart アップロードのチューニング

`osvfs` は `multipart-threshold` 以上のアップロードを S3 multipart
パスに振り分け、ペイロードを `multipart-part-size` のチャンクに分割
して `TransferUtility` が並列にアップロードします。既定値 (閾値 16 MiB
/ パートサイズ 5 MiB) は AWS SDK v4 の `MinSizeBeforePartUpload` 既定値
に揃えています。次の場面では明示的なチューニングが効きます。

| シナリオ | 推奨設定 | 理由 |
| --- | --- | --- |
| 太い回線 / 大きなファイル中心 | `multipart-threshold = "64M"`、`multipart-part-size = "64M"` | 大きなパートにすればリクエスト単位のオーバーヘッドが薄まり、複数 GiB のファイルでもパート数を抑えられる |
| 小さなファイルが大量 | `multipart-threshold = "16M"` (パートは 5M のまま) | 小さなファイルで multipart のセッション交渉を省き、単一 PUT のほうが速い領域を広げる |
| 不安定な回線 | 既定値 | パートが小さいほどリトライ時の再送量も小さい |

S3 はパートサイズに 3 つの上限を設けています。いずれを破っても
`osvfs` は起動を拒否し、サーバ側も Complete 時にアップロードを失敗
させます。

- `multipart-part-size` は **5 MiB (`5M`) 以上**であること。最終
  パート以外でこれより小さいパートは S3 が拒否します。
- `multipart-part-size` は **5 GiB (`5G`) 以下**であること。これを
  超えるパートサイズは S3 がサポートしません。
- 1 つの multipart アップロードは **最大 10 000 パート**です。した
  がって 1 オブジェクトの最大サイズは `パートサイズ × 10 000` (16 MiB
  パートなら最大 160 GiB、64 MiB パートなら最大 640 GiB) です。
  扱う最大ファイルサイズが収まるよう、十分大きなパートサイズを選んで
  ください。

## リクエスト並列度のチューニング

`osvfs` は方向ごとの S3 同時呼び出し数を上限で抑え、ハイドレーションや
バックグラウンドアップロードのバーストが SDK の HTTP プールやバケット
を圧迫しないようにしています。`osvfs.toml` の独立した 3 つのキーで上限
を制御します。

| キー | 既定値 | 制限対象 |
| --- | --- | --- |
| `max-concurrent-uploads` | `4` | 同時に進行する `UploadAsync` 呼び出し数。1 回の保存で 1 パーミット消費。SDK が内部でマルチパートに分割しても 1 回としてカウントします |
| `max-concurrent-downloads` | `8` | 同時に進行する `ReadRangeAsync` 呼び出し数 (ProjFS のハイドレーション 1 件につき 1 つ) |
| `max-multipart-parts` | `10` | **1 回の `UploadAsync` 内で**並列アップロードするマルチパートのパート数。`TransferUtilityConfig.ConcurrentServiceRequests` に伝播します |

外側のゲート (`max-concurrent-uploads`) と内側のゲート (`max-multipart-parts`)
は直交した制限です。瞬間的に S3 へ同時に飛ぶ部品 PUT 数は最大で
`max-concurrent-uploads × max-multipart-parts` になります。HTTP 接続プ
ールはこの値に余裕を持たせるため
`max(max-concurrent-uploads, max-concurrent-downloads) × 2` に設定し、
接続枯渇がボトルネックにならないようにしています。

| シナリオ | 推奨設定 | 理由 |
| --- | --- | --- |
| 太い回線、複数 GiB のファイル | `max-concurrent-uploads = 2`, `max-multipart-parts = 16` | 同時に走るアップロードは少なくし、各アップロードの内部並列度を上げて単一ファイルのスループットを最大化 |
| 小さなファイル多数 (ビルド成果物・写真など) | `max-concurrent-uploads = 8`, `max-multipart-parts = 4` | 多数の小さな PUT を並列実行。小ファイルではアップロード内の並列化はほぼ無駄 |
| 不安定な上流 / 5xx 多発 | `max-concurrent-uploads = 2`, `max-concurrent-downloads = 4` | バーストを抑え、SDK の Adaptive リトライのトークンバケットに余裕を残す |
| TPS クォータが厳しいバケット | 全項目を半分に | 1 秒あたりのリクエスト数を抑え `RequestLimitExceeded` を回避 |

```toml
bucket                    = "my-bucket"
root-folder               = "C:/Users/you/OSVFS"
max-concurrent-uploads    = 4
max-concurrent-downloads  = 8
max-multipart-parts       = 10
```

3 つのキーはいずれも 1 以上である必要があります。0 や負の値が指定された
場合 OSVFS は起動時に拒否します。

## HTTP トランスポートのチューニング

OSVFS は AWS SDK に独自の `HttpClientFactory` を渡し、内部の
`SocketsHttpHandler` を「コネクション無期限保持・プール上限なし」と
いうフレームワーク既定値ではなく、運用上安全な値に明示固定します。
ファクトリはバックエンドごとに 1 つだけ生成され、マウントの寿命と同じ
だけ SDK と共有されるため、単一の `AmazonS3Client` でソケットリークや
DNS の固定化を起こさずに長時間の高スループット通信を継続できます。

| 設定 | 値 | 目的 |
| --- | --- | --- |
| `PooledConnectionLifetime` | `5 分` | プールされた TCP コネクションの寿命を制限し、DNS 変更 (S3 エンドポイントのローテーション、VPC エンドポイントのフェイルオーバーなど) をプロセス再起動なしで反映できるようにします |
| `PooledConnectionIdleTimeout` | `2 分` | 一定時間アイドルだったコネクションを閉じ、バーストが収まった後にホストが速やかにソケットを解放できるようにします |
| `MaxConnectionsPerServer` | `max(uploads, downloads) × 2` | SDK の `AmazonS3Config.MaxConnectionsPerServer` と同じ値。並列度設定から導出しているため、束縛要因はあくまでも方向別ゲートであり、コネクション枯渇ではないことを保証します |
| `EnableMultipleHttp2Connections` | `true` | 単一の HTTP/2 接続が `SETTINGS_MAX_CONCURRENT_STREAMS` を使い切った際に、追加の接続をプールが開けるようにします |
| HTTP/2 への昇格 | AWS エンドポイントで有効 | 送出リクエストを `HttpVersion.Version20` + `RequestVersionOrLower` ポリシーで発行するため、HTTP/1.1 しか話さないエンドポイント (LocalStack、MinIO) は透過的に HTTP/1.1 へネゴシエーションダウンします。`endpoint-url` が設定されている場合は無効です |

これらの値は `osvfs.toml` から変更できません。サポート対象のすべての
構成で適切な値であり、マウント単位でチューニングする運用上の理由が
存在しないためです。AWS 以外でコネクションのセマンティクスが大きく
異なるオブジェクトストアに対してフォークする場合のみ、コードを直接
書き換えてください。

## リトライポリシー

オブジェクトストアの一時的な失敗は AWS SDK のリトライパイプラインに委
ねており、OSVFS は `RetryMode.Adaptive` (標準の指数バックオフに加えて、
サービスが過負荷を返した際に後続リクエストを抑止するクライアントサイド
スロットリング用トークンバケット) と `MaxErrorRetry = retry-max-attempts − 1`
を設定します。リトライ対象のエラー分類は SDK 組み込みのものを利用しま
す。

| エラー | リトライ対象? | 補足 |
| --- | --- | --- |
| HTTP 5xx (`500`, `502`, `503`, `504` …) | はい | サーバ / ロードバランサ側エラー。SDK が一時的とみなす |
| HTTP 408 `Request Timeout` | はい | サーバ側タイムアウト。SDK がバックオフ付きでリトライ |
| `Throttling` / `ThrottlingException` / `RequestThrottled*` / `TooManyRequestsException` / `ProvisionedThroughputExceededException` / `RequestLimitExceeded` / `SlowDown` | はい | AWS のスロットリング系エラー。Adaptive モードは続くリクエストもトークンバケットで抑止 |
| `RequestTimeout` / ネットワークエラー / 接続切断 | はい | ローカルのソケット / コネクションエラー |
| HTTP 4xx (408 を除く `400`, `401`, `403`, `404`, `409`, `412` …) | いいえ | 呼び出し側起因のエラー (不正リクエスト / 権限不足 / 未存在)。即座に伝播 |
| `OperationCanceledException` / `TaskCanceledException` | いいえ | キャンセルはリトライせずに伝播 |

スケジュールは SDK 側が所有しています: `MaxErrorRetry` 回までジッタ付き
の指数バックオフでリトライします。`retry-max-attempts = 1` を指定すると
SDK のリトライは 0 回になり、初回呼び出しのみが実行されます。SDK の
`TransferUtility` はマルチパートアップロードの個別パートを内部でリトラ
イするため、`retry-max-attempts = 3` 設定でも数 GiB のアップロード全体
を再送せずに済みます (失敗したパートだけ最大 3 回まで再送)。

```toml
bucket             = "my-bucket"
root-folder        = "C:/Users/you/OSVFS"
retry-max-attempts = 5         # 試行回数 5 回 (初回 1 + リトライ 4)
```

