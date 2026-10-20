using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Interfaces;

/// <summary>物理マイクから Wave Link への音声送信セッションを所有する。</summary>
public interface IVoiceTranslationService : IAsyncDisposable
{
    VoiceTranslationState State { get; }
    VoiceSendMode Mode { get; }
    bool IsMonitoring { get; }
    event Action<VoiceTranslationState>? StateChanged;
    event Action<string>? TranscriptReceived;
    event Action<float, float>? LevelsChanged;
    event Action<Exception>? ErrorReceived;
    event Action<bool>? MonitoringChanged;
    event Action<Exception>? MonitoringErrorReceived;
    Task StartAsync(VoiceTranslationSettings settings, OpenAIRealtimeSettings openAi, CancellationToken ct = default);
    Task SetModeAsync(VoiceSendMode mode, CancellationToken ct = default);
    Task SetMonitoringAsync(string? outputDeviceId, CancellationToken ct = default);
    /// <summary>ボイス通訳専用の入力・出力ゲインを、接続を作り直さず反映する。</summary>
    void SetGains(float inputGainDb, float outputGainDb);
    Task StopAsync();
}
