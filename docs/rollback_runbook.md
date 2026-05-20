# ロールバック手順書 (壊れた更新を出してしまった時の復旧手順)

rere レビュー P1 #3 (F-1) で「Velopack ロールバック手段の欠如」が指摘されたため、
この runbook を作成。 過去の事故 (v1.0.9 SelfUpdateWindow XAML バグ → v1.0.11 まで自動更新経路が
完全に詰まった、 v1.0.9-v1.0.10 字幕長文化) と同質の事故が再発した時の対処手順をまとめる。

## 0. 前提

- 配信元は **Cloudflare R2** (`realtimetranslator-updates` バケット、公開 URL `https://rtt.nephilim.jp`)。
- Velopack の `SimpleWebSource` は **`releases.win-x64.json` マニフェストを見て、そこに載っている最新バージョン**へ向かう。
  つまり「クライアントがどのバージョンを取得するか」は **R2 上の `releases.win-x64.json` の内容で決まる**。
- `UpdateService.cs` の `SimpleWebSource(baseUrl)` でユーザー側のバージョン指定はできない (`UpdateBaseUrl` は [JsonIgnore] ハードコード固定)。
- R2 は **過去バージョンの nupkg を削除せず保持** する (delta 更新のため)。よって直前の正常版 nupkg は R2 上に残っており、マニフェストを差し替えれば即座にそこへ戻せる。
- Velopack 仕様上 `--allowDowngrade` なしでは downgrade は拒否される。

## 1. 「壊れた v1.0.X を出した」と気づいた直後 (5 分以内)

優先度: **「v1.0.X 取得をこれ以上広げない」 → 「forward-fix の v1.0.(X+1) を準備」**

### 1-A. R2 のマニフェストを直前の正常版に戻す (即時実施)

クライアントが取得するバージョンは R2 上の `releases.win-x64.json` で決まるので、**そのマニフェストを
v1.0.(X-1) 時点のもので上書き** すれば、これから起動するユーザーは正常な v1.0.(X-1) を取得する。
v1.0.(X-1) の `releases.win-x64.json` は当時の CI 成果物 (artifact) または手元の `vpk pack` 出力から入手する。

```bash
# wrangler 認証 (OAuth) 済み前提。未認証なら `wrangler login`。
# 1. 直前の正常版 v1.0.(X-1) の releases.win-x64.json で R2 のマニフェストを上書き
wrangler r2 object put "realtimetranslator-updates/releases.win-x64.json" \
  --file=./good/releases.win-x64.json --remote

# 2. (任意) 壊れた v1.0.X の nupkg を R2 から削除して、マニフェストを手で復元する余地も断つ
wrangler r2 object delete "realtimetranslator-updates/RealTimeTranslator-1.0.X-win-x64-full.nupkg" --remote

# 3. 配信確認: マニフェストの最新バージョンが v1.0.(X-1) に戻ったか
curl -sS https://rtt.nephilim.jp/releases.win-x64.json | head -20
```

> ⚠️ 「マニフェストを v1.0.(X-1) に戻す」のは、 **これから起動するユーザーが正常に動く v1.0.(X-1) を
> 取得するため**。 既に v1.0.X をインストール済みのユーザーは Velopack downgrade 拒否仕様で
> 自動回復しないので、 別途 §2 の手順が必要。

### 1-B. v1.0.X を取得済みのユーザーへの個別案内

`docs/issue_response_template.md` (将来作成) または GitHub Issues で:

```
RealTimeTranslator v1.0.X に重大な不具合が見つかりました。
お手数ですが、 以下の手順で v1.0.(X-1) に戻してください:

1. https://rtt.nephilim.jp/RealTimeTranslator-win-x64-Setup.exe から
   Setup.exe をダウンロード (R2 のマニフェストを §1-A で v1.0.(X-1) に戻してあれば、これは正常版を指す)
2. 実行 → 既存 v1.0.X に上書きインストール
3. 起動を確認

v1.0.(X+1) で修正版を準備中です。 完了次第改めて案内します。
```

## 2. 「v1.0.X が起動不能で自動更新も動かない」ユーザーへの最終手段

- 現在のアップデート確認は **起動時に 1 回のみ** (Komorebi 互換、 v1.0.13 以降)。 起動できないユーザーは forward-fix の v1.0.(X+1) も自動取得できない。
- 起動できるユーザーは forward-fix の v1.0.(X+1) を起動時自動チェックで取得して自動回復する。
- 起動できないユーザーには **手動 Setup.exe 再インストール** が唯一の解。
- §1-A で R2 マニフェストを v1.0.(X-1) に戻していれば、 起動不能ユーザーは v1.0.(X-1) を Setup.exe で入れ直して
  以降の更新サイクルに復帰できる。

## 3. forward-fix の v1.0.(X+1) リリース

- `/vava` スキルで通常通り v+1 bump → CI 経由で R2 へリリース (`r2-upload` job が `releases.win-x64.json` を v1.0.(X+1) に更新)
- v1.0.(X+1) 配信後、混乱を避けるため §1-A で R2 に残した壊れた v1.0.X の nupkg を **完全 delete** する

```bash
# wrangler 認証 (OAuth) 済み前提
wrangler r2 object delete "realtimetranslator-updates/RealTimeTranslator-1.0.X-win-x64-full.nupkg" --remote
# delta があれば delta nupkg も削除
wrangler r2 object delete "realtimetranslator-updates/RealTimeTranslator-1.0.X-win-x64-delta.nupkg" --remote
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

- `.github/workflows/release.yml` (`r2-upload` job: Cloudflare R2 へ Velopack 成果物をアップロード)
- `src/RealTimeTranslator.UI/Services/UpdateService.cs` (`SimpleWebSource` + UpdateBaseUrl 検証 + UpdateDialog 呼出)
- rere レビュー P1 #3 (F-1) / 議題 3 (D-4 VelopackUpdateDialog.Avalonia 依存)
