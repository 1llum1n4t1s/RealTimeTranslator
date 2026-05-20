# RealTimeTranslator

Windows 向けのリアルタイム字幕翻訳デスクトップアプリです。指定したプロセス（ゲーム / 動画プレーヤー / 配信視聴クライアント等）の音声を WASAPI Process Loopback でキャプチャし、OpenAI Realtime Translate API（WebSocket）にストリーミング送信して、翻訳字幕を半透明オーバーレイで表示します。

> ⚠️ **データ送信について**: 本アプリは BYOK（Bring Your Own Key）モデルです。キャプチャした音声はあなたが指定した OpenAI アカウントの Realtime API に送信され、課金もあなたの OpenAI アカウントから発生します。完全なローカル処理ではありません。

## 概要

- **プロセス単位の音声キャプチャ**: WASAPI Process Loopback API で対象プロセスの音声のみを取得します（Windows 11 / Windows Server 2022 以降）。
- **ストリーミング翻訳**: OpenAI Realtime Translate API (`gpt-realtime-translate`) に PCM16 / 24kHz の音声をストリーミング送信し、`response.output_audio_transcript.delta` / `.done` イベントで翻訳テキストを逐次受け取ります。
- **半透明オーバーレイ字幕**: 透過・最前面・クリック透過対応の Avalonia ウィンドウに字幕を表示します。フォント・色・表示時間・行数を設定で調整可能。
- **日本語フォント同梱**: 字幕に最適な日本語フォント 5 種類 (IBM Plex Sans JP / Noto Sans JP / LINE Seed JP / Zen Maru Gothic / M PLUS Rounded 1c) を OFL 1.1 ライセンスで同梱しており、 OS にフォントが入っていなくても綺麗な字幕表示が可能です。
- **背景色 12 種類**: 字幕の見やすさをシーンに合わせて選べる多色背景 (黒 3 段階 / 白 / 灰 / 濃紺 / 濃緑 / 濃赤 / 茶 / 紫 / 透明 等)。 枠色は背景の輝度から自動派生して常に視認性を保ちます。
- **発話の自然な区切り**: 句点 (`。！？.!?`) でセグメントを区切り、 句点が来ない長文に対しては読点フォールバック (100 文字超で「、」分割) で字幕が無限成長する UX バグを防ぎます。
- **VAD ゲート + コスト見える化**: Silero VAD (ONNX, 16kHz/512 サンプル) で人の声らしさを判定し、 無音区間の送信を抑制して OpenAI Realtime API の課金を削減します。 プリセット (Balanced / 頭尻尾重視 / 節約重視 / Custom) を用意。 セッションの経過時間 / 推定入力トークン / 推定コスト (USD) / VAD 節約秒数をリアルタイム表示します。
- **翻訳ログ**: 確定字幕を `%APPDATA%\RealTimeTranslator\logs\translations\TranslationLog_YYYYMMDD.tsv` に TSV 形式で永続化し、 ADV ゲーム風の会話ログ画面で閲覧できます (保持期間 0=無制限 / 7 / 30 / 90 / 180 / 365 日を設定で選択)。
- **自動アップデート**: Velopack による Cloudflare R2 経由 (`SimpleWebSource`) の自動更新に対応します。起動時に 1 回だけ自動チェック (30 秒タイムアウト)、 周期チェックなし、 「このバージョンを無視」で次回起動時もスキップ可能、 手動チェックはバージョンタブの「更新の確認」ボタンから。
- **設定ホットリロード**: `settings.json` の変更は `IOptionsMonitor` で検出し再起動なしで反映されます。起動時には設定値の妥当性検証 (リスト外のフォント名 / 色 / サイズ等を既定値に矯正) を実行し、設定 UI が未選択状態になる UX バグを防ぎます。

## システム構成

```
プロセス音声 (WASAPI Loopback)
  ↓ 16kHz mono float32
TranslationPipelineService
  ↓ resample 16k→24k + Float32→PCM16
OpenAIRealtimeClient (WebSocket / wss://api.openai.com/v1/realtime/translations)
  ↓ response.output_audio_transcript.delta / .done
SubtitleGenerated event
  ↓
OverlayViewModel → 半透明オーバーレイに描画
```

## 動作環境

| 項目 | 要件 |
| --- | --- |
| OS | Windows 11 / Windows Server 2022（Build 20348 以降。WASAPI Process Loopback 必須） |
| ランタイム | .NET 10 (`net10.0-windows10.0.20348.0`) |
| アーキテクチャ | x64 のみ |
| 必須 | OpenAI API キー（Realtime API へのアクセス権限つき） |

## 主要依存ライブラリ

| ライブラリ | 目的 |
| --- | --- |
| [Avalonia 12](https://avaloniaui.net/) | UI フレームワーク（Fluent テーマ） |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | `[ObservableProperty]` / `[RelayCommand]` 等の MVVM ユーティリティ |
| [1llum1n4t1s.NAudio](https://www.nuget.org/packages/1llum1n4t1s.NAudio) | NAudio フォーク（WASAPI Process Loopback 拡張） |
| [SuperLightLogger](https://www.nuget.org/packages/SuperLightLogger) | 軽量ロガー |
| [Velopack](https://github.com/velopack/velopack) | 自動更新（Cloudflare R2 / `SimpleWebSource` バックエンド） |
| [VelopackUpdateDialog.Avalonia](https://www.nuget.org/packages/VelopackUpdateDialog.Avalonia) | 自動更新ダイアログ UI (Avalonia + Velopack 統合、 Komorebi 流挙動を採用) |
| [Microsoft.ML.OnnxRuntime](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime) | Silero VAD (ONNX) の推論ランタイム |
| [Microsoft.Extensions.{DI, Options, Configuration, Logging}](https://learn.microsoft.com/dotnet/core/extensions/) | DI / 設定 / オプション |

## セットアップ

1. リポジトリをクローン
   ```bash
   git clone https://github.com/1llum1n4t1s/RealTimeTranslator.git
   cd RealTimeTranslator
   ```

2. ソリューションを開くかコマンドラインでビルド
   - VS で開く場合: `RealTimeTranslator.slnx` を開く
   - CLI でビルド: 後述の [Build & Test](#build--test) を参照

3. OpenAI API キーを準備
   - [OpenAI Platform](https://platform.openai.com/) でアカウント作成・課金設定・API キーを発行
   - Realtime API（`gpt-realtime-translate`）へのアクセス権限があることを確認

4. 初回起動後、設定画面の「OpenAI Realtime」セクションに API キーを入力

## Build & Test

```pwsh
# Restore + Build (Platform は常に x64 を指定)
rtk dotnet restore RealTimeTranslator.slnx
rtk dotnet build RealTimeTranslator.slnx -c Release -p:Platform=x64

# ユニットテストのみ実行
rtk dotnet test RealTimeTranslator.slnx -c Release -p:Platform=x64 --filter "TestCategory!=Integration"

# アプリ実行
rtk dotnet run --project src/RealTimeTranslator.UI -c Release -p:Platform=x64

# 自己完結発行 (win-x64)
rtk dotnet publish src/RealTimeTranslator.UI -c Release -r win-x64 --self-contained
```

> プラットフォームは常に `x64` を指定してください（x86 / ARM はサポートしていません）。

## 設定（settings.json）

初回起動時、`%APPDATA%\RealTimeTranslator\settings.json`（Roaming AppData）にコピーされる設定ファイルを編集するか、アプリ内の設定画面から変更できます。

| セクション | 主な項目 |
| --- | --- |
| `OpenAIRealtime` | `ApiKey`（BYOK）、`OutputLanguage`（`ja`/`en`/`zh`/`ko` 等 15 言語）、`Model`（既定: `gpt-realtime-translate`）、`Endpoint`、`ReconnectDelayMs`、`MaxReconnectAttempts` |
| `Overlay` | `FontFamily`（既定 `IBM Plex Sans JP` / システム 4 種 + 同梱 5 種選択可）、`FontSize`、`PartialTextColor` / `FinalTextColor`（7 色）、`BackgroundColor`（12 色）、`DisplayDuration`、`FadeOutDuration`、`BottomMarginPercent`、`MaxLines` |
| `AudioCapture` | `SampleRate`（既定 16000）、`EnableVad`（既定 true）、`VadPreset`（`Balanced` / `PrioritizeEdges` / `AggressiveSavings` / `Custom`）、`VadThreshold` / `VadPreRollMs` / `VadHangoverMs`（Custom 時の詳細値）、`AutoPauseOnSilenceSec`（無音継続で自動 Pause、 既定 0=無効） |
| `Update` | `Enabled`（既定 true）、`IgnoredTagName`（「このバージョンを無視」で押されたタグを永続化、 次回起動時の自動チェックでスキップ）。**配信元 URL はセキュリティ上ハードコード固定**（`UpdateBaseUrl`、 `settings.json` からは変更不可） |
| `TranslationLog` | `RetentionDays`（翻訳ログ保持日数、 既定 0=無制限 / 7 / 30 / 90 / 180 / 365 から選択） |

> ⚠️ **API キーの取り扱い**: `ApiKey` は Windows DPAPI（CurrentUser scope）で暗号化されたうえで `settings.json` に `dpapi:` プレフィックス付き base64 として保存されます。別ユーザー / 別 PC では復号できないため、他環境への持ち出しはできません。

### 同梱フォント

字幕用に最適化された日本語フォント 5 種類を OFL (SIL Open Font License) 1.1 ライセンスで同梱しています。 設定画面の「フォント」ドロップダウンで切り替え可能です。

| 名称 | 配布元 | 特徴 |
| --- | --- | --- |
| IBM Plex Sans JP | [google/fonts/ofl/ibmplexsansjp](https://github.com/google/fonts/tree/main/ofl/ibmplexsansjp) | **既定**。 IBM のコーポレートフォント、 視認性高め |
| Noto Sans JP (Variable) | [google/fonts/ofl/notosansjp](https://github.com/google/fonts/tree/main/ofl/notosansjp) | Google × Adobe の標準的日本語フォント、 漢字字数最多 |
| LINE Seed JP | [google/fonts/ofl/lineseedjp](https://github.com/google/fonts/tree/main/ofl/lineseedjp) | LINE のブランドフォント、 親しみやすい角丸 |
| Zen Maru Gothic | [google/fonts/ofl/zenmarugothic](https://github.com/google/fonts/tree/main/ofl/zenmarugothic) | 柔らかい丸ゴシック、 長文も読みやすい |
| M PLUS Rounded 1c | [google/fonts/ofl/mplusrounded1c](https://github.com/google/fonts/tree/main/ofl/mplusrounded1c) | 丸みのあるモダンサンセリフ |

ライセンス本文は同梱の [`src/RealTimeTranslator.UI/Assets/Fonts/LICENSES/OFL.txt`](src/RealTimeTranslator.UI/Assets/Fonts/LICENSES/OFL.txt) を参照してください。 システムフォント（Yu Gothic UI / Meiryo UI / Segoe UI / MS Gothic）も引き続き選択できます。

### 自動更新

配信元は **Cloudflare R2**（バケット `realtimetranslator-updates`、 公開 URL `https://rtt.nephilim.jp`）です。 クライアントは Velopack の `SimpleWebSource` で `https://rtt.nephilim.jp/releases.win-x64.json` を取得します。 起動時に 1 回だけチェックし、 新版があればアップデートダイアログを表示します。 DNS 半切断 / TCP ハング対策として **30 秒タイムアウト** で打ち切り、 UpToDate / Failed / 無視タグ一致なら Window を開きません (UX を邪魔しない設計、 Komorebi 互換)。

- **手動チェック**: バージョンタブの「更新の確認」ボタン
- **「このバージョンを無視」**: ダイアログから選択すると `Update.IgnoredTagName` に保存され、 次回起動時の自動チェックで同タグが検出されてもダイアログをスキップ
- **周期チェック**: なし (起動時のみ。長時間起動アプリでもダイアログが不意に出ない)
- **旧バージョン (GitHub Releases 配信時代) からの移行**: 旧クライアントは GitHub Releases に残した「踏み台」リリース (R2 対応版) を経由して 1 度更新すれば、 以降は自動的に R2 から更新を受け取ります

## プロジェクト構成

```
src/
├── RealTimeTranslator.Core/             # インターフェース / モデル / Infrastructure サービス（UI 非依存）
│   ├── Interfaces/                      # IAudioCaptureService, IOpenAIRealtimeClient, ITranslationPipelineService, IUpdateService
│   ├── Models/                          # AppSettings, TranslationLogEntry, SubtitleItem, UpdateNotifications
│   └── Services/                        # AudioCaptureService, AudioFormatConverter, OpenAIRealtimeClient,
│                                        #   TranslationPipelineService（パイプラインオーケストレーター）,
│                                        #   SileroVadDetector, CostEstimator, TranslationLogService, SettingsService, LoggerService
├── RealTimeTranslator.UI/               # Avalonia デスクトップアプリ
│   ├── Views/                           # MainWindow（サイドバー 6 タブ）, OverlayWindow (axaml + cs)
│   ├── ViewModels/                      # MainViewModel, OverlayViewModel, SettingsViewModel, TranslationLogViewModel
│   ├── Services/                        # UpdateService, JapaneseUpdateDialogStrings
│   └── App.axaml.cs / Program.cs        # DI 構成 / エントリポイント
└── RealTimeTranslator.Tests/            # MSTest ユニットテスト
```

## リリース・公開手順

- リリースは `release/x.y.z` ブランチに push すると GitHub Actions が起動し、Velopack で nupkg を作成して **Cloudflare R2**（バケット `realtimetranslator-updates`、 公開 URL `https://rtt.nephilim.jp`）にアップロードします（orchestrator `.github/workflows/release.yml` → `build.yml` / `velopack.yml`）。
- バージョンは `Directory.Build.props` の `<Version>` に従います（更新は `/vava` ワークフロー経由）。

## トラブルシュート

### ログの場所

ログは `%APPDATA%\RealTimeTranslator\logs\RealTimeTranslator_YYYYMMDD.log` (Roaming AppData) に
日次でローテーション保存されます (デフォルト 7 日間保持)。 アプリ内では **バージョンタブの
「ログフォルダを開く」ボタン** から直接エクスプローラで開けます。

### 設定の場所

設定は `%APPDATA%\RealTimeTranslator\settings.json` に保存されます。 API キーは Windows DPAPI で
暗号化されているため別ユーザー / 別 PC では復号できません。 アプリ内では **バージョンタブの
「設定フォルダを開く」ボタン** から直接エクスプローラで開けます。

### 自動更新が動かない時

1. **バージョンタブの「更新の確認」ボタン** を押して手動チェック
2. 失敗する場合はログを確認 (`transcript.delta` / `transcript.done` / `OnTranscriptCompleted` /
   `UpdateService.ShowUpdateDialogAsync 失敗` 等のキーワードで検索)
3. 「このバージョンを無視」を間違えて押して以降スキップされてる場合は、 `settings.json` の
   `Update.IgnoredTagName` を空文字 `""` に書き戻すか、 アプリを再起動して手動チェックボタンを押す
4. それでも復旧しない場合は配信元 `https://rtt.nephilim.jp/RealTimeTranslator-win-x64-Setup.exe`
   から最新インストーラを手動 DL してインストール (旧 GithubSource 版を使っている場合は
   [Releases ページ](https://github.com/1llum1n4t1s/RealTimeTranslator/releases) の踏み台版でも可)

### 設定 UI で項目が未選択 (空欄) になる時

旧バージョンの `settings.json` を持ち越したときに、現在の選択肢一覧から外れた値が入っていると
ComboBox が未選択になる可能性があります。 起動時の `SettingsViewModel.SanitizeSettings()` が
リスト外の値を既定値 (フォント: `IBM Plex Sans JP` / フォントサイズ: 24 / 行数: 3 / 各色: 一覧先頭 /
言語: `ja`) に矯正して自動保存しますが、 もし反映されない場合はログで `SettingsViewModel.Sanitize`
キーワードを検索すると矯正履歴が確認できます。

### 翻訳結果が表示されない時

1. プロセス選択が正しいか確認 (オーディオ再生中のプロセスのみ翻訳対象になる)
2. ログで `transcript.delta 受信` が出ているか確認 (出ていなければ OpenAI 接続失敗 or 音声無し)
3. OpenAI API キー / 課金状態を [OpenAI Platform](https://platform.openai.com/) で確認
4. WASAPI Process Loopback 仕様で「無音時はキャプチャがサイレントデータを返す」ケースあり
   (ログに `raw16 が [-1,1] のみ` Warn が出る)

### Issue 提出時のお願い

不具合報告は [GitHub Issues](https://github.com/1llum1n4t1s/RealTimeTranslator/issues) に
添付ログ + 再現手順 + 環境情報 (Windows バージョン / .NET ランタイム) でお願いします。
ログには翻訳テキストの先頭 40 文字が含まれるため、 機密情報は事前に除去してください。

## 連絡先

- 開発者: ゆろち
- GitHub: https://github.com/1llum1n4t1s

## ライセンス

MIT License
