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

### Pipeline Flow (TranslationPipelineService in Core/Services/)

1. `AudioCaptureService` feeds 16kHz mono float32 audio chunks via `AudioDataAvailable` event
2. `TranslationPipelineService` resamples to 24kHz, converts to PCM16, sends via `OpenAIRealtimeClient`
3. API returns translation text as `response.output_audio_transcript.delta` / `response.output_text.delta` (streaming) and `.done` (final). Legacy event names (`output_transcript.*`, `response.audio_transcript.*`) are still recognized for compatibility.
4. Delta events fire `SubtitleGenerated` with `IsFinal=false` (throttled per `TranslationPipelineService.DeltaThrottle`, 現在 30ms), done fires with `IsFinal=true`
5. `OverlayViewModel` displays subtitles, tracking updates by `SegmentId`

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
