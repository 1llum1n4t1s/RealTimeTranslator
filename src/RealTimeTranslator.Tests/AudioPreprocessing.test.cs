using Microsoft.VisualStudio.TestTools.UnitTesting;
using RealTimeTranslator.Core.Services.Audio;

namespace RealTimeTranslator.Tests;

/// <summary>
/// v1.0.30 入力プリプロセス DSP 4 クラスのユニットテスト。
///
/// 各 DSP の検証ポイント:
/// - IsEnabled=false で完全 bypass (入力配列を一切変更しない)
/// - 設計通りのゲイン / 圧縮 / リミット動作 (代表的な入力レベルで挙動確認)
/// - Reset() で内部状態 (envelope / running gain / sum-of-squares) が完全クリア
///
/// WebRestrictionRemoval (Chrome 拡張音量ブースター) から移植したパラメータをベースにしているため、
/// 厳密な数値ではなく「方向 (大音は抑える / 小音は持ち上げる)」を中心に検証する。
/// </summary>
[TestClass]
public class AudioPreprocessingTests
{
    private const int SampleRate = 48000;

    // ═════════════════ 共通 bypass テスト ═════════════════
    // IsEnabled=false / GainDb=0 のとき、 入力配列を一切変更しないことを保証する。
    // これが破れると default 設定 (現状動作と互換) の保証が崩れる。

    [TestMethod]
    public void NightModeCompressor_Disabled_DoesNotMutateBuffer()
    {
        var c = new NightModeCompressor(SampleRate, enabled: false);
        var samples = new float[] { 0.1f, -0.2f, 0.3f, -0.4f, 0.5f };
        var original = (float[])samples.Clone();
        c.Process(samples);
        CollectionAssert.AreEqual(original, samples);
    }

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
    public void AntiClipLimiter_Disabled_DoesNotMutateBuffer()
    {
        var l = new AntiClipLimiter(SampleRate, enabled: false);
        var samples = new float[] { 0.1f, -0.2f, 0.3f, -0.4f, 0.5f };
        var original = (float[])samples.Clone();
        l.Process(samples);
        CollectionAssert.AreEqual(original, samples);
    }

    // ═════════════════ InputGainStage ═════════════════

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

    // ═════════════════ NightModeCompressor ═════════════════

    [TestMethod]
    public void NightModeCompressor_QuietSignal_NoCompression()
    {
        // -50 dBFS (knee start -36 dBFS より下) は圧縮対象外
        var c = new NightModeCompressor(SampleRate, enabled: true);
        const float inputAmp = 0.00316f; // 10^(-50/20)
        var samples = GenerateSineWave(440f, inputAmp, SampleRate, durationSec: 0.5);
        var initialPeak = MaxAbs(samples);
        c.Process(samples);
        var finalPeak = MaxAbs(samples);
        // 圧縮されない (envelope follower の attack 影響だけは多少出るが、 大きく変わらないはず)
        Assert.AreEqual(initialPeak, finalPeak, initialPeak * 0.1f,
            "knee 範囲外 (threshold-knee/2 未満) は圧縮対象外");
    }

    [TestMethod]
    public void NightModeCompressor_LoudSignal_AppliesCompression()
    {
        // -10 dBFS (knee end -24 dBFS より上) は強く圧縮されるはず
        var c = new NightModeCompressor(SampleRate, enabled: true);
        const float inputAmp = 0.316f; // 10^(-10/20)
        var samples = GenerateSineWave(440f, inputAmp, SampleRate, durationSec: 1.0);
        var initialPeak = MaxAbs(samples);
        c.Process(samples);

        // envelope が追従しきった後半でチェック (attack 20ms なので 1 秒なら十分追従)
        var tailPeak = MaxAbs(samples.AsSpan(samples.Length / 2).ToArray());
        Assert.IsTrue(tailPeak < initialPeak * 0.7f,
            $"-10dBFS は ratio 4:1 で圧縮されるべき (initial={initialPeak:F4}, tail={tailPeak:F4})");
    }

    [TestMethod]
    public void NightModeCompressor_Reset_ClearsEnvelopeState()
    {
        // 大音量で envelope を引き上げた後 Reset → 小音量入力で過去の envelope が漏れない
        var c = new NightModeCompressor(SampleRate, enabled: true);
        var loud = GenerateSineWave(440f, 0.5f, SampleRate, durationSec: 0.1);
        c.Process(loud);

        c.Reset();

        // Reset 後の小音量信号は圧縮されない (envelope が floor から再スタート)
        var probe = GenerateSineWave(440f, 0.001f, SampleRate, durationSec: 0.05);
        var probeInitialPeak = MaxAbs(probe);
        c.Process(probe);
        var probeFinalPeak = MaxAbs(probe);
        Assert.AreEqual(probeInitialPeak, probeFinalPeak, probeInitialPeak * 0.2f,
            "Reset 後の小音量は圧縮されない (envelope が clear されている証拠)");
    }

    // ═════════════════ AntiClipLimiter ═════════════════

    [TestMethod]
    public void AntiClipLimiter_BelowThreshold_PassesThrough()
    {
        // -10 dBFS は threshold -3 dBFS 未満なので圧縮されない
        var l = new AntiClipLimiter(SampleRate, enabled: true);
        const float inputAmp = 0.316f; // 10^(-10/20)
        var samples = GenerateSineWave(440f, inputAmp, SampleRate, durationSec: 0.1);
        var initialPeak = MaxAbs(samples);
        l.Process(samples);
        var finalPeak = MaxAbs(samples);
        Assert.AreEqual(initialPeak, finalPeak, initialPeak * 0.05f,
            "threshold 未満は素通し");
    }

    [TestMethod]
    public void AntiClipLimiter_PeakAboveOne_ClampsToHardLimit()
    {
        // 振幅 5.0 の極端なオーバー入力でも、 最終的に |sample| <= 0.999 (HardClipMax) に収まる
        var l = new AntiClipLimiter(SampleRate, enabled: true);
        var samples = new float[SampleRate / 4]; // 250ms
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (i % 2 == 0) ? 5.0f : -5.0f;
        l.Process(samples);
        foreach (var s in samples)
        {
            Assert.IsTrue(MathF.Abs(s) <= 0.999f + 0.0001f,
                $"hard clamp で |sample| <= 0.999 になるべき (s={s})");
        }
    }

    [TestMethod]
    public void AntiClipLimiter_ModerateOverThreshold_ReducesAmplitude()
    {
        // -1 dBFS (threshold -3 dBFS 超え) は限定的に圧縮される
        var l = new AntiClipLimiter(SampleRate, enabled: true);
        const float inputAmp = 0.891f; // 10^(-1/20)
        var samples = GenerateSineWave(440f, inputAmp, SampleRate, durationSec: 0.2);
        var initialPeak = MaxAbs(samples);
        l.Process(samples);

        // attack 1ms なのですぐ追従、 ratio 12:1 で threshold を僅かに超えた程度に収まる
        var tailPeak = MaxAbs(samples.AsSpan(samples.Length / 2).ToArray());
        Assert.IsTrue(tailPeak < initialPeak,
            $"threshold 超えは抑制されるべき (initial={initialPeak:F4}, tail={tailPeak:F4})");
        Assert.IsTrue(tailPeak <= 0.999f + 0.0001f,
            $"最終的に hard clamp 内 (tail={tailPeak:F4})");
    }

    // ═════════════════ ヘルパー ═════════════════

    private static float[] GenerateSineWave(float freq, float amplitude, int sampleRate, double durationSec)
    {
        int n = (int)(sampleRate * durationSec);
        var samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            samples[i] = amplitude * MathF.Sin(2f * MathF.PI * freq * i / sampleRate);
        }
        return samples;
    }

    private static float MaxAbs(float[] arr)
    {
        float max = 0f;
        foreach (var v in arr)
        {
            var abs = MathF.Abs(v);
            if (abs > max) max = abs;
        }
        return max;
    }
}
