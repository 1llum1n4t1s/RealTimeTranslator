namespace RealTimeTranslator.Core.Interfaces;

/// <summary>
/// ASR（Automatic Speech Recognition）サービスのインターフェース
/// 二段構え構成：低遅延ASR（仮字幕）と高精度ASR（確定字幕）
/// </summary>
public interface IASRService : IDisposable
{
    /// <summary>
    /// 低遅延ASRで音声を文字起こし（仮字幕用）
    /// small/mediumモデルを使用し、即時性を重視
    /// </summary>
    /// <param name="segment">発話区間</param>
    /// <returns>仮字幕の文字起こし結果</returns>
    Task<TranscriptionResult> TranscribeFastAsync(SpeechSegment segment);

    /// <summary>
    /// 高精度ASRで音声を文字起こし（確定字幕用）
    /// large系モデルを使用し、精度を重視
    /// </summary>
    /// <param name="segment">発話区間</param>
    /// <returns>確定字幕の文字起こし結果</returns>
    Task<TranscriptionResult> TranscribeAccurateAsync(SpeechSegment segment);

    /// <summary>
    /// ホットワードリストを設定（固有名詞対策）
    /// </summary>
    /// <param name="hotwords">ホットワードのリスト</param>
    void SetHotwords(IEnumerable<string> hotwords);

    /// <summary>
    /// 初期プロンプトを設定
    /// </summary>
    /// <param name="prompt">初期プロンプト</param>
    void SetInitialPrompt(string prompt);

    /// <summary>
    /// ASR誤変換補正辞書を設定
    /// </summary>
    /// <param name="dictionary">誤変換補正辞書（原文 -> 補正後）</param>
    void SetCorrectionDictionary(Dictionary<string, string> dictionary);

    /// <summary>
    /// モデルが読み込まれているか
    /// </summary>
    bool IsModelLoaded { get; }
}

/// <summary>
/// 文字起こし結果
/// </summary>
public class TranscriptionResult
{
    /// <summary>
    /// 発話ID（SpeechSegmentと紐づけ）
    /// </summary>
    public string SegmentId { get; set; } = string.Empty;

    /// <summary>
    /// 文字起こしテキスト
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// 確定字幕かどうか
    /// </summary>
    public bool IsFinal { get; set; }

    /// <summary>
    /// 信頼度（0.0〜1.0）
    /// </summary>
    public float Confidence { get; set; }

    /// <summary>
    /// 検出された言語
    /// </summary>
    public string DetectedLanguage { get; set; } = "en";

    /// <summary>
    /// 処理時間（ミリ秒）
    /// </summary>
    public long ProcessingTimeMs { get; set; }
}
