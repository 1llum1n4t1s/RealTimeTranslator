using Microsoft.VisualStudio.TestTools.UnitTesting;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// StreamingResampler の回帰テスト。
///
/// 背景 (2026-05-23): VADゲートパスが 512 サンプル/32ms フレームごとに
/// AudioFormatConverter.ResampleTo24kHz を呼んでいた (＝呼び出しごとに WdlResamplingSampleProvider
/// を新規生成)。 WDL は FIR sinc 補間でステートフルなため、 フレーム境界でフィルタヒストリが
/// ゼロリセットされ、 振幅最大 90% のクリックノイズが 32ms ごとに挿入されていた。
/// これが OpenAI Realtime Translation の文境界検出を破壊し「区切りがおかしい / 句点が来ない」を
/// 引き起こしていた (VAD 無効時は大 chunk を 1 回でリサンプルするので発生しなかった)。
///
/// StreamingResampler は単一の WDL インスタンスにフレームを連続供給し、 フィルタ状態を
/// 引き継ぐことでこのアーティファクトを構造的に排除する。 本テストはその不変条件を固定する。
/// </summary>
[TestClass]
public sealed class StreamingResamplerTests
{
    private const int SourceRate = 16000;
    private const int TargetRate = 24000;
    private const int FrameSize = 512; // VAD フレームサイズ (32ms @ 16kHz)

    private static float[] MakeSine(int samples, double freq = 440.0)
    {
        var s = new float[samples];
        for (int i = 0; i < samples; i++)
            s[i] = (float)Math.Sin(2 * Math.PI * freq * i / SourceRate);
        return s;
    }

    /// <summary>
    /// 512 サンプルずつ StreamingResampler に流した結果が、 全体を 1 回でリサンプルした結果と
    /// (重なる範囲で) 完全一致すること。 ＝ フレーム境界アーティファクトが無いことの保証。
    /// 旧フレーム単位リサンプルではここで最大誤差 0.90 が出ていた。
    /// </summary>
    [TestMethod]
    public void Resample_FrameByFrame_MatchesContinuousResample()
    {
        const int frames = 8;
        const int n = FrameSize * frames;
        var signal = MakeSine(n);

        var continuous = AudioFormatConverter.ResampleTo24kHz(signal);

        var streamer = new StreamingResampler();
        var streamed = new List<float>();
        for (int off = 0; off < n; off += FrameSize)
        {
            var frame = new float[FrameSize];
            Array.Copy(signal, off, frame, 0, FrameSize);
            streamed.AddRange(streamer.Resample(frame));
        }

        int compareLen = Math.Min(continuous.Length, streamed.Count);
        double maxDiff = 0;
        for (int i = 0; i < compareLen; i++)
            maxDiff = Math.Max(maxDiff, Math.Abs(continuous[i] - streamed[i]));

        // フレーム境界アーティファクトが無ければ連続リサンプルとほぼ完全一致する。
        Assert.IsTrue(maxDiff < 1e-4,
            $"ストリーミングリサンプルが連続リサンプルと乖離 (最大誤差={maxDiff:F6})。フレーム境界アーティファクトの疑い。");
    }

    /// <summary>
    /// フレーム境界 (出力の 768 の倍数付近) に誤差スパイクが局在しないこと。
    /// 旧実装では境界に振幅最大 90% のクリックが規則的に出ていた。
    /// </summary>
    [TestMethod]
    public void Resample_NoBoundaryArtifactSpikes()
    {
        const int frames = 6;
        const int n = FrameSize * frames;
        var signal = MakeSine(n);

        var continuous = AudioFormatConverter.ResampleTo24kHz(signal);

        var streamer = new StreamingResampler();
        var streamed = new List<float>();
        for (int off = 0; off < n; off += FrameSize)
        {
            var frame = new float[FrameSize];
            Array.Copy(signal, off, frame, 0, FrameSize);
            streamed.AddRange(streamer.Resample(frame));
        }

        int compareLen = Math.Min(continuous.Length, streamed.Count);
        // 出力フレーム境界 (768, 1536, 2304, ...) ±2 サンプルでの誤差が小さいこと。
        for (int boundary = 768; boundary < compareLen; boundary += 768)
        {
            for (int j = -2; j <= 2; j++)
            {
                int idx = boundary + j;
                if (idx < 0 || idx >= compareLen) continue;
                double diff = Math.Abs(continuous[idx] - streamed[idx]);
                Assert.IsTrue(diff < 1e-4,
                    $"フレーム境界 {boundary} 付近 (位置={idx}) に誤差スパイク {diff:F6} を検出。");
            }
        }
    }

    /// <summary>
    /// 連続して流したときの累積出力サンプル数が入力 × (24000/16000) にほぼ一致すること
    /// (末尾の先読みマージンぶんだけ少ない)。 サンプルの系統的な欠落/重複が無いことの保証。
    /// </summary>
    [TestMethod]
    public void Resample_CumulativeSampleCount_MatchesRatio()
    {
        const int frames = 30; // 約 1 秒
        const int n = FrameSize * frames;
        var signal = MakeSine(n);

        var streamer = new StreamingResampler();
        int total = 0;
        for (int off = 0; off < n; off += FrameSize)
        {
            var frame = new float[FrameSize];
            Array.Copy(signal, off, frame, 0, FrameSize);
            total += streamer.Resample(frame).Length;
        }

        int expected = n * TargetRate / SourceRate; // 23040
        // 末尾の先読みマージン (64 サンプル) ぶんは保留されるので、 expected より少し少ない範囲。
        Assert.IsTrue(total <= expected && total >= expected - 128,
            $"累積出力 {total} が期待値 {expected} から乖離。");
    }

    /// <summary>
    /// Reset 後はフィルタ状態と累積カウンタがクリアされ、 新規インスタンスと同じ出力になること。
    /// </summary>
    [TestMethod]
    public void Reset_ProducesSameOutputAsFreshInstance()
    {
        var signal = MakeSine(FrameSize * 3);

        var fresh = new StreamingResampler();
        var freshOut = new List<float>();
        for (int off = 0; off < signal.Length; off += FrameSize)
        {
            var frame = new float[FrameSize];
            Array.Copy(signal, off, frame, 0, FrameSize);
            freshOut.AddRange(fresh.Resample(frame));
        }

        var reused = new StreamingResampler();
        // 別の信号を流して状態を汚す
        for (int off = 0; off < signal.Length; off += FrameSize)
        {
            var frame = new float[FrameSize];
            Array.Copy(MakeSine(FrameSize * 3, 880.0), off, frame, 0, FrameSize);
            reused.Resample(frame);
        }
        reused.Reset();
        var reusedOut = new List<float>();
        for (int off = 0; off < signal.Length; off += FrameSize)
        {
            var frame = new float[FrameSize];
            Array.Copy(signal, off, frame, 0, FrameSize);
            reusedOut.AddRange(reused.Resample(frame));
        }

        Assert.AreEqual(freshOut.Count, reusedOut.Count, "Reset 後の出力サンプル数が新規インスタンスと不一致。");
        for (int i = 0; i < freshOut.Count; i++)
            Assert.AreEqual(freshOut[i], reusedOut[i], 1e-6f, $"Reset 後の出力が位置 {i} で不一致。");
    }

    /// <summary>
    /// 入力側リサンプラ (WASAPI 48kHz/2ch → 16kHz/mono) の境界アーティファクト排除テスト。
    /// WASAPI チャンクは約 10ms (480 サンプル @ 48kHz)。 旧 AudioCaptureService.Resample は
    /// チャンクごとに WdlResamplingSampleProvider を新規生成しており、 100Hz 周期で境界クリック
    /// ノイズが入り ARC Raiders 等の動的音声で VAD/STT を断続的に騙していた (2026-05-24 確定)。
    /// StreamingResampler を汎用化 (sourceRate/targetRate 引数) して同じパターンで修正できる
    /// ことを実証する回帰テスト。
    /// </summary>
    [TestMethod]
    public void Resample_48kTo16k_StreamingPreservesContinuity()
    {
        const int sourceRate = 48000;
        const int targetRate = 16000;
        const int wasapiChunk = 480; // 48kHz × 10ms
        const int chunks = 12;
        const int n = wasapiChunk * chunks;

        var signal = new float[n];
        for (int i = 0; i < n; i++)
            signal[i] = (float)Math.Sin(2 * Math.PI * 440 * i / sourceRate);

        // (A) 連続: 全体を 1 個のリサンプラに渡す (正解)
        var continuousResampler = new StreamingResampler(sourceRate, targetRate);
        var continuous = continuousResampler.Resample(signal);

        // (B) ステートフル: 同じインスタンスにチャンクずつ流す (= 修正後の AudioCaptureService 動作)
        var streamingResampler = new StreamingResampler(sourceRate, targetRate);
        var streamed = new List<float>();
        for (int off = 0; off < n; off += wasapiChunk)
        {
            var chunk = new float[wasapiChunk];
            Array.Copy(signal, off, chunk, 0, wasapiChunk);
            streamed.AddRange(streamingResampler.Resample(chunk));
        }

        // ステートフル供給なら連続リサンプルとほぼ完全一致する (= 境界アーティファクトなし)
        int compareLen = Math.Min(continuous.Length, streamed.Count);
        double maxDiff = 0;
        for (int i = 0; i < compareLen; i++)
            maxDiff = Math.Max(maxDiff, Math.Abs(continuous[i] - streamed[i]));

        Assert.IsTrue(maxDiff < 1e-4,
            $"48k→16k ステートフルストリーミングが連続リサンプルと乖離 (最大誤差={maxDiff:F6})。 入力側境界アーティファクトの疑い。");
    }

    [TestMethod]
    public void Resample_EmptyInput_ReturnsEmpty()
    {
        var streamer = new StreamingResampler();
        Assert.AreEqual(0, streamer.Resample([]).Length);
    }

    [TestMethod]
    public void Resample_NullInput_Throws()
    {
        var streamer = new StreamingResampler();
        Assert.ThrowsExactly<ArgumentNullException>(() => streamer.Resample(null!));
    }
}
