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
/// WasapiCapture を使用したプロセス単位のオーディオキャプチャを実装
/// </summary>
public class AudioCaptureService : IAudioCaptureService
{
    private const int AudioChunkDurationMs = 100; // 音声チャンクの長さ（ミリ秒）
    private const int MonoChannelCount = 1; // モノラルチャンネル数
    private const int BytesPerFloat = 4;
    private const int RetryIntervalMs = 1000; // リトライ間隔（ミリ秒）
    private const int MaxBufferSize = 48000; // 最大バッファサイズ（1秒分の48kHzオーディオ）
    private IWaveIn? _capture;
    private WaveFormat? _targetFormat;
    private readonly AudioCaptureSettings _settings;
    // Queue&lt;float&gt; を使うことで先頭からの Dequeue が O(1) になる。
    // 旧 List&lt;float&gt;.RemoveRange(0, N) は内部 memmove で O(残量) だった。
    private readonly Queue<float> _audioBuffer = new();
    private readonly System.Threading.Lock _bufferLock = new();
    private bool _isCapturing;
    private bool _isDisposed;
    private int _targetProcessId;

    // ⭐ rere P1 #6 修正: StopCapture と StartCaptureWithRetryAsync の race ガード。
    // TranslationPipelineService.StopAsync は Task.Run + WaitAsync(3s) で StopCapture を
    // バックグラウンド実行する設計なので、 3 秒タイムアウト後にユーザーが急いで「再開」を
    // 押すと、 旧 StopCapture 完了前に新 StartCapture が走り _capture フィールドが race する。
    // _isStopping フラグで「stop 進行中」を明示し、 新 Start は完了を待つ。
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
    {
        _settings = settings ?? new AudioCaptureSettings();
        _targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(_settings.SampleRate, MonoChannelCount);
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

        if (_capture != null)
            StopCapture();

        _targetProcessId = processId;
        _audioBuffer.Clear();

        _capture = new ProcessLoopbackCapture(_targetProcessId);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        _capture.StartRecording();
        _isCapturing = true;
    }

    /// <summary>
    /// 指定したプロセスIDの音声キャプチャを開始（リトライ付き）
    /// ProcessLoopbackCapture を使用してプロセス単位のキャプチャを実現
    /// </summary>
    /// <inheritdoc />
    public async Task<bool> StartCaptureWithRetryAsync(int processId, CancellationToken cancellationToken, SynchronizationContext? captureCreationContext = null)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId), "プロセスIDは正の値で指定してください。");

        // ⭐ rere P1 #6: 進行中の StopCapture を最大 5 秒待つ (race ガード)。
        // TranslationPipelineService.StopAsync が 3 秒タイムアウトしてバックグラウンドで継続中の
        // 場合、 新 Start で _capture フィールドが上書き race するため、 ここで完了を待つ。
        var stopWaitStopwatch = Stopwatch.StartNew();
        while (_isStopping && stopWaitStopwatch.ElapsedMilliseconds < 5000)
        {
            if (cancellationToken.IsCancellationRequested) return false;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        if (_isStopping)
            LoggerService.LogWarning($"[キャプチャ] StopCapture が 5 秒を超えても完了せず — race の可能性 (旧 capture が残る可能性)");

        if (_capture != null)
            StopCapture();

        _targetProcessId = processId;
        _audioBuffer.Clear();
        _hasReceivedNonSilentDataSinceStart = false;
        _loggedPlaceholderWarning = false;

        var retryCount = 0;
        var retryStopwatch = Stopwatch.StartNew();
        var maxRetries = 30;

        LoggerService.LogInfo($"[キャプチャ] 受け取ったプロセスID={processId} で CreateForProcessCaptureAsync を実行します");
        LoggerService.LogDebug($"StartCaptureWithRetryAsync: Starting process capture for PID {processId}, captureCreationContext={(captureCreationContext != null ? "UI" : "null")}");

        while (retryCount < maxRetries && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (captureCreationContext != null)
                {
                    await RunFullCaptureStartOnContextAsync(captureCreationContext, processId).ConfigureAwait(false);
                }
                else
                {
                    // 対象プロセス (とその子) の音声を取り込む (IncludeTargetProcessTree)。 ProcessLoopbackMode enum を
                    // Windows 公式準拠 (INCLUDE=0/EXCLUDE=1) に修正したのに伴い includeProcessTree=true を指定 (ネイティブ動作は従来と不変)。
                    var capture = await WasapiCapture.CreateForProcessCaptureAsync(processId, true).ConfigureAwait(false);
                    _capture = capture;
                    AttachCaptureEvents();
                    _capture.StartRecording();
                }
                _isCapturing = true;

                OnCaptureStatusChanged("音声キャプチャを開始しました。", false);
                LoggerService.LogInfo($"StartCaptureWithRetryAsync: Successfully started capture for process {processId}");
                return true;
            }
            catch (ArgumentException argEx)
            {
                // プロセスがまだ音を出していない可能性
                LoggerService.LogWarning($"StartCaptureWithRetryAsync: Process not found or not outputting audio - {argEx.Message}");
                CleanupCapture();
            }
            catch (InvalidOperationException opEx)
            {
                // WasapiCapture がサポートされていない環境の可能性
                LoggerService.LogWarning($"StartCaptureWithRetryAsync: WasapiCapture not supported - {opEx.Message}");
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

    /// <summary>
    /// 指定した SynchronizationContext（UI）上で、CreateForProcessCaptureAsync の await 継続からイベント登録・StartRecording までを一括で実行する。
    /// NAudio の Process Loopback テストと同様に、create と start を同一 UI スレッドの async フローで行うことで収録を有効にする。
    /// </summary>
    /// <summary>
    /// Process Loopback は CreateForProcessCaptureAsync の await 継続が実行されるスレッドの SynchronizationContext が WasapiCapture に保存される。
    /// そのため Post コールバック内で await に ConfigureAwait(true) を付けて継続を同一スレッドにし、同じスレッドで StartRecording を呼ぶ。
    /// </summary>
    private async Task RunFullCaptureStartOnContextAsync(SynchronizationContext context, int processId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            async void Run()
            {
                var threadId = Thread.CurrentThread.ManagedThreadId;
                var syncCtxBefore = SynchronizationContext.Current;
                LoggerService.LogDebug($"[Capture] Post callback started: ThreadId={threadId}, SyncContextNull={syncCtxBefore == null}");
                try
                {
                    // ConfigureAwait(true) で継続をこのスレッドに固定。ここで WasapiCapture が SyncContext をキャプチャする。
                    // includeProcessTree=true で対象プロセス (とその子) を取り込む (enum 公式準拠化に伴いネイティブ動作は不変の Include)。
                    var capture = await WasapiCapture.CreateForProcessCaptureAsync(processId, true).ConfigureAwait(true);
                    var threadIdAfter = Thread.CurrentThread.ManagedThreadId;
                    var syncCtxAfter = SynchronizationContext.Current;
                    LoggerService.LogDebug($"[Capture] CreateForProcessCaptureAsync continuation: ThreadId={threadIdAfter}, SyncContextNull={syncCtxAfter == null}, sameThread={threadId == threadIdAfter}");
                    _capture = capture;
                    AttachCaptureEvents();
                    _capture.StartRecording();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }
            Run();
        }, null);
        await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// _capture にイベントを登録し、WaveFormat をログする。
    /// </summary>
    private void AttachCaptureEvents()
    {
        if (_capture == null) return;
        if (_capture is WasapiCapture wasapi)
        {
            _packetCount = 0;
            wasapi.CapturePacketReceived += OnCapturePacketReceived;
        }
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
            if (_capture is WasapiCapture wasapi)
            {
                wasapi.CapturePacketReceived -= OnCapturePacketReceived;
            }
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            if (_capture is IDisposable disposable)
            {
                disposable.Dispose();
            }
            _capture = null;
        }
    }

    /// <summary>
    /// 診断用: パケットごとの Silent フラグをログ（500 パケットに 1 回、または Silent のときのみ）
    /// </summary>
    private void OnCapturePacketReceived(object? sender, WasapiCapturePacketEventArgs e)
    {
        var n = Interlocked.Increment(ref _packetCount);
        if (n % 500 == 0 || e.IsSilent)
        {
            LoggerService.LogDebug($"[Packet] #{n} IsSilent={e.IsSilent}, Flags={e.BufferFlags}, Frames={e.FramesAvailable}");
        }
    }

    /// <summary>
    /// キャプチャ状態変更イベントを発火
    /// </summary>
    private void OnCaptureStatusChanged(string message, bool isWaiting)
    {
        CaptureStatusChanged?.Invoke(this, new CaptureStatusEventArgs(message, isWaiting));
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

        try
        {
            if (_capture == null)
            {
                _isCapturing = false;
                return;
            }

            if (_capture is WasapiCapture wasapi)
            {
                wasapi.CapturePacketReceived -= OnCapturePacketReceived;
            }
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;

            if (_isCapturing)
            {
                _capture.StopRecording();
            }

            if (_capture is IDisposable disposable)
            {
                disposable.Dispose();
            }
            _capture = null;
            _isCapturing = false;
        }
        finally
        {
            lock (_stopLock) { _isStopping = false; }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
            return;

        // NAudio の内部バッファは再利用されるため即コピーしてから扱う。
        // ArrayPool 借用で Gen0 ヒープ通過量を削減 (このメソッドは ~100ms ごとに発火、
        // 48kHz/2ch/float32 = 38.4 KB/call * 10 calls/sec = 385 KB/s = 約 685 MB/30min)。
        // Rent は要求以上のサイズを返すため、 サイズ参照箇所は必ず e.BytesRecorded を使う。
        var bufferCopy = ArrayPool<byte>.Shared.Rent(e.BytesRecorded);
        try
        {
            Array.Copy(e.Buffer, 0, bufferCopy, 0, e.BytesRecorded);

            var sourceFormat = _capture!.WaveFormat;
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
                var len = Math.Min(16, e.BytesRecorded);
                var hex = BitConverter.ToString(bufferCopy, 0, len).Replace("-", " ");
                LoggerService.LogDebug($"[Capture] #{callCount} bufferCopy (first {len} bytes): {hex}");
            }
            // NAudio の RawSourceWaveStream ＋ ToSampleProvider で float 変換（2ch の場合は StereoToMono 含む）
            var samples = ConvertToFloat(bufferCopy, e.BytesRecorded, sourceFormat);

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
                    var (raw16Min, raw16Max) = GetRaw16BitRange(bufferCopy, e.BytesRecorded, sourceFormat);
                    var sum = 0f;
                    for (var i = 0; i < samples.Length; i++)
                        sum += Math.Abs(samples[i]);
                    var avg = samples.Length > 0 ? sum / samples.Length : 0f;
                    var raw16Str = raw16Min.HasValue ? $", raw16=[{raw16Min.Value},{raw16Max!.Value}]" : "";
                    LoggerService.LogDebug($"[Capture] OnDataAvailable #{callCount}: bytes={e.BytesRecorded}, samples={samples.Length}, max={max:F6}, avg={avg:F6}{raw16Str}");
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
            // 2. `OnCaptureStatusChanged` 経由で UI に終了を通知 (緑のまま固まる UX バグ解消)
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
                isWaiting: false);
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
            var read = sourceProvider.Read(samples, 0, totalSamples);
            return read == totalSamples ? samples : samples.AsSpan(0, read).ToArray();
        }
        if (format.Channels == 2)
        {
            var stereoTomono = new StereoToMonoSampleProvider(sourceProvider);
            var monoCount = totalSamples / 2;
            var samples = new float[monoCount];
            var read = stereoTomono.Read(samples, 0, monoCount);
            return read == monoCount ? samples : samples.AsSpan(0, read).ToArray();
        }
        var allSamples = new float[totalSamples];
        var totalRead = sourceProvider.Read(allSamples, 0, totalSamples);
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
