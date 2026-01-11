using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.ASR.Services;

/// <summary>
/// 音声キャプチャサービス
/// プロセス単位のループバックキャプチャを実装
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
    private const int FileNotFoundHResult = unchecked((int)0x80070002);

    private IWaveIn? _capture;
    private WaveFormat? _targetFormat;
    private readonly AudioCaptureSettings _settings;
    private readonly List<float> _audioBuffer = [];
    private readonly object _bufferLock = new();
    private bool _isCapturing;
    private bool _isDisposed;
    private int _targetProcessId;

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
    /// 指定したプロセスIDの音声キャプチャを開始
    /// </summary>
    public void StartCapture(int processId)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId), "プロセスIDは正の値で指定してください。");

        if (_capture != null)
            StopCapture();

        _targetProcessId = processId;
        _audioBuffer.Clear();

        // Windows Core Audio API(AudioClientActivationParams/IAudioClient3)で対象プロセスのみを初期化する
        _capture = new ProcessLoopbackCapture(_targetProcessId);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        _capture.StartRecording();
        _isCapturing = true;
    }

    /// <summary>
    /// 指定したプロセスIDの音声キャプチャを開始（オーディオセッションが見つかるまで待機）
    /// Chromeなどのマルチプロセスアプリの場合は、関連プロセスも試行
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
        var processesToTry = GetProcessesToTry(processId);

        LoggerService.LogDebug($"StartCaptureWithRetryAsync: Will try {processesToTry.Count} process(es): {string.Join(", ", processesToTry)}");

        while (!cancellationToken.IsCancellationRequested)
        {
            // 複数プロセスを順番に試す
            foreach (var currentProcessId in processesToTry)
            {
                try
                {
                    // Windows Core Audio API(AudioClientActivationParams/IAudioClient3)で対象プロセスのみを初期化する
                    _capture = new ProcessLoopbackCapture(currentProcessId);
                    _capture.DataAvailable += OnDataAvailable;
                    _capture.RecordingStopped += OnRecordingStopped;

                    _capture.StartRecording();
                    _isCapturing = true;
                    _targetProcessId = currentProcessId; // 成功したプロセスIDを記録

                    var message = currentProcessId == processId
                        ? "音声キャプチャを開始しました。"
                        : $"音声キャプチャを開始しました。(PID: {currentProcessId})";
                    OnCaptureStatusChanged(message, false);
                    LoggerService.LogInfo($"StartCaptureWithRetryAsync: Successfully started capture for process {currentProcessId}");
                    return true;
                }
                catch (COMException ex) when (ex.HResult == FileNotFoundHResult)
                {
                    // このプロセスではオーディオセッションが見つからない
                    LoggerService.LogWarning($"StartCaptureWithRetryAsync: Audio session not found (HRESULT 0x80070002) for process {currentProcessId}");
                    CleanupCapture();
                    // 次のプロセスを試す
                    continue;
                }
                catch (FileNotFoundException fex)
                {
                    // このプロセスではオーディオセッションが見つからない
                    LoggerService.LogError($"StartCaptureWithRetryAsync: FileNotFoundException for process {currentProcessId}: {fex.Message}");
                    CleanupCapture();
                    // 次のプロセスを試す
                    continue;
                }
                catch (TimeoutException tex)
                {
                    // このプロセスではタイムアウト
                    Debug.WriteLine($"StartCaptureWithRetryAsync: Activation timeout for process {currentProcessId}: {tex.Message}");
                    CleanupCapture();
                    // 次のプロセスを試す
                    continue;
                }
                catch (Exception ex)
                {
                    LoggerService.LogError($"StartCaptureWithRetryAsync: Error for process {currentProcessId} - {ex.GetType().Name}: {ex.Message}");
                    Debug.WriteLine($"StartCaptureWithRetryAsync: Error for process {currentProcessId} - {ex.GetType().Name}: {ex.Message}");
                    CleanupCapture();
                    // 次のプロセスを試す
                    continue;
                }
            }

            // 全プロセスを試したが失敗
            retryCount++;
            var elapsedSeconds = Math.Round(retryStopwatch.Elapsed.TotalSeconds, 1);
            var statusMessage = $"音声の再生を待機中... ({elapsedSeconds}秒, 試行: {retryCount})";
            OnCaptureStatusChanged(statusMessage, true);
            Debug.WriteLine($"StartCaptureWithRetryAsync: No audio session found in any process, waiting... (attempt {retryCount}, elapsed {elapsedSeconds}s)");

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
    /// 試行対象のプロセスIDリストを取得
    /// メインプロセスに加え、子プロセス（Chromeのレンダラープロセスなど）も含める
    /// </summary>
    private static List<int> GetProcessesToTry(int mainProcessId)
    {
        var processesToTry = new List<int> { mainProcessId };

        try
        {
            var mainProcess = Process.GetProcessById(mainProcessId);
            var parentPid = GetParentProcessId(mainProcessId);

            // 親プロセスが存在する場合、親プロセスのIDも追加（マルチプロセスアプリの場合）
            if (parentPid > 0 && parentPid != mainProcessId)
            {
                processesToTry.Insert(0, parentPid);
                Debug.WriteLine($"GetProcessesToTry: Found parent process {parentPid}");
            }

            // 同じプロセス名の他のプロセスも追加（Chromeの複数インスタンスなど）
            try
            {
                var relatedProcesses = Process.GetProcessesByName(mainProcess.ProcessName)
                    .Where(p => p.Id != mainProcessId && !processesToTry.Contains(p.Id))
                    .OrderBy(p => p.Id)
                    .Select(p => p.Id)
                    .ToList();

                if (relatedProcesses.Count > 0)
                {
                    processesToTry.AddRange(relatedProcesses);
                    Debug.WriteLine($"GetProcessesToTry: Found {relatedProcesses.Count} related processes: {string.Join(", ", relatedProcesses)}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetProcessesToTry: Error getting related processes: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetProcessesToTry: Error - {ex.Message}");
        }

        return processesToTry;
    }

    /// <summary>
    /// 親プロセスIDを取得（現在は未実装、常に0を返す）
    /// </summary>
    private static int GetParentProcessId(int processId)
    {
        // 親プロセス取得はWMI等で複雑なため、現在は未実装
        // 今後、必要に応じて実装可能
        return 0;
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

        // リサンプリング（必要に応じて）
        if (sourceFormat.SampleRate != targetSampleRate)
        {
            samples = Resample(samples, sourceFormat.SampleRate, targetSampleRate);
        }

        // モノラルに変換（必要に応じて）
        if (sourceFormat.Channels > 1)
        {
            samples = ConvertToMono(samples, sourceFormat.Channels);
        }

        // バッファに追加
        lock (_bufferLock)
        {
            _audioBuffer.AddRange(samples);

            // 一定量のデータが溜まったらイベントを発火
            var samplesPerChunk = targetSampleRate * AudioChunkDurationMs / 1000;
            while (_audioBuffer.Count >= samplesPerChunk)
            {
                var chunk = _audioBuffer.Take(samplesPerChunk).ToArray();
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
            // エラーログ（WPFアプリではDebug出力を使用）
            System.Diagnostics.Debug.WriteLine($"Recording stopped with error: {e.Exception.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack trace: {e.Exception.StackTrace}");
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
        var resampled = new float[newLength];

        for (int i = 0; i < newLength; i++)
        {
            double sourceIndex = i / ratio;
            int index = (int)sourceIndex;
            double fraction = sourceIndex - index;

            if (index + 1 < samples.Length)
            {
                resampled[i] = (float)(samples[index] * (1 - fraction) + samples[index + 1] * fraction);
            }
            else if (index < samples.Length)
            {
                resampled[i] = samples[index];
            }
        }

        return resampled;
    }

    private float[] ConvertToMono(float[] samples, int channels)
    {
        if (channels == 1)
            return samples;

        int monoLength = samples.Length / channels;
        var mono = new float[monoLength];

        for (int i = 0; i < monoLength; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                sum += samples[i * channels + ch];
            }
            mono[i] = sum / channels;
        }

        return mono;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        StopCapture();
    }
}
