# 公開手順（Velopack）

このプロジェクトの公開は **`releases` ブランチへの push** をトリガーにして、GitHub Actions が Velopack のリリース作成と GitHub Releases へのアップロードを行います。

## 前提

- GitHub Actions が有効であること
- ワークフローが `contents: write` を持っていること
- `releases` ブランチへの push 権限があること

## 公開の流れ

1. `releases` ブランチを最新化します。
   ```bash
   git checkout releases
   git merge main
   ```

2. 必要な変更をコミットして push します。
   ```bash
   git push origin releases
   ```

3. `Velopack リリース作成` ワークフローが起動し、
   - アプリの公開ビルド
   - Velopack パッケージ作成
   - GitHub Releases へのアップロード
   を実行します。

## 補足

- タグはワークフロー内で作成しません。
- リリース版番号はワークフローの `RELEASE_VERSION`（実行番号ベース）で自動付与されます。
- ワークフロー定義は `.github/workflows/velopack-release.yml` にあります。
