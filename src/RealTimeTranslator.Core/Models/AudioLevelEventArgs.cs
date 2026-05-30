namespace RealTimeTranslator.Core.Models;

/// <summary>
/// 入力音声のレベルメーター更新イベント引数。
/// <see cref="PeakDb"/> は「入力ゲイン適用後」のピーク振幅を dBFS で表す
/// (0 dBFS = フルスケール / クリップ、 無音は <see cref="Services.Audio.DspMath.FloorDb"/>)。
/// UI 側 (音声処理タブのレベルメーター) が購読して表示する。 約 50ms 間隔で発火する。
/// </summary>
public class AudioLevelEventArgs : EventArgs
{
    /// <summary>ゲイン適用後のピークレベル (dBFS、 おおむね -120〜0)。</summary>
    public float PeakDb { get; init; }
}
