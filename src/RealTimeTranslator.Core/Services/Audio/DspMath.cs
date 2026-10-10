namespace RealTimeTranslator.Core.Services.Audio;

/// <summary>
/// プレビューと翻訳パイプラインの入力レベル計測で共用する振幅・dB 変換。
/// </summary>
internal static class DspMath
{
    /// <summary>
    /// 振幅 → dB 変換時の床値。 log10(0) を避けるための極小ガード。
    /// 完全無音時の戻り値として共通利用する。
    /// </summary>
    internal const float FloorDb = -120f;

    /// <summary>
    /// 振幅 (絶対値前提、 正の float) を dBFS に変換する。 入力が極小なら <see cref="FloorDb"/> を返して
    /// log10(0) の負無限大を避ける。 1e-6 のしきい値は -120 dBFS 相当 (FloorDb と整合)。
    /// </summary>
    internal static float AmplitudeToDb(float amp)
    {
        if (amp < 1e-6f) return FloorDb;
        return 20f * MathF.Log10(amp);
    }
}
