using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Interfaces;

/// <summary>物理マイクから VB-CABLE への音声送信セッションを所有する。</summary>
public interface IVoiceTranslationService : IAsyncDisposable
{
    VoiceTranslationState State { get; }
    VoiceSendMode Mode { get; }
    event Action<VoiceTranslationState>? StateChanged;
    event Action<string>? TranscriptReceived;
    event Action<float, float>? LevelsChanged;
    event Action<Exception>? ErrorReceived;
    Task StartAsync(VoiceTranslationSettings settings, OpenAIRealtimeSettings openAi, CancellationToken ct = default);
    Task SetModeAsync(VoiceSendMode mode, CancellationToken ct = default);
    Task StopAsync();
}
