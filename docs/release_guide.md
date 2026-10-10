# 公開手順（Velopack + Cloudflare R2）

公開は Windows の `scripts/release-local.ps1` で行います。SimplySign の証明書でアプリとインストーラを署名し、Velopack の win-x64 パッケージを Cloudflare R2 (`realtimetranslator-updates`) へアップロードします。自動更新は `https://rtt.kagayoi.com/releases.win-x64.json` を参照します。GitHub Actions は検証を担当し、ブランチへの push だけでは配布されません。

## 前提

- Windows x64、.NET 10 SDK と SimplySign Desktop が利用できること
- SimplySign にログインし、コード署名証明書の秘密鍵を使用できること。証明書が表示されるだけでは署名成功を確認できません
- `C:\Users\IMT\dev\Secret\secrets.json` の Cloudflare 設定で既存 R2 バケットへアクセスできること
- バージョン更新・公開は `/vava` スキルで行うこと

## 公開の流れ

1. `/vava` で次のバージョンを一度決め、変更履歴と版情報を更新します。
2. Release/x64 のビルドとテストを実行し、署名を含む配布物を `-SkipUpload` で事前確認します。
3. 検証済みの変更を main へ commit・push し、CI 成功を確認します。同じ commit を指す `release/X.Y.Z` ブランチを作成・push します。
4. `scripts/release-local.ps1` で署名・Velopack パッケージ作成・R2 公開を行います。
5. 配信マニフェストの版、配布物の署名・サイズ・ハッシュと公開 URL を確認します。

## 保存先と配信

作業出力はリポジトリ外の作業別 Temp に置きます。スクリプトの出力先・保持オプションは `Get-Help scripts/release-local.ps1 -Detailed` または冒頭のパラメーターを確認してください。既存のビルド出力や共有ツールを削除しません。

R2 への書き込みは `wrangler r2 object put --remote` を使用し、パッケージを先に、更新マニフェストを後にアップロードします。公開 URL の内容を照合し、古い内容が返るときだけ対象 URL のキャッシュを削除します。

配信元は Cloudflare R2 です。通常のリリースでは GitHub Releases へのアップロードを行いません。R2 の過去パッケージは最新2版を保持し、既存の旧ドメインによる更新経路を維持します。
