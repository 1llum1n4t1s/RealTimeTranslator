# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

RealTimeTranslator is a Windows desktop app for real-time subtitle translation. It captures audio from a specific process, runs VAD (Silero) → ASR (Whisper) → LLM translation (LLamaSharp) and displays translated subtitles in a transparent overlay window.

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
Audio Capture → Silero VAD → Whisper ASR → LLM Translation → Overlay
```

### Project Structure

- **RealTimeTranslator.Core** — Interfaces, models, and infrastructure services (audio capture, VAD, logging). No UI dependency.
- **RealTimeTranslator.Translation** — ASR (Whisper.net) and translation (LLamaSharp) implementations. Prompt builders per model format (Phi3, Mistral, Gemma, Qwen). References Core.
- **RealTimeTranslator.UI** — Avalonia desktop app. Views, ViewModels (CommunityToolkit.Mvvm), DI setup, pipeline orchestration. References Core + Translation.
- **RealTimeTranslator.Tests** — MSTest unit tests. References Core.

### Key Interfaces (in Core/Interfaces/)

| Interface | Responsibility |
|---|---|
| `ITranslationPipelineService` | Orchestrates the full pipeline. Events: `SubtitleGenerated`, `StatsUpdated`, `ErrorOccurred` |
| `IAudioCaptureService` | WASAPI process loopback capture (16kHz mono float) |
| `IVADService` | Silero VAD v4 via ONNX. Speech segment detection with configurable sensitivity |
| `IASRService` | Whisper transcription. Dual-mode: Fast (small model) + Accurate (large model) |
| `ITranslationService` | LLM inference via LLamaSharp. Auto-detects GGUF model format |

### Pipeline Flow (TranslationPipelineService in UI/Services/)

1. `AudioCaptureService` feeds audio chunks via `AudioDataAvailable` event
2. `VADService` detects speech segments, outputs `SpeechSegment`
3. Segments enqueued to a `Channel<SpeechSegment>` (bounded, 100 items, DropOldest)
4. Consumer loop runs ASR (fast then accurate) with semaphore-limited parallelism (max 2)
5. Translation results fire `SubtitleGenerated` event → `OverlayViewModel` displays them

### DI & Configuration

- DI configured in `App.axaml.cs` (`OnFrameworkInitializationCompleted`)
- Settings loaded from `settings.json` via `IOptionsMonitor<AppSettings>` (supports hot-reload)
- `AppSettings` contains sub-sections: `ASR`, `Translation`, `Overlay`, `AudioCapture`, `GameProfiles[]`, `Update`
- Single-instance enforcement via named Mutex in `Program.cs`

### UI Framework

- **Avalonia 11** with Semi.Avalonia theme (recently migrated from WPF)
- MVVM via CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`)
- Main views: `MainWindow` (process selector, controls), `OverlayWindow` (transparent subtitle display), `SettingsWindow`
- UI thread dispatch: `Dispatcher.UIThread.Post()`

### GPU & Native Libraries

- ONNX Runtime (DirectML/CUDA) for VAD
- LLamaSharp with CUDA 12 backend for translation
- Whisper.net with GPU runtime for ASR
- GPU type configured in `AppSettings.ASR.GPU` (Auto/NVIDIA_CUDA/AMD_Vulkan/CPU)
- CPU variant filtering at publish time: only AVX2 kept (configurable via `LLamaSharpCpuVariant` MSBuild property)
- Non-Windows runtimes stripped during publish

## Key Conventions

- **Async**: All service methods use `Async` suffix, propagate `CancellationToken`
- **Thread safety**: `lock` for shared collections, `volatile` for cross-thread flags, `Channel<T>` for producer-consumer
- **Error propagation**: Event-based (`ErrorOccurred` events bubble up to UI)
- **Output paths**: Simplified — `bin/{Configuration}/` (no TFM/platform subdirectories, set in Directory.Build.props)
- **Auto-update**: Velopack. Release via `release/**` branch push → GitHub Actions → GitHub Releases

## Models (Auto-Downloaded)

Models download automatically on first launch if missing:
- **VAD**: `models/vad/silero_vad.onnx`
- **ASR**: `ggml-base.bin` (fast), `ggml-large-v3.bin` (accurate) — Whisper GGML format
- **Translation**: `Phi-3-mini-4k-instruct-q4.gguf` — LLamaSharp GGUF format
