namespace RealTimeTranslator.Core.Services.Audio;

/// <summary>
/// 最終段クリップ防止用の高速ピークリミッタ (brick-wall 風)。
///
/// 高 ratio (12:1) + 高速 attack (1 ms) で「実質的なハードリミッタ」として動作。
/// 前段の <see cref="NightModeCompressor"/> / <see cref="InputGainStage"/> で
/// ピークが threshold (-3 dBFS) を超えた場合だけ受け止め、
/// PCM16 変換時のクリップ歪みを防ぐ。
///
/// 設計の根拠 (WebRestrictionRemoval ANTI_CLIP_PRESET 移植):
/// - threshold -3 dBFS: 0 dBFS の手前で確実に止める安全マージン
/// - ratio 12:1: 実質ハードリミット (threshold + 12dB の入力でも +1dB しか出ない)
/// - attack 1 ms: 瞬間ピークを確実に抑える最優先設定
/// - release 50 ms: 過渡応答後すぐリリースして連続圧縮を避ける
///
/// 最終的に <c>|sample| &lt; HardClipMax</c> でクランプ (envelope follower の遅延でリーク
/// した極小ピークを物理的に止めるための保険)。
/// </summary>
public sealed class AntiClipLimiter : IAudioPreprocessor
{
    private const float ThresholdDb = -3f;
    private const float Ratio = 12f;
    private const float AttackSec = 0.001f;
    private const float ReleaseSec = 0.05f;

    /// <summary>最終ハードクランプの上限値 (0.999 ≈ -0.01 dBFS、 PCM16 変換時の正側オーバーフロー防止)。</summary>
    private const float HardClipMax = 0.999f;

    private readonly float _attackCoeff;
    private readonly float _releaseCoeff;
    private float _envelopeDb = DspMath.FloorDb;

    public bool IsEnabled { get; set; }

    public AntiClipLimiter(int sampleRate, bool enabled = false)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _attackCoeff = DspMath.ExpEnvCoeff(AttackSec, sampleRate);
        _releaseCoeff = DspMath.ExpEnvCoeff(ReleaseSec, sampleRate);
        IsEnabled = enabled;
    }

    public void Process(Span<float> samples)
    {
        if (!IsEnabled) return;
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i];
            float absDb = DspMath.AmplitudeToDb(MathF.Abs(s));
            float coeff = absDb > _envelopeDb ? _attackCoeff : _releaseCoeff;
            _envelopeDb += (absDb - _envelopeDb) * coeff;
            float grDb = _envelopeDb > ThresholdDb
                ? (_envelopeDb - ThresholdDb) * (1f - 1f / Ratio)
                : 0f;
            float gain = MathF.Pow(10f, -grDb / 20f);
            float limited = s * gain;
            // hard clamp 保険 (envelope 遅延でリークした極小ピークを物理的に止める)
            if (limited > HardClipMax) limited = HardClipMax;
            else if (limited < -HardClipMax) limited = -HardClipMax;
            samples[i] = limited;
        }
    }

    public void Reset() => _envelopeDb = DspMath.FloorDb;
}
