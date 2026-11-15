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
    public void Float32ToPcm16_NullInput_ShouldThrowArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => AudioFormatConverter.Float32ToPcm16(null!));
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

    // ═══════════════════════════════════════════════════════════════
    // 💀 カテゴリ3: リソース枯渇・DoS耐性（Resource Exhaustion）
    // ═══════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════
    // 🔀 カテゴリ4: 状態遷移の矛盾（State Machine Abuse）
    // ═══════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════
    // ⚡ カテゴリ2: 並行性（Concurrency Chaos）
    // ═══════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════
    // 🔁 Pcm16ToFloat32（送話・試聴の出力バッファが共用する逆変換）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Pcm16ToFloat32_BoundaryValues_ShouldScaleBy32768()
    {
        // 送話と試聴で同じ音量になる契約: short / 32768。最小値は -1.0、最大値は 1.0 未満。
        short[] values = [short.MinValue, -1, 0, 1, short.MaxValue];
        var bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), values[i]);

        var result = AudioFormatConverter.Pcm16ToFloat32(bytes);

        Assert.AreEqual(values.Length, result.Length);
        for (int i = 0; i < values.Length; i++)
            Assert.AreEqual(values[i] / 32768f, result[i], $"index {i}");
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Pcm16ToFloat32_OddByteCount_ShouldIgnoreTrailingByte()
    {
        var result = AudioFormatConverter.Pcm16ToFloat32([0x00, 0x40, 0x7F]);
        Assert.AreEqual(1, result.Length);
        Assert.AreEqual(0.5f, result[0]);
        Assert.AreEqual(0, AudioFormatConverter.Pcm16ToFloat32([]).Length);
    }
}
