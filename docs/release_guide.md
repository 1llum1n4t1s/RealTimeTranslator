# 公開手順（Velopack + Cloudflare R2）

公開は GitHub Actions の `.github/workflows/release.yml`（手動起動・Environment `release`）が Windows ランナーで `scripts/release-local.ps1` を実行して行います。[Sigilune](https://github.com/1llum1n4t1s/Sigilune) が Certum SimplySign のクラウド鍵でアプリとインストーラを署名し（SimplySign Desktop は不要）、Velopack の win-x64 パッケージを Cloudflare R2 (`realtimetranslator-updates`) へアップロードします。自動更新は `https://rtt.kagayoi.com/releases.win-x64.json` を参照します。配信前には `test` job が `ci.yml` を呼び、ビルドとテストが通らなければ署名・配信は行いません。`ci.yml` / `build.yml` は検証を担当し、ブランチへの push だけでは配布されません。同じスクリプトはローカルでも実行でき、検証や再開に使います。

## 前提

- CI: リポジトリの Environment `release` に Secrets `SIGILUNE_EMAIL` / `SIGILUNE_TOTP_URI` / `CLOUDFLARE_API_TOKEN` が登録されていること。これらを読むのは `release.yml` だけです
- ローカル: Windows x64、.NET 11 SDK と Sigilune が利用でき、`sigilune setup` で Certum アカウントを登録済みであること（実行ファイルは `SIGILUNE_EXE` → PATH → `%USERPROFILE%\.local\Sigilune\bin\sigilune.exe` の順に探します）
- 署名は Sigilune が署名後に有効性・署名者・タイムスタンプを検証します。ログインに失敗したら `sigilune status` で登録を確認し、署名を省略して続行しません
- 環境変数 `CLOUDFLARE_API_TOKEN`、無ければ `D:\Dev\Secret\secrets.json` の Cloudflare 設定で既存 R2 バケットへアクセスできること
- バージョン更新・公開は `/vava` スキルで行うこと

## 公開の流れ

1. `/vava` で次のバージョンを一度決め、変更履歴と版情報を更新します。
2. 開発工程の検証結果を引き継ぎます。`/vava` では開発テストや lint を再実行しません。
3. リリース入力の変更を main へ commit・push し、同じ commit を指す `release/X.Y.Z` ブランチを作成・push します。
4. `gh workflow run release.yml --ref release/X.Y.Z` で `release.yml` を起動し（`-f skip_upload=true` は配信せず署名済み成果物だけを Actions に残す）、`vigilia watch HEAD` で完了を確認します（REST枠を使わずGraphQLで取得し、同じ対象は全セッションでキャッシュ共有されるため複数セッションが同時に見てよい）。ワークフローは `scripts/release-local.ps1 -SkipCleanup` を一度実行し、Release/x64 の publish・署名・Velopack パッケージ作成・公開前検証・R2 公開を行います。スクリプトは最初の upload より前に、最終 nupkg 内の版・必須ファイル・main EXE の署名、Setup の署名、win-x64 manifest のサイズ・SHA256・SHA1 を照合し、不一致なら公開を停止します。
5. 公開前検証済みの成果物について、R2 書込みと固定 URL 全件のキャッシュパージ API の受付成功を確認します。公開後の配信内容取得・署名やハッシュの再照合・CDN 伝播待ちは行いません。

## 保存先と配信

この PC の作業出力は、OS の一時フォルダー配下にある `Directory.Build.local.props` の `RttWorkRoot`（`<TEMP>\Codex\RealTimeTranslator`）の下の `run-vava-X.Y.Z-<一意なID>` に置きます。ローカルの restore・publish・MSBuild 評価は `scripts/Dev.ps1` を通し、外部 props への差替えは拒否します。CI・他の checkout にはローカル専用設定を強制しません。`-WorkDirectory` は未作成の絶対パスを指定してください。

公開前検証の記録は同じ作業先の `release-verification.json` に保存します。`-SkipUpload` は公開せず署名済み成果物を保持する場合に使い、通常公開の事前工程として二重に実行しません。API の受付成功確認後の清掃は `PRUNLET.md` に従い、Prunlet CLI を CMD から直接実行します。スクリプト自身は作業先を削除しません。

R2 への書き込みは同梱 `scripts/R2Distribution.Common.ps1` の `Publish-R2Object`（PowerShell 7.2 以上）を使用し、パッケージを先に、更新マニフェストを後にアップロードします。バージョン付きの不変成果物を除いた固定 URL を全件パージし、API の受付成功を確認します。

配信元は Cloudflare R2 です。通常のリリースでは GitHub Releases へのアップロードを行いません。R2 の過去パッケージは最新2版を保持し、既存の旧ドメインによる更新経路を維持します。
