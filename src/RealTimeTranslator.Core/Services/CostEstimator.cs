namespace RealTimeTranslator.Core.Services;

/// <summary>
/// OpenAI Realtime API の audio input token / 推定コストを概算するヘルパー。
/// 正確な値はサーバー側 <c>response.done</c> の <c>usage.input_token_details.audio_tokens</c> を使うべきだが、
/// 取得できないケース (response が来る前 / フォールバック) では送信した秒数から推定する。
///
/// 単価出典 (2026-05 時点公表値、 USD per 1M input audio tokens):
/// - gpt-realtime-2 / gpt-realtime-1.5 (現行フル)        : $32 / 1M
/// - gpt-realtime-mini (現行 mini、 節約版)              : $10 / 1M
/// - gpt-realtime-translate (Translation 専用エンドポイント) : per-minute 課金 ($0.034/min audio output)。
///   audio input 課金は公式 pricing 表に明示なしのため、 安全側で gpt-realtime-2 と同等 ($32/1M) を見積もり
/// - gpt-4o-realtime-preview (旧フル、 deprecated)        : $100 / 1M (旧価格、 互換維持のため保持)
/// - gpt-4o-mini-realtime-preview (旧 mini、 deprecated)  : $10  / 1M
/// 不明モデルは現行フルレート ($32) で見積もり (過大評価寄りの安全側)。
///
/// 注意: 「実際の請求額」は OpenAI ダッシュボード (Settings → Billing) で確認すること。
/// 本クラスの推定値は UI 表示用の目安であり、 OpenAI 側の料金改定や per-minute 課金の精算には追従しきれない。
/// </summary>
public static class CostEstimator
{
    // ───── 現行料金 (2026-05) ─────
    private const decimal GptRealtimeFullRatePerMillion = 32m;   // gpt-realtime-2 / gpt-realtime-1.5 / gpt-realtime-translate
    private const decimal GptRealtimeMiniRatePerMillion = 10m;   // gpt-realtime-mini

    // ───── 旧料金 (deprecated、 互換維持) ─────
    // 旧 settings.json が "gpt-4o-realtime-preview" を指している既存ユーザー向け。
    // OpenAI 側がモデルを完全廃止すれば実害なくなるが、 それまでは旧料金で見積もる方が当時の課金実感に近い。
    private const decimal LegacyGpt4oRealtimeRatePerMillion = 100m;  // gpt-4o-realtime-preview
    private const decimal LegacyGpt4oMiniRealtimeRatePerMillion = 10m; // gpt-4o-mini-realtime-preview

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
    ///
    /// 優先順位 (上から評価、 マッチした時点で確定):
    ///  1. "gpt-4o-mini-realtime"  → 旧 mini      = $10  (legacy 互換)
    ///  2. "gpt-4o-realtime"       → 旧フル       = $100 (legacy 互換)
    ///  3. "mini"                  → 現行 mini    = $10  (gpt-realtime-mini 等)
    ///  4. "realtime" or "translate" → 現行フル   = $32  (gpt-realtime-2 / gpt-realtime-translate 等)
    ///  5. 不明                    → 現行フル    = $32  (安全側 fallback)
    ///
    /// gpt-4o 系のレガシー判定を先に置くのは、 旧モデルが OpenAI 側にまだ残っていた時代の課金実感と
    /// 整合させるため (旧 $100/h を想定していた人が UI で $32/h と表示されると「現実より安い」と
    /// 誤解する経路を避ける)。
    /// </summary>
    public static decimal ResolveRatePerMillion(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return GptRealtimeFullRatePerMillion;
        var lower = modelName.ToLowerInvariant();

        // 1. 旧 mini (gpt-4o-mini-realtime-preview) — 詳細名から優先評価
        if (lower.Contains("gpt-4o-mini-realtime")) return LegacyGpt4oMiniRealtimeRatePerMillion;
        // 2. 旧フル (gpt-4o-realtime-preview)
        if (lower.Contains("gpt-4o-realtime")) return LegacyGpt4oRealtimeRatePerMillion;
        // 3. 現行 mini 系全般 (gpt-realtime-mini / gpt-realtime-mini-* 日付付きバリアント等)
        if (lower.Contains("mini")) return GptRealtimeMiniRatePerMillion;
        // 4. 現行フル系全般 (gpt-realtime-2 / gpt-realtime-1.5 / gpt-realtime-translate / gpt-realtime 等)
        if (lower.Contains("realtime") || lower.Contains("translate")) return GptRealtimeFullRatePerMillion;
        // 5. 不明モデルは現行フルレート $32 で見積もる (旧 $100 から下げて 2026 年現実に合わせ済)
        return GptRealtimeFullRatePerMillion;
    }
}
