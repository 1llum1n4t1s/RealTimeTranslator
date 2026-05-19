using RealTimeTranslator.UI.ViewModels;

namespace RealTimeTranslator.Tests;

/// <summary>
/// SettingsViewModel.ComposeArgbHex / SplitArgbToRgbAndOpacity の round-trip と不正値 fallback を検証する
/// (rere B2-001 対応)。 旧 #AARRGGBB BackgroundColor 単体管理 → 新 BackgroundColorBase + BackgroundOpacityPercent
/// への自動マイグレーションが破綻すると、 ユーザーが意図した背景色が黙って黒に矯正される UX バグになる。
///
/// テスト方針:
///  - 標準ケース (#80000000, #FFFFFFFF 等) で round-trip が完全一致
///  - 不正値 (空文字 / 短すぎ / 長すぎ / 非 hex 文字) で安全なデフォルトに fallback
///  - 不透明度の境界値 (0%, 100%) でも正しい alpha 値
///  - 大文字小文字の入力が出力で UpperInvariant に正規化される
/// </summary>
[TestClass]
public class BackgroundColorRoundTripTests
{
    // ===== ComposeArgbHex (#RRGGBB + 0-100% → #AARRGGBB) =====

    [TestMethod]
    public void ComposeArgbHex_StandardBlack50Percent_ProducesAA80()
    {
        // 50% × 255 = 127.5 → 四捨五入で 128 = 0x80
        var result = SettingsViewModel.ComposeArgbHex("#000000", 50);
        Assert.AreEqual("#80000000", result);
    }

    [TestMethod]
    public void ComposeArgbHex_FullOpaqueWhite_ProducesFFFFFFFF()
    {
        var result = SettingsViewModel.ComposeArgbHex("#FFFFFF", 100);
        Assert.AreEqual("#FFFFFFFF", result);
    }

    [TestMethod]
    public void ComposeArgbHex_FullyTransparent_ProducesAlpha00()
    {
        var result = SettingsViewModel.ComposeArgbHex("#0F1A4C", 0);
        Assert.AreEqual("#000F1A4C", result);
    }

    [TestMethod]
    public void ComposeArgbHex_Opacity25Percent_RoundsTo0x40()
    {
        // 25% × 255 = 63.75 → 四捨五入で 64 = 0x40
        var result = SettingsViewModel.ComposeArgbHex("#404040", 25);
        Assert.AreEqual("#40404040", result);
    }

    [TestMethod]
    public void ComposeArgbHex_Opacity75Percent_RoundsTo0xBF()
    {
        // 75% × 255 = 191.25 → 四捨五入で 191 = 0xBF
        var result = SettingsViewModel.ComposeArgbHex("#2A0F4C", 75);
        Assert.AreEqual("#BF2A0F4C", result);
    }

    [TestMethod]
    public void ComposeArgbHex_LowercaseInput_NormalizedToUpper()
    {
        var result = SettingsViewModel.ComposeArgbHex("#aabbcc", 100);
        Assert.AreEqual("#FFAABBCC", result);
    }

    [TestMethod]
    public void ComposeArgbHex_NoHashPrefix_StillAccepted()
    {
        var result = SettingsViewModel.ComposeArgbHex("000000", 50);
        Assert.AreEqual("#80000000", result);
    }

    [TestMethod]
    public void ComposeArgbHex_EightDigitInput_TrimsAlphaToLast6()
    {
        // 旧 #AARRGGBB が紛れ込んだ場合は下位 6 桁 (RGB) だけ使う
        var result = SettingsViewModel.ComposeArgbHex("#80FFFFFF", 100);
        Assert.AreEqual("#FFFFFFFF", result);
    }

    [TestMethod]
    public void ComposeArgbHex_EmptyInput_FallbacksToBlack()
    {
        var result = SettingsViewModel.ComposeArgbHex(string.Empty, 50);
        Assert.AreEqual("#80000000", result);
    }

    [TestMethod]
    public void ComposeArgbHex_MalformedLength_FallbacksToBlack()
    {
        // 5 桁 (#RRGGB) は不正、 #000000 にフォールバック
        var result = SettingsViewModel.ComposeArgbHex("#ABCDE", 100);
        Assert.AreEqual("#FF000000", result);
    }

    [TestMethod]
    public void ComposeArgbHex_NegativeOpacity_ClampsToZero()
    {
        var result = SettingsViewModel.ComposeArgbHex("#FF0000", -10);
        Assert.AreEqual("#00FF0000", result);
    }

    [TestMethod]
    public void ComposeArgbHex_Over100Opacity_ClampsTo100()
    {
        var result = SettingsViewModel.ComposeArgbHex("#FF0000", 200);
        Assert.AreEqual("#FFFF0000", result);
    }

    // ===== SplitArgbToRgbAndOpacity (#AARRGGBB → #RRGGBB + 0-100%) =====

    [TestMethod]
    public void SplitArgbToRgbAndOpacity_StandardBlack_RoundtripsCorrectly()
    {
        var (rgb, pct) = SettingsViewModel.SplitArgbToRgbAndOpacity("#80000000");
        Assert.AreEqual("#000000", rgb);
        Assert.AreEqual(50, pct);
    }

    [TestMethod]
    public void SplitArgbToRgbAndOpacity_FullOpaque_Returns100Percent()
    {
        var (rgb, pct) = SettingsViewModel.SplitArgbToRgbAndOpacity("#FFFFFFFF");
        Assert.AreEqual("#FFFFFF", rgb);
        Assert.AreEqual(100, pct);
    }

    [TestMethod]
    public void SplitArgbToRgbAndOpacity_FullyTransparent_Returns0Percent()
    {
        var (rgb, pct) = SettingsViewModel.SplitArgbToRgbAndOpacity("#000F1A4C");
        Assert.AreEqual("#0F1A4C", rgb);
        Assert.AreEqual(0, pct);
    }

    [TestMethod]
    public void SplitArgbToRgbAndOpacity_SixDigitOnly_TreatedAsFullyOpaque()
    {
        // alpha 省略 (#RRGGBB) は 100% = 完全不透明として扱う
        var (rgb, pct) = SettingsViewModel.SplitArgbToRgbAndOpacity("#FFFFFF");
        Assert.AreEqual("#FFFFFF", rgb);
        Assert.AreEqual(100, pct);
    }

    [TestMethod]
    public void SplitArgbToRgbAndOpacity_EmptyInput_FallbacksToBlack50()
    {
        var (rgb, pct) = SettingsViewModel.SplitArgbToRgbAndOpacity(string.Empty);
        Assert.AreEqual("#000000", rgb);
        Assert.AreEqual(50, pct);
    }

    [TestMethod]
    public void SplitArgbToRgbAndOpacity_MalformedLength_FallbacksToBlack50()
    {
        // 7 桁 (不正) はフォールバック
        var (rgb, pct) = SettingsViewModel.SplitArgbToRgbAndOpacity("#1234567");
        Assert.AreEqual("#000000", rgb);
        Assert.AreEqual(50, pct);
    }

    [TestMethod]
    public void SplitArgbToRgbAndOpacity_NonHexAlpha_FallbacksToBlack50()
    {
        // 先頭 2 桁が hex でない (GG = 非 hex) → fallback
        var (rgb, pct) = SettingsViewModel.SplitArgbToRgbAndOpacity("#GG000000");
        Assert.AreEqual("#000000", rgb);
        Assert.AreEqual(50, pct);
    }

    // ===== Round-trip (Split → Compose で同値性) =====

    [TestMethod]
    [DataRow("#80000000")]
    [DataRow("#FFFFFFFF")]
    [DataRow("#000F1A4C")] // alpha 0% (完全透明)
    [DataRow("#40404040")] // alpha 25%
    [DataRow("#BF2A0F4C")] // alpha 75%
    [DataRow("#CC0F4C1F")] // alpha 80% (旧 #CC = 204/255 ≈ 80%)
    public void SplitThenCompose_PreservesArgbApproximately(string original)
    {
        // 旧 #AARRGGBB → 分解 → 合成 で alpha は段階に丸まる可能性があるが、 標準 5 段階値
        // (0/25/50/75/100) なら完全に戻るはず。 旧 0xCC (80%) のような非標準値は丸めが発生する。
        var (rgb, pct) = SettingsViewModel.SplitArgbToRgbAndOpacity(original);
        var composed = SettingsViewModel.ComposeArgbHex(rgb, pct);

        // RGB 部分は完全一致するはず
        Assert.AreEqual(original.Substring(3).ToUpperInvariant(), composed.Substring(3));
        // alpha は ±1 程度の誤差を許容 (50% ↔ 0x80 のように四捨五入を経るため)
        int origAlpha = Convert.ToInt32(original.Substring(1, 2), 16);
        int composedAlpha = Convert.ToInt32(composed.Substring(1, 2), 16);
        Assert.IsTrue(Math.Abs(origAlpha - composedAlpha) <= 1,
            $"alpha mismatch: original=0x{origAlpha:X2} composed=0x{composedAlpha:X2}");
    }
}
