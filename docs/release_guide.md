# 公開手順（Velopack + Cloudflare R2）

公開は Windows の `scripts/release-local.ps1` で行います。SimplySign の証明書でアプリとインストーラを署名し、Velopack の win-x64 パッケージを Cloudflare R2 (`realtimetranslator-updates`) へアップロードします。自動更新は `https://rtt.kagayoi.com/releases.win-x64.json` を参照します。GitHub Actions は検証を担当し、ブランチへの push だけでは配布されません。

## 前提

- Windows x64、.NET 10 SDK と SimplySign Desktop が利用できること
- SimplySign にログインし、コード署名証明書の秘密鍵を使用できること。証明書が表示されるだけでは署名成功を確認できません
- `D:\Dev\Secret\secrets.json` の Cloudflare 設定で既存 R2 バケットへアクセスできること
- バージョン更新・公開は `/vava` スキルで行うこと

## 公開の流れ

1. `/vava` で次のバージョンを一度決め、変更履歴と版情報を更新します。
2. 開発工程の検証結果を引き継ぎます。`/vava` では開発テストや lint を再実行しません。
3. リリース入力の変更を main へ commit・push し、同じ commit を指す `release/X.Y.Z` ブランチを作成・push します。
4. `scripts/release-local.ps1 -SkipCleanup` を一度実行し、Release/x64 の publish・署名・Velopack パッケージ作成・公開前検証・R2 公開を行います。スクリプトは最初の upload より前に、最終 nupkg 内の版・必須ファイル・main EXE の署名、Setup の署名、win-x64 manifest のサイズ・SHA256・SHA1 を照合し、不一致なら公開を停止します。
5. 配信マニフェストの版、配布物の署名・サイズ・ハッシュと公開 URL を確認します。

## 保存先と配信

この PC の作業出力は `D:\Codex\RealTimeTranslator\run-vava-X.Y.Z-<一意なID>` に置きます。ローカルの restore・publish・MSBuild 評価は `scripts/Dev.ps1` を通し、外部 props への差替えは拒否します。CI・他の checkout にはローカル専用設定を強制しません。`-WorkDirectory` は未作成の絶対パスを指定してください。

公開前検証の記録は同じ作業先の `release-verification.json` に保存します。`-SkipUpload` は公開せず署名済み成果物を保持する場合に使い、通常公開の事前工程として二重に実行しません。公開確認後の清掃は `PRUNLET.md` に従い、Prunlet CLI を CMD から直接実行します。スクリプト自身は作業先を削除しません。

R2 への書き込みは `wrangler r2 object put --remote` を使用し、パッケージを先に、更新マニフェストを後にアップロードします。公開 URL の内容を照合し、古い内容が返るときだけ対象 URL のキャッシュを削除します。

配信元は Cloudflare R2 です。通常のリリースでは GitHub Releases へのアップロードを行いません。R2 の過去パッケージは最新2版を保持し、既存の旧ドメインによる更新経路を維持します。
