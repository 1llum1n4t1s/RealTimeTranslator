using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// OpenAI Realtime API から受信した error イベント由来の例外。
/// 種別 (Kind) と「ユーザーに見せる日本語メッセージ」(FriendlyMessage) を持つ。
///
/// rere レビュー後の追加修正 (2026-05-17 ゆろさんのクォータ超過ログ起点):
/// - QuotaExceeded / InvalidApiKey / Forbidden は再接続しても回復しないため <see cref="IsFatal"/> = true
/// - RateLimit / BadRequest / Unknown は一過性の可能性があり再接続可能
/// </summary>
public sealed class OpenAIApiException : InvalidOperationException
{
    public OpenAIApiErrorKind Kind { get; }

    /// <summary>ユーザー向けの日本語補助メッセージ (UI に出す前提)。</summary>
    public string FriendlyMessage { get; }

    /// <summary>OpenAI API が返した原文メッセージ (ログ / デバッグ用)。</summary>
    public string OriginalMessage { get; }

    /// <summary>
    /// 再接続しても回復しない致命的エラーか。
    /// true の場合、 OpenAIRealtimeClient は _shouldReconnect=false + Failed 状態に倒し、
    /// TranslationPipelineService はキャプチャを即停止する。
    /// </summary>
    public bool IsFatal => Kind is OpenAIApiErrorKind.QuotaExceeded
                              or OpenAIApiErrorKind.InvalidApiKey
                              or OpenAIApiErrorKind.Forbidden;

    public OpenAIApiException(OpenAIApiErrorKind kind, string friendlyMessage, string originalMessage)
        : base($"{friendlyMessage}（OpenAI 原文: {originalMessage}）")
    {
        Kind = kind;
        FriendlyMessage = friendlyMessage;
        OriginalMessage = originalMessage;
    }

    /// <summary>
    /// OpenAI からの error.message / error.code を元に <see cref="OpenAIApiErrorKind"/> を判定する。
    /// 既知パターンは大文字小文字無視で部分マッチ、 該当なしは Unknown。
    /// </summary>
    public static OpenAIApiErrorKind Classify(string? message, string? code)
    {
        var m = message ?? string.Empty;
        var c = code ?? string.Empty;

        if (Contains(m, "exceeded your current quota") || Contains(m, "insufficient_quota") || Contains(c, "insufficient_quota"))
            return OpenAIApiErrorKind.QuotaExceeded;

        if (Contains(c, "invalid_api_key") || Contains(c, "incorrect_api_key")
            || Contains(m, "invalid api key") || Contains(m, "incorrect api key"))
            return OpenAIApiErrorKind.InvalidApiKey;

        if (Contains(c, "rate_limit") || Contains(m, "rate limit"))
            return OpenAIApiErrorKind.RateLimit;

        if (Contains(c, "permission") || Contains(m, "forbidden") || Contains(c, "model_not_found"))
            return OpenAIApiErrorKind.Forbidden;

        if (Contains(c, "invalid_request_error") || Contains(c, "unknown_parameter") || Contains(m, "invalid value"))
            return OpenAIApiErrorKind.BadRequest;

        return OpenAIApiErrorKind.Unknown;

        static bool Contains(string haystack, string needle)
            => haystack.Contains(needle, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 種別に応じた日本語の補助メッセージを返す。 UI に出す前提のシンプルな案内文。
    /// </summary>
    public static string FriendlyMessageFor(OpenAIApiErrorKind kind, string originalMessage) => kind switch
    {
        OpenAIApiErrorKind.QuotaExceeded =>
            "OpenAI API のクォータを超過しました。 https://platform.openai.com/account/billing で課金設定 / 残高を確認してください。",
        OpenAIApiErrorKind.InvalidApiKey =>
            "OpenAI API キーが無効です。 設定画面で正しいキーを入力するか、 https://platform.openai.com/api-keys で再発行してください。",
        OpenAIApiErrorKind.RateLimit =>
            "OpenAI API のレート制限に達しました。 しばらく待ってから再試行してください。",
        OpenAIApiErrorKind.Forbidden =>
            "OpenAI API へのアクセス権限がありません。 モデル (gpt-realtime-translate) の利用可否や組織設定を確認してください。",
        OpenAIApiErrorKind.BadRequest =>
            "OpenAI API リクエストが不正でした。 設定値 (Model / Endpoint / OutputLanguage) を確認してください。",
        _ => string.IsNullOrWhiteSpace(originalMessage)
            ? "OpenAI API から不明なエラーが返されました。"
            : $"OpenAI API エラー: {originalMessage}",
    };
}
