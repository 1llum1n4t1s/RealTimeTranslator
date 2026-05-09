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

### Pipeline Flow (TranslationPipelineService in UI/Services/)

1. `AudioCaptureService` feeds 16kHz mono float32 audio chunks via `AudioDataAvailable` event
2. `TranslationPipelineService` resamples to 24kHz, converts to PCM16, sends via `OpenAIRealtimeClient`
3. API returns translation text as `output_transcript.delta` (streaming) and `output_transcript.done` (final)
4. Delta events fire `SubtitleGenerated` with `IsFinal=false` (100ms throttled), done fires with `IsFinal=true`
5. `OverlayViewModel` displays subtitles, tracking updates by `SegmentId`

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
- **Auto-update**: Velopack. Release via `release/**` branch push → GitHub Actions → GitHub Releases
- **API Key**: BYOK model. User provides their own OpenAI API key, stored in `settings.json` (runtime copy in bin/)
