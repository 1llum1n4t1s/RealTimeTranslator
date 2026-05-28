namespace RealTimeTranslator.Core.Services.Audio;

/// <summary>
/// ユーザー手動の入力ゲインステージ (デシベル指定の単純乗算)。
///
/// ゲーム音量設定 / Windows アプリ別音量ミキサーで下げてる人のために、
/// パイプライン入り口で底上げできるユーザー操作可能なゲイン。 デフォルト 0 dB (= no-op)。
///
/// 信号フロー上は WASAPI capture の直後、 <see cref="AntiClipLimiter"/> の前段。
/// ゲイン適用後にピークが 0 dBFS を超えても、 後段のリミッタが受け止めるため安全。
///
/// 範囲: -24 dB 〜 +24 dB (SettingsViewModel で制約)。
/// </summary>
public sealed class InputGainStage : IAudioPreprocessor
{
    private float _gain = 1f;
    private float _gainDb;

    /// <summary>0 dB ピッタリ (= 倍率 1.0) のとき no-op で完全 bypass。</summary>
    public bool IsEnabled => MathF.Abs(_gainDb) > 0.01f;

    /// <summary>ゲイン (dB)。 設定経由で hot-reload される。</summary>
    public float GainDb
    {
        get => _gainDb;
        set
        {
            _gainDb = value;
            _gain = MathF.Pow(10f, value / 20f);
        }
    }

    public InputGainStage(float gainDb = 0f)
    {
        GainDb = gainDb;
    }

    public void Process(Span<float> samples)
    {
        if (!IsEnabled) return;
        float gain = _gain;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }
    }

    public void Reset() { /* ステートレス: gain 値は設定経由で更新されるためここでは何もしない */ }
}
