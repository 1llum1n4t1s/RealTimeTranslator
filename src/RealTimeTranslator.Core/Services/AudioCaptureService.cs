using NAudio.CoreAudioApi;
using NAudio.Wave;

using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// 音声キャプチャサービス
/// WasapiCapture を使用したプロセス単位のオーディオキャプチャを実装
/// </summary>
public class AudioCaptureService : IAudioCaptureService
{
    private const int AudioChunkDurationMs = 100; // 音声チャンクの長さ（ミリ秒）
    private const int MonoChannelCount = 1; // モノラルチャンネル数
    private const int BytesPerInt16 = 2;
    private const int BytesPerInt32 = 4;
    private const int BytesPerFloat = 4;
    private const float Int16MaxValue = 32768f; // 16-bit PCMの最大値
    private const int BitsPerSample16 = 16;
    private const int BitsPerSample32 = 32;
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
    private readonly ConcurrentQueue<AudioProcessingTask> _processingQueue = [];
    private Task? _processingTask;
    private CancellationTokenSource? _processingCancellation;
    private WaveFileWriter? _debugRawAudioWriter;
    private WaveFileWriter? _debugResampledAudioWriter;

    /// <summary>
    /// キャプチャ中かどうか
    /// </summary>
    public bool IsCapturing => _isCapturing;

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
        StartProcessingTask();
    }

    /// <summary>
    /// 指定したプロセスIDの音声キャプチャを開始（リトライ付き）
    /// ProcessLoopbackCapture を使用してプロセス単位のキャプチャを実現
    /// </summary>
    public async Task<bool> StartCaptureWithRetryAsync(int processId, CancellationToken cancellationToken)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId), "プロセスIDは正の値で指定してください。");

        if (_capture != null)
            StopCapture();

        _targetProcessId = processId;
        _audioBuffer.Clear();

        var retryCount = 0;
        var retryStopwatch = Stopwatch.StartNew();
        var maxRetries = 30;

        LoggerService.LogDebug($"StartCaptureWithRetryAsync: Starting process capture for PID {processId}");

        while (retryCount < maxRetries && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                // WasapiCapture.CreateForProcessCaptureAsync でプロセス単位のキャプチャを開始
                // includeTree=true により、子プロセスの音声も含める
                _capture = await WasapiCapture.CreateForProcessCaptureAsync(processId, true);
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;

                _capture.StartRecording();
                _isCapturing = true;
                StartProcessingTask();

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
    /// キャプチャオブジェクトをクリーンアップ
    /// </summary>
    private void CleanupCapture()
    {
        if (_capture != null)
        {
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

        // バイトデータをfloatに変換
        var sourceFormat = _capture!.WaveFormat;
        var samples = ConvertToFloat(e.Buffer, e.BytesRecorded, sourceFormat);

        int targetSampleRate;
        lock (_bufferLock)
        {
            targetSampleRate = _settings.SampleRate;
        }

        // モノラルに変換（必要に応じて）
        if (sourceFormat.Channels > 1)
        {
            samples = ConvertToMono(samples, sourceFormat.Channels);
        }

        // Debug: raw audio before resampling to WAV file (モノラル変換後、リサンプリング前)
        try
        {
            var debugRawFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_audio_raw");
            Directory.CreateDirectory(debugRawFolder);

            if (_debugRawAudioWriter == null)
            {
                var debugRawPath = Path.Combine(debugRawFolder, "debug_audio_raw.wav");
                // モノラル変換後なので MonoChannelCount を使用
                var format = WaveFormat.CreateIeeeFloatWaveFormat(sourceFormat.SampleRate, MonoChannelCount);
                _debugRawAudioWriter = new WaveFileWriter(debugRawPath, format);
                LoggerService.LogDebug($"OnDataAvailable: Created debug raw audio file at {debugRawPath}");
            }

            var byteArray = new byte[samples.Length * BytesPerFloat];
            Buffer.BlockCopy(samples, 0, byteArray, 0, byteArray.Length);
            _debugRawAudioWriter.Write(byteArray, 0, byteArray.Length);
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"OnDataAvailable: Failed to write debug raw audio file - {ex.Message}");
        }

        // 非同期処理キューに追加
        var processingTask = new AudioProcessingTask(samples, sourceFormat.SampleRate, targetSampleRate);
        _processingQueue.Enqueue(processingTask);

        // バッファに追加（VAD用）
        lock (_bufferLock)
        {
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

    private float[] ConvertToFloat(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        int sampleCount;
        float[] samples;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            sampleCount = bytesRecorded / BytesPerFloat;
            samples = new float[sampleCount];
            Buffer.BlockCopy(buffer, 0, samples, 0, bytesRecorded);
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm)
        {
            if (format.BitsPerSample == BitsPerSample16)
            {
                sampleCount = bytesRecorded / BytesPerInt16;
                samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    short sample = BitConverter.ToInt16(buffer, i * BytesPerInt16);
                    samples[i] = sample / Int16MaxValue;
                }
            }
            else if (format.BitsPerSample == BitsPerSample32)
            {
                sampleCount = bytesRecorded / BytesPerInt32;
                samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    int sample = BitConverter.ToInt32(buffer, i * BytesPerInt32);
                    samples[i] = sample / (float)int.MaxValue;
                }
            }
            else
            {
                throw new NotSupportedException($"Unsupported bits per sample: {format.BitsPerSample}");
            }
        }
        else
        {
            throw new NotSupportedException($"Unsupported encoding: {format.Encoding}");
        }

        return samples;
    }

    private float[] Resample(float[] samples, int sourceSampleRate, int targetSampleRate)
    {
        if (sourceSampleRate == targetSampleRate)
            return samples;

        double ratio = (double)targetSampleRate / sourceSampleRate;
        int newLength = (int)(samples.Length * ratio);

        var rentedArray = ArrayPool<float>.Shared.Rent(newLength);
        try
        {
            for (int i = 0; i < newLength; i++)
            {
                double sourceIndex = i / ratio;
                int index = (int)sourceIndex;
                double fraction = sourceIndex - index;

                if (index + 1 < samples.Length)
                {
                    rentedArray[i] = (float)(samples[index] * (1 - fraction) + samples[index + 1] * fraction);
                }
                else if (index < samples.Length)
                {
                    rentedArray[i] = samples[index];
                }
            }

            var result = new float[newLength];
            Array.Copy(rentedArray, result, newLength);
            return result;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedArray);
        }
    }

    private float[] ConvertToMono(float[] samples, int channels)
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
    /// 非同期リサンプリング処理タスクを開始
    /// </summary>
    private void StartProcessingTask()
    {
        if (_processingTask != null)
            return;

        _processingCancellation = new CancellationTokenSource();
        _processingTask = ProcessAudioQueueAsync(_processingCancellation.Token);
        LoggerService.LogDebug("AudioCaptureService: Processing task started");
    }

    /// <summary>
    /// 非同期リサンプリング処理タスクを停止
    /// </summary>
    private void StopProcessingTask()
    {
        if (_processingCancellation != null)
        {
            _processingCancellation.Cancel();
            try
            {
                _processingTask?.Wait(5000);
            }
            catch (OperationCanceledException)
            {
            }
            _processingCancellation.Dispose();
            _processingCancellation = null;
            _processingTask = null;
            LoggerService.LogDebug("AudioCaptureService: Processing task stopped");
        }
    }

    /// <summary>
    /// 音声処理キューを非同期処理
    /// </summary>
    private async Task ProcessAudioQueueAsync(CancellationToken cancellationToken)
    {
        LoggerService.LogDebug("ProcessAudioQueueAsync: Background task started");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_processingQueue.TryDequeue(out var task))
                {
                    try
                    {
                        ProcessAudioData(task);
                    }
                    catch (Exception ex)
                    {
                        LoggerService.LogError($"ProcessAudioQueueAsync: Error processing audio - {ex.Message}");
                    }
                }
                else
                {
                    await Task.Delay(10, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        LoggerService.LogDebug("ProcessAudioQueueAsync: Background task stopped");
    }

    /// <summary>
    /// 音声データを処理（リサンプリング、正規化、ファイル出力）
    /// </summary>
    private void ProcessAudioData(AudioProcessingTask processingTask)
    {
        try
        {
            var samples = processingTask.Samples;
            var sourceSampleRate = processingTask.SourceSampleRate;
            var targetSampleRate = processingTask.TargetSampleRate;

            // リサンプリング（必要に応じて）
            if (sourceSampleRate != targetSampleRate)
            {
                samples = Resample(samples, sourceSampleRate, targetSampleRate);
            }

            // amplitude normalization: 最大値が0.95f を超える場合のみ減衰
            var maxAmplitude = samples.Length > 0 ? samples.Max(s => Math.Abs(s)) : 0f;
            if (maxAmplitude > 0.95f)
            {
                var attenuationFactor = 0.95f / maxAmplitude;
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] *= attenuationFactor;
                }
            }

            // Debug: resampled audio to WAV file
            try
            {
                var debugResampledFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_audio");
                Directory.CreateDirectory(debugResampledFolder);

                if (_debugResampledAudioWriter == null)
                {
                    var debugResampledPath = Path.Combine(debugResampledFolder, "debug_audio.wav");
                    var format = WaveFormat.CreateIeeeFloatWaveFormat(targetSampleRate, MonoChannelCount);
                    _debugResampledAudioWriter = new WaveFileWriter(debugResampledPath, format);
                    LoggerService.LogDebug($"ProcessAudioData: Created debug resampled audio file at {debugResampledPath}");
                }

                var byteArray = new byte[samples.Length * BytesPerFloat];
                Buffer.BlockCopy(samples, 0, byteArray, 0, byteArray.Length);
                _debugResampledAudioWriter.Write(byteArray, 0, byteArray.Length);
            }
            catch (Exception ex)
            {
                LoggerService.LogError($"ProcessAudioData: Failed to write debug audio file - {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"ProcessAudioData: Error during audio processing - {ex.Message}");
        }
    }

    /// <summary>
    /// 音声処理タスク用の内部クラス
    /// </summary>
    private class AudioProcessingTask
    {
        public float[] Samples { get; }
        public int SourceSampleRate { get; }
        public int TargetSampleRate { get; }

        /// <summary>
        /// コンストラクタ
        /// </summary>
        public AudioProcessingTask(float[] samples, int sourceSampleRate, int targetSampleRate)
        {
            Samples = samples;
            SourceSampleRate = sourceSampleRate;
            TargetSampleRate = targetSampleRate;
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
        StopProcessingTask();
        _debugRawAudioWriter?.Dispose();
        _debugResampledAudioWriter?.Dispose();
    }
}
