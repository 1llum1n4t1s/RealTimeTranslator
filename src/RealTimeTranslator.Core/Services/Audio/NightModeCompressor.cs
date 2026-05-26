namespace RealTimeTranslator.Core.Services.Audio;

/// <summary>
/// ナイトモード用 Dynamic Range Compressor。
///
/// peak envelope follower + soft knee コンプレッサーで、 大音 (BGM ピーク / 爆発音) を抑え
/// 小音 (ささやき声 / 遠くの声) を相対的に持ち上げる。
/// VoIP 系で音声明瞭度向上のために定番の DSP 構成 (TV / サウンドバーの「ナイトモード」と同等)。
///
/// 設計の根拠 (WebRestrictionRemoval NIGHT_MODE_PRESET 移植、 動画運用で実証済み):
/// - threshold -30 dBFS: ダイアログ帯 (-22〜-28 dBFS) を確実に圧縮対象に収める
/// - knee 12 dB: threshold 越えの折れ目を滑らかにし「圧縮感」を耳に付きにくくする
/// - ratio 4:1: 放送向け圧縮の標準値 (aggressive すぎず dynamic range を確実に潰す)
/// - attack 20 ms / release 1.0 s: 句末ポンピング抑制を最優先 (release を長くする)
///
/// 信号フロー上は最前段 (<see cref="InputGainStage"/> の前段)。
/// v1.0.32 で LoudnessNormalizer を削除し、 大音抑制 + 小音持ち上げの責務は NightMode 単独に集約。
/// </summary>
public sealed class NightModeCompressor : IAudioPreprocessor
{
    private const float ThresholdDb = -30f;
    private const float KneeDb = 12f;
    private const float Ratio = 4f;
    private const float AttackSec = 0.02f;
    private const float ReleaseSec = 1.0f;
    private const float FloorDb = -120f; // log10(0) を避けるための床値

    private readonly float _attackCoeff;
    private readonly float _releaseCoeff;
    private readonly float _kneeStartDb;
    private readonly float _kneeEndDb;

    private float _envelopeDb = FloorDb;

    public bool IsEnabled { get; set; }

    public NightModeCompressor(int sampleRate, bool enabled = false)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _attackCoeff = 1f - MathF.Exp(-1f / (AttackSec * sampleRate));
        _releaseCoeff = 1f - MathF.Exp(-1f / (ReleaseSec * sampleRate));
        _kneeStartDb = ThresholdDb - KneeDb / 2f;
        _kneeEndDb = ThresholdDb + KneeDb / 2f;
        IsEnabled = enabled;
    }

    public void Process(Span<float> samples)
    {
        if (!IsEnabled) return;
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i];
            // peak envelope follower: 絶対値を attack/release 別係数で追従
            float absDb = AmplitudeToDb(MathF.Abs(s));
            float coeff = absDb > _envelopeDb ? _attackCoeff : _releaseCoeff;
            _envelopeDb += (absDb - _envelopeDb) * coeff;
            // soft knee で gain reduction 計算
            float grDb = ComputeGainReductionDb(_envelopeDb);
            float gain = MathF.Pow(10f, -grDb / 20f);
            samples[i] = s * gain;
        }
    }

    public void Reset() => _envelopeDb = FloorDb;

    /// <summary>
    /// ソフトニーつきコンプレッサーの gain reduction を計算する。
    ///  - knee 範囲未満: 圧縮なし (linear pass-through)
    ///  - knee 範囲内 : 二次曲線で滑らかに圧縮率を上げる (t^2 で出力 GR を補間)
    ///  - knee 範囲超 : 線形圧縮 ((input - threshold) * (1 - 1/ratio))
    /// </summary>
    private float ComputeGainReductionDb(float inputDb)
    {
        if (inputDb <= _kneeStartDb) return 0f;
        float overThreshold = inputDb - ThresholdDb;
        float fullGr = overThreshold - overThreshold / Ratio;
        if (inputDb >= _kneeEndDb) return fullGr;
        // knee 内 (二次曲線で滑らかに)
        float t = (inputDb - _kneeStartDb) / KneeDb; // 0..1
        return fullGr * t * t;
    }

    private static float AmplitudeToDb(float amp)
    {
        if (amp < 1e-6f) return FloorDb;
        return 20f * MathF.Log10(amp);
    }
}
