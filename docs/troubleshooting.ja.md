# トラブルシューティング

[← README に戻る](../README.ja.md) · [English](./troubleshooting.md)

`osvfs doctor` セルフチェックと `osvfs lost-and-found` 退避ファイル
復元 CLI の使い方。

---

## doctor セルフチェック

マウントが起動しない (`StartVirtualizing failed`、"bucket not found"、
"AccessDenied"、認証情報の有効期限切れ など) ときは **まず
`osvfs doctor` を実行してください**。doctor は読み取り専用の環境
セルフチェックを順番に実行し、色付きのサマリーを出力します。

```powershell
# osvfs.toml の最初の [[mount]] からバケット / リージョン / プロファイルを引用
osvfs doctor

# CLI で完全に上書き (まだ設定ファイルがないとき向け)
osvfs doctor --bucket my-bucket --region ap-northeast-1 --profile prod

# LocalStack / MinIO 風
osvfs doctor --bucket my-bucket --endpoint-url http://localhost:4566
```

doctor が確認する項目は以下の順です。

1. **Windows ProjFS 機能 (`Client-ProjFS`)** — PrjFlt minifilter が
   登録されているかと、ユーザーモード DLL `ProjectedFSLib.dll` の
   存在を確認。`Get-WindowsOptionalFeature -FeatureName Client-ProjFS`
   と同等の判定です。
2. **`StartVirtualizing` スモークテスト** — 一時ディレクトリを作成し
   仮想化ルート化、`StartVirtualizing` を試行してから片付けます。
   レジストリ確認では拾えない「機能はインストール済みだが PrjFlt
   サービスが停止」「EDR / アンチウイルスがブロック」といった失敗を
   検出できます。
3. **AWS 認証情報の解決** — OSVFS プロファイル (`--profile`) または
   SDK チェーンから資格情報を解決し、ソース・アクセスキーの末尾 4
   桁・一時資格情報 (セッショントークン保持) かどうかを表示します。
4. **バケット到達性 (`HeadBucket`)** — `GetBucketLocation` を呼び
   出します。403 はリスト権限不足、404 はリージョン違いの可能性が
   高いです。
5. **バケットバージョニング** — OSVFS の衝突解決に必須です。
   未設定 / Suspended の場合は、有効化のための
   `aws s3api put-bucket-versioning` コマンドをそのまま提示します。

各行は先頭に `[OK]` / `[!!]` / `[XX]` / `[--]` (skipped) のマーカーが
付きます。プロセスは全項目が PASS なら **0**、いずれかが要対処なら
**2** で終了するため、起動スクリプトの先頭にも組み込めます。

```powershell
osvfs doctor --bucket $env:OSVFS_BUCKET; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
osvfs mount-all
```

`NO_COLOR=1` の指定や stdout のリダイレクトでは ANSI 制御コードを
省略するため、ログ収集や CI のスクレイピングでも素のテキストとして
読み込めます。

## 隔離された退避ファイルの確認・復元 (`lost-and-found`)

リモート側の変更が同期されていないローカル編集と衝突したとき、ウォッ
チャーは「リモート側が真」というポリシーに従ってローカルファイルを上書
きしますが、上書き直前にダーティなローカルコピーをマウント直下の
`.osvfs-lost+found` ディレクトリへ退避します。`osvfs lost-and-found`
サブコマンドを使うと、シェルだけでこれらの退避ファイルを確認し復元
できます。

```powershell
# 退避ファイル一覧 (新しい順)。元のパスとサイズも表示されます
osvfs lost-and-found list

# osvfs.toml に複数マウントがある場合は --name で 1 つを選択
osvfs lost-and-found list --name docs

# 退避ファイルと現在のリモートオブジェクトを diff
# テキスト: 外部 `git diff --no-index --color`
# バイナリ (先頭 8 KiB に NUL バイトあり): SHA-256 とサイズの比較
osvfs lost-and-found diff 20260510T123456789Z_docs%2Fnotes.md

# 退避ファイルを任意の場所にコピー
# (--target を省略するとカレントディレクトリへ元ファイル名で保存)
osvfs lost-and-found restore 20260510T123456789Z_docs%2Fnotes.md `
  --target C:\Users\you\Desktop\notes-recovered.md
```

`list` の 1 列目 (`FILENAME`) が `diff` / `restore` で渡す識別子です。
そのままコピー＆ペーストしてください。ファイル名は `<UTC タイムスタン
プ>_<URL エスケープ済みの元パス>` 形式なので、`list` は併せて復号後の
`ORIGINAL-PATH` も表示します。`restore` は既存ファイルを上書きしないた
め、強制上書きしたい場合は `--force` を追加してください。`diff` はテ
キスト比較に `git` を使用するため、`PATH` に `git` が無い場合はバイナ
リ用のサマリ表示にフォールバックします。

