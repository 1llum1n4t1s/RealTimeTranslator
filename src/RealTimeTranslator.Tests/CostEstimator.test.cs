using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

[TestClass]
public sealed class CostEstimatorTests
{
    // ───────── EstimateTokensFromAudioSeconds ─────────

    [TestMethod]
    public void EstimateTokensFromAudioSeconds_OneSecond_Returns100()
    {
        Assert.AreEqual(100L, CostEstimator.EstimateTokensFromAudioSeconds(1.0));
    }

    [TestMethod]
    public void EstimateTokensFromAudioSeconds_TenSeconds_Returns1000()
    {
        Assert.AreEqual(1000L, CostEstimator.EstimateTokensFromAudioSeconds(10.0));
    }

    [TestMethod]
    public void EstimateTokensFromAudioSeconds_Zero_ReturnsZero()
    {
        Assert.AreEqual(0L, CostEstimator.EstimateTokensFromAudioSeconds(0.0));
    }

    [TestMethod]
    public void EstimateTokensFromAudioSeconds_Negative_ReturnsZero()
    {
        Assert.AreEqual(0L, CostEstimator.EstimateTokensFromAudioSeconds(-5.0));
    }

    [TestMethod]
    public void EstimateTokensFromAudioSeconds_Nan_ReturnsZero()
    {
        Assert.AreEqual(0L, CostEstimator.EstimateTokensFromAudioSeconds(double.NaN));
    }

    [TestMethod]
    public void EstimateTokensFromAudioSeconds_Infinity_ReturnsZero()
    {
        Assert.AreEqual(0L, CostEstimator.EstimateTokensFromAudioSeconds(double.PositiveInfinity));
    }

    // ───────── EstimateTokensFromSamples ─────────

    [TestMethod]
    public void EstimateTokensFromSamples_16kHzOneSecond_Returns100()
    {
        Assert.AreEqual(100L, CostEstimator.EstimateTokensFromSamples(16000, 16000));
    }

    [TestMethod]
    public void EstimateTokensFromSamples_24kHzOneSecond_Returns100()
    {
        Assert.AreEqual(100L, CostEstimator.EstimateTokensFromSamples(24000, 24000));
    }

    [TestMethod]
    public void EstimateTokensFromSamples_InvalidSampleRate_ReturnsZero()
    {
        Assert.AreEqual(0L, CostEstimator.EstimateTokensFromSamples(16000, 0));
        Assert.AreEqual(0L, CostEstimator.EstimateTokensFromSamples(16000, -1));
    }

    // ───────── ResolveRatePerMillion: 現行モデル (2026-05) ─────────

    [TestMethod]
    public void ResolveRatePerMillion_GptRealtime2_ReturnsCurrentFullRate32()
    {
        Assert.AreEqual(32m, CostEstimator.ResolveRatePerMillion("gpt-realtime-2"));
    }

    [TestMethod]
    public void ResolveRatePerMillion_GptRealtime15_ReturnsCurrentFullRate32()
    {
        Assert.AreEqual(32m, CostEstimator.ResolveRatePerMillion("gpt-realtime-1.5"));
    }

    [TestMethod]
    public void ResolveRatePerMillion_GptRealtimeBare_ReturnsCurrentFullRate32()
    {
        Assert.AreEqual(32m, CostEstimator.ResolveRatePerMillion("gpt-realtime"));
    }

    [TestMethod]
    public void ResolveRatePerMillion_GptRealtimeTranslate_ReturnsCurrentFullRate32()
    {
        // gpt-realtime-translate は per-minute 課金だが、 安全側 (過大評価) で
        // 現行フルレート相当を見積もる。 詳細は実際の OpenAI 課金で確認。
        Assert.AreEqual(32m, CostEstimator.ResolveRatePerMillion("gpt-realtime-translate"));
    }

    [TestMethod]
    public void ResolveRatePerMillion_GptRealtimeMini_ReturnsMiniRate10()
    {
        Assert.AreEqual(10m, CostEstimator.ResolveRatePerMillion("gpt-realtime-mini"));
    }

    [TestMethod]
    public void ResolveRatePerMillion_GptRealtimeMiniDated_ReturnsMiniRate10()
    {
        // 日付付きバリアント (gpt-realtime-mini-2025-10-06 等) も mini レート
        Assert.AreEqual(10m, CostEstimator.ResolveRatePerMillion("gpt-realtime-mini-2025-10-06"));
    }

    // ───────── ResolveRatePerMillion: 旧モデル (deprecated、 互換維持) ─────────

    [TestMethod]
    public void ResolveRatePerMillion_LegacyGpt4oRealtime_ReturnsLegacyFullRate100()
    {
        // 旧 settings.json 互換: 旧フルモデルは旧価格 $100/1M で見積もる
        Assert.AreEqual(100m, CostEstimator.ResolveRatePerMillion("gpt-4o-realtime-preview"));
    }

    [TestMethod]
    public void ResolveRatePerMillion_LegacyGpt4oMiniRealtime_ReturnsLegacyMiniRate10()
    {
        Assert.AreEqual(10m, CostEstimator.ResolveRatePerMillion("gpt-4o-mini-realtime-preview"));
    }

    // ───────── ResolveRatePerMillion: フォールバック ─────────

    [TestMethod]
    public void ResolveRatePerMillion_Null_DefaultsToCurrentFullRate32()
    {
        Assert.AreEqual(32m, CostEstimator.ResolveRatePerMillion(null));
    }

    [TestMethod]
    public void ResolveRatePerMillion_Empty_DefaultsToCurrentFullRate32()
    {
        Assert.AreEqual(32m, CostEstimator.ResolveRatePerMillion(""));
    }

    [TestMethod]
    public void ResolveRatePerMillion_UnknownModel_DefaultsToCurrentFullRate32()
    {
        // 不明モデルは安全側 (過大評価) で現行フルレート扱い
        Assert.AreEqual(32m, CostEstimator.ResolveRatePerMillion("future-model-name"));
    }

    [TestMethod]
    public void ResolveRatePerMillion_CaseInsensitive_DetectsCurrentMini()
    {
        Assert.AreEqual(10m, CostEstimator.ResolveRatePerMillion("GPT-REALTIME-MINI"));
    }

    [TestMethod]
    public void ResolveRatePerMillion_CaseInsensitive_DetectsLegacyFull()
    {
        Assert.AreEqual(100m, CostEstimator.ResolveRatePerMillion("GPT-4O-REALTIME-PREVIEW"));
    }

    [TestMethod]
    public void ResolveRatePerMillion_LegacyMiniTakesPrecedenceOverGenericMini()
    {
        // gpt-4o-mini-realtime-preview は両方 mini-rate ($10) なので結果は同じだが、
        // 評価順序として旧 mini 判定が先に効くことを担保する
        var rate = CostEstimator.ResolveRatePerMillion("gpt-4o-mini-realtime-preview");
        Assert.AreEqual(10m, rate);
    }

    // ───────── EstimateUsd: 現行モデル ─────────

    [TestMethod]
    public void EstimateUsd_CurrentFullRate_OneMillionTokens_Returns32Usd()
    {
        Assert.AreEqual(32m, CostEstimator.EstimateUsd("gpt-realtime-2", 1_000_000));
    }

    [TestMethod]
    public void EstimateUsd_CurrentMiniRate_OneMillionTokens_Returns10Usd()
    {
        Assert.AreEqual(10m, CostEstimator.EstimateUsd("gpt-realtime-mini", 1_000_000));
    }

    [TestMethod]
    public void EstimateUsd_GptRealtimeTranslate_OneHourAudio_ApproxExpected()
    {
        // gpt-realtime-translate (現使用デフォルト) を audio input rate で見積もる場合:
        // 1 時間 = 3600 秒 × 100 tokens/sec = 360,000 tokens × $32/1M = $11.52
        // (実際は per-minute 課金で OpenAI 公式は $2/h 前後だが、 安全側で過大評価する)
        var tokens = CostEstimator.EstimateTokensFromAudioSeconds(3600);
        Assert.AreEqual(360_000L, tokens);
        var usd = CostEstimator.EstimateUsd("gpt-realtime-translate", tokens);
        Assert.AreEqual(11.52m, usd);
    }

    [TestMethod]
    public void EstimateUsd_CurrentMini_OneHourAudio_ApproxExpected()
    {
        // 1 時間 = 360,000 tokens × $10/1M = $3.6
        var tokens = CostEstimator.EstimateTokensFromAudioSeconds(3600);
        Assert.AreEqual(3.6m, CostEstimator.EstimateUsd("gpt-realtime-mini", tokens));
    }

    // ───────── EstimateUsd: 旧モデル ─────────

    [TestMethod]
    public void EstimateUsd_LegacyFullRate_OneMillionTokens_Returns100Usd()
    {
        Assert.AreEqual(100m, CostEstimator.EstimateUsd("gpt-4o-realtime-preview", 1_000_000));
    }

    [TestMethod]
    public void EstimateUsd_LegacyMiniRate_OneMillionTokens_Returns10Usd()
    {
        Assert.AreEqual(10m, CostEstimator.EstimateUsd("gpt-4o-mini-realtime-preview", 1_000_000));
    }

    [TestMethod]
    public void EstimateUsd_LegacyFullRate_OneHourAudio_ApproxExpected36()
    {
        // 旧モデル: 360,000 tokens × $100/1M = $36 (旧 README / memory bank と整合)
        var tokens = CostEstimator.EstimateTokensFromAudioSeconds(3600);
        Assert.AreEqual(36m, CostEstimator.EstimateUsd("gpt-4o-realtime-preview", tokens));
    }

    // ───────── EstimateUsd: エッジケース ─────────

    [TestMethod]
    public void EstimateUsd_ZeroTokens_ReturnsZero()
    {
        Assert.AreEqual(0m, CostEstimator.EstimateUsd("gpt-realtime-2", 0));
    }

    [TestMethod]
    public void EstimateUsd_NegativeTokens_ReturnsZero()
    {
        Assert.AreEqual(0m, CostEstimator.EstimateUsd("gpt-realtime-2", -100));
    }
}
