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
    Task StartAsync(string microphoneId, string outputDeviceId, CancellationToken ct = default);
    void WriteOutput(byte[] pcm16, int sampleRate);
    void ClearOutput();
    Task StopAsync();
}
