using Microsoft.VisualStudio.TestTools.UnitTesting;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// AudioFormatConverter の嫌がらせテスト
/// </summary>
[TestClass]
public sealed class AudioFormatConverterAdversarialTests
{
    // ═══════════════════════════════════════════════════════════════
    // 🗡️ カテゴリ1: 境界値・極端入力（Boundary Assault）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void ResampleTo24kHz_NullInput_ShouldThrowArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => AudioFormatConverter.ResampleTo24kHz(null!));
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_NullInput_ShouldThrowArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => AudioFormatConverter.Float32ToPcm16(null!));
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void ResampleTo24kHz_EmptyArray_ShouldReturnEmpty()
    {
        var result = AudioFormatConverter.ResampleTo24kHz([]);
        Assert.AreEqual(0, result.Length);
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_EmptyArray_ShouldReturnEmpty()
    {
        var result = AudioFormatConverter.Float32ToPcm16([]);
        Assert.AreEqual(0, result.Length);
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void ResampleTo24kHz_SingleSample_ShouldNotCrash()
    {
        var result = AudioFormatConverter.ResampleTo24kHz([0.5f]);
        Assert.IsTrue(result.Length >= 1, $"出力サンプル数: {result.Length}");
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_MaxPositiveValue_ShouldClampTo32767()
    {
        var result = AudioFormatConverter.Float32ToPcm16([1.0f]);
        var value = BitConverter.ToInt16(result, 0);
        Assert.AreEqual((short)32767, value);
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_MaxNegativeValue_ShouldClampToMinus32767()
    {
        var result = AudioFormatConverter.Float32ToPcm16([-1.0f]);
        var value = BitConverter.ToInt16(result, 0);
        Assert.AreEqual((short)-32767, value);
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_BeyondPositiveRange_ShouldClamp()
    {
        // float が 1.0 を超える場合（オーバードライブ音声）
        var result = AudioFormatConverter.Float32ToPcm16([5.0f, 100.0f, float.MaxValue]);
        for (int i = 0; i < 3; i++)
        {
            var value = BitConverter.ToInt16(result, i * 2);
            Assert.AreEqual((short)32767, value, $"index {i}: 範囲外の正値はクランプされるべき");
        }
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_BeyondNegativeRange_ShouldClamp()
    {
        var result = AudioFormatConverter.Float32ToPcm16([-5.0f, -100.0f, float.MinValue]);
        for (int i = 0; i < 3; i++)
        {
            var value = BitConverter.ToInt16(result, i * 2);
            Assert.AreEqual((short)-32767, value, $"index {i}: 範囲外の負値はクランプされるべき");
        }
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_NaN_ShouldNotCrash()
    {
        var result = AudioFormatConverter.Float32ToPcm16([float.NaN]);
        Assert.AreEqual(2, result.Length, "NaN入力でも2バイト（1サンプル分）のPCM16を返すべき");
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_PositiveInfinity_ShouldClamp()
    {
        var result = AudioFormatConverter.Float32ToPcm16([float.PositiveInfinity]);
        var value = BitConverter.ToInt16(result, 0);
        Assert.AreEqual((short)32767, value);
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_NegativeInfinity_ShouldClamp()
    {
        var result = AudioFormatConverter.Float32ToPcm16([float.NegativeInfinity]);
        var value = BitConverter.ToInt16(result, 0);
        Assert.AreEqual((short)-32767, value);
    }

    /// <adversarial category="boundary" severity="low" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_Zero_ShouldReturnZero()
    {
        var result = AudioFormatConverter.Float32ToPcm16([0.0f]);
        var value = BitConverter.ToInt16(result, 0);
        Assert.AreEqual((short)0, value);
    }

    /// <adversarial category="boundary" severity="low" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_NegativeZero_ShouldReturnZero()
    {
        var result = AudioFormatConverter.Float32ToPcm16([-0.0f]);
        var value = BitConverter.ToInt16(result, 0);
        Assert.AreEqual((short)0, value);
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_ByteOrderShouldBeLittleEndian()
    {
        // PCM16 LE: 0x00FF = 255 → bytes[0]=0xFF, bytes[1]=0x00
        var result = AudioFormatConverter.Float32ToPcm16([0.5f]);
        var value = BitConverter.ToInt16(result, 0);
        Assert.IsTrue(value > 0, "0.5f は正のPCM16値になるべき");
        Assert.AreEqual(result[0], (byte)(value & 0xFF), "下位バイトが先（LE）");
        Assert.AreEqual(result[1], (byte)((value >> 8) & 0xFF), "上位バイトが後（LE）");
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void ToPcm16Base64_EmptyArray_ShouldReturnEmptyBase64()
    {
        var result = AudioFormatConverter.ToPcm16Base64([]);
        Assert.AreEqual("", result);
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void ToPcm16Base64_ValidInput_ShouldReturnValidBase64()
    {
        var input = new float[1600]; // 100ms of 16kHz audio
        var result = AudioFormatConverter.ToPcm16Base64(input);
        Assert.IsTrue(result.Length > 0, "Base64出力が空であってはならない");
        // Base64デコードできることを確認
        var decoded = Convert.FromBase64String(result);
        Assert.IsTrue(decoded.Length > 0, "デコード後のバイト列が空であってはならない");
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void ResampleTo24kHz_OutputLengthRatio_ShouldBe1_5x()
    {
        // 16kHz→24kHz = 1.5倍のサンプル数
        var input = new float[16000]; // 1秒分
        var result = AudioFormatConverter.ResampleTo24kHz(input);
        var expectedApprox = 24000;
        Assert.IsTrue(Math.Abs(result.Length - expectedApprox) <= 2,
            $"出力 {result.Length} サンプルは約 {expectedApprox} であるべき（±2）");
    }

    // ═══════════════════════════════════════════════════════════════
    // 💀 カテゴリ3: リソース枯渇・DoS耐性（Resource Exhaustion）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="resource" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public void ResampleTo24kHz_LargeInput_ShouldCompleteWithinTimeout()
    {
        // 10秒分の16kHz音声（160,000サンプル）
        var input = new float[160000];
        var result = AudioFormatConverter.ResampleTo24kHz(input);
        Assert.IsTrue(result.Length > 0);
    }

    /// <adversarial category="resource" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public void Float32ToPcm16_LargeInput_ShouldCompleteWithinTimeout()
    {
        // 1M サンプル
        var input = new float[1_000_000];
        var result = AudioFormatConverter.Float32ToPcm16(input);
        Assert.AreEqual(2_000_000, result.Length);
    }

    /// <adversarial category="resource" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void ResampleTo24kHz_RepeatedCalls_ShouldNotLeakMemory()
    {
        var before = GC.GetTotalMemory(true);
        for (int i = 0; i < 100; i++)
        {
            var input = new float[1600]; // 100ms chunks
            _ = AudioFormatConverter.ResampleTo24kHz(input);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var after = GC.GetTotalMemory(true);
        var growth = after - before;
        // 許容範囲: 5MB以内の成長
        Assert.IsTrue(growth < 5 * 1024 * 1024,
            $"メモリ成長が異常: {growth / 1024.0:F0}KB");
    }

    // ═══════════════════════════════════════════════════════════════
    // 🔀 カテゴリ4: 状態遷移の矛盾（State Machine Abuse）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="state" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void ResampleTo24kHz_ConsecutiveCallsWithDifferentSizes_ShouldBeIndependent()
    {
        // static メソッドが内部状態を持っていないことを確認
        var r1 = AudioFormatConverter.ResampleTo24kHz([1.0f]);
        var r2 = AudioFormatConverter.ResampleTo24kHz(new float[1600]);
        var r3 = AudioFormatConverter.ResampleTo24kHz([0.5f, -0.5f]);

        Assert.IsTrue(r1.Length >= 1);
        Assert.IsTrue(r2.Length > 100);
        Assert.IsTrue(r3.Length >= 2);
    }

    /// <adversarial category="state" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_OutputLengthIsAlways2xInput()
    {
        for (int size = 0; size <= 100; size++)
        {
            var input = new float[size];
            var result = AudioFormatConverter.Float32ToPcm16(input);
            Assert.AreEqual(size * 2, result.Length,
                $"入力 {size} サンプル → 出力 {size * 2} バイトであるべき");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 🎭 カテゴリ5: 型パンチ・プロトコル違反（Type Punching）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="type" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_Subnormals_ShouldNotCrash()
    {
        // 非正規化浮動小数点数（極めて小さい値）
        var subnormals = new[] { float.Epsilon, -float.Epsilon, 1e-38f, -1e-38f };
        var result = AudioFormatConverter.Float32ToPcm16(subnormals);
        Assert.AreEqual(8, result.Length);
        // 極めて小さいので全て0に変換されるべき
        for (int i = 0; i < 4; i++)
        {
            var value = BitConverter.ToInt16(result, i * 2);
            Assert.AreEqual((short)0, value, $"サブノーマル index {i} は0になるべき");
        }
    }

    /// <adversarial category="type" severity="low" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void ToPcm16Base64_OutputIsValidBase64()
    {
        var input = new float[160]; // 10ms
        for (int i = 0; i < input.Length; i++)
            input[i] = MathF.Sin(2 * MathF.PI * 440 * i / 16000); // 440Hz tone

        var base64 = AudioFormatConverter.ToPcm16Base64(input);

        // Base64文字セットのみで構成されていることを確認
        Assert.IsTrue(base64.All(c =>
            char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '='),
            "Base64以外の文字が含まれている");
    }

    // ═══════════════════════════════════════════════════════════════
    // ⚡ カテゴリ2: 並行性（Concurrency Chaos）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="concurrency" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public void ResampleTo24kHz_ConcurrentCalls_ShouldBeThreadSafe()
    {
        // static メソッドが並行呼び出しで壊れないことを確認
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            try
            {
                for (int j = 0; j < 50; j++)
                {
                    var input = new float[1600];
                    input[0] = i * 0.1f;
                    var result = AudioFormatConverter.ResampleTo24kHz(input);
                    Assert.IsTrue(result.Length > 0);
                }
            }
            catch (Exception ex) { exceptions.Add(ex); }
        })).ToArray();

        Task.WaitAll(tasks);
        Assert.AreEqual(0, exceptions.Count,
            $"並行実行で {exceptions.Count} 件のエラー: {exceptions.FirstOrDefault()?.Message}");
    }

    /// <adversarial category="concurrency" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public void Float32ToPcm16_ConcurrentCalls_ShouldBeThreadSafe()
    {
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            try
            {
                for (int j = 0; j < 100; j++)
                {
                    var input = new float[480];
                    input[0] = 0.5f;
                    var result = AudioFormatConverter.Float32ToPcm16(input);
                    Assert.AreEqual(960, result.Length);
                }
            }
            catch (Exception ex) { exceptions.Add(ex); }
        })).ToArray();

        Task.WaitAll(tasks);
        Assert.AreEqual(0, exceptions.Count,
            $"並行実行で {exceptions.Count} 件のエラー: {exceptions.FirstOrDefault()?.Message}");
    }

    // ═══════════════════════════════════════════════════════════════
    // 🌪️ カテゴリ6: 環境異常（Environmental Chaos）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="chaos" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_AllSameValue_ShouldProduceConsistentOutput()
    {
        // 全サンプルが同一値（DCオフセット）
        var input = Enumerable.Repeat(0.3f, 1000).ToArray();
        var result = AudioFormatConverter.Float32ToPcm16(input);
        var firstValue = BitConverter.ToInt16(result, 0);
        for (int i = 1; i < 1000; i++)
        {
            var value = BitConverter.ToInt16(result, i * 2);
            Assert.AreEqual(firstValue, value, $"index {i}: 全サンプル同一値なら出力も同一であるべき");
        }
    }

    /// <adversarial category="chaos" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void ResampleTo24kHz_SilentAudio_ShouldProduceSilentOutput()
    {
        var input = new float[1600]; // 全ゼロ = 無音
        var result = AudioFormatConverter.ResampleTo24kHz(input);
        // リサンプリング後もゼロ（無音）であるべき
        var maxAbs = result.Max(MathF.Abs);
        Assert.IsTrue(maxAbs < 0.001f,
            $"無音入力のリサンプリング出力の最大絶対値: {maxAbs}（0.001未満であるべき）");
    }

    /// <adversarial category="chaos" severity="low" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Float32ToPcm16_Symmetry_PositiveAndNegativeShouldBeSymmetric()
    {
        var positive = AudioFormatConverter.Float32ToPcm16([0.5f]);
        var negative = AudioFormatConverter.Float32ToPcm16([-0.5f]);
        var pVal = BitConverter.ToInt16(positive, 0);
        var nVal = BitConverter.ToInt16(negative, 0);
        Assert.AreEqual(pVal, (short)-nVal, "正負対称な入力は対称な出力を生むべき");
    }
}
