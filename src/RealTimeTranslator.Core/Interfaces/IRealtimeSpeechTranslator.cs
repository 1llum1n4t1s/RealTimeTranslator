namespace RealTimeTranslator.Core.Interfaces;

/// <summary>
/// 翻訳字幕と、翻訳された PCM16 mono 音声を受信するクライアント。
/// </summary>
public interface IRealtimeSpeechTranslator : IRealtimeTranscriber
{
    /// <summary>翻訳音声のサンプルレート (Hz)。</summary>
    int OutputSampleRate { get; }

    /// <summary>
    /// 完全な PCM16 mono 音声チャンク。受信処理を止めないよう、購読側は再生キューへ渡す。
    /// </summary>
    event Action<byte[]>? TranslatedAudioReceived;
}
