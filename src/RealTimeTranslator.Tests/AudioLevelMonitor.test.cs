using Microsoft.VisualStudio.TestTools.UnitTesting;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// AudioLevelMonitor (「開始」前のプレビュー音量メーター) の単体テスト。
/// 内部 ctor (IAudioCaptureService 注入) で WASAPI を使わずモック駆動する。
///
/// 検証:
/// - 計測中に音声フレームが来るとゲイン適用後ピークを dBFS で通知する
/// - 入力ゲインがメーター値に反映される (+6dB で約 +6dB シフト)
/// - Stop で無音 (FloorDb) を 1 回通知し、 以降は転送しない
/// - 計測開始前のフレームは通知しない
/// - キャプチャ開始失敗時は無音通知 + IsMonitoring=false (silent-fail)
/// </summary>
[TestClass]
public sealed class AudioLevelMonitorTests
{
    /// <summary>AudioDataAvailable を任意に発火できるテスト用キャプチャ。</summary>
    private sealed class RaisingAudioCaptureService : IAudioCaptureService
    {
        public bool StartResult { get; set; } = true;
        public bool IsCapturing { get; private set; }
        public bool HasReceivedNonSilentDataSinceStart => false;
        public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
#pragma warning disable CS0067
        public event EventHandler<CaptureStatusEventArgs>? CaptureStatusChanged;
#pragma warning restore CS0067

        public void StartCapture(int processId) { IsCapturing = true; }
        public Task<bool> StartCaptureWithRetryAsync(int processId, CancellationToken cancellationToken, SynchronizationContext? captureCreationContext = null)
        {
            IsCapturing = StartResult;
            return Task.FromResult(StartResult);
        }
        public void StopCapture() { IsCapturing = false; }
        public void ApplySettings(AudioCaptureSettings settings) { }
        public void Dispose() { }

        public void Raise(float[] samples)
            => AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(samples, DateTime.Now));
    }

    private static AudioLevelMonitor CreateMonitor(out RaisingAudioCaptureService capture)
    {
        capture = new RaisingAudioCaptureService();
        // internal ctor (InternalsVisibleTo=RealTimeTranslator.Tests)。
        return new AudioLevelMonitor(capture);
    }

    [TestMethod]
    public async Task StartThenFrame_EmitsPostGainPeakDb()
    {
        var monitor = CreateMonitor(out var capture);
        var levels = new List<double>();
        monitor.LevelUpdated += (_, e) => levels.Add(e.PeakDb);

        await monitor.StartAsync(1234);
        Assert.IsTrue(monitor.IsMonitoring, "開始成功で IsMonitoring=true");

        // ピーク 0.5 → 20*log10(0.5) ≈ -6.02 dBFS (ゲイン 0dB)
        capture.Raise(new[] { 0.1f, -0.5f, 0.3f });

        Assert.AreEqual(1, levels.Count, "フレーム 1 件で 1 回通知 (初回はスロットルを通過)");
        Assert.AreEqual(-6.02, levels[0], 0.1, "0.5 のピークは約 -6.02 dBFS");

        monitor.Dispose();
    }

    [TestMethod]
    public async Task Gain_ShiftsMeterLevel()
    {
        var monitor = CreateMonitor(out var capture);
        monitor.GainDb = 6f; // ×1.9953
        double? db = null;
        monitor.LevelUpdated += (_, e) => db ??= e.PeakDb;

        await monitor.StartAsync(1);
        // 0.5 * 1.9953 ≈ 0.9976 → 20*log10(0.9976) ≈ -0.02 dBFS
        capture.Raise(new[] { 0.5f });

        Assert.IsNotNull(db);
        Assert.AreEqual(0.0, db!.Value, 0.2, "+6dB ゲインで 0.5 のピークがほぼ 0 dBFS になる");

        monitor.Dispose();
    }

    [TestMethod]
    public async Task Stop_EmitsSilence_AndStopsForwarding()
    {
        var monitor = CreateMonitor(out var capture);
        var levels = new List<double>();
        monitor.LevelUpdated += (_, e) => levels.Add(e.PeakDb);

        await monitor.StartAsync(1);
        capture.Raise(new[] { 0.5f });       // 1 件目: ピーク
        int afterFirst = levels.Count;

        monitor.Stop();                       // 無音 (FloorDb) を 1 回
        Assert.IsFalse(monitor.IsMonitoring, "Stop 後は IsMonitoring=false");
        Assert.AreEqual(afterFirst + 1, levels.Count, "Stop で無音通知が 1 回増える");
        Assert.AreEqual(DspMathFloor(), levels[^1], 0.01, "Stop の通知は床値 (無音)");

        capture.Raise(new[] { 0.9f });        // 停止後のフレームは無視される
        Assert.AreEqual(afterFirst + 1, levels.Count, "停止後のフレームは通知しない");

        monitor.Dispose();
    }

    [TestMethod]
    public void FrameBeforeStart_DoesNotEmit()
    {
        var monitor = CreateMonitor(out var capture);
        var count = 0;
        monitor.LevelUpdated += (_, _) => count++;

        capture.Raise(new[] { 0.9f }); // 開始前
        Assert.AreEqual(0, count, "開始前のフレームは通知しない");

        monitor.Dispose();
    }

    [TestMethod]
    public async Task StartFailure_EmitsSilence_AndNotMonitoring()
    {
        var monitor = CreateMonitor(out var capture);
        capture.StartResult = false; // キャプチャ開始失敗を模す
        var levels = new List<double>();
        monitor.LevelUpdated += (_, e) => levels.Add(e.PeakDb);

        await monitor.StartAsync(1);

        Assert.IsFalse(monitor.IsMonitoring, "開始失敗で IsMonitoring=false (silent-fail)");
        Assert.IsTrue(levels.Count >= 1, "開始失敗時は無音を通知してメーターを 0 に倒す");
        Assert.AreEqual(DspMathFloor(), levels[^1], 0.01, "無音通知は床値");

        monitor.Dispose();
    }

    // DspMath.FloorDb は internal なので、 期待値 -120 をテスト側に定数で持つ (実装と一致を維持)。
    private static double DspMathFloor() => -120.0;
}
