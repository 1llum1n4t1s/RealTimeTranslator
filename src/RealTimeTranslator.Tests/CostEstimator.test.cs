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

    // ───────── ResolveRatePerMillion ─────────

    [TestMethod]
    public void ResolveRatePerMillion_GptRealtimeFull_Returns100()
    {
        Assert.AreEqual(100m, CostEstimator.ResolveRatePerMillion("gpt-4o-realtime-preview"));
    }

    [TestMethod]
    public void ResolveRatePerMillion_GptMiniRealtime_Returns10()
    {
        Assert.AreEqual(10m, CostEstimator.ResolveRatePerMillion("gpt-4o-mini-realtime-preview"));
    }

    [TestMethod]
    public void ResolveRatePerMillion_Null_DefaultsToFullRate()
    {
        Assert.AreEqual(100m, CostEstimator.ResolveRatePerMillion(null));
    }

    [TestMethod]
    public void ResolveRatePerMillion_Empty_DefaultsToFullRate()
    {
        Assert.AreEqual(100m, CostEstimator.ResolveRatePerMillion(""));
    }

    [TestMethod]
    public void ResolveRatePerMillion_UnknownModel_DefaultsToFullRate()
    {
        // 不明モデルは安全側 (過小評価しない) でフル料金扱い
        Assert.AreEqual(100m, CostEstimator.ResolveRatePerMillion("future-model-name"));
    }

    [TestMethod]
    public void ResolveRatePerMillion_CaseInsensitive_DetectsMini()
    {
        Assert.AreEqual(10m, CostEstimator.ResolveRatePerMillion("GPT-4O-MINI-REALTIME"));
    }

    // ───────── EstimateUsd ─────────

    [TestMethod]
    public void EstimateUsd_FullRate_OneMillionTokens_Returns100Usd()
    {
        Assert.AreEqual(100m, CostEstimator.EstimateUsd("gpt-4o-realtime-preview", 1_000_000));
    }

    [TestMethod]
    public void EstimateUsd_MiniRate_OneMillionTokens_Returns10Usd()
    {
        Assert.AreEqual(10m, CostEstimator.EstimateUsd("gpt-4o-mini-realtime-preview", 1_000_000));
    }

    [TestMethod]
    public void EstimateUsd_FullRate_OneHourAudio_ApproxExpected()
    {
        // 1 時間音声 = 3600 秒 × 100 tokens/sec = 360,000 tokens
        // フルレート: $100/1M × 360,000 = $36
        var tokens = CostEstimator.EstimateTokensFromAudioSeconds(3600);
        Assert.AreEqual(360_000L, tokens);
        Assert.AreEqual(36m, CostEstimator.EstimateUsd("gpt-4o-realtime-preview", tokens));
    }

    [TestMethod]
    public void EstimateUsd_ZeroTokens_ReturnsZero()
    {
        Assert.AreEqual(0m, CostEstimator.EstimateUsd("gpt-4o-realtime-preview", 0));
    }

    [TestMethod]
    public void EstimateUsd_NegativeTokens_ReturnsZero()
    {
        Assert.AreEqual(0m, CostEstimator.EstimateUsd("gpt-4o-realtime-preview", -100));
    }
}
