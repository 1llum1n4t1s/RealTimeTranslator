# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

RealTimeTranslator is a Windows desktop app for real-time subtitle translation. It captures audio from a specific process via WASAPI Process Loopback, sends it to the OpenAI Realtime Translate API via WebSocket, and displays translated subtitles in a transparent overlay window.

**Language**: Japanese (UI, comments, commit messages, README are all in Japanese)

## Build & Test Commands

```bash
# Restore + Build (x64 only, always specify platform)
rtk dotnet restore RealTimeTranslator.slnx
rtk dotnet build RealTimeTranslator.slnx -c Release -p:Platform=x64

# Run tests (MSTest)
rtk dotnet test RealTimeTranslator.slnx -c Release -p:Platform=x64

# Run unit tests only (exclude integration)
rtk dotnet test RealTimeTranslator.slnx -c Release -p:Platform=x64 --filter "TestCategory!=Integration"

# Run the app
rtk dotnet run --project src/RealTimeTranslator.UI -c Release -p:Platform=x64

# Publish (self-contained, win-x64)
rtk dotnet publish src/RealTimeTranslator.UI -c Release -r win-x64 --self-contained
```

Platform is **always x64** — there is no x86 support.

## Architecture

```
Audio Capture (WASAPI) → Resample 16kHz→24kHz → PCM16 → OpenAI Realtime API (WebSocket) → Subtitle Overlay
```

### Project Structure

- **RealTimeTranslator.Core** — Interfaces, models, and infrastructure services (audio capture, audio format conversion, OpenAI Realtime WebSocket client, logging). No UI dependency.
- **RealTimeTranslator.UI** — Avalonia desktop app. Views, ViewModels (CommunityToolkit.Mvvm), DI setup, pipeline orchestration. References Core.
- **RealTimeTranslator.Tests** — MSTest unit tests. References Core.

### Key Interfaces (in Core/Interfaces/)

| Interface | Responsibility |
|---|---|
| `ITranslationPipelineService` | Orchestrates the full pipeline. Events: `SubtitleGenerated`, `StatsUpdated`, `ErrorOccurred` |
| `IAudioCaptureService` | WASAPI process loopback capture (16kHz mono float) |

### Key Services (in Core/Services/)

| Service | Responsibility |
|---|---|
| `OpenAIRealtimeClient` | WebSocket client for OpenAI Realtime Translate API. Handles connection, reconnection (exponential backoff), audio send/receive loops |
| `AudioFormatConverter` | Static utility: 16kHz float32 → 24kHz PCM16 conversion for API input |
| `AudioCaptureService` | WASAPI process loopback capture with custom COM interop + NAudio |
| `SileroVadDetector` | Silero VAD (ONNX) で「人の声らしさ」を判定する VAD ゲート。 16kHz / 512 サンプル / 32ms フレーム固定 |
| `CostEstimator` | OpenAI Realtime API の audio input tokens 数 / 推定コスト (USD) を計算 |

### Pipeline Flow (TranslationPipelineService in Core/Services/)

1. `AudioCaptureService` feeds 16kHz mono float32 audio chunks via `AudioDataAvailable` event
2. `TranslationPipelineService` resamples to 24kHz, converts to PCM16, sends via `OpenAIRealtimeClient`
3. API returns translation text as `response.output_audio_transcript.delta` / `response.output_text.delta` (streaming) and `.done` (final). Legacy event names (`output_transcript.*`, `response.audio_transcript.*`) are still recognized for compatibility.
4. Delta events fire `SubtitleGenerated` with `IsFinal=false` (throttled per `TranslationPipelineService.DeltaThrottle`, 現在 30ms), done fires with `IsFinal=true`
5. `OverlayViewModel` displays subtitles, tracking updates by `SegmentId`

### VAD ゲート + コスト見える化 (案 D + 案 G) 🎯

OpenAI Realtime API は **送信した音声を全部 audio input token として課金** する (server VAD は「いつ response を出すか」の判定だけで課金には影響しない)。 ゲーム音 BGM の垂れ流しを放置すると `gpt-4o-realtime-preview` で **$36/時間** という事故になる。

- **VAD ゲート (`SileroVadDetector`)**: snakers4/silero-vad v5 (MIT、 ~2MB) を `src/RealTimeTranslator.Core/Assets/silero_vad.onnx` に同梱。 16kHz / 512 サンプル / 32ms フレーム固定。 LSTM hidden state は `SileroVadDetector` が内部保持、 `TranslationPipelineService.StartAsync` で `Reset()` を呼ぶ。
- **状態機 (`TranslationPipelineService.ProcessVadFrame`)**: Silence/InSpeech/Hangover の 3 状態。 PreRoll Queue で発話冒頭の取りこぼし防止、 Hangover カウンタで末尾切れ防止。 EnableVad=false の旧素通しパスは緊急時 fallback + 後方互換のため残す。
- **設定**: `AppSettings.AudioCapture` に `EnableVad=true` / `VadPreset="Balanced"` / `VadThreshold=0.5` / `VadPreRollMs=600` / `VadHangoverMs=400` / `AutoPauseOnSilenceSec=0`。 UI は「音声処理」タブ (`MainWindow.axaml` Tab 3)。
- **VAD プリセット (`VadPreset`)**: `Balanced` (推奨 threshold=0.5 / preroll=600 / hangover=400) / `PrioritizeEdges` (頭尻尾重視 threshold=0.4 / preroll=800 / hangover=600) / `AggressiveSavings` (節約重視 threshold=0.6 / preroll=300 / hangover=150) / `Custom` (詳細 3 値を個別調整) の 4 値。 Custom 以外を選択すると `SettingsViewModel.SelectedVadPreset` setter + `SanitizeSettings` の両方が Threshold/PreRoll/Hangover を **強制同期** する。 settings.json を手で書いて preset 名は Balanced のまま 3 値だけ別値、 は次回起動時に上書きされる仕様。 Balanced は threshold=0.5 維持で BGM 誤反応を抑えつつ、 preroll/hangover を厚めに取って発話冒頭/語尾の取りこぼしを防ぐバランス調整。
- **自動 Pause (`AutoPauseLoopAsync`)**: 5 秒間隔ポーリングで `Now - _lastSpeechUtc >= AutoPauseOnSilenceSec` なら `StopCapture()`。 1 回発火で `break` (ユーザーが Start 押し直すと新タスク起動)。 VAD 有効時のみ機能。 UI は ComboBox (無効/30秒/1分/3分/5分/10分/30分) で選択 (旧 NumericUpDown 廃止)。
- **コスト計算 (`CostEstimator`)**: モデル名から rate 解決 ("mini" 含 → $10/1M, 他 → $100/1M, 不明はフル料金で安全側)。 サンプル数 / レートから秒数 → tokens (公表値 100 tokens/sec)。 サーバー usage 値 (`response.done.usage.input_token_details.audio_tokens`) が取れる場合は優先採用、 取れない場合は推定 fallback。
- **リアルタイム stats tick (`StatsTickLoopAsync`)**: 1 秒周期で `StatsUpdated` を発火し、 silence 区間でも UI の経過時間 / 累計トークン / VAD 節約秒数を即時更新する。 旧実装は `response.done` でしか発火せず「Start 直後の数十秒間 stats が止まる」体感バグだった。 全 stats invoke 経路は `BuildCurrentStats(statusText)` ヘルパーで統一して累積フィールド 0 上書きレースを構造的に排除している (rere C2-001 / B2-005)。
- **歌モノ BGM (ボーカル入り音楽) も翻訳対象として送信する (仕様)**: Silero VAD は formant 特性で判定するためボーカル入り音楽は speech 扱いになる。 これはゆろさん指定の **意図された挙動** (歌詞も字幕として翻訳したいため、 ゲーム OP/ED や BGM 歌詞も訳す)。 将来「歌は翻訳したくない」要望が来たら GameProfile 単位の VAD on/off 切替を検討。
- **DI**: `App.axaml.cs` で `services.AddSingleton<IVoiceActivityDetector>(sp => ...)` を factory 化、 SileroVadDetector 構築失敗時は `NullVoiceActivityDetector` (常に prob=1.0、 旧素通し動作と等価) にフォールバック (rere F-002 対応)。 onnx 同梱漏れ / アンチウイルス隔離 / VC++ Runtime 不足等でも起動 brick を防ぐ。 Singleton にすることで onnx ロード (~10ms) は起動時 1 回のみ。
- **VAD フレームサイズ (512) を変更しない**: Silero VAD v5 16kHz 専用仕様で固定。

### 表示設定 (Overlay) の管理 🎨

- **`OverlaySettings`**: `FontFamily` / `FontSize` / `FontWeight` (Normal/Bold) / `PartialTextColor` / `FinalTextColor` / `BackgroundColor` (`#AARRGGBB` 後方互換) / `BackgroundColorBase` (`#RRGGBB`) / `BackgroundOpacityPercent` (0/25/50/75/100 の 5 段階) / `DisplayDuration` / `MaxLines`。
- **背景色の三重管理**: `BackgroundColor` は派生値、 真実は `BackgroundColorBase` + `BackgroundOpacityPercent`。 SettingsViewModel の Selected\* setter で `ComposeArgbHex` 経由で同期する。 旧 settings.json (#AARRGGBB 単一フィールド) からは `SanitizeSettings` の `SplitArgbToRgbAndOpacity` で起動時 1 度だけ逆算してマイグレート。 一覧外の色は黒 50% に矯正 + `SanitizeWarnings` 経由でユーザーに通知バナー (rere F-007)。
- **フォントの太さ**: `OverlaySettings.FontWeight` を `"Normal"` または `"Bold"` で保持し、 `OverlayViewModel.ResolveFontWeight` で `Avalonia.Media.FontWeight` enum に変換して `TextBlock.FontWeight` にバインド。

### OpenAI Realtime Translation API の癖 ⚠️

`/v1/realtime?intent=translation` エンドポイントには、 標準の `/v1/realtime` と異なる非自明な挙動がある。 修正前に必ず以下を読んで。

- **`turn_detection` は session.update に入れられない**: `session.audio.input.turn_detection` も `session.turn_detection` も `Unknown parameter` で拒否される (v1.0.2 / v1.0.6 で検証済み)。 デフォルトの server VAD に任せるしかない。 `OpenAIRealtimeClient.SendSessionUpdateAsync` で turn_detection を **絶対に送らない** こと。
- **`session.input_audio_buffer.append` 形式 (session. prefix 必須)**: 通常の `input_audio_buffer.append` ではない。 `SendLoopAsync` の type は `"session.input_audio_buffer.append"` で固定。
- **`transcript.done` はセッション通算の累積全文を返す**: 各 response 単位ではなく「会話開始からの全文」が毎 done で送られてくる挙動が観測されている (2026-05-16)。 そのまま `finalText` として overlay に出すと「まあ → まあ、 → まあ、ノ → ...」と1字幕が無限成長する UX 不具合になる。 `TranslationPipelineService._lastFinalizedTranscript` で確定済みテキストを保持し、 done 受信時に **差分抽出 → 句点 `。！？.!?` で文分割 → 文ごとに新 SegmentId で emit** する設計でこの挙動を吸収している。
- **句点なしマシンガントークの分割**: 現状は API の自動句読点挿入に頼っている。 病的に句点が来ない発話があれば、 文字数しきい値で「、」フォールバック分割を追加する余地がある (未実装)。

### DI & Configuration

- DI configured in `App.axaml.cs` (`OnFrameworkInitializationCompleted`)
- Settings loaded from `settings.json` via `IOptionsMonitor<AppSettings>` (supports hot-reload)
- Runtime settings changes pushed via `ApplySettings()` pattern to bypass file watcher delay
- `AppSettings` contains sub-sections: `OpenAIRealtime`, `Overlay`, `AudioCapture`, `GameProfiles[]`, `Update`
- Single-instance enforcement via named Mutex in `Program.cs`

### UI Framework

- **Avalonia 12** with Fluent theme
- MVVM via CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`)
- Main views: `MainWindow` (process selector, controls, settings tab), `OverlayWindow` (transparent subtitle display)
- UI thread dispatch: `Dispatcher.UIThread.Post()`

## Key Conventions

- **Async**: All service methods use `Async` suffix, propagate `CancellationToken`
- **Thread safety**: `lock` for shared collections, `Channel<T>` for producer-consumer (audio send buffer)
- **Error propagation**: Event-based (`ErrorOccurred` events bubble up to UI)
- **Output paths**: Simplified — `bin/{Configuration}/` (no TFM/platform subdirectories, set in Directory.Build.props)
- **Auto-update**: Velopack with `GithubSource` (NOT `SimpleWebSource` — the latter can't resolve GitHub repo top URLs and 404s). Release via `release/**` branch push → GitHub Actions → GitHub Releases. CI uploads **all** `velopack-packages/*` assets (full pack + deltas + RELEASES manifest), not just `*-Setup.exe`.
- **Velopack 操作の直列化**: `UpdateService._velopackOpLock` (static SemaphoreSlim) で Check / Download / Apply を直列化する。 これがないと起動時自動チェック + Periodic タイマー + 手動更新が並走して `.velopack_lock` 衝突 (`AcquireLockFailedException`) になる。 例外は `ex.GetType().Name == "AcquireLockFailedException"` で文字列マッチして握る (Velopack 内部型に依存しない)。
- **API Key & 設定の保存先**: **Roaming** AppData (`%APPDATA%/RealTimeTranslator/settings.json`)。 Velopack 更新で `%LocalAppData%/RealTimeTranslator/packages` 配下が再構築されても消えないようにするため。 旧 LocalAppData 配置からは `SettingsService.MigrateLegacySettingsIfNeeded` で自動移行する。 API キーは DPAPI (CurrentUser scope) で暗号化し `dpapi:` プレフィックス付き base64 で保存。
- **Logging**: SuperLightLogger (NLog-compatible API)。 ログは `%APPDATA%/RealTimeTranslator/logs/` (Roaming AppData。 Velopack インストールルート `%LocalAppData%` と衝突しないため Roaming 配置。 Velopack 更新で再配置されてもログ自体は残す)。 「log4net → NLog 移行」コミットの実体は SuperLightLogger の `LogManager.Configure(... AddSuperLightFile ...)`。
- **プロセス確実終了**: `App.axaml.cs` の `OnShutdownRequested` で多重防御を組んでいる: (1) AudioCapture を明示停止 → (2) ITranslationPipelineService.DisposeAsync を 2秒タイムアウト → (3) `Environment.Exit(0)` → (4) 5秒後の `Process.Kill()` 保険タイマー。 NAudio / WASAPI のネイティブハンドルが残ってプロセスゾンビ化する経路を全部塞ぐ目的。

## Operations / トラブルシュート

- **ログの場所**: `%APPDATA%/RealTimeTranslator/logs/RealTimeTranslator_yyyyMMdd.log` (Roaming AppData、 ローテーションあり、 デフォルト 7 日間保持)。
- **設定ファイルの場所**: `%APPDATA%/RealTimeTranslator/settings.json` (Roaming)。 旧 `%LocalAppData%/RealTimeTranslator/settings.json` からは起動時に自動移行される。 API キーは DPAPI (CurrentUser scope) で暗号化されており、別ユーザー / 別 PC では復号できない。
- **API キー漏洩疑惑時の対応**: `settings.json` の `OpenAIRealtime.ApiKey` は `dpapi:` プレフィックス付き base64 で保存されている必要がある。生の `sk-...` 形式が見えたら旧形式のまま（次回保存で自動暗号化）。
- **Velopack 更新失敗時**: `LoggerService.LogError` で `UpdateService.CheckAndDownloadCoreAsync 失敗` の例外詳細が記録される。FeedUrl が `github.com` または `objects.githubusercontent.com` 以外を指している場合、`TryGetValidFeedUri` で拒否される。 `.velopack_lock` 衝突は `_velopackOpLock` セマフォで通常起きない設計だが、 別プロセス (旧 version) が同時起動している場合は `AcquireLockFailedException` を握ってスキップする。
- **接続失敗時**: `OpenAIRealtimeClient.ValidateEndpoint` で wss + `api.openai.com` 以外は拒否される。`KeepAliveInterval=15s` / `KeepAliveTimeout=20s` で半切断を検知し、`NetworkChange.NetworkAvailabilityChanged` で復帰時に再接続カウンタをリセットする。
- **不明な API イベントの調査**: `OpenAIRealtimeClient.ProcessMessage` に `_seenEventTypes` HashSet があり、 初見の event type を Info ログに1回だけ吐く診断機構が入っている。 「字幕が来ない / 文が切れない」系の問題はまずログで「どの event が来ているか」を確認すること。
- **クラッシュレポート**: `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException` / `Dispatcher.UIThread.UnhandledException` の 3 つのハンドラがログに記録する。バックグラウンド Task の例外もここで拾える。

## バージョン管理 / リリース

- **バージョン番号の更新は `/vava` スキル経由のみ**: コード修正のついでに `Directory.Build.props` の `<Version>` を勝手に上げない。 `/vava` がバージョン計算 + コミット + `release/X.Y.Z` ブランチ作成 + GitHub Actions トリガー + 古いリリースブランチ掃除まで一括処理する。
- **`release/**` push → CI**: `.github/workflows/release.yml` が `release/X.Y.Z` ブランチ push で発火し、 Velopack pack → GitHub Release 作成 → 全 assets アップロードまで自動化されている。 リトライは `/vava` の retry モード (同 version で fast-forward push) を使う。
- **メモリーバンクの freshness gate**: 一部の git commit hook で `memory-bank/RealTimeTranslator/activeContext.md` の更新日時がコード変更より古いとブロックされる。 大きめの変更後は `memory_bank_update` で `activeContext.md` を更新してからコミットすること。
