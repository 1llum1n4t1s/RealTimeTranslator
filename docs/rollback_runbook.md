# ロールバック手順書 (壊れた更新を出してしまった時の復旧手順)

rere レビュー P1 #3 (F-1) で「Velopack ロールバック手段の欠如」が指摘されたため、
この runbook を作成。 過去の事故 (v1.0.9 SelfUpdateWindow XAML バグ → v1.0.11 まで自動更新経路が
完全に詰まった、 v1.0.9-v1.0.10 字幕長文化) と同質の事故が再発した時の対処手順をまとめる。

## 0. 前提

- Velopack は **latest release の `RELEASES-*` を見る** 設計なので、 ユーザーは強制的に
  「現在 GitHub Releases で latest になっているもの」へ向かう。
- `UpdateService.cs` の `GithubSource(..., accessToken: string.Empty, prerelease: false)` で
  ユーザー側でバージョン指定はできない。
- `.github/workflows/release.yml` の Cleanup ジョブは「latest 3 件以外を削除」 (release.yml:89 の `Cleanup old releases (keep latest 3)`、 jq クエリ `.[3:]` 経由)。
  これにより**直近 3 件の既知バージョンの nupkg を保持** している。
- Velopack 仕様上 `--allowDowngrade` なしでは downgrade は拒否される。

## 1. 「壊れた v1.0.X を出した」と気づいた直後 (5 分以内)

優先度: **「v1.0.X 取得をこれ以上広げない」 → 「forward-fix の v1.0.(X+1) を準備」**

### 1-A. v1.0.X release を latest から外す (即時実施)

```bash
# 1. v1.0.X の release を取得済みかつ起動不能な場合、 v1.0.(X-1) を再度 latest に戻す
gh release edit 1.0.(X-1) --latest

# 2. v1.0.X の release を draft に戻す or delete する
# (draft なら revert 可能、 delete はやり直しが効かないので慎重に)
gh release edit 1.0.X --draft

# velopack-1.0.X 側 (vpk pack のメタ) も同様
gh release edit velopack-1.0.X --draft
```

> ⚠️ 「latest を v1.0.(X-1) に戻す」のは、 **これから起動するユーザーが正常に動く v1.0.(X-1) を
> 取得するため**。 既に v1.0.X をインストール済みのユーザーは Velopack downgrade 拒否仕様で
> 自動回復しないので、 別途 §2 の手順が必要。

### 1-B. v1.0.X を取得済みのユーザーへの個別案内

`docs/issue_response_template.md` (将来作成) または GitHub Issues で:

```
RealTimeTranslator v1.0.X に重大な不具合が見つかりました。
お手数ですが、 以下の手順で v1.0.(X-1) に戻してください:

1. https://github.com/1llum1n4t1s/RealTimeTranslator/releases/tag/1.0.(X-1) から
   `RealTimeTranslator-win-Setup.exe` をダウンロード
2. 実行 → 既存 v1.0.X に上書きインストール
3. 起動を確認

v1.0.(X+1) で修正版を準備中です。 完了次第改めて案内します。
```

## 2. 「v1.0.X が起動不能で自動更新も動かない」ユーザーへの最終手段

- 現在のアップデート確認は **起動時に 1 回のみ** (Komorebi 互換、 v1.0.13 以降)。 起動できないユーザーは forward-fix の v1.0.(X+1) も自動取得できない。
- 起動できるユーザーは forward-fix の v1.0.(X+1) を起動時自動チェックで取得して自動回復する。
- 起動できないユーザーには **手動 Setup.exe 再インストール** が唯一の解。
- §1-A で latest を v1.0.(X-1) に戻していれば、 起動不能ユーザーは v1.0.(X-1) を Setup.exe で入れ直して
  以降の更新サイクルに復帰できる。

## 3. forward-fix の v1.0.(X+1) リリース

- `/vava` スキルで通常通り v+1 bump → CI 経由でリリース
- v1.0.(X+1) リリース後に §1-A で draft 化した v1.0.X release を **完全 delete** する
  (混乱を避けるため)

```bash
gh release delete 1.0.X --cleanup-tag --yes
gh release delete velopack-1.0.X --cleanup-tag --yes
```

## 4. 予防策 (議論中)

rere レビュー Agent F (Day-2 Ops) で提案された予防策:

1. **canary 機構**: リリース直後 24 時間は自動更新を段階的に有効化 (canary 5% → 50% → 100%)
   → `UpdateSettings.Channel = "stable" | "canary"` を導入
2. **起動 self-test**: アプリ起動時に DI 構築失敗 / WS 接続初回失敗連続 N 回などを検出して
   「safe mode 起動 + 過去版ダウングレード手順表示」
3. **AllowDowngrade 機構**: Velopack の AllowDowngrade を UI ボタンから一時有効化
4. **Authenticode 署名**: 配信物の改竄検知 (rere A3-003 議題化、 別途検討)

## 5. 過去のインシデント記録

| 事故 | 影響期間 | 復旧方法 |
|------|---------|---------|
| v1.0.9 SelfUpdateWindow XAML 型解決バグ | v1.0.9 → v1.0.11 (3 バージョン分自動更新経路詰まり) | v1.0.11 を手動 DL 案内 |
| v1.0.9-v1.0.10 字幕長文化 | v1.0.9 → v1.0.10 で UX 問題 | 自動更新でカバー (起動はできた) |

## 関連

- `.github/workflows/release.yml` (cleanup latest 3 件保持の設定)
- `src/RealTimeTranslator.UI/Services/UpdateService.cs` (FeedUrl 検証 + UpdateDialog 呼出)
- rere レビュー P1 #3 (F-1) / 議題 3 (D-4 VelopackUpdateDialog.Avalonia 依存)
