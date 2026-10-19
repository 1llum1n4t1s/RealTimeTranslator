using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Interfaces;

/// <summary>
/// OpenAI Realtime Translation API の字幕クライアントと検証用実装の境界。
/// </summary>
public interface IRealtimeTranscriber : IAsyncDisposable, IDisposable
{
    ConnectionState State { get; }

    /// <summary>
    /// OpenAI へ送信する mono PCM16 音声のサンプルレート (Hz)。
    /// 24 kHz の送信音声形式をパイプラインと共有する。
    /// </summary>
    int InputSampleRate { get; }

    /// <summary>
    /// 送信前にチャネルから DropOldest で破棄された音声チャンクの累計数。
    /// 字幕が抜けた原因が NW 詰まりか API 遅延か判別する診断メトリクス。
    /// </summary>
    long DroppedAudioChunkCount { get; }

    /// <summary>
    /// 接続中セッションで <see cref="SendAudio"/> 経由で実際にサーバーへ送られた
    /// PCM16 サンプル数 (24kHz 換算) の累積。 統計表示・cost 概算・自動 Pause 判定に使う。
    /// セッションをまたぐと <see cref="ConnectAsync"/> 内でリセットされる。
    /// </summary>
    long TotalAudioInputSamples24kHz { get; }

    /// <summary>
    /// サーバーから <c>response.done</c> の <c>usage.input_token_details.audio_tokens</c> として
    /// 報告された audio input tokens の累積。 サーバーが報告しない場合は 0 のまま。
    /// </summary>
    long ServerReportedAudioInputTokens { get; }

    event Action<string>? TranscriptDeltaReceived;
    event Action<string>? TranscriptCompleted;
    event Action<Exception>? ErrorReceived;
    event Action<ConnectionState>? StateChanged;

    Task ConnectAsync(OpenAIRealtimeSettings settings, CancellationToken ct = default);
    void SendAudio(byte[] pcm16Audio);

    Task DisconnectAsync();
}
