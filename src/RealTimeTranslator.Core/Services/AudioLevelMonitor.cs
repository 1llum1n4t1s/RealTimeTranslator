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
    // Codex 指摘 [3329103853] 対応: Start/Stop を直列化して「停止完了前に同じ _capture で再開」レースを防ぐ。
    // Stop が投げたバックグラウンド StopCapture (WASAPI native 待ち) を、 次の Start が必ず await してから
    // StartCaptureWithRetryAsync を呼ぶようにする。 _capture は 1 インスタンス使い回しのままで安全になる。
    private readonly SemaphoreSlim _startStopGate = new(1, 1);
    private Task _pendingStop = Task.CompletedTask;

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

        // 既存セッション (と進行中のリトライループ) を止める (idempotent)。 Stop は UI をブロックしないよう
        // StopCapture をバックグラウンドへ逃がし、 その停止 Task を _pendingStop に残す。
        Stop();
        Task pendingStop;
        lock (_stateLock) { pendingStop = _pendingStop; }

        // このプレビュー専用の CTS を作り、 外部トークンとリンクする。 所有権はこの StartAsync にあり、
        // finally で dispose する。 Stop は _cts 経由で cancel だけ行う (無限リトライを確実に終わらせる)。
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_stateLock) { _cts = cts; }

        // Codex 指摘 [3329103853]: Start/Stop を直列化し、 直前の StopCapture (WASAPI native 待ち) が
        // 完了してから同じ _capture で再開する。 これがないと StartCaptureWithRetryAsync 内の同期 StopCapture と
        // バックグラウンド停止が競合し、 プロセス切替フリーズ/レースが再発する。
        await _startStopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 直前の停止完了を待つ (UI スレッドではなくここ = await 後の継続スレッドで待つので UI は固まらない)。
            try { await pendingStop.ConfigureAwait(false); } catch { /* 停止失敗は無害 */ }
            if (cts.IsCancellationRequested) return;

            // WASAPI Process Loopback は STA (UI) スレッドにバインドするため、 UI コンテキストを渡す
            // (本番パイプラインの MainViewModel.StartAsync と同じ作法)。
            var started = await _capture.StartCaptureWithRetryAsync(processId, cts.Token, captureCreationContext).ConfigureAwait(false);

            bool isCurrent;
            bool cancelledButStarted = false;
            lock (_stateLock)
            {
                // 自分が最新の Start (cts 未差し替え) かつ、 この await 中に Stop でキャンセルされていない場合だけ
                // 状態を publish する。 Codex [3329151908]: cts.IsCancellationRequested を見ないと、 開始直後に
                // Stop された (capture が started=true で完了する) とき _cts==cts のまま _isMonitoring=true を
                // 再点灯してしまい、 本番翻訳キャプチャと二重起動になる。
                bool stillOwner = _cts == cts;
                isCurrent = stillOwner && !cts.IsCancellationRequested;
                if (isCurrent)
                {
                    _isMonitoring = started;
                    _lastEmitUtc = DateTime.MinValue;
                    _peakAccum = 0f;
                }
                else if (stillOwner && started)
                {
                    // まだ最新 Start だが Stop でキャンセルされた。 起動してしまった capture を停止する必要がある。
                    // (新 Start が登場して stillOwner=false の場合は、 その新 Start が StartCaptureWithRetryAsync 内で
                    //  既存 capture を停止するので、 ここでは何もしない。)
                    cancelledButStarted = true;
                }
            }
            if (cancelledButStarted)
            {
                // キャンセル済みなのに起動した capture を停止 (二重キャプチャ防止)。 _pendingStop に chain するので
                // 次の Start はこの停止完了後に再開する。
                ScheduleStopCapture();
            }
            // キャプチャ不可 (音声未再生プロセス等) はエラーにせず無音表示に倒す。
            else if (isCurrent && !started) EmitSilence();
        }
        catch (OperationCanceledException)
        {
            // Stop/Dispose による正常キャンセル。 状態は Stop 側で false 済み。
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
            // CodeRabbit 指摘: Dispose() がゲート解放待ちをタイムアウトして先に _startStopGate を破棄した
            // 稀ケースでは Release が ObjectDisposedException になるため握り潰す (通常は Dispose が解放を待つ)。
            try { _startStopGate.Release(); }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
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
        // Codex 指摘 [3329103853] / [3329151910]: 停止 Task を _pendingStop に積み、 次の StartAsync が await して
        // から再開する。 これで「停止完了前に同じ _capture を再利用」する race を防ぐ。 既に in-flight な停止が
        // あれば置き換えず chain する (置き換えると AudioCaptureService の _isStopping ガードで no-op 停止が
        // 先に完了し、 次の Start が「停止完了」と誤認してしまう)。
        ScheduleStopCapture();

        if (wasMonitoring) EmitSilence();
    }

    /// <summary>
    /// StopCapture をバックグラウンドで直列に積む (Codex [3329151910])。 既に in-flight な停止 Task があれば
    /// それを await してから StopCapture を呼ぶ。 単純に <c>_pendingStop = Task.Run(StopCapture)</c> で置き換えると、
    /// <see cref="AudioCaptureService.StopCapture"/> は同時呼び出しを <c>_isStopping</c> ガードで即 return するため、
    /// 置き換え Task が「native 停止していない no-op」のまま先に完了し、 次の Start が「停止完了」と誤認して
    /// 旧 capture をまだ破棄中なのに再利用するレースになる。 chain することで _pendingStop は常に
    /// 「これまでの全 native 停止の完了」を表す。 WASAPI 停止は数百 ms ブロックするため UI スレッド外で実行する。
    /// </summary>
    private void ScheduleStopCapture()
    {
        var capture = _capture;
        lock (_stateLock)
        {
            var previous = _pendingStop;
            _pendingStop = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch (Exception) { /* 前段停止失敗は無害 */ }
                try { capture.StopCapture(); } catch (Exception) { /* プレビュー停止失敗は無害 (次の Start で再生成) */ }
            });
        }
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
        Stop(); // in-flight StartAsync の cts をキャンセルし、 速やかにゲートを解放させる

        // CodeRabbit 指摘: in-flight な StartAsync がゲートを保持したまま _startStopGate.Dispose() すると、
        // その finally の _startStopGate.Release() が ObjectDisposedException になる。 Stop() で cts を
        // キャンセル済みなので StartCaptureWithRetryAsync は速やかに抜けてゲートを解放する。 解放を待ってから
        // 破棄する (in-flight が無ければ count=1 なので即 acquire。 万一抜けない場合に備えてタイムアウト付き)。
        try { _startStopGate.Wait(TimeSpan.FromSeconds(1)); }
        catch (ObjectDisposedException) { /* 二重 Dispose 等は無視 */ }

        try { _capture.Dispose(); } catch (Exception) { /* best effort */ }
        _startStopGate.Dispose();
    }
}
