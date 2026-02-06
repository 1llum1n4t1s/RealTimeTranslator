using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

using System.Buffers;
using System.Diagnostics;
using System.Linq;
using System.Threading;

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
    private readonly List<float> _audioBuffer = [];
    private readonly object _bufferLock = new();
    private bool _isCapturing;
    private bool _isDisposed;
    private int _targetProcessId;
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
            _settings.VADSensitivity = settings.VADSensitivity;
            _settings.MinSpeechDuration = settings.MinSpeechDuration;
            _settings.MaxSpeechDuration = settings.MaxSpeechDuration;
            _settings.SilenceThreshold = settings.SilenceThreshold;

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
                    // Minimal で実音が取れた条件に合わせる（includeProcessTree: false）
                    var capture = await WasapiCapture.CreateForProcessCaptureAsync(processId, false).ConfigureAwait(false);
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
                    // ConfigureAwait(true) で継続をこのスレッドに固定。ここで WasapiCapture が SyncContext をキャプチャする。includeProcessTree=false は Minimal で実音が取れた条件に合わせる。
                    var capture = await WasapiCapture.CreateForProcessCaptureAsync(processId, false).ConfigureAwait(true);
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
        var n = System.Threading.Interlocked.Increment(ref _packetCount);
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

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
            return;

        // NAudio の内部バッファは再利用されるため、イベント内で即コピーしてから扱う
        var bufferCopy = new byte[e.BytesRecorded];
        Array.Copy(e.Buffer, 0, bufferCopy, 0, e.BytesRecorded);

        var sourceFormat = _capture!.WaveFormat;
        var callCount = System.Threading.Interlocked.Increment(ref _dataAvailableCallCount);
        if (callCount == 1)
        {
            LoggerService.LogDebug($"[Capture] WaveFormat: Encoding={sourceFormat.Encoding}, SampleRate={sourceFormat.SampleRate}, Channels={sourceFormat.Channels}, BitsPerSample={sourceFormat.BitsPerSample}");
        }
        var logHex = callCount <= 3 || callCount % 100 == 0;
        if (logHex)
        {
            var len = Math.Min(16, bufferCopy.Length);
            var hex = BitConverter.ToString(bufferCopy, 0, len).Replace("-", " ");
            LoggerService.LogDebug($"[Capture] #{callCount} bufferCopy (first {len} bytes): {hex}");
        }
        // 16bit PCM のときは生値の範囲をログ用に取得（無音判定の切り分け用）
        var (raw16Min, raw16Max) = GetRaw16BitRange(bufferCopy, e.BytesRecorded, sourceFormat);

        // NAudio の RawSourceWaveStream ＋ ToSampleProvider で float 変換（2ch の場合は StereoToMono 含む）
        var samples = ConvertToFloat(bufferCopy, e.BytesRecorded, sourceFormat);
        var max = samples.Length > 0 ? samples.Max(s => Math.Abs(s)) : 0f;
        if (max > NonSilentAmplitudeThreshold)
            _hasReceivedNonSilentDataSinceStart = true;
        if (callCount <= 5 || callCount % 50 == 0)
        {
            var avg = samples.Length > 0 ? samples.Average(s => Math.Abs(s)) : 0f;
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

        // バッファに追加（VAD用）
        lock (_bufferLock)
        {
            var targetSampleRate = _settings.SampleRate;

            // VAD用のリサンプリング済みサンプルを準備
            var resampledForVad = sourceFormat.SampleRate != targetSampleRate
                ? Resample(samples, sourceFormat.SampleRate, targetSampleRate)
                : (float[])samples.Clone();

            _audioBuffer.AddRange(resampledForVad);

            // バッファサイズが上限を超えた場合は古いデータを削除
            if (_audioBuffer.Count > MaxBufferSize)
            {
                var excessSamples = _audioBuffer.Count - MaxBufferSize;
                _audioBuffer.RemoveRange(0, excessSamples);
                LoggerService.LogDebug($"Audio buffer overflow prevented: removed {excessSamples} samples");
            }

            // 一定量のデータが溜まったらイベントを発火
            var samplesPerChunk = targetSampleRate * AudioChunkDurationMs / 1000;
            while (_audioBuffer.Count >= samplesPerChunk)
            {
                var chunk = new float[samplesPerChunk];
                _audioBuffer.CopyTo(0, chunk, 0, samplesPerChunk);
                _audioBuffer.RemoveRange(0, samplesPerChunk);

                AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(chunk, DateTime.Now));
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _isCapturing = false;
        if (e.Exception != null)
        {
            LoggerService.LogError($"Recording stopped with error: {e.Exception.Message}");
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
            return read == totalSamples ? samples : samples.Take(read).ToArray();
        }
        if (format.Channels == 2)
        {
            var stereoTomono = new StereoToMonoSampleProvider(sourceProvider);
            var monoCount = totalSamples / 2;
            var samples = new float[monoCount];
            var read = stereoTomono.Read(samples, 0, monoCount);
            return read == monoCount ? samples : samples.Take(read).ToArray();
        }
        var allSamples = new float[totalSamples];
        var totalRead = sourceProvider.Read(allSamples, 0, totalSamples);
        return ConvertToMono(allSamples.Take(totalRead).ToArray(), format.Channels);
    }

    /// <summary>
    /// NAudio の WdlResamplingSampleProvider でリサンプリングする。
    /// </summary>
    private float[] Resample(float[] samples, int sourceSampleRate, int targetSampleRate)
    {
        if (sourceSampleRate == targetSampleRate)
            return samples;

        var inputFormat = WaveFormat.CreateIeeeFloatWaveFormat(sourceSampleRate, MonoChannelCount);
        var inputProvider = new BufferSampleProvider(samples, inputFormat);
        var resampler = new WdlResamplingSampleProvider(inputProvider, targetSampleRate);
        var outputCount = (int)(samples.Length * (double)targetSampleRate / sourceSampleRate);
        var output = new float[outputCount];
        var read = resampler.Read(output, 0, outputCount);
        return read == outputCount ? output : output.Take(read).ToArray();
    }

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

    /// <summary>
    /// float[] を ISampleProvider として読み出すためのラッパー（WdlResamplingSampleProvider の入力用）
    /// </summary>
    private sealed class BufferSampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public BufferSampleProvider(float[] samples, WaveFormat waveFormat)
        {
            _samples = samples;
            WaveFormat = waveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var toRead = Math.Min(count, _samples.Length - _position);
            if (toRead <= 0)
                return 0;
            Array.Copy(_samples, _position, buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }
    }
}
