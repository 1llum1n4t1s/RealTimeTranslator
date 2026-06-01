# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

RealTimeTranslator is a Windows desktop app for real-time subtitle translation. It captures audio from a specific process via WASAPI Process Loopback, sends it to the OpenAI Realtime Translate API via WebSocket, and displays translated subtitles in a transparent overlay window.

**Language**: Japanese (UI, comments, commit messages, README are all in Japanese)

## Build & Test Commands

⚠️ **lockfile RID を維持する正規手順** (v1.0.29 / v1.0.30 で 2 回踏んだ罠の構造的回避):

```bash
# 1 回だけ実行 (lockfile を win-x64 RID 込みで生成)
rtk dotnet restore RealTimeTranslator.slnx -r win-x64 --force-evaluate

# 以降は必ず --no-restore を付ける (暗黙 restore が lockfile から RID を消すのを抑止)
rtk dotnet build RealTimeTranslator.slnx -c Release -p:Platform=x64 --no-restore

# Run tests (MSTest)、 --no-restore + --no-build で再ビルドも抑止
rtk dotnet test RealTimeTranslator.slnx -c Release -p:Platform=x64 --no-restore --no-build

# Run unit tests only (exclude integration)
rtk dotnet test RealTimeTranslator.slnx -c Release -p:Platform=x64 --no-restore --no-build --filter "TestCategory!=Integration"

# Run the app
rtk dotnet run --project src/RealTimeTranslator.UI -c Release -p:Platform=x64 --no-restore

# Publish (self-contained, win-x64)
rtk dotnet publish src/RealTimeTranslator.UI -c Release -r win-x64 --self-contained --no-restore
```

**なぜ `--no-restore` が必須か**: `dotnet build` / `dotnet test` を裸で実行すると暗黙 restore が走り、
**RID 指定なしで lockfile を書き換える**。 これにより `packages.lock.json` から RID 情報が消失して、
CI の `dotnet publish -r win-x64` が NU1004 (lockfile inconsistent) で落ちる。 v1.0.29 と v1.0.30 で
**完全に同じパターンで 2 回踏んだ**ため、 ローカル restore は **1 回だけ + 以降は --no-restore** で
lockfile を CI 用に維持するのが正規手順。

⚠️ **`-r win-x64` は solution に対しては禁止** (NETSDK1134)。 restore コマンドにのみ指定する。
build / test は solution 単位で OK、 lockfile を読み取って RID を解決する。

⚠️ **lockfile の win-x64 RID がバックグラウンドで剥がれ続ける** (v1.0.37 で再発・対処確立): IDE / Roslyn LSP /
OmniSharp 等が裏で **裸 restore を走らせ続け**、 working tree の `packages.lock.json` (特に Core / Tests) から
`net10.0-windows10.0.20348/win-x64` セクションを **明示的な dotnet 実行が無くても繰り返し消す**。 剥がれた版を
commit すると CI publish が NU1004 で落ちる (`The project's runtime identifiers have changed from. ... lock file's
runtime identifiers .` が出たらこれ)。 リリース時はこう動く:

- **コミット前後で staged / committed blob を検証する**: `rtk git show :src/RealTimeTranslator.Core/packages.lock.json | grep -c win-x64`
  で staged blob に win-x64 が 1 以上あることを確認してから commit、 commit 後も `rtk git show HEAD:<path>` で再確認してから
  push する。 working tree の grep だけ見ると「直ったつもり」で剥がれた版を commit する。
- **パッケージ版を変えたリリース** → `rtk dotnet restore RealTimeTranslator.slnx -r win-x64 --force-evaluate && rtk git add <3 lockfile>`
  を **同一コマンドチェーンで atomic に** 実行する。 restore と git add の間に別ツール (= 裏 restore の発火点) を挟ませず、
  剥がれる前に index へ win-x64 を確定させる。
- **パッケージ版を変えないリリース (doc のみ等)** → lockfile を **staged しない**。 `rtk git checkout HEAD -- <剥がれた lockfile>`
  で HEAD の正しい版に戻すか、 whitelist (バージョンファイル + README 等) だけ stage すれば、 commit には HEAD の win-x64 入り
  lockfile がそのまま引き継がれる。

Platform is **always x64** — there is no x86 support.

### テスト構造

MSTest、 `TestCategory!=Integration` フィルタでユニットテストのみ実行可能 (CI のデフォルト)。 主要テストファイル (`src/RealTimeTranslator.Tests/`):

| ファイル | カバー対象 |
|---|---|
| `*.adversarial.test.cs` | 異常系 / 攻撃ベクトル検証 (LoggerService / OpenAIRealtimeClient / AudioFormatConverter) |
| `TranslationPipelineService.Happy.test.cs` / `.Adversarial.test.cs` | パイプライン統合 (正常系 + 異常系 / 並行・破棄・例外耐性) |
| `TranslationPipelineService.SentenceSplit.test.cs` | 句点分割 + transcript.done 累積差分抽出 + D-7 強制分割 |
| `VadGate.test.cs` / `SileroVadDetector.test.cs` | VAD 状態機 + ONNX 推論 |
| `StreamingResampler.test.cs` | WDL sinc リサンプラの境界連続性 / レート変換 |
| `AudioPreprocessing.test.cs` | 入力ゲイン DSP (`Core/Services/Audio/InputGainStage`)。 クリップ防止リミッタは削除済み |
| `AudioLevelMonitor.test.cs` | プレビュー音量メーター (ゲイン反映 / Stop で無音通知 / 開始前無視 / 開始失敗 silent-fail) |
| `DebugAudioRecorder.test.cs` | 送信 PCM16 の WAV 録音 + silent-fail 契約 |
| `CostEstimator.test.cs` | 料金計算 (現行 + legacy モデル両方) |
| `BackgroundColorRoundTrip.test.cs` | 色マイグレーション (#AARRGGBB ↔ #RRGGBB + opacity%) round-trip |
| `SubtitleDisplayItem.test.cs` | オーバーレイ表示アイテムの状態遷移 |
| `TranslationLogEntry.test.cs` | TSV シリアライズ / パース |
| `UpdateSettings.test.cs` | Velopack 更新設定 / feed URI 防御チェック |
| `ProcessLoopbackCaptureTests.cs` | Process Loopback の InternalsVisibleTo 検証 (`TestCategory="Integration"` 含む) |

## Architecture

```
Audio Capture (WASAPI native 48kHz/2ch)
  → StereoToMono → [DSP: InputGain] (ゲイン後ピークをレベルメーターへ通知)
                 ├→ 48k→16k (StreamingResampler、 VAD 判定用)
                 └→ 48k→24k (StreamingResampler、 OpenAI 送信用) → PCM16
  → OpenAI Realtime Translate API (WebSocket) → Subtitle Overlay (Avalonia 透明窓)
```

**経路履歴**: v1.0.23-26 は **並列 2 系統** (`48k→16k VAD + 48k→24k 送信`)、 v1.0.27〜v1.0.35 は **1 系統二段** (`48k→16k→24k`) に統合してパイプライン整合性を優先していたが、 中継 16k で Nyquist 8kHz の高域カットが入り OpenAI transcribe 精度を削いでいた。 **v1.0.36 で並列 2 系統に復帰** し、 送信音声は `48k→24k 直` で Nyquist 12kHz の帯域を確保する。 VAD パスは Silero VAD v5 の 16kHz 固定仕様のため変わらず `48k→16k` 直。 VAD 有効/無効パスとも `_sendResampler` は 48k→24k 直経路を通る (VAD 無効時は `_vadResampler` も呼んで戻り値を捨て、 hot-reload 時の状態同期を維持)。

### Project Structure

- **RealTimeTranslator.Core** — Interfaces, models, and infrastructure services (audio capture, audio format conversion, OpenAI Realtime WebSocket client, logging). No UI dependency.
- **RealTimeTranslator.UI** — Avalonia desktop app. Views, ViewModels (CommunityToolkit.Mvvm), DI setup, pipeline orchestration. References Core.
- **RealTimeTranslator.Tests** — MSTest unit tests. References Core + UI (UI helper `SettingsViewModel.ComposeArgbHex` 等の internal テスト用、 InternalsVisibleTo で接続)。
- **`src/MinimalProcessLoopbackWpf/`** は **`RealTimeTranslator.slnx` 未参加の実験プロジェクト** (WASAPI Process Loopback の最小検証用、 本体ビルドに影響しない)。 触る場合は別途 csproj 単体ビルド。

### Key Interfaces (in Core/Interfaces/)

| Interface | Responsibility |
|---|---|
| `ITranslationPipelineService` | Orchestrates the full pipeline. Events: `SubtitleGenerated`, `StatsUpdated`, `ErrorOccurred`, `AudioLevelUpdated` (ゲイン後ピーク dBFS を ~50ms 間隔でレベルメーターへ) |
| `IAudioCaptureService` | WASAPI process loopback capture (WASAPI native rate、 typically 48kHz mono float32)。 v1.0.27 から native rate 出力で TranslationPipelineService 側がリサンプルを担当 |
| `IAudioLevelMonitor` | 「開始」前に選択プロセスの音量をメーター表示するプレビュー専用モニタ。 専用 `AudioCaptureService` を内包し OpenAI 非送信。 `LevelUpdated` (ゲイン適用後ピーク dBFS) を発火。 翻訳開始時は MainViewModel が停止し本番メーターへ切替 (二重キャプチャ防止)、 停止時に再開 |

### Key Services (in Core/Services/)

| Service | Responsibility |
|---|---|
| `OpenAIRealtimeClient` | WebSocket client for OpenAI Realtime Translate API. Handles connection, reconnection (exponential backoff), audio send/receive loops |
| `AudioFormatConverter` / `StreamingResampler` | リサンプル本体は `StreamingResampler` (v1.0.21 導入、 WDL sinc 補間の状態保持版で chunk 境界クリック防止)。 `AudioFormatConverter.Float32ToPcm16` は静的 PCM16 変換だけ使用。 `AudioFormatConverter.ResampleTo24kHz` はテスト reference 用に残置 (プロダクション未使用) |
| `AudioCaptureService` | WASAPI process loopback capture with custom COM interop + NAudio |
| `AudioLevelMonitor` (`IAudioLevelMonitor`) | 「開始」前のプレビュー音量メーター。 専用 `AudioCaptureService` で選択プロセスをキャプチャ → 入力ゲイン適用後ピークを `LevelUpdated` 発火 (OpenAI 非送信)。 内部 CTS で無限リトライを Stop/Dispose 時に確実停止、 stale 完了の状態上書きを防ぐガード付き。 `MainViewModel` が `SelectedProcess` 変更/ゲイン変更/開始停止に合わせて起動制御 |
| `SileroVadDetector` | Silero VAD (ONNX) で「人の声らしさ」を判定する VAD ゲート。 16kHz / 512 サンプル / 32ms フレーム固定 |
| `CostEstimator` | OpenAI Realtime API の audio input tokens 数 / 推定コスト (USD) を計算 |
| `Audio/InputGainStage` / `Audio/DspMath` | 入力プリプロセス DSP (`IAudioPreprocessor` 実装)。 信号フローは `StereoToMono → InputGain (-24〜+24dB 手動底上げ、 UI は OBS 風フェーダー)`。 `TranslationPipelineService` がシングルインスタンス保持し、 `Process(Span<float>)` で in-place 処理、 `Reset()` で内部状態をクリア。 `IsEnabled=false` (0dB) 時は呼び出し直後に return して完全 bypass。 ⚠️ 旧 `AntiClipLimiter` (クリップ防止リミッタ) は削除済み — ゲイン後ピークのレベルメーターを見てユーザーが手動でレベル管理する OBS 方式に変更 (`DspMath.AmplitudeToDb` はメーターの dBFS 変換で利用) |
| `DebugAudioRecorder` (`IDebugAudioRecorder`) | OpenAI に送る PCM16 (24kHz/Mono) を `IRealtimeTranscriber.SendAudio` 入口でフックして WAV 録音するデバッグ機能。 出力先 `%APPDATA%/RealTimeTranslator/debug/SentAudio_*.wav`。 録音中でなければ no-op、 ファイル open / 書き込み失敗は例外を伝播せず silent-fail (`IsRecording=false` に倒れる + `WriteFailed` イベント発火)。 「実際に送った音声」の音質確認に使う |

### Pipeline Flow (TranslationPipelineService in Core/Services/)

1. `AudioCaptureService` feeds WASAPI native rate (typically 48kHz/2ch) audio chunks via `AudioDataAvailable` event
2. `TranslationPipelineService` がステレオ→モノラル化、 `StreamingResampler` で `48k→16k` (VAD 判定用) と `48k→24k` (OpenAI 送信用) を **並列 2 系統** (v1.0.36 で復活) で処理し、 PCM16 に変換して `OpenAIRealtimeClient` に渡す。 v1.0.27〜v1.0.35 は `48k→16k→24k` の 1 系統二段だったが、 中継 16k で Nyquist 8kHz 高域カットが入り transcribe 精度を下げていたため revert。
3. API returns translation text as `response.output_audio_transcript.delta` / `response.output_text.delta` (streaming) and `.done` (final). Legacy event names (`output_transcript.*`, `response.audio_transcript.*`) are still recognized for compatibility.
4. Delta events fire `SubtitleGenerated` with `IsFinal=false` (throttled per `TranslationPipelineService.DeltaThrottle`, 現在 20ms)、 句点 (`。！？.!?`) 到達または D-7 fallback (`MaxPartialChars`、 default 50 — v1.0.31 で 80 → 50 に短縮) 発火で完結文を切り出して `IsFinal=true` 発火 + 新 `SegmentId` 発行
5. `OverlayViewModel` displays subtitles, tracking updates by `SegmentId`

### VAD ゲート + コスト見える化 (案 D + 案 G) 🎯

OpenAI Realtime API は **送信した音声を全部 audio input token として課金** する (server VAD は「いつ response を出すか」の判定だけで課金には影響しない)。 ゲーム音 BGM の垂れ流しを放置すると現行フルモデル `gpt-realtime-2` で **約 $11.5/時間**、 旧 `gpt-4o-realtime-preview` で **$36/時間** という事故になる。 現使用の Translate 専用モデル `gpt-realtime-translate` は per-minute 課金で **約 $2/時間** と最安。

- **VAD ゲート (`SileroVadDetector`)**: snakers4/silero-vad v5 (MIT、 ~2MB) を `src/RealTimeTranslator.Core/Assets/silero_vad.onnx` に同梱。 16kHz / 512 サンプル / 32ms フレーム固定。 LSTM hidden state は `SileroVadDetector` が内部保持、 `TranslationPipelineService.StartAsync` で `Reset()` を呼ぶ。
- **状態機 (`TranslationPipelineService.ProcessVadFrame`)**: Silence/InSpeech/Hangover の 3 状態。 PreRoll Queue で発話冒頭の取りこぼし防止、 Hangover カウンタで末尾切れ防止。 EnableVad=false の旧素通しパスは緊急時 fallback + 後方互換のため残す。
- **設定**: `AppSettings.AudioCapture` に `EnableVad=true` / `VadPreset="Balanced"` / `VadThreshold=0.3` / `VadPreRollMs=1000` / `VadHangoverMs=400` / `AutoPauseOnSilenceSec=0`。 UI は「音声処理」タブ (`MainWindow.axaml` Tab 3)。
- **VAD プリセット (`VadPreset`)**: `Balanced` (推奨 threshold=0.3 / preroll=1000 / hangover=400) / `PrioritizeEdges` (頭尻尾重視 threshold=0.2 / preroll=1200 / hangover=600) / `AggressiveSavings` (節約重視 threshold=0.4 / preroll=700 / hangover=150) / `Custom` (詳細 3 値を個別調整) の 4 値。 Custom 以外を選択すると `SettingsViewModel.SelectedVadPreset` setter + `SanitizeSettings` の両方が Threshold/PreRoll/Hangover を **強制同期** する。 settings.json を手で書いて preset 名は Balanced のまま 3 値だけ別値、 は次回起動時に上書きされる仕様。 v1.0.42 で全プリセット -0.1 連動シフト (Balanced 0.3 / PrioritizeEdges 0.2 / AggressiveSavings 0.4)。 preroll/hangover は Balanced を 1000/400 に変更し全プリセットを PreRoll +200ms / Hangover -200ms 一括シフト (頭尻尾重視 > ふつう > 節約 の相対関係を維持)。 遠距離小音量の声拾いは入力プリプロセス DSP に役割分担。
- **自動 Pause (`AutoPauseLoopAsync`)**: 5 秒間隔ポーリングで `Now - _lastSpeechUtc >= AutoPauseOnSilenceSec` なら `StopCapture()`。 1 回発火で `break` (ユーザーが Start 押し直すと新タスク起動)。 VAD 有効時のみ機能。 UI は ComboBox (無効/30秒/1分/3分/5分/10分/30分) で選択 (旧 NumericUpDown 廃止)。
- **コスト計算 (`CostEstimator`)**: モデル名から rate 解決 (2026-05 現行料金: 現行 mini = $10/1M、 現行フル系 (`gpt-realtime-2` / `gpt-realtime-1.5` / `gpt-realtime-translate`) = $32/1M。 旧 `gpt-4o-realtime-preview` = $100/1M, 旧 `gpt-4o-mini-realtime-preview` = $10/1M は legacy 互換で維持。 不明はフル料金で安全側)。 サンプル数 / レートから秒数 → tokens (公表値 100 tokens/sec)。 サーバー usage 値 (`response.done.usage.input_token_details.audio_tokens`) が取れる場合は優先採用、 取れない場合は推定 fallback。 `gpt-realtime-translate` は実際は per-minute 課金 ($0.034/min audio output) なので、 token ベースの見積もりは過大評価寄り (UI 表示は目安、 正確には OpenAI ダッシュボードで確認)。
- **リアルタイム stats tick (`StatsTickLoopAsync`)**: 1 秒周期で `StatsUpdated` を発火し、 silence 区間でも UI の経過時間 / 累計トークン / VAD 節約秒数を即時更新する。 旧実装は `response.done` でしか発火せず「Start 直後の数十秒間 stats が止まる」体感バグだった。 全 stats invoke 経路は `BuildCurrentStats(statusText)` ヘルパーで統一して累積フィールド 0 上書きレースを構造的に排除している (rere C2-001 / B2-005)。
- **歌モノ BGM (ボーカル入り音楽) も翻訳対象として送信する (仕様)**: Silero VAD は formant 特性で判定するためボーカル入り音楽は speech 扱いになる。 これはゆろさん指定の **意図された挙動** (歌詞も字幕として翻訳したいため、 ゲーム OP/ED や BGM 歌詞も訳す)。 将来「歌は翻訳したくない」要望が来たら GameProfile 単位の VAD on/off 切替を検討。
- **DI**: `App.axaml.cs` で `services.AddSingleton<IVoiceActivityDetector>(sp => ...)` を factory 化、 SileroVadDetector 構築失敗時は `NullVoiceActivityDetector` (常に prob=1.0、 旧素通し動作と等価) にフォールバック (rere F-002 対応)。 onnx 同梱漏れ / アンチウイルス隔離 / VC++ Runtime 不足等でも起動 brick を防ぐ。 Singleton にすることで onnx ロード (~10ms) は起動時 1 回のみ。
- **VAD フレームサイズ (512) を変更しない**: Silero VAD v5 16kHz 専用仕様で固定。
- **VAD Silence 中の無音 PCM 5 秒継続送信 (v1.0.27 戦略、 v1.0.28 で実機確証)** 🏆: `AppSettings.OpenAIRealtime.SilencePaddingMs` (default 5000ms) 以内は VAD Hangover → Silence 遷移後にゼロ埋め PCM16 を継続送信する。 OpenAI Realtime Translate API は continuous streaming 前提で「入力途絶 → delta 保留」挙動を取るため (v1.0.26 ログ実証)、 無音 PCM で server に「入力継続中」をアピールして保留 delta を flush させる。 5 秒超で送信停止 (token 節約)、 Silence → InSpeech 再遷移で `_silenceStartUtc` リセット。 v1.0.28 実機検証 (ARC Raiders) で完結文 emit 11 件発生 (v1.0.27: 0 件) を観測、 戦略採用確定 (/rere #D-001 の根拠不足批判は実機証明で解消)。 `_silencePaddingPcm16` バッファをキャッシュして再利用 (中身ずっとゼロ)。
- **VAD fallback サイレント検知 UI バナー (v1.0.28、 /rere #D-003 対応)**: `SileroVadDetector` 構築失敗で `NullVoiceActivityDetector` (全送信 fallback) に倒れたとき、 `MainViewModel` 起動時に `ErrorOccurred` 発火 → ステータスバナーで「⚠️ VAD 無効・BGM 全部送信中・課金注意」を恒常表示する。 onnx 同梱漏れ / アンチウイルス隔離時の課金事故 ($11.5/時間 リスク) を予防。

### 表示設定 (Overlay) の管理 🎨

- **`OverlaySettings`**: `FontFamily` / `FontSize` / `FontWeight` (Normal/Bold) / `PartialTextColor` / `FinalTextColor` / `BackgroundColor` (`#AARRGGBB` 後方互換) / `BackgroundColorBase` (`#RRGGBB`) / `BackgroundOpacityPercent` (0/25/50/75/100 の 5 段階) / `DisplayDuration` / `MaxLines`。
- **背景色の三重管理**: `BackgroundColor` は派生値、 真実は `BackgroundColorBase` + `BackgroundOpacityPercent`。 SettingsViewModel の Selected\* setter で `ComposeArgbHex` 経由で同期する。 旧 settings.json (#AARRGGBB 単一フィールド) からは `SanitizeSettings` の `SplitArgbToRgbAndOpacity` で起動時 1 度だけ逆算してマイグレート。 一覧外の色は黒 50% に矯正 + `SanitizeWarnings` 経由でユーザーに通知バナー (rere F-007)。
- **フォントの太さ**: `OverlaySettings.FontWeight` を `"Normal"` または `"Bold"` で保持し、 `OverlayViewModel.ResolveFontWeight` で `Avalonia.Media.FontWeight` enum に変換して `TextBlock.FontWeight` にバインド。

### 翻訳ログ機能 (`TranslationLogService`) 📜

- 確定字幕 (`IsFinal=true`) を TSV 形式で永続化: `%APPDATA%/RealTimeTranslator/logs/translations/TranslationLog_yyyyMMdd.tsv` (Roaming AppData なので Velopack 自動更新で消えない。 `%APPDATA%` は既に `AppData\Roaming` を指す環境変数なので `/Roaming/` を重ねない)。
- 1 行 = `Timestamp\tLanguage\tSessionId\tProcessName\tText`、 翻訳テキスト中の `\t \n \r` は半角空白に正規化して 1 行 1 エントリを保証 (`TranslationLogEntry.ToTsvLine`)。
- **書き込み系 (`Append` / `ClearAllAsync` / `PerformRetentionCleanupAsync`) は `Channel<Func<Task>>` + 単一ワーカーで直列化** → Append の発火順序が TSV ファイル上で保証 + ClearAll 直後の Append race を構造的に排除 (rere R-C1 / R-H3 対応)。
- **読み取り系 (`ReadAllAsync`) は lock を取らず並行可能** (起動時 1 度のみ、 翻訳開始前に完了する想定)。
- `TranslationLogViewModel` は `_isHistoryLoaded` フラグで履歴ロード前の AddEntry を抑制 → ObservableCollection への二重追加 race を回避 (rere R-H2 対応)。
- メモリ上限 `MaxDisplayEntries=2000` で古い側を Remove、 ファイルには無制限残る (「保存フォルダを開く」ボタンで閲覧)。
- 保持期間: `AppSettings.TranslationLog.RetentionDays` (0 = 無制限デフォルト / 7 / 30 / 90 / 180 / 365 日)。 `PerformRetentionCleanupAsync` の cutoff は `Today - (N-1)` で「今日含む N 日分残す」直感に揃えてある (rere R-H4 対応)。
- セッション ID は `MainViewModel.StartAsync` で `Guid.NewGuid().ToString("N")[..8]` で発行、 同セッション内の確定字幕はすべて同 ID で記録。

### OpenAI Realtime Translation API の癖 ⚠️

`/v1/realtime?intent=translation` エンドポイントには、 標準の `/v1/realtime` と異なる非自明な挙動がある。 修正前に必ず以下を読んで。

- **`turn_detection` は session.update に入れられない**: `session.audio.input.turn_detection` も `session.turn_detection` も `Unknown parameter` で拒否される (v1.0.2 / v1.0.6 で検証済み)。 デフォルトの server VAD に任せるしかない。 `OpenAIRealtimeClient.SendSessionUpdateAsync` で turn_detection を **絶対に送らない** こと。
- **`session.input_audio_buffer.append` 形式 (session. prefix 必須)**: 通常の `input_audio_buffer.append` ではない。 `SendLoopAsync` の type は `"session.input_audio_buffer.append"` で固定。
- **`transcript.done` はセッション通算の累積全文を返す**: 各 response 単位ではなく「会話開始からの全文」が毎 done で送られてくる挙動が観測されている (2026-05-16)。 そのまま `finalText` として overlay に出すと「まあ → まあ、 → まあ、ノ → ...」と1字幕が無限成長する UX 不具合になる。 `TranslationPipelineService._lastFinalizedTranscript` で確定済みテキストを保持し、 done 受信時に **差分抽出 → 句点 `。！？.!?` で文分割 → 文ごとに新 SegmentId で emit** する設計でこの挙動を吸収している。
- **句点なしマシンガントークの分割 (D-7 fallback、 v1.0.28 復活)** 🛡️: 通常は API の自動句読点挿入に頼って `IsSentenceBoundaryAt` で完結文を切り出すが、 server が句点を一切返さない発話 (ARC Raiders のセリフ等で 2026-05-24 実機観測、 1 セグメント 127 文字に膨張した事例) に備えて `AppSettings.OpenAIRealtime.MaxPartialChars` (default 80) の文字数閾値に達したら強制分割する fallback を `OnTranscriptDelta` 内の **while ループ** で実装している。 探索優先順位: ① 末尾 30 文字以内の「、」「,」 → ② 半角/全角空白 → ③ maxChars 位置で強制切断。 同種文字連続 (例: 「あ」x80) は Bigram Jaccard 類似抑制 (v1.0.20) で 1 件以下に収束する。 内部関数は `internal static FindForcedSplitIndex` でテスト直接呼び出し可能 (`SentenceSplit.test.cs` に単体 10 件 + 統合 3 件)。 v1.0.27 棚卸しで一度削除されたが、 翌日 v1.0.28 で実機破綻を観測して復活した経緯あり (教訓: 対症療法削除は実機確証してから)。

### DI & Configuration

- DI configured in `App.axaml.cs` (`OnFrameworkInitializationCompleted`)
- Settings loaded from `settings.json` via `IOptionsMonitor<AppSettings>` (supports hot-reload)
- Runtime settings changes pushed via `ApplySettings()` pattern to bypass file watcher delay
- `AppSettings` contains sub-sections: `OpenAIRealtime`, `Overlay`, `AudioCapture`, `Update`, `TranslationLog`
- Single-instance enforcement via named Mutex in `Program.cs`

### UI Framework

- **Avalonia 12** with Fluent theme
- MVVM via CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`)
- Main views: `MainWindow` (サイドバー 6 タブ構成), `OverlayWindow` (transparent subtitle display)
- UI thread dispatch: `Dispatcher.UIThread.Post()`
- compiled bindings (`AvaloniaUseCompiledBindingsByDefault=true`) — 全 axaml で `x:DataType` 必須

#### MainWindow サイドバー 6 タブ (各 `PathIcon` Material Design Icons 付き)

| # | Tab | アイコン | 主な内容 |
|---|---|---|---|
| 1 | メイン | home | プロセス選択 / 出力言語 / **入力ゲイン (OBS 風フェーダー + ゲイン後ピークのレベルメーター。 プロセス選択でリアルタイムプレビュー)** / ステータス / 開始停止ボタン |
| 2 | API設定 | key | APIキー入力 / 接続テスト / **OpenAI APIキー取得方法ガイド (5 ステップ + 課金目安 + ダッシュボードリンク)** |
| 3 | 音声処理 | waveform | VAD プリセット / Custom 詳細スライダー / 自動 Pause / デバッグ録音 (入力ゲイン UI はメインタブへ移動済み) |
| 4 | 表示設定 | palette | フォント / 色 + 濃さ / 最大行数 / 表示時間 / 翻訳ログ保持期間 |
| 5 | 翻訳ログ | history clock | ADV ゲーム風会話ログ表示 / 保存フォルダを開く / すべて削除 |
| 6 | バージョン | info | バージョン表示 / 更新確認 / ログ・設定フォルダリンク / フィードバック |

タブの `Header` は `<TabItem.Header><StackPanel Orientation="Horizontal" Spacing="10">` で PathIcon + TextBlock を組む。 アイコンは親 `Foreground` を継承するため hover / selected 状態の色変化に自動追従する。

### Localization

- アプリ全体 UI は **日本語固定** (ハードコード文字列、 リソースファイル / `.resx` 未使用)。
- **VelopackUpdateDialog の日本語化**: `JapaneseUpdateDialogStrings : IUpdateDialogStrings` シングルトンを `UpdateDialogOptions.Strings` に渡す (`UpdateService.ShowDialogOnUiThreadAsync`)。 全 8 文言 (Title / AvailableHeader / DownloadAndInstall / IgnoreThisVersion / UpToDateMessage / ErrorHeader / Close / CheckingMessage) を翻訳済み。 パッケージ更新時 (v1.0.x → v1.1.x) は API 互換性を要確認。

## Key Conventions

- **Async**: All service methods use `Async` suffix, propagate `CancellationToken`
- **Thread safety**: `lock` for shared collections, `Channel<T>` for producer-consumer (audio send buffer)
- **Error propagation**: Event-based (`ErrorOccurred` events bubble up to UI)
- **Output paths**: Simplified — `bin/{Configuration}/` (no TFM/platform subdirectories, set in Directory.Build.props)
- **Auto-update**: Velopack with `SimpleWebSource` pointing at **Cloudflare R2** (`https://rtt.nephilim.jp`、 バケット `realtimetranslator-updates`)。 配信元 URL は `AppSettings.UpdateSettings.UpdateBaseUrl` に `[JsonIgnore]` でハードコード固定 (settings.json から書き換え不可。 旧 `FeedUrl` + host/owner-repo allowlist は撤廃済み — URL 固定で攻撃面が消えたため `UpdateService.TryGetValidFeedUri` は HTTPS + 絶対 URI + userinfo 排除の防御チェックのみ)。 channel は `win-x64` のみ (`releases.win-x64.json`)。 **配信ドメインは中立ドメイン `nephilim.jp` を使う** — `1llum1n4t1.com` 系はクラウド/企業 egress の SNI フィルタで false positive を起こしエッジ→R2 が 522 になるため使わない。 旧 `GithubSource` クライアント (≤v1.0.15) 救済の「踏み台」R2 対応版を GitHub Releases に 1 つ (v1.0.16) だけ残してある (**削除しない**。 旧クライアントはこれ経由で 1 度更新 → 再起動後 R2 を見る 2 段階アップデート)。
- **Velopack 操作の直列化**: `UpdateService._velopackOpLock` (static SemaphoreSlim) で Check / Download / Apply を直列化する。 これがないと起動時自動チェック + Periodic タイマー + 手動更新が並走して `.velopack_lock` 衝突 (`AcquireLockFailedException`) になる。 例外は `ex.GetType().Name == "AcquireLockFailedException"` で文字列マッチして握る (Velopack 内部型に依存しない)。
- **API Key & 設定の保存先**: **Roaming** AppData (`%APPDATA%/RealTimeTranslator/settings.json`)。 Velopack 更新で `%LocalAppData%/RealTimeTranslator/packages` 配下が再構築されても消えないようにするため。 旧 LocalAppData 配置からは `SettingsService.MigrateLegacySettingsIfNeeded` で自動移行する。 API キーは DPAPI (CurrentUser scope) で暗号化し `dpapi:` プレフィックス付き base64 で保存。
- **Logging**: SuperLightLogger (NLog-compatible API)。 ログは `%APPDATA%/RealTimeTranslator/logs/` (Roaming AppData。 Velopack インストールルート `%LocalAppData%` と衝突しないため Roaming 配置。 Velopack 更新で再配置されてもログ自体は残す)。 「log4net → NLog 移行」コミットの実体は SuperLightLogger の `LogManager.Configure(... AddSuperLightFile ...)`。
- **プロセス確実終了**: `App.axaml.cs` の `OnShutdownRequested` で多重防御を組んでいる: (1) AudioCapture を明示停止 → (2) ITranslationPipelineService.DisposeAsync を 2秒タイムアウト → (3) `Environment.Exit(0)` → (4) 5秒後の `Process.Kill()` 保険タイマー。 NAudio / WASAPI のネイティブハンドルが残ってプロセスゾンビ化する経路を全部塞ぐ目的。

## Operations / トラブルシュート

- **ログの場所**: `%APPDATA%/RealTimeTranslator/logs/RealTimeTranslator_yyyyMMdd.log` (Roaming AppData、 ローテーションあり、 デフォルト 7 日間保持)。
- **設定ファイルの場所**: `%APPDATA%/RealTimeTranslator/settings.json` (Roaming)。 旧 `%LocalAppData%/RealTimeTranslator/settings.json` からは起動時に自動移行される。 API キーは DPAPI (CurrentUser scope) で暗号化されており、別ユーザー / 別 PC では復号できない。
- **API キー漏洩疑惑時の対応**: `settings.json` の `OpenAIRealtime.ApiKey` は `dpapi:` プレフィックス付き base64 で保存されている必要がある。生の `sk-...` 形式が見えたら旧形式のまま（次回保存で自動暗号化）。
- **Velopack 更新失敗時**: `LoggerService.LogError` で `UpdateService.ShowUpdateDialogAsync 失敗` の例外詳細が記録される。 配信元は R2 (`UpdateBaseUrl = https://rtt.nephilim.jp`、 ハードコード固定)。 R2 配信そのものの疑いは `curl -I https://rtt.nephilim.jp/releases.win-x64.json` で切り分ける (HTTP 200 + `Content-Type: application/json` なら配信は健全)。 `.velopack_lock` 衝突は `_velopackOpLock` セマフォで通常起きない設計だが、 別プロセス (旧 version) が同時起動している場合は `AcquireLockFailedException` を握ってスキップする。
- **自動更新は常時有効 (2026-05-25 で `Enabled` 切替廃止)**: 旧 `AppSettings.Update.Enabled` プロパティを撤去し、 settings.json で false に書かれていても `System.Text.Json` の unknown property として黙殺される。 別 PC で「更新チェックは無効です」表示で詰む旧事例の構造的解消。 「このバージョンを無視」機能 (`IgnoredTagName`) は維持され、 起動時自動チェックでは無視タグが適用されるが手動チェック (バージョンタブの「更新の確認」ボタン) では適用されない。
- **接続失敗時**: `OpenAIRealtimeClient.ValidateEndpoint` で wss + `api.openai.com` 以外は拒否される。`KeepAliveInterval=15s` / `KeepAliveTimeout=20s` で半切断を検知し、`NetworkChange.NetworkAvailabilityChanged` で復帰時に再接続カウンタをリセットする。
- **不明な API イベントの調査**: `OpenAIRealtimeClient.ProcessMessage` に `_seenEventTypes` HashSet があり、 初見の event type を Info ログに1回だけ吐く診断機構が入っている。 「字幕が来ない / 文が切れない」系の問題はまずログで「どの event が来ているか」を確認すること。
- **クラッシュレポート**: `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException` / `Dispatcher.UIThread.UnhandledException` の 3 つのハンドラがログに記録する。バックグラウンド Task の例外もここで拾える。

## バージョン管理 / リリース

- **バージョン番号の更新は `/vava` スキル経由のみ**: コード修正のついでに `Directory.Build.props` の `<Version>` を勝手に上げない。 `/vava` がバージョン計算 + コミット + `release/X.Y.Z` ブランチ作成 + GitHub Actions トリガー + 古いリリースブランチ掃除まで一括処理する。
- **`release/**` push → CI**: `.github/workflows/release.yml` が `release/X.Y.Z` ブランチ push で発火し、 build → Velopack pack → **Cloudflare R2 アップロード** (`r2-upload` job) まで自動化されている (R2 単独配信。 GitHub Releases への継続 publish はしない)。 リトライは `/vava` の retry モード (同 version で fast-forward push) を使う。 移行作業時の踏み台 publish は `/transfer-cf` Step 11.5 管轄 (通常リリースには含めない)。
- **メモリーバンクの freshness gate**: 一部の git commit hook で `memory-bank/RealTimeTranslator/activeContext.md` の更新日時がコード変更より古いとブロックされる。 大きめの変更後は `memory_bank_update` で `activeContext.md` を更新してからコミットすること。

### CI Workflows (`.github/workflows/`)

| ファイル | トリガー | 役割 |
|---|---|---|
| `ci.yml` | PR / main push | 通常 CI (restore + build + test、 PR 検証用) |
| `build.yml` | `workflow_call` | publish ワークフロー (release.yml から呼ばれる再利用 job、 self-contained win-x64 で publish) |
| `velopack.yml` | `workflow_call` | Velopack pack ワークフロー (vpk を `dotnet tool install` で実行。 ピン版は velopack.yml 内で管理) |
| `release.yml` | `release/**` ブランチ push | リリース本体: build → velopack → `r2-upload` (Cloudflare R2 `realtimetranslator-updates` へ `wrangler r2 object put` + `https://rtt.nephilim.jp/releases.win-x64.json` の HTTP 200 検証)。 R2 単独配信、 workflow level `permissions: contents: read`。 GitHub Releases は作らない |

全 actions は SHA 固定 (`@<sha> # vX.Y` 形式)、 サプライチェーン対策。 Dependabot が自動 PR を上げる構成。 `r2-upload` job は GitHub Secrets の `CLOUDFLARE_API_TOKEN` (Workers R2 Storage / Edit) + `CLOUDFLARE_ACCOUNT_ID` を使う。 wrangler は `4.93.0` / setup-node `v6.4.0` / Node 22 で pin。
