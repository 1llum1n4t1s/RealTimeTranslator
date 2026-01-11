using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.ASR.Services;

/// <summary>
/// VAD（Voice Activity Detection）サービス
/// エネルギーベースの簡易VAD実装
/// </summary>
public class VADService : IVADService
{
    private const int FramesPerSecond = 100; // 1秒あたりのフレーム数 (10ms/フレーム)
    private const float BaseEnergyThreshold = 0.01f; // 基本エネルギー閾値
    private const float MaxEnergyThreshold = 0.1f; // 最大エネルギー閾値

    private int _sampleRate;
    private float _sensitivity;
    private float _minSpeechDuration;
    private float _maxSpeechDuration;
    private float _silenceThreshold;

    private readonly List<float> _audioBuffer = new();
    private readonly object _settingsLock = new();
    private readonly object _stateLock = new(); // スレッドセーフティのための状態ロック
    private float _currentTime = 0;
    private bool _isSpeaking = false;
    private float _speechStartTime = 0;
    private readonly List<float> _currentSpeechBuffer = new();
    private float _silenceDuration = 0;

    public VADService(AudioCaptureSettings? settings = null)
    {
        var s = settings ?? new AudioCaptureSettings();
        lock (_settingsLock)
        {
            _sampleRate = s.SampleRate;
            _sensitivity = s.VADSensitivity;
            _minSpeechDuration = s.MinSpeechDuration;
            _maxSpeechDuration = s.MaxSpeechDuration;
            _silenceThreshold = s.SilenceThreshold;
        }
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

        lock (_settingsLock)
        {
            _sampleRate = settings.SampleRate;
            _sensitivity = settings.VADSensitivity;
            _minSpeechDuration = settings.MinSpeechDuration;
            _maxSpeechDuration = settings.MaxSpeechDuration;
            _silenceThreshold = settings.SilenceThreshold;
        }

        Reset();
    }

    /// <summary>
    /// VADの感度設定（0.0～1.0）
    /// </summary>
    public float Sensitivity
    {
        get
        {
            lock (_settingsLock)
            {
                return _sensitivity;
            }
        }
        set
        {
            lock (_settingsLock)
            {
                _sensitivity = Math.Clamp(value, 0.0f, 1.0f);
            }
        }
    }

    /// <summary>
    /// 最小発話長（秒）
    /// </summary>
    public float MinSpeechDuration
    {
        get
        {
            lock (_settingsLock)
            {
                return _minSpeechDuration;
            }
        }
        set
        {
            lock (_settingsLock)
            {
                _minSpeechDuration = Math.Max(value, 0.0f);
            }
        }
    }

    /// <summary>
    /// 最大発話長（秒）
    /// </summary>
    public float MaxSpeechDuration
    {
        get
        {
            lock (_settingsLock)
            {
                return _maxSpeechDuration;
            }
        }
        set
        {
            lock (_settingsLock)
            {
                _maxSpeechDuration = Math.Max(value, 0.0f);
            }
        }
    }

    /// <summary>
    /// 音声データを処理し、発話区間を検出
    /// </summary>
    public IEnumerable<SpeechSegment> DetectSpeech(float[] audioData)
    {
        if (audioData == null || audioData.Length == 0)
        {
            return Enumerable.Empty<SpeechSegment>();
        }

        lock (_stateLock)
        {
            var segments = new List<SpeechSegment>();

            // 設定値をスレッドセーフに読み取り
            int frameSize;
            float maxSpeechDuration;
            float silenceThreshold;
            lock (_settingsLock)
            {
                frameSize = _sampleRate / FramesPerSecond;
                maxSpeechDuration = _maxSpeechDuration;
                silenceThreshold = _silenceThreshold;
            }

            float frameDuration = 1.0f / FramesPerSecond;

            for (int i = 0; i < audioData.Length; i += frameSize)
            {
                int frameEnd = Math.Min(i + frameSize, audioData.Length);
                var frame = audioData.AsSpan(i, frameEnd - i);

                // フレームのエネルギー（RMS）を計算
                float energy = CalculateRMS(frame);
                bool isSpeech = energy > GetEnergyThreshold();

                if (isSpeech)
                {
                    if (!_isSpeaking)
                    {
                        // 発話開始
                        _isSpeaking = true;
                        _speechStartTime = _currentTime;
                        _currentSpeechBuffer.Clear();
                        _silenceDuration = 0;
                    }

                    _currentSpeechBuffer.AddRange(frame.ToArray());
                    _silenceDuration = 0;
                }
                else if (_isSpeaking)
                {
                    // 発話中の無音
                    _silenceDuration += frameDuration;
                    _currentSpeechBuffer.AddRange(frame.ToArray());

                    // 無音が閾値を超えたら発話終了
                    if (_silenceDuration >= silenceThreshold)
                    {
                        var segment = CreateSegment();
                        if (segment != null)
                        {
                            segments.Add(segment);
                        }
                        _isSpeaking = false;
                        _currentSpeechBuffer.Clear();
                    }
                }

                // 最大発話長を超えた場合は強制的に分割
                float currentSpeechDuration = _currentTime - _speechStartTime;
                if (_isSpeaking && currentSpeechDuration >= maxSpeechDuration)
                {
                    var segment = CreateSegment();
                    if (segment != null)
                    {
                        segments.Add(segment);
                    }
                    _isSpeaking = false;
                    _currentSpeechBuffer.Clear();
                }

                _currentTime += frameDuration;
            }

            return segments;
        }
    }

    /// <summary>
    /// 残留バッファを確定して返す
    /// </summary>
    public SpeechSegment? FlushPendingSegment()
    {
        lock (_stateLock)
        {
            if (!_isSpeaking || _currentSpeechBuffer.Count == 0)
            {
                ResetInternal();
                return null;
            }

            var segment = CreateSegment();
            ResetInternal();
            return segment;
        }
    }

    private SpeechSegment? CreateSegment()
    {
        float duration = _currentTime - _speechStartTime;

        // 最小発話長未満は無視
        float minSpeechDuration;
        lock (_settingsLock)
        {
            minSpeechDuration = _minSpeechDuration;
        }

        if (duration < minSpeechDuration)
            return null;

        return new SpeechSegment
        {
            StartTime = _speechStartTime,
            EndTime = _currentTime,
            AudioData = _currentSpeechBuffer.ToArray()
        };
    }

    private float CalculateRMS(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
            return 0;

        float sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }
        return MathF.Sqrt(sum / samples.Length);
    }

    private float GetEnergyThreshold()
    {
        // 感度に基づいて閾値を調整
        // 感度が高い（1.0に近い）ほど、閾値は低くなる
        float sensitivity;
        lock (_settingsLock)
        {
            sensitivity = _sensitivity;
        }
        return BaseEnergyThreshold + (MaxEnergyThreshold - BaseEnergyThreshold) * (1 - sensitivity);
    }

    /// <summary>
    /// バッファをリセット
    /// </summary>
    public void Reset()
    {
        lock (_stateLock)
        {
            ResetInternal();
        }
    }

    /// <summary>
    /// バッファをリセット（内部メソッド、ロック不要）
    /// </summary>
    private void ResetInternal()
    {
        _currentTime = 0;
        _isSpeaking = false;
        _speechStartTime = 0;
        _silenceDuration = 0;
        _currentSpeechBuffer.Clear();
    }
}
