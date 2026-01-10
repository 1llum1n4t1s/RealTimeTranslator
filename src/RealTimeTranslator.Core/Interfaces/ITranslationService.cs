using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Interfaces;

/// <summary>
/// 翻訳サービスのインターフェース
/// 確定ASRのテキストのみを翻訳（仮字幕は翻訳しない）
/// </summary>
public interface ITranslationService : IDisposable
{
    event EventHandler<ModelDownloadProgressEventArgs>? ModelDownloadProgress;
    event EventHandler<ModelStatusChangedEventArgs>? ModelStatusChanged;

    /// <summary>
    /// 翻訳エンジンを初期化
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// テキストを翻訳
    /// </summary>
    /// <param name="text">原文</param>
    /// <param name="sourceLanguage">ソース言語（デフォルト: en）</param>
    /// <param name="targetLanguage">ターゲット言語（デフォルト: ja）</param>
    /// <returns>翻訳結果</returns>
    Task<TranslationResult> TranslateAsync(string text, string sourceLanguage = "en", string targetLanguage = "ja");

    /// <summary>
    /// 用語辞書を設定（翻訳前の正規化用）
    /// </summary>
    /// <param name="dictionary">用語辞書（原文 -> 正規化後）</param>
    void SetPreTranslationDictionary(Dictionary<string, string> dictionary);

    /// <summary>
    /// 置換辞書を設定（翻訳後の補正用）
    /// </summary>
    /// <param name="dictionary">置換辞書（翻訳結果 -> 補正後）</param>
    void SetPostTranslationDictionary(Dictionary<string, string> dictionary);

    /// <summary>
    /// 翻訳キャッシュをクリア
    /// </summary>
    void ClearCache();

    /// <summary>
    /// モデルが読み込まれているか
    /// </summary>
    bool IsModelLoaded { get; }
}

/// <summary>
/// 翻訳結果
/// </summary>
public class TranslationResult
{
    /// <summary>
    /// 原文
    /// </summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>
    /// 翻訳後テキスト
    /// </summary>
    public string TranslatedText { get; set; } = string.Empty;

    /// <summary>
    /// ソース言語
    /// </summary>
    public string SourceLanguage { get; set; } = "en";

    /// <summary>
    /// ターゲット言語
    /// </summary>
    public string TargetLanguage { get; set; } = "ja";

    /// <summary>
    /// キャッシュから取得したか
    /// </summary>
    public bool FromCache { get; set; }

    /// <summary>
    /// 処理時間（ミリ秒）
    /// </summary>
    public long ProcessingTimeMs { get; set; }
}
