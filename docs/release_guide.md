# 公開手順（Velopack + Cloudflare R2）

このプロジェクトの公開は **`release/**` ブランチへの push** をトリガーにして、GitHub Actions が Velopack パッケージを作成し、**Cloudflare R2 (`realtimetranslator-updates` バケット)** へアップロードします。自動更新クライアント (`UpdateService` の `SimpleWebSource`) は `https://rtt.nephilim.jp/releases.win-x64.json` を参照して更新を取得します。

## 前提

- GitHub Actions が有効であること
- GitHub Secrets に `CLOUDFLARE_API_TOKEN` (Workers R2 Storage / Edit) と `CLOUDFLARE_ACCOUNT_ID` が登録済みであること
- `release/**` ブランチへの push 権限があること
- バージョン更新・リリースは `/vava` スキル経由で行うこと（手動でのバージョン書き換えは行わない）

## 公開の流れ

1. `/vava` でバージョンを上げ、`release/X.Y.Z` ブランチを作成・push します（`/vava` が一括処理）。

2. `Release` ワークフロー (`.github/workflows/release.yml`) が起動し、
   - アプリの公開ビルド (`build.yml`、self-contained win-x64)
   - Velopack パッケージ作成 (`velopack.yml`、`vpk pack --channel win-x64`)
   - **Cloudflare R2 へのアップロード** (`r2-upload` job、wrangler CLI)
   - 配信確認 (`releases.win-x64.json` の HTTP 200 検証)
   を実行します。

## 補足

- 配信元は **Cloudflare R2 単独**。GitHub Releases への継続的なアップロードは行いません。
- 旧 `GithubSource` クライアント救済のための「踏み台」(R2 対応版を GitHub Releases に 1 つだけ publish) は移行作業時に一度だけ実施済みで、以降の通常リリースでは不要です。
- R2 上の同名 `releases.win-x64.json` は冪等に上書きされ、過去バージョンの nupkg は delta 更新のため残します。
- ワークフロー定義は `.github/workflows/release.yml`（orchestrator）/ `build.yml` / `velopack.yml` にあります。
