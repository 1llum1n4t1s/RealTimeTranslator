namespace RealTimeTranslator.Core.Interfaces;

using RealTimeTranslator.Core.Models;

/// <summary>字幕用キャプチャと独立したマイク入力・Wave Link 出力。</summary>
public interface IVoiceAudioService : IAsyncDisposable
{
    /// <summary>最後に作成したセッションの識別子。停止後も保持する。</summary>
    long SessionId { get; }
    IReadOnlyList<VoiceAudioDevice> GetMicrophones();
    IReadOnlyList<VoiceAudioDevice> GetOutputs();
    event Action<VoiceMicrophoneFrame>? MicrophoneFrame;
    event Action<long, Exception>? ErrorReceived;
    /// <summary>次の開始に適用するゲーム送信用 PTT。既存実装では無効設定だけを受け付ける。</summary>
    void ConfigureGamePushToTalk(VoiceTranslationSettings settings)
    {
        if (settings.GamePushToTalkEnabled)
            throw new NotSupportedException("この音声出力はゲーム送信用PTTに対応していません。");
    }
    /// <summary>自動入力を除いたキー状態。専用の追跡を持たない実装では null。</summary>
    bool? GetPhysicalPushToTalkKeyState(int virtualKey) => null;
    Task StartAsync(string microphoneId, string outputDeviceId, CancellationToken ct = default);
    void WriteOutput(byte[] pcm16, int sampleRate);
    void ClearOutput();
    /// <summary>未再生の音声だけを破棄し、すでに再生へ渡した音声のゲームPTT保持は残す。</summary>
    void ClearPendingOutput() => ClearOutput();
    Task StopAsync();
}
