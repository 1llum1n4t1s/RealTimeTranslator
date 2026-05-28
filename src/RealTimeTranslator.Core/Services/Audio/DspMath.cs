namespace RealTimeTranslator.Core.Services.Audio;

/// <summary>
/// 入力プリプロセス DSP (AntiClipLimiter / 将来追加) で共通利用する
/// 純粋関数ユーティリティ。 各 DSP に同じ定数・関数を重複定義していた状態を一箇所に集約する。
///
/// 履歴: /opop バッチ Q (A-1) で v1.0.33 候補に追加。 当初は NightModeCompressor / AntiClipLimiter の
/// 2 つの envelope follower 型 DSP の共通 boilerplate を抽出した。 v1.0.36 で NightModeCompressor を
/// 削除したため、 現状の利用者は AntiClipLimiter のみ。
/// </summary>
internal static class DspMath
{
    /// <summary>
    /// 振幅 → dB 変換時の床値。 log10(0) を避けるための極小ガード。
    /// envelope follower の初期値 / 完全無音時の戻り値として共通利用する。
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

    /// <summary>
    /// envelope follower の指数追従係数を求める。 <c>y[n] = y[n-1] + (target - y[n-1]) * coeff</c>
    /// に使う標準形 <c>coeff = 1 - exp(-1 / (τ * sampleRate))</c>。
    ///
    /// attack / release の時定数 τ (秒) とサンプリングレートを渡すと、 1 サンプルあたりの
    /// 追従係数を返す。 DSP コンストラクタで 1 度計算してフィールドに保持する想定。
    /// </summary>
    internal static float ExpEnvCoeff(float timeConstantSec, int sampleRate)
    {
        return 1f - MathF.Exp(-1f / (timeConstantSec * sampleRate));
    }
}
