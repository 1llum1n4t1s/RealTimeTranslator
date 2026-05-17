namespace RealTimeTranslator.Core.Models;

/// <summary>
/// OpenAI Realtime API から返ってくるエラーの種別。
/// 再接続継続可否 (Fatal 系) の判定 + UI への日本語メッセージ補助に使う。
/// </summary>
public enum OpenAIApiErrorKind
{
    /// <summary>分類不能なエラー (原文メッセージで対処)</summary>
    Unknown,

    /// <summary>クォータ / billing 上限超過 (HTTP 429 / "insufficient_quota" / "exceeded your current quota")</summary>
    QuotaExceeded,

    /// <summary>API キー無効 / 期限切れ ("invalid_api_key" / "incorrect_api_key" / 401)</summary>
    InvalidApiKey,

    /// <summary>レート制限 (短期スロットリング、 リトライで回復可能)</summary>
    RateLimit,

    /// <summary>権限不足 (403 / モデル / endpoint へのアクセス不可)</summary>
    Forbidden,

    /// <summary>不正なリクエスト (400 / モデル名 / パラメータ誤り)</summary>
    BadRequest,
}
