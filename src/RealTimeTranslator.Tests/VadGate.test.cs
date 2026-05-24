using Microsoft.Extensions.Options;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// TranslationPipelineService の VAD ゲート状態機 (Silence/InSpeech/Hangover) を ProcessVadFrame /
/// ProcessAudioWithVadGate の直接呼び出しで検証する。 audio loop の非同期 Channel を介さず、
/// 状態機ロジックそのものをユニットテストする (internal メソッドを InternalsVisibleTo 経由で呼ぶ)。
///
/// 観測手段は「RecordingTranscriber.SendAudio 呼び出し回数」。 状態機が PreRoll を吐き出した /
/// Hangover フレームを送信した / Silence で送信を抑制した、 を間接的に検証する。
/// </summary>
[TestClass]
public sealed class VadGateTests
{
    // ───────── テスト用 mock ─────────

    /// <summary>NextProb を外から変更して任意の speech probability を返せる VAD。</summary>
    private sealed class ControllableVadDetector : IVoiceActivityDetector
    {
        public int RequiredFrameSize => 512;
        public int SampleRate => 16000;
        public float NextProb { get; set; }
        public int InferenceCount { get; private set; }
        public int ResetCount { get; private set; }
        public float DetectSpeechProb(ReadOnlySpan<float> frame16kHz)
        {
            InferenceCount++;
            return NextProb;
        }
        public void Reset() { ResetCount++; }
        public void Dispose() { }
    }

    /// <summary>SendAudio 呼び出し回数を記録するだけの最小 transcriber。</summary>
    private sealed class RecordingTranscriber : IRealtimeTranscriber
    {
        public ConnectionState State => ConnectionState.Connected;
        public long TotalAudioInputSamples24kHz { get; private set; }
        public long ServerReportedAudioInputTokens => 0;
        public List<int> SentAudioByteLengths { get; } = new();
#pragma warning disable CS0067 // テストでは未使用
        public event Action<string>? TranscriptDeltaReceived;
        public event Action<string>? TranscriptCompleted;
        public event Action<Exception>? ErrorReceived;
        public event Action<ConnectionState>? StateChanged;
#pragma warning restore CS0067
        public Task ConnectAsync(OpenAIRealtimeSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public void SendAudio(byte[] pcm16Audio)
        {
            SentAudioByteLengths.Add(pcm16Audio.Length);
            TotalAudioInputSamples24kHz += pcm16Audio.Length / 2;
        }
        // v1.0.26: VAD Silence で commit 試験送信が呼ばれることを検証するカウンタ。
        public int SendCommitCallCount { get; private set; }
        public void SendCommit() => SendCommitCallCount++;
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
        // v1.0.26: partial 蓄積テスト用に delta を直接発火するヘルパー (TranslationPipelineService.OnTranscriptDelta を起動)。
        public void RaiseDelta(string delta) => TranscriptDeltaReceived?.Invoke(delta);
    }

    // TestAudioCaptureService / TestSettingsService / StubOptionsMonitor は TestDoubles.cs に共通化。

    private static (TranslationPipelineService pipeline, ControllableVadDetector vad, RecordingTranscriber transcriber, AudioCaptureSettings vadSettings) Create(
        int preRollMs = 384,  // 384/32 = 12 frames
        int hangoverMs = 192, // 192/32 = 6 frames
        int silenceFinalizeMs = 2000) // v1.0.26: VAD Silence 継続 → commit + 強制確定の閾値 (テストでは短くする)
    {
        var vad = new ControllableVadDetector();
        var transcriber = new RecordingTranscriber();
        var audio = new TestAudioCaptureService();
        var settings = new AppSettings
        {
            AudioCapture = new AudioCaptureSettings
            {
                EnableVad = true,
                VadThreshold = 0.5f,
                VadPreRollMs = preRollMs,
                VadHangoverMs = hangoverMs,
            },
            OpenAIRealtime = new OpenAIRealtimeSettings
            {
                ApiKey = "test-key",
                Model = "gpt-realtime-translate",
                SilenceFinalizeMs = silenceFinalizeMs,
            }
        };
        var monitor = new StubOptionsMonitor(settings);
        var settingsService = new TestSettingsService();
        var pipeline = new TranslationPipelineService(audio, transcriber, monitor, settingsService, vad);
        return (pipeline, vad, transcriber, settings.AudioCapture);
    }

    /// <summary>512 サンプル (32ms) の空フレームを返すヘルパー (VAD 判定用 16kHz)。</summary>
    private static float[] Frame() => new float[512];

    /// <summary>768 サンプル (32ms) の空フレームを返すヘルパー (送信用 24kHz、 ProcessVadFrame 第2引数)。</summary>
    private static float[] Frame24k() => new float[768];

    // ═══════════════════════════════════════════════════════════════
    // ProcessVadFrame: 状態機の挙動
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("VadGate")]
    public void Silence_AllBelowThreshold_NoSendButPreRollAccumulates()
    {
        var (pipeline, vad, transcriber, settings) = Create();
        vad.NextProb = 0.1f;
        for (int i = 0; i < 5; i++)
        {
            pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        }
        Assert.AreEqual(0, transcriber.SentAudioByteLengths.Count, "Silence 中は OpenAI に送信されない");
        Assert.AreEqual(5, vad.InferenceCount, "全フレームで VAD 推論は走る");
    }

    [TestMethod]
    [TestCategory("VadGate")]
    public void Silence_SpeechDetected_PreRollFlushedPlusCurrentSent()
    {
        var (pipeline, vad, transcriber, settings) = Create();
        // Silence で 3 フレーム積む
        vad.NextProb = 0.1f;
        for (int i = 0; i < 3; i++)
        {
            pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        }
        Assert.AreEqual(0, transcriber.SentAudioByteLengths.Count);

        // Speech 検出 → PreRoll 3 件 + 当該 1 件 = 4 件送信される
        vad.NextProb = 0.9f;
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        Assert.AreEqual(4, transcriber.SentAudioByteLengths.Count, "Speech 開始時に PreRoll + 現在フレームが送信される");
    }

    [TestMethod]
    [TestCategory("VadGate")]
    public void InSpeech_ContinuousSpeech_AllFramesSent()
    {
        var (pipeline, vad, transcriber, settings) = Create();
        vad.NextProb = 0.9f;
        for (int i = 0; i < 10; i++)
        {
            pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        }
        Assert.AreEqual(10, transcriber.SentAudioByteLengths.Count, "Speech 中は全フレーム送信される");
    }

    [TestMethod]
    [TestCategory("VadGate")]
    public void InSpeech_SpeechStops_HangoverFramesAreStillSent()
    {
        // hangoverMs=192 → 6 frames
        var (pipeline, vad, transcriber, settings) = Create(hangoverMs: 192);
        // InSpeech に入る
        vad.NextProb = 0.9f;
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        Assert.AreEqual(1, transcriber.SentAudioByteLengths.Count);

        // 連続無音 10 フレーム流す:
        //   iter 1: InSpeech, !isSpeech → 送信 (count=2), Hangover 突入, 残=6
        //   iter 2-7: Hangover, !isSpeech → 各 1 件送信 (count=3..8), 残=5..0
        //   iter 8-10: Silence, !isSpeech → PreRoll 蓄積のみ (送信なし)
        vad.NextProb = 0.1f;
        for (int i = 0; i < 10; i++)
        {
            pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        }
        Assert.AreEqual(8, transcriber.SentAudioByteLengths.Count,
            "InSpeech(1) + Hangover 境界送信(1) + Hangover 残 6 = 計 8 件");
    }

    [TestMethod]
    [TestCategory("VadGate")]
    public void Hangover_SpeechResumes_ReturnsToInSpeechWithoutGap()
    {
        var (pipeline, vad, transcriber, settings) = Create(hangoverMs: 192);
        // InSpeech
        vad.NextProb = 0.9f;
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        // Hangover 突入 (依然送信される)
        vad.NextProb = 0.1f;
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        // Hangover 中に speech 再開 → InSpeech 復帰、 送信継続
        vad.NextProb = 0.9f;
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        // さらに speech 続行
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);

        Assert.AreEqual(4, transcriber.SentAudioByteLengths.Count, "Hangover 中の speech 再開で送信が途切れない");
    }

    [TestMethod]
    [TestCategory("VadGate")]
    public void Silence_PreRollEvictsOldestFrames()
    {
        // PreRoll = 96ms = 3 frames
        var (pipeline, vad, transcriber, settings) = Create(preRollMs: 96);
        vad.NextProb = 0.1f;
        // 10 frames 投入 → PreRoll は 3 まで、 7 frames は押し出される
        for (int i = 0; i < 10; i++)
        {
            pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        }
        Assert.AreEqual(0, transcriber.SentAudioByteLengths.Count, "Silence で送信なし");

        // 突然 speech → PreRoll の 3 件 + 当該 1 件 = 4 件
        vad.NextProb = 0.9f;
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        Assert.AreEqual(4, transcriber.SentAudioByteLengths.Count, "PreRoll は 3 件にキャップされる (古いものは押し出し)");
    }

    [TestMethod]
    [TestCategory("VadGate")]
    public void Threshold_BoundaryBehavior_GreaterEqualIsSpeech()
    {
        var (pipeline, vad, transcriber, settings) = Create();
        // ちょうど threshold = 0.5 → speech 扱い (`prob >= threshold`)
        vad.NextProb = 0.5f;
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        Assert.AreEqual(1, transcriber.SentAudioByteLengths.Count, "prob == threshold は speech 扱い");
    }

    // ═══════════════════════════════════════════════════════════════
    // ProcessAudioWithVadGate: フレーム切り出し / chunk 跨ぎ
    // ═══════════════════════════════════════════════════════════════
    //
    // ⚠️ 2026-05-24 リファクタ (48k→16k/24k 並列リサンプル化) で ProcessAudioWithVadGate の
    //   入力契約が「16k 直接」→「48k 入力 + 内部 2 系統ステートフルリサンプル」に変わったため、
    //   旧テスト (16k フレームサイズ単位での推論回数を厳密検証) は意味を失った。
    //   StreamingResampler の LatencyMargin (4ms) による初回 warmup で、48k 入力 1536 sample が
    //   そのまま 1 推論にならないなど挙動が変わる。 状態機の本質的な検証は ProcessVadFrame
    //   (16k+24k フレームペアを直接渡す) 経路の上記テスト群で維持されている。
    //   下記 4 テストは設計変更後の「48k 入力 + warmup 許容」を考慮した統計的テストに
    //   書き直す課題として残す (v1.0.24 以降で対応予定)。

    [TestMethod]
    [TestCategory("VadGate")]
    [Ignore("48k入力リファクタ後の再設計待ち (LatencyMargin warmup を考慮した統計的テストに書き換え)")]
    public void ProcessAudioWithVadGate_ExactFrameSize_OneInference()
    {
        var (pipeline, vad, _, settings) = Create();
        vad.NextProb = 0.9f;
        pipeline.ProcessAudioWithVadGate(new float[512], settings);
        Assert.AreEqual(1, vad.InferenceCount);
    }

    [TestMethod]
    [TestCategory("VadGate")]
    [Ignore("48k入力リファクタ後の再設計待ち")]
    public void ProcessAudioWithVadGate_MultipleOfFrameSize_MultipleInferences()
    {
        var (pipeline, vad, _, settings) = Create();
        vad.NextProb = 0.9f;
        pipeline.ProcessAudioWithVadGate(new float[1024], settings);
        Assert.AreEqual(2, vad.InferenceCount);
    }

    [TestMethod]
    [TestCategory("VadGate")]
    [Ignore("48k入力リファクタ後の再設計待ち")]
    public void ProcessAudioWithVadGate_NotAlignedToFrame_AccumulatesAcrossChunks()
    {
        var (pipeline, vad, _, settings) = Create();
        vad.NextProb = 0.9f;
        pipeline.ProcessAudioWithVadGate(new float[800], settings);
        Assert.AreEqual(1, vad.InferenceCount, "800 サンプルでは 1 フレーム分のみ推論");
        pipeline.ProcessAudioWithVadGate(new float[224], settings);
        Assert.AreEqual(2, vad.InferenceCount, "chunk 跨ぎ後、 残り 288+224=512 で 1 推論");
    }

    [TestMethod]
    [TestCategory("VadGate")]
    [Ignore("48k入力リファクタ後の再設計待ち")]
    public void ProcessAudioWithVadGate_SmallChunks_AccumulateUntilFrameReady()
    {
        var (pipeline, vad, _, settings) = Create();
        vad.NextProb = 0.9f;
        for (int i = 0; i < 5; i++)
        {
            pipeline.ProcessAudioWithVadGate(new float[100], settings);
        }
        Assert.AreEqual(0, vad.InferenceCount, "512 サンプル未満では推論されない");
        pipeline.ProcessAudioWithVadGate(new float[12], settings);
        Assert.AreEqual(1, vad.InferenceCount);
    }

    // ═══════════════════════════════════════════════════════════════
    // v1.0.26: VAD Silence 継続 → 能動的区切り (SendCommit + 強制確定)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// VAD Silence が SilenceFinalizeMs 継続し、 かつ partial が蓄積されている場合、
    /// input_audio_buffer.commit が試験送信される (server gap 対策の能動的区切り)。
    /// </summary>
    [TestMethod]
    [TestCategory("VadGate")]
    public async Task Silence_PartialPresent_AfterSilenceFinalizeMs_TriggersCommitOnce()
    {
        // SilenceFinalizeMs=200 で短く設定 (テスト時間最小化)。 hangoverMs も小さく (32ms=1 フレーム)。
        var (pipeline, vad, transcriber, settings) = Create(hangoverMs: 32, silenceFinalizeMs: 200);

        // 1. partial を蓄積 (delta を発火 → TranslationPipelineService._accumulatedText に「そ」)
        transcriber.RaiseDelta("そ");

        // 2. Silence → InSpeech → Hangover → Silence の遷移を強制
        vad.NextProb = 0.9f;
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings); // Silence → InSpeech
        vad.NextProb = 0.1f;
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings); // InSpeech → Hangover (1 frame)
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings); // Hangover → Silence (hangoverMs=32 → 1 frame で抜ける)

        // 3. SilenceFinalizeMs (200ms) 超えるまで待機
        await Task.Delay(300);

        // 4. もう 1 フレーム投入で Silence 状態の継続判定が走る
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);

        Assert.AreEqual(1, transcriber.SendCommitCallCount,
            "VAD Silence 継続 + partial あり で SendCommit が 1 回だけ呼ばれるはず");

        // 5. 追加で何フレーム投入しても 2 回目は呼ばれない (_silenceCommitFired フラグで連発抑制)
        for (int i = 0; i < 5; i++)
        {
            pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        }
        Assert.AreEqual(1, transcriber.SendCommitCallCount,
            "_silenceCommitFired フラグで SendCommit は 1 回限り (連発しない)");
    }

    /// <summary>
    /// VAD Silence が継続しても partial が無ければ SendCommit は呼ばれない (server 状態を不必要に揺らさない)。
    /// </summary>
    [TestMethod]
    [TestCategory("VadGate")]
    public async Task Silence_NoPartial_AfterSilenceFinalizeMs_DoesNotTriggerCommit()
    {
        var (pipeline, vad, transcriber, settings) = Create(hangoverMs: 32, silenceFinalizeMs: 200);

        // partial を蓄積しない (RaiseDelta を呼ばない)
        vad.NextProb = 0.9f;
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        vad.NextProb = 0.1f;
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);

        await Task.Delay(300);
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);

        Assert.AreEqual(0, transcriber.SendCommitCallCount,
            "partial が無いときは SendCommit がスキップされるはず (server 状態を不必要に揺らさない)");
    }

    /// <summary>
    /// VAD Silence → InSpeech 再遷移で _silenceCommitFired フラグがリセットされ、
    /// 次の Silence 継続時に再度 SendCommit が発火可能になる。
    /// </summary>
    [TestMethod]
    [TestCategory("VadGate")]
    public async Task Silence_ResumeSpeechThenSilenceAgain_CommitFiresAgain()
    {
        var (pipeline, vad, transcriber, settings) = Create(hangoverMs: 32, silenceFinalizeMs: 200);

        // 1 回目: partial → silence 継続 → commit 1 回
        transcriber.RaiseDelta("そ");
        vad.NextProb = 0.9f;
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        vad.NextProb = 0.1f;
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        await Task.Delay(300);
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);
        Assert.AreEqual(1, transcriber.SendCommitCallCount, "1 回目の silence で commit 発火");

        // 強制確定が走るのを待つ (Task.Run の非同期完了)
        await Task.Delay(100);

        // 2 回目: 発話再開 (Silence → InSpeech) → 新しい partial → 再 silence → commit 2 回目
        transcriber.RaiseDelta("つ");
        vad.NextProb = 0.9f;
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings); // Silence → InSpeech (フラグリセット)
        vad.NextProb = 0.1f;
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings); // InSpeech → Hangover
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings); // Hangover → Silence
        await Task.Delay(300);
        pipeline.ProcessVadFrame(Frame(), Frame24k(), settings);

        Assert.AreEqual(2, transcriber.SendCommitCallCount,
            "発話再開 → 再 silence で _silenceCommitFired がリセットされ、 commit が再発火するはず");
    }
}
