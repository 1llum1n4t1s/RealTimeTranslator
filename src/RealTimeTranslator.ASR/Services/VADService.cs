using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.ASR.Services;

/// <summary>
/// VAD（Voice Activity Detection）サービス
/// エネルギーベースの簡易VAD実装
/// </summary>
public class VADService : IVADService
{
    private int _sampleRate;
    private readonly List<float> _audioBuffer = new();
    private readonly object _bufferLock = new();
    private float _currentTime = 0;
    private bool _isSpeaking = false;
    private float _speechStartTime = 0;
    private readonly List<float> _currentSpeechBuffer = new();

    public float Sensitivity { get; set; } = 0.5f;
    public float MinSpeechDuration { get; set; } = 0.5f;
    public float MaxSpeechDuration { get; set; } = 6.0f;

    private float _silenceThreshold = 0.3f;
    private float _silenceDuration = 0;

    public VADService(AudioCaptureSettings? settings = null)
    {
        var s = settings ?? new AudioCaptureSettings();
        _sampleRate = s.SampleRate;
        Sensitivity = s.VADSensitivity;
        MinSpeechDuration = s.MinSpeechDuration;
        MaxSpeechDuration = s.MaxSpeechDuration;
        _silenceThreshold = s.SilenceThreshold;
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

        _sampleRate = settings.SampleRate;
        Sensitivity = settings.VADSensitivity;
        MinSpeechDuration = settings.MinSpeechDuration;
        MaxSpeechDuration = settings.MaxSpeechDuration;
        _silenceThreshold = settings.SilenceThreshold;

        Reset();
    }

    /// <summary>
    /// 音声データを処理し、発話区間を検出
    /// </summary>
    public IEnumerable<SpeechSegment> DetectSpeech(float[] audioData)
    {
        var segments = new List<SpeechSegment>();

        // フレーム単位で処理（10ms = 160サンプル @ 16kHz）
        int frameSize = _sampleRate / 100;
        float frameDuration = 1.0f / 100;

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
                if (_silenceDuration >= _silenceThreshold)
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
            if (_isSpeaking && currentSpeechDuration >= MaxSpeechDuration)
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

    private SpeechSegment? CreateSegment()
    {
        float duration = _currentTime - _speechStartTime;

        // 最小発話長未満は無視
        if (duration < MinSpeechDuration)
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
        float baseThreshold = 0.01f;
        float maxThreshold = 0.1f;
        return baseThreshold + (maxThreshold - baseThreshold) * (1 - Sensitivity);
    }

    /// <summary>
    /// バッファをリセット
    /// </summary>
    public void Reset()
    {
        _currentTime = 0;
        _isSpeaking = false;
        _speechStartTime = 0;
        _silenceDuration = 0;
        _currentSpeechBuffer.Clear();
    }
}
