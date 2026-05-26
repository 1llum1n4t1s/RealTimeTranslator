namespace RealTimeTranslator.Core.Services.Audio;

/// <summary>
/// 短時間 RMS ベースの自動ラウドネス正規化器。
///
/// 一定 chunk (default 400ms) ごとに RMS を測定し、 目標 RMS (-24 dBFS) に揃える gain を算出。
/// sample-by-sample で <c>setTargetAtTime</c> 風の指数追従式 <c>y[n] = y[n-1] + (target - y[n-1]) * coeff</c>
/// で滑らかに gain を変動させ、 境界クリックやポンピング感を抑制する。
///
/// 設計の根拠 (WebRestrictionRemoval v1.0.33 実証パラメータを移植):
/// - <see cref="TargetRmsDb"/> = -24: ダイアログ帯の中央値。 厳密な LUFS ではなくリアルタイム用途の近似
/// - <see cref="SilenceGateDb"/> = -38: BGM の言葉と言葉の隙間 (微小残響) を「有効音」と誤判定すると
///   発話中はゲイン下げ → 隙間は上げ、 の周期的ポンピングが発生する。 -38 にすることで隙間を無音扱い
/// - clamp ±12 dB: 過剰持ち上げによる環境ノイズ増幅を抑制
/// - dead zone 3 dB: ±3dB 以内のターゲット変動はスキップ (RMS 揺れによる微小ポンピングを抑制)
/// - DOWN τ=2s / UP τ=4s: 動画再生想定の "サスペンションダンパー" 設計。 ゲーム音声字幕用途では
///   応答性を上げたい場合に τ を 1〜2 倍速める余地あり (P2 検討)
/// </summary>
public sealed class LoudnessNormalizer : IAudioPreprocessor
{
    // ────────── 実証済みパラメータ (WebRestrictionRemoval NORMALIZE_* 定数を移植) ──────────
    private const float TargetRmsDb = -24f;
    private const float SilenceGateDb = -38f;
    private const float MinGainDb = -12f;
    private const float MaxGainDb = 12f;
    private const float DeadZoneDb = 3f;
    private const float UpdateIntervalMs = 400f;
    private const float DownTimeConstantSec = 2.0f;
    private const float UpTimeConstantSec = 4.0f;

    private readonly int _measurementBufferSize;
    private readonly float _silenceGate;
    private readonly float _minGain;
    private readonly float _maxGain;
    private readonly float _targetRmsLinear;

    // sample-by-sample で _currentGain を _targetGain へ指数追従させる係数。
    // y[n] = y[n-1] + (target - y[n-1]) * coeff、 coeff = 1 - exp(-1 / (τ * sampleRate))
    private readonly float _upCoeff;
    private readonly float _downCoeff;

    // RMS 測定用の累積二乗和 (double で overflow / 精度落ちを回避)
    private double _sumOfSquares;
    private int _samplesSinceLastUpdate;
    private float _currentGain = 1f;
    private float _targetGain = 1f;

    public bool IsEnabled { get; set; }

    public LoudnessNormalizer(int sampleRate, bool enabled = false)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _measurementBufferSize = Math.Max(1, (int)(sampleRate * UpdateIntervalMs / 1000f));
        _silenceGate = DbToGain(SilenceGateDb);
        _minGain = DbToGain(MinGainDb);
        _maxGain = DbToGain(MaxGainDb);
        _targetRmsLinear = DbToGain(TargetRmsDb);
        _upCoeff = 1f - MathF.Exp(-1f / (UpTimeConstantSec * sampleRate));
        _downCoeff = 1f - MathF.Exp(-1f / (DownTimeConstantSec * sampleRate));
        IsEnabled = enabled;
    }

    public void Process(Span<float> samples)
    {
        if (!IsEnabled) return;
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i];
            _sumOfSquares += s * s;
            _samplesSinceLastUpdate++;
            if (_samplesSinceLastUpdate >= _measurementBufferSize)
            {
                UpdateTargetGain();
                _sumOfSquares = 0;
                _samplesSinceLastUpdate = 0;
            }
            // UP/DOWN で異なる時定数で _targetGain に追従 (大音源 fast down / 小音源 slow up)
            float coeff = _currentGain < _targetGain ? _upCoeff : _downCoeff;
            _currentGain += (_targetGain - _currentGain) * coeff;
            samples[i] = s * _currentGain;
        }
    }

    public void Reset()
    {
        _sumOfSquares = 0;
        _samplesSinceLastUpdate = 0;
        _currentGain = 1f;
        _targetGain = 1f;
    }

    private void UpdateTargetGain()
    {
        float rms = MathF.Sqrt((float)(_sumOfSquares / _measurementBufferSize));
        if (!float.IsFinite(rms)) return;
        if (rms < _silenceGate)
        {
            // silence ゲート: BGM 隙間を有効音扱いしてポンピングしないよう、 gain=1 に強制
            _targetGain = 1f;
            return;
        }
        float candidate = _targetRmsLinear / rms;
        if (candidate < _minGain) candidate = _minGain;
        else if (candidate > _maxGain) candidate = _maxGain;
        // dead zone ヒステリシス (3 dB 以内の変動はスキップ)
        if (_targetGain > 0f && candidate > 0f)
        {
            float deltaDb = MathF.Abs(20f * MathF.Log10(candidate / _targetGain));
            if (deltaDb < DeadZoneDb) return;
        }
        _targetGain = candidate;
    }

    private static float DbToGain(float db) => MathF.Pow(10f, db / 20f);
}
