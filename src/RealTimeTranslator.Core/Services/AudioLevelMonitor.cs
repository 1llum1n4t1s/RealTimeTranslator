using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services.Audio;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// <see cref="IAudioLevelMonitor"/> の実装。 「開始」前にドロップダウンで選択中のプロセスの音量を
/// リアルタイム表示するためのプレビュー計測器。
///
/// 設計:
/// - 専用の <see cref="AudioCaptureService"/> を 1 つ内包 (本番パイプラインの DI Singleton とは別インスタンス)。
///   OpenAI には繋がず、 WASAPI で取得した mono float32 を入力ゲイン適用 → ピーク算出に使うだけ。
/// - ピークは入力ゲイン適用後の値を ~50ms スロットルで <see cref="LevelUpdated"/> 発火 (本番メーターと同仕様)。
/// - Start は idempotent (既存セッションを止めてから開始)。 失敗は silent-fail (IsMonitoring=false)。
/// - 翻訳開始時に MainViewModel が <see cref="Stop"/> し、 本番メーターへ切替 (二重キャプチャ防止)。
/// </summary>
public sealed class AudioLevelMonitor : IAudioLevelMonitor
{
    private static readonly TimeSpan EmitInterval = TimeSpan.FromMilliseconds(50);

    private readonly IAudioCaptureService _capture;
    private readonly InputGainStage _gain = new(0f);
    private readonly System.Threading.Lock _stateLock = new();

    private DateTime _lastEmitUtc = DateTime.MinValue;
    private float _peakAccum;
    private bool _isMonitoring;
    private bool _isDisposed;
    // キャプチャ開始リトライ (StartCaptureWithRetryAsync は cancel まで 1 秒間隔で無限リトライする) を
    // プロセス切替/停止時に確実に止めるための CTS。 これがないと音声未再生プロセス選択時にバックグラウンドで
    // 永久リトライが残り、 次の Start のループと _capture を競合する。
    private CancellationTokenSource? _cts;

    public AudioLevelMonitor()
    {
        // プレビュー専用キャプチャ。 本番と同じ AudioCaptureService を別インスタンスで使う。
        _capture = new AudioCaptureService();
        _capture.AudioDataAvailable += OnAudioDataAvailable;
    }

    /// <summary>テスト用 (キャプチャをモック差し替え可能にする)。</summary>
    internal AudioLevelMonitor(IAudioCaptureService capture)
    {
        _capture = capture;
        _capture.AudioDataAvailable += OnAudioDataAvailable;
    }

    public float GainDb
    {
        get => _gain.GainDb;
        set => _gain.GainDb = value;
    }

    public bool IsMonitoring
    {
        get { lock (_stateLock) { return _isMonitoring; } }
    }

    public event EventHandler<AudioLevelEventArgs>? LevelUpdated;

    public async Task StartAsync(int processId, SynchronizationContext? captureCreationContext = null, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || processId <= 0) return;

        // 既存セッション (と進行中のリトライループ) を止めてから開始 (idempotent)。
        Stop();

        // このプレビュー専用の CTS を作り、 外部トークンとリンクする。 所有権はこの StartAsync にあり、
        // finally で dispose する。 Stop は _cts 経由で cancel だけ行う (無限リトライを確実に終わらせる)。
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_stateLock) { _cts = cts; }

        try
        {
            // WASAPI Process Loopback は STA (UI) スレッドにバインドするため、 UI コンテキストを渡す
            // (本番パイプラインの MainViewModel.StartAsync と同じ作法)。
            var started = await _capture.StartCaptureWithRetryAsync(processId, cts.Token, captureCreationContext).ConfigureAwait(false);

            bool isCurrent;
            lock (_stateLock)
            {
                // 自分が最新の Start でなければ (cts が差し替えられた) 状態を触らない (stale 完了の上書き防止)。
                isCurrent = _cts == cts;
                if (isCurrent)
                {
                    _isMonitoring = started;
                    _lastEmitUtc = DateTime.MinValue;
                    _peakAccum = 0f;
                }
            }
            // キャプチャ不可 (音声未再生プロセス等) はエラーにせず無音表示に倒す。
            if (isCurrent && !started) EmitSilence();
        }
        catch (Exception)
        {
            // silent-fail: プレビューは補助機能なので失敗しても翻訳本体に影響させない。
            bool isCurrent;
            lock (_stateLock)
            {
                isCurrent = _cts == cts;
                if (isCurrent) _isMonitoring = false;
            }
            if (isCurrent) EmitSilence();
        }
        finally
        {
            lock (_stateLock) { if (_cts == cts) _cts = null; }
            cts.Dispose();
        }
    }

    public void Stop()
    {
        bool wasMonitoring;
        CancellationTokenSource? cts;
        lock (_stateLock)
        {
            wasMonitoring = _isMonitoring;
            _isMonitoring = false;
            cts = _cts; // dispose は所有者 (StartAsync の finally) に任せ、 ここでは cancel のみ。
        }

        // 進行中のリトライループを止める。 race で既に dispose 済みでも例外を握り潰す。
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }

        // ⚠️ WASAPI の StopRecording + Dispose は native callback 完了待ちで数百 ms ブロックする。
        // Stop は UI スレッド (OnSelectedProcessChanged → StartPreviewMonitor 経由) から同期で呼ばれるため、
        // ここで直接 _capture.StopCapture() を呼ぶと UI がフリーズする (本番パイプラインと同じ既知の罠、 2026-05-17)。
        // バックグラウンドスレッドへ逃がして UI をブロックしない。 _isMonitoring は既に false なので、
        // 停止中に来る AudioDataAvailable は OnAudioDataAvailable 冒頭の monitoring ガードで無視される。
        var capture = _capture;
        Task.Run(() =>
        {
            try { capture.StopCapture(); }
            catch (Exception) { /* プレビュー停止失敗は無害 (次の Start で再生成) */ }
        });

        if (wasMonitoring) EmitSilence();
    }

    private void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
    {
        lock (_stateLock)
        {
            if (!_isMonitoring) return;
        }

        // 入力ゲインを in-place 適用 (本番パイプラインと同じ前処理)。 mono float32 想定。
        var span = e.AudioData.AsSpan();
        _gain.Process(span);

        float peak = _peakAccum;
        for (int i = 0; i < span.Length; i++)
        {
            float a = MathF.Abs(span[i]);
            if (a > peak) peak = a;
        }
        _peakAccum = peak;

        var now = DateTime.UtcNow;
        if (now - _lastEmitUtc < EmitInterval) return;
        _lastEmitUtc = now;
        float db = DspMath.AmplitudeToDb(_peakAccum);
        _peakAccum = 0f;
        LevelUpdated?.Invoke(this, new AudioLevelEventArgs { PeakDb = db });
    }

    private void EmitSilence()
        => LevelUpdated?.Invoke(this, new AudioLevelEventArgs { PeakDb = DspMath.FloorDb });

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _capture.AudioDataAvailable -= OnAudioDataAvailable;
        Stop();
        _capture.Dispose();
    }
}
