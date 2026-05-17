namespace RealTimeTranslator.Core.Services;

/// <summary>
/// OpenAI Realtime API の audio input token / 推定コストを概算するヘルパー。
/// 正確な値はサーバー側 <c>response.done</c> の <c>usage.input_token_details.audio_tokens</c> を使うべきだが、
/// 取得できないケース (response が来る前 / フォールバック) では送信した秒数から推定する。
///
/// 単価出典 (2026-05 時点公表値、 USD per 1M input audio tokens):
/// - gpt-4o-realtime-preview         : $100 / 1M
/// - gpt-4o-mini-realtime-preview    : $10  / 1M
/// 不明モデルはフル料金 ($100) で見積もり (過小評価を避ける)。
/// </summary>
public static class CostEstimator
{
    private const decimal GptRealtimeFullRatePerMillion = 100m;
    private const decimal GptMiniRealtimeRatePerMillion = 10m;

    /// <summary>OpenAI Realtime API の input audio tokens / 秒 (公表値 = 100)。</summary>
    private const double InputAudioTokensPerSecond = 100.0;

    /// <summary>
    /// 送信した音声の秒数から audio input token 数を概算する。
    /// </summary>
    public static long EstimateTokensFromAudioSeconds(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return 0;
        return (long)(seconds * InputAudioTokensPerSecond);
    }

    /// <summary>
    /// PCM サンプル数とサンプルレートから audio input token 数を概算する。
    /// </summary>
    public static long EstimateTokensFromSamples(long sampleCount, int sampleRate)
    {
        if (sampleCount <= 0 || sampleRate <= 0) return 0;
        return EstimateTokensFromAudioSeconds((double)sampleCount / sampleRate);
    }

    /// <summary>
    /// モデル名と token 数から推定コスト (USD) を計算する。
    /// </summary>
    public static decimal EstimateUsd(string? modelName, long audioInputTokens)
    {
        if (audioInputTokens <= 0) return 0m;
        var ratePerMillion = ResolveRatePerMillion(modelName);
        return ratePerMillion * audioInputTokens / 1_000_000m;
    }

    /// <summary>
    /// モデル名から audio input token の単価 (USD per 1M) を解決する。
    /// "mini" を含む場合は mini レート、 それ以外はフルレート。
    /// </summary>
    public static decimal ResolveRatePerMillion(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return GptRealtimeFullRatePerMillion;
        var lower = modelName.ToLowerInvariant();
        // "gpt-4o-mini-realtime-preview" → mini, "gpt-4o-realtime-preview" → full
        if (lower.Contains("mini")) return GptMiniRealtimeRatePerMillion;
        return GptRealtimeFullRatePerMillion;
    }
}
