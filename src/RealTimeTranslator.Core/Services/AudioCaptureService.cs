using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
// System.Linq / System.Threading は GlobalUsings に含まれるため明示 using 不要 (rere /opop Cleaner #4)。

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// 音声キャプチャサービス
/// WasapiRecorder を使用したプロセス単位のオーディオキャプチャを実装
/// </summary>
public class AudioCaptureService : IAudioCaptureService
{
    private const int AudioChunkDurationMs = 100; // 音声チャンクの長さ（ミリ秒）
    private const int MonoChannelCount = 1; // モノラルチャンネル数
    private const int BytesPerFloat = 4;
    private const int RetryIntervalMs = 1000; // リトライ間隔（ミリ秒）
    private const int ProcessLoopbackBufferMs = 20; // 旧フォーク版と同じ Process Loopback バッファ長
    private const int CaptureStateTransitionTimeoutMs = 3000;
    private const int MaxBufferSize = 48000; // 最大バッファサイズ（1秒分の48kHzオーディオ）
    private static readonly WaveFormat ProcessLoopbackFormat = new(48000, 16, 2);
    private ICaptureRecorder? _capture;
    private WaveFormat? _targetFormat;
    private readonly AudioCaptureSettings _settings;
    private readonly Func<int, CancellationToken, Task<ICaptureRecorder>> _createRecorder;
    // Queue&lt;float&gt; を使うことで先頭からの Dequeue が O(1) になる。
    // 旧 List&lt;float&gt;.RemoveRange(0, N) は内部 memmove で O(残量) だった。
    private readonly Queue<float> _audioBuffer = new();
    private readonly System.Threading.Lock _bufferLock = new();
    private bool _isCapturing;
    private volatile bool _isDisposed;
    private int _targetProcessId;

    // pipeline の停止期限後もネイティブ停止は継続する。参照の生成から破棄までを
    // 同じゲートで囲み、旧停止が新しい recorder の所有権を消さないようにする。
    private readonly SemaphoreSlim _captureLifecycleGate = new(1, 1);
    private readonly System.Threading.Lock _stopLock = new();
    private volatile bool _isStopping;
    private int _dataAvailableCallCount;
    private int _packetCount;
    private const float NonSilentAmplitudeThreshold = 0.001f;
    private volatile bool _hasReceivedNonSilentDataSinceStart;
    private bool _loggedPlaceholderWarning;

    /// <summary>
    /// キャプチャ中かどうか
    /// </summary>
    public bool IsCapturing => _isCapturing;

    /// <summary>
    /// 今回のキャプチャ開始以降、無音でないデータを一度でも受信したか
    /// </summary>
    public bool HasReceivedNonSilentDataSinceStart => _hasReceivedNonSilentDataSinceStart;

    /// <summary>
    /// 音声データが利用可能になったときに発火するイベント
    /// </summary>
    public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;

    /// <summary>
    /// キャプチャ状態が変化したときに発火するイベント
    /// </summary>
    public event EventHandler<CaptureStatusEventArgs>? CaptureStatusChanged;

    public AudioCaptureService(AudioCaptureSettings? settings = null)
        : this(settings, CreateProcessLoopbackRecorderAsync)
    {
    }

    internal AudioCaptureService(
        AudioCaptureSettings? settings,
        Func<int, CancellationToken, Task<ICaptureRecorder>> createRecorder)
    {
        _settings = settings ?? new AudioCaptureSettings();
        _targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(_settings.SampleRate, MonoChannelCount);
        _createRecorder = createRecorder;
    }

    // ネイティブ境界だけを差し替え、開始・停止・所有状態は本サービスで検証する。
    internal interface ICaptureRecorder : IDisposable
    {
        WaveFormat WaveFormat { get; }
        CaptureState CaptureState { get; }
        event CaptureDataAvailableHandler DataAvailable;
        event EventHandler<StoppedEventArgs> RecordingStopped;
        void StartRecording();
        void StopRecording();
    }

    private sealed class WasapiCaptureRecorder(WasapiRecorder recorder) : ICaptureRecorder
    {
        public WaveFormat WaveFormat => recorder.WaveFormat;
        public CaptureState CaptureState => recorder.CaptureState;
        public event CaptureDataAvailableHandler DataAvailable
        {
            add => recorder.DataAvailable += value;
            remove => recorder.DataAvailable -= value;
        }
        public event EventHandler<StoppedEventArgs> RecordingStopped
        {
            add => recorder.RecordingStopped += value;
            remove => recorder.RecordingStopped -= value;
        }
        public void StartRecording() => recorder.StartRecording();
        public void StopRecording() => recorder.StopRecording();
        public void Dispose() => recorder.Dispose();
    }

    /// <summary>
    /// 設定を再適用
    /// </summary>
    public void ApplySettings(AudioCaptureSettings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        lock (_bufferLock)
        {
            _settings.SampleRate = settings.SampleRate;

            _targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(_settings.SampleRate, MonoChannelCount);
            _audioBuffer.Clear();
        }
    }

    /// <summary>
    /// 指定したプロセスIDの音声キャプチャを開始（同期版）
    /// </summary>
    public void StartCapture(int processId)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId), "プロセスIDは正の値で指定してください。");

        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!_captureLifecycleGate.Wait(0))
            throw new InvalidOperationException("音声キャプチャの開始・停止処理が完了していません。");
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_isStopping)
                throw new InvalidOperationException("音声キャプチャの停止処理が完了していません。");

            CleanupCapture();
            _isCapturing = false;
            _targetProcessId = processId;
            _audioBuffer.Clear();

            _capture = _createRecorder(_targetProcessId, CancellationToken.None)
                .GetAwaiter().GetResult();
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_isStopping)
                throw new InvalidOperationException("音声キャプチャの停止が要求されました。");
            AttachCaptureEvents();
            StartRecorderAsync(_capture, CancellationToken.None).GetAwaiter().GetResult();
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_isStopping)
                throw new InvalidOperationException("音声キャプチャの停止が要求されました。");
            _isCapturing = true;
        }
        catch
        {
            CleanupCapture();
            throw;
        }
        finally
        {
            _captureLifecycleGate.Release();
        }
    }

    /// <summary>
    /// 指定したプロセスIDの音声キャプチャを開始（リトライ付き）
    /// WasapiRecorderBuilder を使用してプロセス単位のキャプチャを実現
    /// </summary>
    /// <inheritdoc />
    public async Task<bool> StartCaptureWithRetryAsync(int processId, CancellationToken cancellationToken)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId), "プロセスIDは正の値で指定してください。");

        if (_isDisposed) return false;
        bool entered;
        try
        {
            entered = await _captureLifecycleGate.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            OnCaptureStatusChanged("音声キャプチャがキャンセルされました。", false);
            return false;
        }
        if (!entered)
        {
            LoggerService.LogWarning("[キャプチャ] 前の開始・停止処理が 5 秒以内に完了しなかったため、開始しませんでした。");
            OnCaptureStatusChanged("前の音声キャプチャの開始・停止処理が完了していません。完了後に再開してください。", false);
            return false;
        }
        try
        {
            if (_isDisposed || _isStopping) return false;
            return await StartCaptureWithRetryCoreAsync(processId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _captureLifecycleGate.Release();
        }
    }

    private async Task<bool> StartCaptureWithRetryCoreAsync(int processId, CancellationToken cancellationToken)
    {
        CleanupCapture();
        _isCapturing = false;

        _targetProcessId = processId;
        _audioBuffer.Clear();
        _hasReceivedNonSilentDataSinceStart = false;
        _loggedPlaceholderWarning = false;

        var retryCount = 0;
        var retryStopwatch = Stopwatch.StartNew();
        var maxRetries = 30;

        LoggerService.LogInfo($"[キャプチャ] 受け取ったプロセスID={processId} で WasapiRecorder を構築します");
        LoggerService.LogDebug($"StartCaptureWithRetryAsync: Starting process capture for PID {processId}");

        while (retryCount < maxRetries && !cancellationToken.IsCancellationRequested && !_isDisposed && !_isStopping)
        {
            try
            {
                _capture = await _createRecorder(processId, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (_isDisposed || _isStopping)
                {
                    CleanupCapture();
                    return false;
                }
                AttachCaptureEvents();
                await StartRecorderAsync(_capture, cancellationToken).ConfigureAwait(false);
                if (_isDisposed || _isStopping)
                {
                    CleanupCapture();
                    return false;
                }
                _isCapturing = true;

                OnCaptureStatusChanged("音声キャプチャを開始しました。", false);
                LoggerService.LogInfo($"StartCaptureWithRetryAsync: Successfully started capture for process {processId}");
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CleanupCapture();
                OnCaptureStatusChanged("音声キャプチャがキャンセルされました。", false);
                return false;
            }
            catch (ArgumentException argEx)
            {
                // プロセスがまだ音を出していない可能性
                LoggerService.LogWarning($"StartCaptureWithRetryAsync: Process not found or not outputting audio - {argEx.Message}");
                CleanupCapture();
            }
            catch (InvalidOperationException opEx)
            {
                // WasapiRecorder がサポートされていない環境の可能性
                LoggerService.LogWarning($"StartCaptureWithRetryAsync: WasapiRecorder not supported - {opEx.Message}");
                CleanupCapture();
                return false;
            }
            catch (Exception ex)
            {
                LoggerService.LogError($"StartCaptureWithRetryAsync: Error for process {processId} - {ex.GetType().Name}: {ex.Message}");
                LoggerService.LogDebug($"StartCaptureWithRetryAsync: StackTrace: {ex.StackTrace}");
                CleanupCapture();
            }

            // リトライ前に最大試行回数をチェック
            retryCount++;
            if (retryCount >= maxRetries)
            {
                LoggerService.LogError($"StartCaptureWithRetryAsync: Max retries ({maxRetries}) exceeded for process {processId}");
                OnCaptureStatusChanged("音声キャプチャの開始に失敗しました。", false);
                return false;
            }

            // リトライ待機
            var elapsedSeconds = Math.Round(retryStopwatch.Elapsed.TotalSeconds, 1);
            var statusMessage = $"音声の再生を待機中... ({elapsedSeconds}秒, 試行: {retryCount})";
            OnCaptureStatusChanged(statusMessage, true);
            LoggerService.LogDebug($"StartCaptureWithRetryAsync: Waiting for audio session (attempt {retryCount}/{maxRetries}, elapsed {elapsedSeconds}s)");

            try
            {
                await Task.Delay(RetryIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                OnCaptureStatusChanged("音声キャプチャがキャンセルされました。", false);
                return false;
            }
        }

        OnCaptureStatusChanged("音声キャプチャがキャンセルされました。", false);
        return false;
    }

    private static async Task<ICaptureRecorder> CreateProcessLoopbackRecorderAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        var recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)processId, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(ProcessLoopbackFormat)
            .WithBufferLength(ProcessLoopbackBufferMs)
            .BuildAsync(cancellationToken).ConfigureAwait(false);
        return new WasapiCaptureRecorder(recorder);
    }

    private static async Task StartRecorderAsync(
        ICaptureRecorder capture,
        CancellationToken cancellationToken)
    {
        capture.StartRecording();

        // NAudio 4.0.0 は StartRecording 直後に StopRecording すると、capture thread が
        // Stopping を Capturing で上書きして Dispose が永久待ちになる競合がある。
        // Capturing への遷移を確認してから開始成功を返し、呼び出し側の即時 Stop を安全にする。
        var stopwatch = Stopwatch.StartNew();
        while (capture.CaptureState == CaptureState.Starting
               && stopwatch.ElapsedMilliseconds < CaptureStateTransitionTimeoutMs)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (capture.CaptureState != CaptureState.Capturing)
        {
            throw new InvalidOperationException(
                $"WasapiRecorder が開始状態へ遷移しませんでした: {capture.CaptureState}");
        }
    }

    private static void StopAndDisposeRecorder(ICaptureRecorder capture)
    {
        if (capture.CaptureState == CaptureState.Starting)
        {
            _ = SpinWait.SpinUntil(
                () => capture.CaptureState != CaptureState.Starting,
                CaptureStateTransitionTimeoutMs);
        }

        if (capture.CaptureState != CaptureState.Stopped)
        {
            capture.StopRecording();
        }
        capture.Dispose();
    }

    /// <summary>
    /// _capture にイベントを登録し、WaveFormat をログする。
    /// </summary>
    private void AttachCaptureEvents()
    {
        if (_capture == null) return;
        _packetCount = 0;
        LoggerService.LogDebug($"StartCaptureWithRetryAsync: Capture WaveFormat SampleRate={_capture.WaveFormat.SampleRate}, Channels={_capture.WaveFormat.Channels}, BitsPerSample={_capture.WaveFormat.BitsPerSample}, Encoding={_capture.WaveFormat.Encoding}");
        _dataAvailableCallCount = 0;
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    /// <summary>
    /// キャプチャオブジェクトをクリーンアップ
    /// </summary>
    private void CleanupCapture()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            StopAndDisposeRecorder(_capture);
            _capture = null;
        }
    }

    /// <summary>
    /// キャプチャ状態変更イベントを発火
    /// </summary>
    private void OnCaptureStatusChanged(string message, bool isWaiting, Exception? error = null)
    {
        CaptureStatusChanged?.Invoke(this, new CaptureStatusEventArgs(message, isWaiting, error));
    }

    /// <summary>
    /// 音声キャプチャを停止
    /// </summary>
    public void StopCapture()
    {
        // ⭐ rere P1 #6: _isStopping で race ガード。 同時呼び出しは 1 回だけ実体実行。
        lock (_stopLock)
        {
            if (_isStopping) return;
            _isStopping = true;
        }

        _captureLifecycleGate.Wait();
        try
        {
            if (_capture == null)
            {
                _isCapturing = false;
                return;
            }

            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;

            StopAndDisposeRecorder(_capture);
            _capture = null;
            _isCapturing = false;
        }
        finally
        {
            lock (_stopLock) { _isStopping = false; }
            _captureLifecycleGate.Release();
        }
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> buffer,
        AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
    {
        var capture = _capture;
        if (buffer.IsEmpty || capture == null)
            return;

        var packetCount = Interlocked.Increment(ref _packetCount);
        var isSilent = (flags & AudioClientBufferFlags.Silent) != 0;
        if (packetCount % 500 == 0 || isSilent)
        {
            LoggerService.LogDebug($"[Packet] #{packetCount} IsSilent={isSilent}, Flags={flags}, Bytes={buffer.Length}, DevicePosition={devicePosition}, QpcPosition={qpcPosition}");
        }

        // NAudio の内部バッファは再利用されるため即コピーしてから扱う。
        // ArrayPool 借用で Gen0 ヒープ通過量を削減 (このメソッドは ~100ms ごとに発火、
        // 48kHz/2ch/float32 = 38.4 KB/call * 10 calls/sec = 385 KB/s = 約 685 MB/30min)。
        // Rent は要求以上のサイズを返すため、 サイズ参照箇所は必ず buffer.Length を使う。
        var bytesRecorded = buffer.Length;
        var bufferCopy = ArrayPool<byte>.Shared.Rent(bytesRecorded);
        try
        {
            buffer.CopyTo(bufferCopy);

            var sourceFormat = capture.WaveFormat;
            var callCount = Interlocked.Increment(ref _dataAvailableCallCount);
            if (callCount == 1)
            {
                LoggerService.LogDebug($"[Capture] WaveFormat: Encoding={sourceFormat.Encoding}, SampleRate={sourceFormat.SampleRate}, Channels={sourceFormat.Channels}, BitsPerSample={sourceFormat.BitsPerSample}");
                // D-C1 止血: 上層 (TranslationPipelineService) の StreamingResampler は 48000Hz 固定でハードコードされている。
                // Hi-Res オーディオ (96kHz/192kHz) や CD レート (44.1kHz) device が default playback だと
                // 時間軸が歪み VAD / transcribe 精度が崩壊する経路。 検知時に WARN ログを 1 度だけ残す。
                // 根本修正 (sampleRate 動的化) は議題化のまま残す (Pipeline 全層に sampleRate 伝搬が必要)。
                if (sourceFormat.SampleRate != 48000)
                {
                    LoggerService.LogWarning($"[Capture] ⚠️ 想定外の SampleRate を検出: {sourceFormat.SampleRate}Hz (期待値 48000Hz)。 リサンプラが 48k 前提のため、 VAD 判定や字幕精度が劣化する可能性があります。 Windows サウンド設定で playback device を 48kHz に変更することを推奨します。");
                }
            }
            var logHex = callCount <= 3 || callCount % 100 == 0;
            if (logHex)
            {
                var len = Math.Min(16, bytesRecorded);
                var hex = BitConverter.ToString(bufferCopy, 0, len).Replace("-", " ");
                LoggerService.LogDebug($"[Capture] #{callCount} bufferCopy (first {len} bytes): {hex}");
            }
            // NAudio の RawSourceWaveStream ＋ ToSampleProvider で float 変換（2ch の場合は StereoToMono 含む）
            var samples = ConvertToFloat(bufferCopy, bytesRecorded, sourceFormat);

            // max スキャン・raw16 範囲・avg は「無音フラグ未確定時」または「診断ログ出力時」のみ計算する。
            // フラグ確定後 (= 一度でも有音を受信後) の常時全サンプル走査 (約10回/秒) は
            // コールバックスレッド (MMCSS) 上の無駄な負荷なので排除する。フラグは単調 true なので挙動同値。
            var needLog = callCount <= 5 || callCount % 50 == 0;
            if (!_hasReceivedNonSilentDataSinceStart || needLog)
            {
                var max = 0f;
                for (var i = 0; i < samples.Length; i++)
                {
                    var abs = Math.Abs(samples[i]);
                    if (abs > max) max = abs;
                }
                if (max > NonSilentAmplitudeThreshold)
                    _hasReceivedNonSilentDataSinceStart = true;

                if (needLog)
                {
                    // 16bit PCM の生値範囲はログ出力時のみ取得（毎チャンクの全走査を排除）。
                    var (raw16Min, raw16Max) = GetRaw16BitRange(bufferCopy, bytesRecorded, sourceFormat);
                    var sum = 0f;
                    for (var i = 0; i < samples.Length; i++)
                        sum += Math.Abs(samples[i]);
                    var avg = samples.Length > 0 ? sum / samples.Length : 0f;
                    var raw16Str = raw16Min.HasValue ? $", raw16=[{raw16Min.Value},{raw16Max!.Value}]" : "";
                    LoggerService.LogDebug($"[Capture] OnDataAvailable #{callCount}: bytes={bytesRecorded}, samples={samples.Length}, max={max:F6}, avg={avg:F6}{raw16Str}");
                    // raw16 が -1～1 のみで再生の有無で変わらない場合は、実音ではなくドライバのプレースホルダーと判断
                    if (!_loggedPlaceholderWarning && raw16Min.HasValue && raw16Max.HasValue
                        && raw16Min.Value >= -1 && raw16Max.Value <= 1 && (raw16Min.Value < 0 || raw16Max.Value > 0))
                    {
                        _loggedPlaceholderWarning = true;
                        LoggerService.LogWarning("[Capture] raw16 が [-1,1] のみです。再生停止時も同じ値なら Process Loopback が実音を返していません（ドライバ／WASAPI の挙動の可能性）。");
                    }
                }
            }

            // バッファに追加。
            // ⭐ 出力契約: WASAPI ネイティブレート (通常 48kHz) のモノラル float[] を発火する。
            // 旧設計 (v1.0.26 以前) は AudioCaptureService 内で 48k→16k リサンプルしていたが、
            // チャンク境界 (~10ms) で WdlResamplingSampleProvider 新規生成による振幅最大 90% の
            // クリックノイズが入り、 ARC Raiders 等の動的音声で VAD/STT を断続的に騙していた。
            // この層では mono 化 (StereoToMono) と native rate 切り出しだけ行い、 リサンプルは
            // TranslationPipelineService 側で「48k→16k (VAD判定用)」「48k→24k (送信用)」の
            // 2 経路に分岐させてステートフル StreamingResampler で行う設計に変更 (2026-05-24)。
            // v1.0.27 で「2 経路は無駄」と統合 (48k→16k→24k) したが、 v1.0.36 で並列 2 系統に
            // revert (中継 16k の Nyquist 8kHz 高域カットが transcribe 精度を下げていたため)。
            lock (_bufferLock)
            {
                var sampleRate = sourceFormat.SampleRate;

                foreach (var s in samples)
                {
                    _audioBuffer.Enqueue(s);
                }

                // バッファサイズが上限を超えた場合は先頭から O(1) で破棄
                if (_audioBuffer.Count > MaxBufferSize)
                {
                    var excessSamples = _audioBuffer.Count - MaxBufferSize;
                    for (var i = 0; i < excessSamples; i++)
                    {
                        _audioBuffer.Dequeue();
                    }
                    LoggerService.LogDebug($"Audio buffer overflow prevented: removed {excessSamples} samples");
                }

                // 一定量のデータが溜まったらイベントを発火（Queue.Dequeue は O(1)）
                var samplesPerChunk = sampleRate * AudioChunkDurationMs / 1000;
                while (_audioBuffer.Count >= samplesPerChunk)
                {
                    var chunk = new float[samplesPerChunk];
                    for (var i = 0; i < samplesPerChunk; i++)
                    {
                        chunk[i] = _audioBuffer.Dequeue();
                    }

                    AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(chunk, DateTime.Now));
                }
            }
        }
        finally
        {
            // ArrayPool 借用は確実に返却 (例外経路含む)。 clearArray=false でゼロ化省略 (audio data は機微性低)。
            ArrayPool<byte>.Shared.Return(bufferCopy);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _isCapturing = false;
        if (e.Exception != null)
        {
            // rere v1.0.32 #F-001 + #F-003:
            // 旧実装は `LogError(e.Exception.Message)` だけで CaptureStatusChanged を発火せず、
            // 対象プロセスが終了した時に UI が「実行中」緑表示のまま固まり、 ユーザーは「字幕が来ない」
            // 原因を切り分けられなかった。 さらに `Message` だけでは COMException の HResult が
            // 落ちて WASAPI 系のトラブルシュートに必要な情報が消えていた。
            //
            // 改修:
            // 1. `LogException` で型・メッセージ・StackTrace + COMException 専用に HResult を 16 進で記録
            // 2. `OnCaptureStatusChanged` へ例外を載せ、 UI が通常の開始成功通知と区別して
            //    パイプライン/WebSocket を停止できるようにする。
            //    typical 原因: 対象プロセス終了 / デバイス切替 / アクセス拒否 等
            string hresultText = string.Empty;
            if (e.Exception is COMException comEx)
            {
                hresultText = $" HResult=0x{comEx.HResult:X8}";
            }
            LoggerService.LogException(
                $"WASAPI キャプチャスレッドが例外で停止しました ({e.Exception.GetType().Name}{hresultText})",
                e.Exception);

            OnCaptureStatusChanged(
                $"音声キャプチャが停止しました (対象プロセス終了 / デバイス切替の可能性: {e.Exception.GetType().Name}{hresultText})",
                isWaiting: false,
                error: e.Exception);
        }
    }

    /// <summary>
    /// 16bit PCM バッファの生値の最小・最大を返す。診断用（無音判定の切り分け）。
    /// </summary>
    private static (int? Min, int? Max) GetRaw16BitRange(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (format.BitsPerSample != 16 || bytesRecorded < 2)
            return (null, null);
        var count = bytesRecorded / 2;
        int min = int.MaxValue;
        int max = int.MinValue;
        for (var i = 0; i < count; i++)
        {
            var s = BitConverter.ToInt16(buffer, i * 2);
            if (s < min) min = s;
            if (s > max) max = s;
        }
        return (min, max);
    }

    /// <summary>
    /// byte[] を NAudio の RawSourceWaveStream ＋ ToSampleProvider で float[] に変換する。
    /// マルチチャンネルの場合は StereoToMono（2ch）または ConvertToMono（3ch 以上）でモノラル化する。
    /// </summary>
    private float[] ConvertToFloat(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        var totalSamples = bytesRecorded / bytesPerSample;
        using var stream = new MemoryStream(buffer, 0, bytesRecorded, false);
        using var rawStream = new RawSourceWaveStream(stream, format);
        var sourceProvider = rawStream.ToSampleProvider();
        if (format.Channels == 1)
        {
            var samples = new float[totalSamples];
            var read = sourceProvider.Read(samples);
            return read == totalSamples ? samples : samples.AsSpan(0, read).ToArray();
        }
        if (format.Channels == 2)
        {
            var stereoTomono = new StereoToMonoSampleProvider(sourceProvider);
            var monoCount = totalSamples / 2;
            var samples = new float[monoCount];
            var read = stereoTomono.Read(samples);
            return read == monoCount ? samples : samples.AsSpan(0, read).ToArray();
        }
        var allSamples = new float[totalSamples];
        var totalRead = sourceProvider.Read(allSamples);
        return ConvertToMono(allSamples.AsSpan(0, totalRead).ToArray(), format.Channels);
    }

    // 旧 Resample(samples, sourceRate, targetRate) は削除。
    // チャンクごとに WdlResamplingSampleProvider を新規生成する設計が境界クリックノイズの
    // 元凶だったため StreamingResampler 経由に置き換え済み (2026-05-24)。

    /// <summary>
    /// マルチチャンネルをモノラルにダウンミックス（平均）。2ch は ConvertToFloat 内で StereoToMonoSampleProvider を使用するため、主に 3ch 以上で使用。
    /// </summary>
    private static float[] ConvertToMono(float[] samples, int channels)
    {
        if (channels == 1)
            return samples;

        int monoLength = samples.Length / channels;

        var rentedArray = ArrayPool<float>.Shared.Rent(monoLength);
        try
        {
            for (int i = 0; i < monoLength; i++)
            {
                float sum = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    sum += samples[i * channels + ch];
                }
                rentedArray[i] = sum / channels;
            }

            var result = new float[monoLength];
            Array.Copy(rentedArray, result, monoLength);
            return result;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedArray);
        }
    }

    /// <summary>
    /// リソースを破棄
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        StopCapture();
    }

}
