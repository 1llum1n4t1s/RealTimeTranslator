namespace RealTimeTranslator.Core.Interfaces;

/// <summary>
/// 音声活動検出 (Voice Activity Detection)。
/// 16kHz float32 PCM のフレームを受け取り、 そのフレームが「人の声を含んでいるか」の
/// 確率 (0.0-1.0) を返す。 BGM や効果音を除外して OpenAI への送信 token を節約する目的で使う。
/// 実装は LSTM ベースの内部 state を持つため、 同一インスタンスで連続フレームを
/// 順番に処理し、 セッション切り替え時には <see cref="Reset"/> を呼ぶこと。
/// </summary>
public interface IVoiceActivityDetector : IDisposable
{
    /// <summary>1 推論あたりに必要なサンプル数。 Silero VAD v5 / 16kHz では 512 (= 32ms)。</summary>
    int RequiredFrameSize { get; }

    /// <summary>想定サンプルレート (16000)。 入力フレームはこのレートに揃える必要がある。</summary>
    int SampleRate { get; }

    /// <summary>
    /// 与えられた 1 フレームから speech probability を計算する。
    /// </summary>
    /// <param name="frame16kHz"><see cref="RequiredFrameSize"/> サンプルの 16kHz float32 PCM</param>
    /// <returns>speech probability (0.0 = 確実に非声 / 1.0 = 確実に声)</returns>
    float DetectSpeechProb(ReadOnlySpan<float> frame16kHz);

    /// <summary>内部 LSTM state をリセットする (再キャプチャ開始時 / コンテキスト切れ時に呼ぶ)。</summary>
    void Reset();
}
