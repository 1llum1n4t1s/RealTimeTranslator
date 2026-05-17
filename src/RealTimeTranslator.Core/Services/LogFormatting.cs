namespace RealTimeTranslator.Core.Services;

/// <summary>
/// ログ整形用の共通ヘルパー。
///
/// rere レビュー D1: OpenAIRealtimeClient と TranslationPipelineService に同名・同実装で
/// 重複していた TruncateForLog / ShouldLogAtCount を 1 箇所に集約。 MainViewModel 側の
/// 簡易 40 文字 truncate もここ経由に統一することで「PII 漏洩抑制方針 (40 文字 + ...)」と
/// 「高頻度ログの間引き戦略」を 1 ファイルで管理する。
/// </summary>
public static class LogFormatting
{
    public const int DefaultTruncateLength = 40;

    /// <summary>
    /// 長文を <paramref name="maxLength"/> 文字に切り詰めて末尾に "..." を付ける。
    /// delta / transcript / 字幕本文など PII を含み得る文字列をログに出す前に必ず通す。
    /// </summary>
    public static string TruncateForLog(string? text, int maxLength = DefaultTruncateLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    /// <summary>
    /// 高頻度カウンタログの間引き判定。
    /// 1, 10, 50, 100, 200, ..., 1000, 1500, ..., 10000, 11000, ... と
    /// 序盤を密に・後半を粗にすることで全体傾向と異常を両方追う。
    /// </summary>
    public static bool ShouldLogAtCount(long count)
    {
        if (count == 1 || count == 10 || count == 50) return true;
        if (count < 1000) return count % 100 == 0;
        if (count < 10000) return count % 500 == 0;
        return count % 1000 == 0;
    }
}
