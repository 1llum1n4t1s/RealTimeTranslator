using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Interfaces;

/// <summary>
/// ストリーミング音声 → テキスト（翻訳）の Realtime API クライアント抽象。
/// 現状の実装は <c>OpenAIRealtimeClient</c> のみだが、将来 Gemini Live / ローカル Whisper など
/// 別プロバイダ実装を差し替えやすくするための抽象境界。
/// </summary>
/// <remarks>
/// 設定型 (<see cref="OpenAIRealtimeSettings"/>) は現状 OpenAI 固有だが、interface ユーザーは
/// プロバイダ別の Settings 型を直接渡さず、専用 ViewModel / Adapter 経由で扱うことが望ましい。
/// 段階的にプロバイダ非依存の Settings 抽象に置き換えていく。
/// </remarks>
public interface IRealtimeTranscriber : IAsyncDisposable, IDisposable
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
