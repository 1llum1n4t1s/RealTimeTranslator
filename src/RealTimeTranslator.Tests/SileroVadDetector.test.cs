using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// SileroVadDetector の単体テスト。
/// 実音声サンプルなしでも検証できる範囲 (load / inference 動作 / state / 例外パス) に絞る。
/// 実音声ベースの BGM 弾き精度検証は手動 QA か Integration カテゴリで別途。
/// </summary>
[TestClass]
public sealed class SileroVadDetectorTests
{
    [TestMethod]
    public void Constructor_LoadsModelSuccessfully()
    {
        using var vad = new SileroVadDetector();
        Assert.AreEqual(512, vad.RequiredFrameSize);
        Assert.AreEqual(16000, vad.SampleRate);
    }

    [TestMethod]
    public void Constructor_NonexistentPath_ThrowsFileNotFoundException()
    {
        Assert.ThrowsExactly<FileNotFoundException>(() =>
            new SileroVadDetector(@"C:\definitely\does\not\exist\silero_vad.onnx"));
    }

    [TestMethod]
    public void DetectSpeechProb_SilenceFrame_ReturnsLowProbability()
    {
        using var vad = new SileroVadDetector();
        var silence = new float[512]; // 全ゼロ = 完全無音

        // LSTM state を安定させるため数フレーム流す
        float lastProb = 0f;
        for (int i = 0; i < 10; i++)
        {
            lastProb = vad.DetectSpeechProb(silence);
        }

        Assert.IsTrue(lastProb >= 0f && lastProb <= 1f,
            $"speech probability は 0-1 の範囲であるべき (実測: {lastProb})");
        Assert.IsTrue(lastProb < 0.3f,
            $"完全無音の speech probability は 0.3 未満を期待 (実測: {lastProb})");
    }

    [TestMethod]
    public void DetectSpeechProb_WrongFrameSize_ThrowsArgumentException()
    {
        using var vad = new SileroVadDetector();
        var wrongSize = new float[256];

        Assert.ThrowsExactly<ArgumentException>(() => vad.DetectSpeechProb(wrongSize));
    }

    [TestMethod]
    public void DetectSpeechProb_EmptyFrame_ThrowsArgumentException()
    {
        using var vad = new SileroVadDetector();

        Assert.ThrowsExactly<ArgumentException>(() => vad.DetectSpeechProb([]));
    }

    [TestMethod]
    public void Reset_AfterInference_AllowsFurtherInferenceWithoutError()
    {
        using var vad = new SileroVadDetector();
        var silence = new float[512];

        for (int i = 0; i < 5; i++) vad.DetectSpeechProb(silence);
        vad.Reset();

        // Reset 後に推論できることだけ確認 (LSTM state クリア確認の直接的アサートは困難)
        var prob = vad.DetectSpeechProb(silence);
        Assert.IsTrue(prob >= 0f && prob <= 1f);
    }

    [TestMethod]
    public void Dispose_DoubleCall_DoesNotThrow()
    {
        var vad = new SileroVadDetector();
        vad.Dispose();
        vad.Dispose(); // 2 回目もエラーにならない
    }

    [TestMethod]
    public void DetectSpeechProb_AfterDispose_ReturnsZero()
    {
        var vad = new SileroVadDetector();
        vad.Dispose();
        var silence = new float[512];

        // Dispose 後は 0 を返して例外を投げない (audio loop での late call 対策)
        var prob = vad.DetectSpeechProb(silence);
        Assert.AreEqual(0f, prob);
    }

    // ═══════════════════════════════════════════════════════════════
    // 実音声 wav による結合テスト (sr scalar 形状修正の決定的検証)
    // ═══════════════════════════════════════════════════════════════
    //
    // 経緯 (2026-05-18):
    // - ONNX Runtime 1.20.1 → 1.26.0 に上がった後、 アプリ実機で「翻訳されない」現象
    // - ログから VAD prob=0.001 で固定 = LSTM state が更新されてない症状を特定
    // - ONNX メタデータログで sr 入力 shape=[] (scalar) なのに [1] で渡してたことが判明
    // - 旧 1.20.1 は緩く受け入れていたが 1.26.0 で厳格化 → state 未更新
    // - 修正 = srTensor を ReadOnlySpan<int>.Empty で 0-rank scalar として作る
    //
    // 既存の無音テストでは「prob<0.3」で pass してしまい検出漏れたため、
    // 公式 silero-vad リポジトリの test.wav (人声サンプル) で「prob>0.5 のフレームが
    // 一定数ある」を assert することで再発防止する。

    /// <summary>
    /// PCM16 16kHz mono の WAV ファイルを読み込んで float[-1.0, 1.0] サンプル列に変換する。
    /// ヘッダー 44 byte 固定想定 (test.wav は標準フォーマットなので OK)。
    /// </summary>
    private static float[] LoadWav16kPcm16(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        fs.Position = 44; // RIFF/fmt/data ヘッダースキップ
        long dataLen = fs.Length - 44;
        int sampleCount = (int)(dataLen / 2);
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = br.ReadInt16() / 32768f;
        }
        return samples;
    }

    [TestMethod]
    public void DetectSpeechProb_RealSpeechWav_ProducesVariedProbabilities()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "test.wav");
        Assert.IsTrue(File.Exists(path), $"test.wav が出力ディレクトリにコピーされていない: {path}");

        var samples = LoadWav16kPcm16(path);
        Assert.IsTrue(samples.Length > 512 * 100, "wav サンプル数が足りない (100 フレーム以上必要)");

        using var vad = new SileroVadDetector();
        int frameSize = vad.RequiredFrameSize; // 512
        // wav 全体 (~60秒, 1800+ フレーム) を解析
        int testFrames = samples.Length / frameSize;

        int highProbCount = 0;
        float maxProb = 0f;
        float minProb = 1f;
        double sumProb = 0;
        // prob 分布バケット (0.0-0.1, 0.1-0.2, ..., 0.9-1.0)
        var buckets = new int[10];
        var frame = new float[frameSize];
        for (int i = 0; i < testFrames; i++)
        {
            Array.Copy(samples, i * frameSize, frame, 0, frameSize);
            var prob = vad.DetectSpeechProb(frame);
            if (prob > maxProb) maxProb = prob;
            if (prob < minProb) minProb = prob;
            sumProb += prob;
            if (prob >= 0.5f) highProbCount++;
            int b = Math.Min(9, (int)(prob * 10));
            buckets[b]++;
        }

        // 診断情報を必ず出す (失敗時の解析用)
        var bucketStr = string.Join(" ", buckets.Select((c, i) => $"[{i / 10.0:F1}-{(i + 1) / 10.0:F1}]={c}"));
        var diag = $"frames={testFrames} maxProb={maxProb:F4} minProb={minProb:F4} avgProb={sumProb / testFrames:F4} highProbCount={highProbCount} buckets: {bucketStr}";
        Console.WriteLine($"[VAD test diag] {diag}");

        // sr scalar 修正が機能していれば、 人声 wav (60秒) に対して speech と判定される
        // フレームが一定数あるはず。 修正前 (sr=[1]) は prob=0.001 固定で 0 件になる。
        Assert.IsTrue(maxProb > 0.5f,
            $"人声 wav の最大 speech probability が低すぎ ({maxProb:F4})。 期待: > 0.5。 診断: {diag}");
        Assert.IsTrue(highProbCount >= 20,
            $"speech 判定フレーム数が少なすぎ ({highProbCount}/{testFrames})。 期待: >= 20。 診断: {diag}");
        // prob が振れていることも確認 (= LSTM state が毎フレーム更新されてる証拠)
        Assert.IsTrue(maxProb - minProb > 0.3f,
            $"prob の振れ幅が小さすぎ ({maxProb - minProb:F4})。 値が固定なら state が更新されていない。 診断: {diag}");
    }

    [TestMethod]
    public void Diagnostic_WavSampleStatistics()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "test.wav");
        var samples = LoadWav16kPcm16(path);
        var max = samples.Max();
        var min = samples.Min();
        float absSum = 0;
        for (int i = 0; i < samples.Length; i++) absSum += MathF.Abs(samples[i]);
        var absAvg = absSum / samples.Length;
        // 最初の数サンプル
        var firstFew = string.Join(",", samples.Take(10).Select(s => s.ToString("F4")));
        Console.WriteLine($"[wav diag] count={samples.Length} max={max:F4} min={min:F4} absAvg={absAvg:F4} first10=[{firstFew}]");

        Assert.IsTrue(samples.Length > 100, "wav サンプルが少なすぎ");
        Assert.IsTrue(max > 0.01f || min < -0.01f, $"wav が無音っぽい (max={max}, min={min})");
        Assert.IsTrue(max <= 1.0f && min >= -1.0f, "wav 値域が [-1, 1] を超えてる");
    }

    [TestMethod]
    public void DetectSpeechProb_RealSpeechWav_AfterReset_ProducesSimilarResults()
    {
        // Reset() 後に同じ wav を流して、 一貫した結果になるか (LSTM state が
        // 確実に初期化されることの検証)
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "test.wav");
        var samples = LoadWav16kPcm16(path);
        using var vad = new SileroVadDetector();
        int frameSize = vad.RequiredFrameSize;

        float MaxProbOver50Frames()
        {
            float max = 0f;
            var frame = new float[frameSize];
            for (int i = 0; i < 50; i++)
            {
                Array.Copy(samples, i * frameSize, frame, 0, frameSize);
                var p = vad.DetectSpeechProb(frame);
                if (p > max) max = p;
            }
            return max;
        }

        var firstRunMax = MaxProbOver50Frames();
        vad.Reset();
        var secondRunMax = MaxProbOver50Frames();

        // 同じ入力 + Reset で似た値になるはず (state が確実に初期化されてる証拠)
        Assert.AreEqual(firstRunMax, secondRunMax, 0.001f,
            $"Reset 前後で max prob が一致しない (firstRun={firstRunMax:F4}, secondRun={secondRunMax:F4})");
    }
}
