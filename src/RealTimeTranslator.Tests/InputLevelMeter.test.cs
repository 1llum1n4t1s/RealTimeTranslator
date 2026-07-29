using Microsoft.VisualStudio.TestTools.UnitTesting;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.UI.ViewModels;

namespace RealTimeTranslator.Tests;

/// <summary>
/// 入力レベルメーター (SettingsViewModel.UpdateInputLevel) の dBFS → 正規化マッピングと
/// クリップインジケータの点灯・ホールド・消灯を検証する。
///
/// 背景 (2026-07-30 ゆろさん報告):
/// 「入力ゲインを上げてもレベルゲージがあまり増えない」— 実装は正しく、 メーターが -60〜0 dBFS の
/// 60 dB 幅を線形表示する仕様のため +6 dB でバーが 1 割しか動かないだけだった。 ただし調査中に
/// 「0 dBFS に張り付いてクランプ (= 波形が潰れて翻訳精度が落ちる) しても画面から分からない」
/// 穴が見つかったため、 クリップインジケータを追加した。 AntiClipLimiter は v1.0.36 で削除済みで
/// 自動保護が無く、 この表示がユーザーにとって歪みに気づく唯一の手掛かりになる。
/// </summary>
[TestClass]
public sealed class InputLevelMeterTests
{
    private static SettingsViewModel CreateViewModel()
    {
        var monitor = new StubOptionsMonitor(new AppSettings());
        var overlay = new OverlayViewModel(monitor);
        return new SettingsViewModel(monitor, new TestSettingsService(), overlay);
    }

    // ───────── dBFS → 正規化 (0..1) マッピング ─────────

    [TestMethod]
    [TestCategory("InputLevelMeter")]
    public void UpdateInputLevel_FloorAndFullScale_MapToZeroAndOne()
    {
        var vm = CreateViewModel();
        var t0 = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);

        vm.UpdateInputLevel(-60.0, t0);
        Assert.AreEqual(0.0, vm.InputLevelNorm, 1e-9, "-60 dBFS はバー 0% のはず");

        vm.UpdateInputLevel(0.0, t0);
        Assert.AreEqual(1.0, vm.InputLevelNorm, 1e-9, "0 dBFS はバー 100% のはず");
    }

    [TestMethod]
    [TestCategory("InputLevelMeter")]
    public void UpdateInputLevel_SixDbIncrease_MovesBarOnlyTenPercent()
    {
        // ゆろさんの体感「ゲインを上げてもあまり増えない」が仕様であることの回帰テスト。
        // 60 dB フルスケールなので +6 dB = 6/60 = バーの 10% しか動かない。
        var vm = CreateViewModel();
        var t0 = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);

        vm.UpdateInputLevel(-30.0, t0);
        double before = vm.InputLevelNorm;
        vm.UpdateInputLevel(-24.0, t0);
        double after = vm.InputLevelNorm;

        Assert.AreEqual(0.10, after - before, 1e-9, "+6 dB でバーは 10% だけ伸びるはず (60 dB フルスケール)");
    }

    [TestMethod]
    [TestCategory("InputLevelMeter")]
    public void UpdateInputLevel_OutOfRange_IsClampedToZeroOne()
    {
        var vm = CreateViewModel();
        var t0 = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);

        vm.UpdateInputLevel(-120.0, t0);
        Assert.AreEqual(0.0, vm.InputLevelNorm, 1e-9, "床を下回っても 0 でクランプされるはず");
        Assert.AreEqual("-∞ dB", vm.InputLevelText, "床以下は -∞ 表記のはず");

        vm.UpdateInputLevel(12.0, t0);
        Assert.AreEqual(1.0, vm.InputLevelNorm, 1e-9, "0 dBFS 超でも 1 でクランプされるはず");
    }

    // ───────── クリップインジケータ ─────────

    [TestMethod]
    [TestCategory("InputLevelMeter")]
    public void UpdateInputLevel_BelowThreshold_DoesNotLightClip()
    {
        var vm = CreateViewModel();
        var t0 = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);

        vm.UpdateInputLevel(-1.0, t0);

        Assert.IsFalse(vm.IsInputClipping, "しきい値未満ではクリップ点灯しないはず");
    }

    [TestMethod]
    [TestCategory("InputLevelMeter")]
    public void UpdateInputLevel_ReachesZeroDbfs_LightsClip()
    {
        var vm = CreateViewModel();
        var t0 = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);

        vm.UpdateInputLevel(0.0, t0);

        Assert.IsTrue(vm.IsInputClipping, "0 dBFS 到達でクリップ点灯するはず (PCM16 変換でクランプされる)");
    }

    [TestMethod]
    [TestCategory("InputLevelMeter")]
    public void UpdateInputLevel_MomentaryClip_HoldsThenClears()
    {
        // 1 フレームだけのクリップでも視認できるようホールドし、 期間経過後に自動で消える。
        var vm = CreateViewModel();
        var t0 = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
        var hold = SettingsViewModel.ClipHoldDuration;

        vm.UpdateInputLevel(0.0, t0);
        Assert.IsTrue(vm.IsInputClipping, "クリップ直後は点灯");

        // ホールド期間内に静かな音が来ても点灯を維持する
        vm.UpdateInputLevel(-40.0, t0 + TimeSpan.FromMilliseconds(hold.TotalMilliseconds / 2));
        Assert.IsTrue(vm.IsInputClipping, "ホールド期間内は静かになっても点灯を維持するはず");

        // 期間を過ぎたら消灯する
        vm.UpdateInputLevel(-40.0, t0 + hold + TimeSpan.FromMilliseconds(1));
        Assert.IsFalse(vm.IsInputClipping, "ホールド期間を過ぎたら消灯するはず");
    }

    [TestMethod]
    [TestCategory("InputLevelMeter")]
    public void UpdateInputLevel_RepeatedClips_ExtendHold()
    {
        // 連続クリップ中はそのたびに期限が延びるので、 点灯が途切れない。
        var vm = CreateViewModel();
        var t0 = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
        var hold = SettingsViewModel.ClipHoldDuration;

        vm.UpdateInputLevel(0.0, t0);
        vm.UpdateInputLevel(0.0, t0 + hold);
        Assert.IsTrue(vm.IsInputClipping, "再クリップで期限が延びるので点灯継続のはず");

        vm.UpdateInputLevel(-40.0, t0 + hold + hold + TimeSpan.FromMilliseconds(1));
        Assert.IsFalse(vm.IsInputClipping, "最後のクリップから期間経過で消灯するはず");
    }

    [TestMethod]
    [TestCategory("InputLevelMeter")]
    public void ResetInputLevel_ClearsClipImmediately()
    {
        // 停止後はレベル更新が止まるため、 ホールド中の点灯が残らないよう Reset で即消す。
        var vm = CreateViewModel();
        var t0 = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);

        vm.UpdateInputLevel(0.0, t0);
        Assert.IsTrue(vm.IsInputClipping, "前提: 点灯している");

        vm.ResetInputLevel();

        Assert.IsFalse(vm.IsInputClipping, "Reset でクリップ点灯が消えるはず");
        Assert.AreEqual(0.0, vm.InputLevelNorm, 1e-9);
        Assert.AreEqual("-∞ dB", vm.InputLevelText);
    }
}
