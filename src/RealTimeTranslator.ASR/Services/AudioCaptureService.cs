using NAudio.Wave;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.ASR.Services;

/// <summary>
/// 音声キャプチャサービス
/// プロセス単位のループバックキャプチャを実装
/// </summary>
public class AudioCaptureService : IAudioCaptureService
{
    private IWaveIn? _capture;
    private WaveFormat? _targetFormat;
    private readonly AudioCaptureSettings _settings;
    private readonly List<float> _audioBuffer = new();
    private readonly object _bufferLock = new();
    private bool _isCapturing;
    private int _targetProcessId;

    public bool IsCapturing => _isCapturing;
    public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;

    public AudioCaptureService(AudioCaptureSettings? settings = null)
    {
        _settings = settings ?? new AudioCaptureSettings();
        _targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(_settings.SampleRate, 1);
    }

    /// <summary>
    /// 指定したプロセスIDの音声キャプチャを開始
    /// </summary>
    public void StartCapture(int processId)
    {
        if (_isCapturing)
            StopCapture();

        _targetProcessId = processId;
        _audioBuffer.Clear();

        // プロセス単位キャプチャはAudioClientActivationParams経由で初期化する必要がある
        _capture = new ProcessLoopbackCapture(_targetProcessId);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        _capture.StartRecording();
        _isCapturing = true;
    }

    /// <summary>
    /// 音声キャプチャを停止
    /// </summary>
    public void StopCapture()
    {
        if (!_isCapturing)
            return;

        _capture?.StopRecording();
        _isCapturing = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
            return;

        // バイトデータをfloatに変換
        var sourceFormat = _capture!.WaveFormat;
        var samples = ConvertToFloat(e.Buffer, e.BytesRecorded, sourceFormat);

        // リサンプリング（必要に応じて）
        if (sourceFormat.SampleRate != _settings.SampleRate)
        {
            samples = Resample(samples, sourceFormat.SampleRate, _settings.SampleRate);
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
            var samplesPerChunk = _settings.SampleRate / 10; // 100ms分
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
            // エラーログ
            Console.WriteLine($"Recording stopped with error: {e.Exception.Message}");
        }
    }

    private float[] ConvertToFloat(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        int sampleCount;
        float[] samples;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            sampleCount = bytesRecorded / 4;
            samples = new float[sampleCount];
            Buffer.BlockCopy(buffer, 0, samples, 0, bytesRecorded);
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm)
        {
            if (format.BitsPerSample == 16)
            {
                sampleCount = bytesRecorded / 2;
                samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    short sample = BitConverter.ToInt16(buffer, i * 2);
                    samples[i] = sample / 32768f;
                }
            }
            else if (format.BitsPerSample == 32)
            {
                sampleCount = bytesRecorded / 4;
                samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    int sample = BitConverter.ToInt32(buffer, i * 4);
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
        StopCapture();
        if (_capture is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _capture = null;
    }
}
