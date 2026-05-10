using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Core.Interfaces;

public interface IOpenAIRealtimeClient : IAsyncDisposable, IDisposable
{
    ConnectionState State { get; }

    event Action<string>? TranscriptDeltaReceived;
    event Action<string>? TranscriptCompleted;
    event Action<Exception>? ErrorReceived;
    event Action<ConnectionState>? StateChanged;

    Task ConnectAsync(OpenAIRealtimeSettings settings, CancellationToken ct = default);
    void SendAudio(byte[] pcm16Audio);
    Task DisconnectAsync();
}
