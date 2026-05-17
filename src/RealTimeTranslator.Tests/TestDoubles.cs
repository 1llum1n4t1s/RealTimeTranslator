using Microsoft.Extensions.Options;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services; // ISettingsService

namespace RealTimeTranslator.Tests;

// 複数テストファイル (VadGate.test.cs / TranslationPipelineService.SentenceSplit.test.cs)
// で共有する test double 群。 重複定義の解消が目的 (rere /opop Cleaner #1)。
// CS0067 (未使用 event) は mock 実装の都合上避けられないため、 ここで一括抑制する。

internal sealed class TestAudioCaptureService : IAudioCaptureService
{
    public bool IsCapturing => false;
    public bool HasReceivedNonSilentDataSinceStart => false;
#pragma warning disable CS0067
    public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
    public event EventHandler<CaptureStatusEventArgs>? CaptureStatusChanged;
#pragma warning restore CS0067
    public void StartCapture(int processId) { }
    public Task<bool> StartCaptureWithRetryAsync(int processId, CancellationToken cancellationToken, SynchronizationContext? captureCreationContext = null) => Task.FromResult(true);
    public void StopCapture() { }
    public void ApplySettings(AudioCaptureSettings settings) { }
    public void Dispose() { }
}

internal sealed class TestSettingsService : ISettingsService
{
    public Task SaveAsync(AppSettings settings) => Task.CompletedTask;
    public void DecryptApiKey(AppSettings settings) { /* テストでは復号不要 */ }
}

internal sealed class StubOptionsMonitor : IOptionsMonitor<AppSettings>
{
    public AppSettings CurrentValue { get; }
    public StubOptionsMonitor(AppSettings settings) { CurrentValue = settings; }
    public AppSettings Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<AppSettings, string?> listener) => null;
}
