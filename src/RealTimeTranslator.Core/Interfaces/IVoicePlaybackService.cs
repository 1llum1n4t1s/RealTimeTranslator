using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Interfaces;

/// <summary>送話用の Wave Link 出力と独立した、訳音声の試聴用再生。</summary>
public interface IVoicePlaybackService : IAsyncDisposable
{
    /// <summary>最後に作成した試聴セッションの識別子。停止後も保持する。</summary>
    /// <summary>開始ごとに増加する識別子。停止後も最後に作成した値を保持する。</summary>
    long SessionId { get; }
    IReadOnlyList<VoiceAudioDevice> GetOutputs();
    event Action<long, Exception>? ErrorReceived;
    Task StartAsync(string deviceId, CancellationToken ct = default);
    void Write(byte[] pcm16, int sampleRate);
    void Clear();
    Task StopAsync();
}
