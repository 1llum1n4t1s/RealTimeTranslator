using Microsoft.Extensions.Options;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services; // ISettingsService

namespace RealTimeTranslator.Tests;

// 複数テストファイル (VadGate.test.cs / TranslationPipelineService.SentenceSplit.test.cs)
// で共有する test double 群。 重複定義の解消が目的 (rere /opop Cleaner #1)。
// TestAudioCaptureService の event は実際に未使用 (mock 実装上避けられない) なので CS0067 抑制。
// TestRealtimeTranscriber の event は RaiseDelta / RaiseDone / RaiseStateChanged 経由で発火するので抑制不要。

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

/// <summary>
/// IRealtimeTranscriber のテスト用 mock。 rere /opop Cleaner v1.0.32 #B2-004 対応で
/// SentenceSplit / Happy / Adversarial の 3 テストファイルから共通化した。
/// 状態変更は ConnectAsync / DisconnectAsync 内で行い、 transcript / error event は
/// RaiseDelta / RaiseDone / RaiseStateChanged メソッドで明示的に発火する。
/// </summary>
internal sealed class TestRealtimeTranscriber : IRealtimeTranscriber
{
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public long TotalAudioInputSamples24kHz => 0;
    public long ServerReportedAudioInputTokens => 0;
    public event Action<string>? TranscriptDeltaReceived;
    public event Action<string>? TranscriptCompleted;
#pragma warning disable CS0067 // ErrorReceived は Raise ヘルパーがなく未使用 (将来 error 発火 mock が必要になったら RaiseError を追加して解除)
    public event Action<Exception>? ErrorReceived;
#pragma warning restore CS0067
    public event Action<ConnectionState>? StateChanged;

    public Task ConnectAsync(OpenAIRealtimeSettings settings, CancellationToken ct = default)
    {
        State = ConnectionState.Connected;
        StateChanged?.Invoke(State);
        return Task.CompletedTask;
    }
    public void SendAudio(byte[] pcm16Audio) { }
    public Task DisconnectAsync()
    {
        State = ConnectionState.Disconnected;
        StateChanged?.Invoke(State);
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }

    public void RaiseDelta(string delta) => TranscriptDeltaReceived?.Invoke(delta);
    public void RaiseDone(string transcript) => TranscriptCompleted?.Invoke(transcript);
    public void RaiseStateChanged(ConnectionState newState)
    {
        State = newState;
        StateChanged?.Invoke(newState);
    }
}

/// <summary>
/// IVoiceActivityDetector の no-op テスト mock (常に prob=0)。 rere /opop Cleaner v1.0.32 #B2-004 対応で
/// SentenceSplit / Happy / Adversarial の 3 テストファイルから共通化した。
/// 多くのテストは RaiseDelta / RaiseDone を直接呼ぶため DetectSpeechProb は実行されないが、
/// DI 注入用に必要 (TranslationPipelineService が IVoiceActivityDetector を要求するため)。
/// </summary>
internal sealed class TestVoiceActivityDetector : IVoiceActivityDetector
{
    public int RequiredFrameSize => 512;
    public int SampleRate => 16000;
    public float DetectSpeechProb(ReadOnlySpan<float> frame16kHz) => 0f;
    public void Reset() { }
    public void Dispose() { }
}
