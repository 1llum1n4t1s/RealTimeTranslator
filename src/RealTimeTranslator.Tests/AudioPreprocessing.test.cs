using Microsoft.VisualStudio.TestTools.UnitTesting;
using RealTimeTranslator.Core.Services.Audio;

namespace RealTimeTranslator.Tests;

/// <summary>
/// 入力プリプロセス DSP (InputGainStage) のユニットテスト。
/// 履歴: v1.0.30 で 4 段導入 → v1.0.32 LoudnessNormalizer 削除 → v1.0.36 NightModeCompressor 削除
/// → クリップ防止リミッタ (AntiClipLimiter) も削除 (レベルメーターを見て手動でレベル管理する OBS 方式に変更)。
/// 現在の入力プリプロセスは InputGainStage 1 段のみ。
///
/// 検証ポイント:
/// - IsEnabled=false (0dB) で完全 bypass (入力配列を一切変更しない)
/// - 設計通りのゲイン動作 (代表的な入力レベルで挙動確認)
/// </summary>
[TestClass]
public class AudioPreprocessingTests
{
    [TestMethod]
    public void InputGainStage_ZeroDb_DoesNotMutateBuffer()
    {
        var g = new InputGainStage(0f);
        var samples = new float[] { 0.1f, -0.2f, 0.3f, -0.4f, 0.5f };
        var original = (float[])samples.Clone();
        g.Process(samples);
        CollectionAssert.AreEqual(original, samples);
    }

    [TestMethod]
    public void InputGainStage_Plus6dB_ApproximatelyDoublesAmplitude()
    {
        // 10^(6/20) ≈ 1.9953 → 0.1 が 0.1995 になるはず
        var g = new InputGainStage(6f);
        var samples = new float[] { 0.1f, 0.2f, 0.3f };
        g.Process(samples);
        Assert.AreEqual(0.1995f, samples[0], 0.001f);
        Assert.AreEqual(0.3991f, samples[1], 0.001f);
        Assert.AreEqual(0.5986f, samples[2], 0.001f);
    }

    [TestMethod]
    public void InputGainStage_Minus6dB_ApproximatelyHalvesAmplitude()
    {
        // 10^(-6/20) ≈ 0.5012 → 0.4 が 0.2005 になるはず
        var g = new InputGainStage(-6f);
        var samples = new float[] { 0.4f };
        g.Process(samples);
        Assert.AreEqual(0.2005f, samples[0], 0.001f);
    }

    [TestMethod]
    public void InputGainStage_GainChange_AppliesNewGainOnNextProcess()
    {
        // 初期 0dB → bypass、 その後 +12dB に変更すると ×3.98 倍
        var g = new InputGainStage(0f);
        var first = new float[] { 0.1f };
        g.Process(first);
        Assert.AreEqual(0.1f, first[0], 0.0001f);

        g.GainDb = 12f;
        var second = new float[] { 0.1f };
        g.Process(second);
        // 10^(12/20) ≈ 3.9811
        Assert.AreEqual(0.3981f, second[0], 0.001f);
    }

    [TestMethod]
    public void InputGainStage_IsEnabled_FollowsGainDb()
    {
        // 0dB は bypass、 ±0.01dB を越えると Enabled
        var g = new InputGainStage(0f);
        Assert.IsFalse(g.IsEnabled, "0dB は IsEnabled=false (bypass)");

        g.GainDb = 0.005f;
        Assert.IsFalse(g.IsEnabled, "±0.01dB 以内は bypass");

        g.GainDb = 0.02f;
        Assert.IsTrue(g.IsEnabled, "±0.01dB を越えると有効");

        g.GainDb = 0f;
        Assert.IsFalse(g.IsEnabled, "0dB に戻すと再び bypass");
    }
}
