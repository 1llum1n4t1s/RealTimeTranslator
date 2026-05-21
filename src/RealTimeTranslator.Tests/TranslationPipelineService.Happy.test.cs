using Microsoft.VisualStudio.TestTools.UnitTesting;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// TranslationPipelineService の正常系 (Happy Path) テスト (stst 隊員1)。
/// done 経路の累積差分抽出 / アイドルタイムアウト確定 / ARC Raiders 分断シナリオの正常動作を検証する。
/// 既存 SentenceSplit テストでカバー済みの IsSentenceBoundaryAt・delta 経路の基本分割とは重複しない未カバー領域に集中。
/// </summary>
[TestClass]
public sealed class TranslationPipelineServiceHappyTests
{
    private sealed class TestRealtimeTranscriber : IRealtimeTranscriber
    {
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public long TotalAudioInputSamples24kHz => 0;
        public long ServerReportedAudioInputTokens => 0;
        public event Action<string>? TranscriptDeltaReceived;
        public event Action<string>? TranscriptCompleted;
#pragma warning disable CS0067
        public event Action<Exception>? ErrorReceived;
#pragma warning restore CS0067
        public event Action<ConnectionState>? StateChanged;

        public Task ConnectAsync(OpenAIRealtimeSettings settings, CancellationToken ct = default)
        { State = ConnectionState.Connected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public void SendAudio(byte[] pcm16Audio) { }
        public Task DisconnectAsync()
        { State = ConnectionState.Disconnected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }

        public void RaiseDelta(string delta) => TranscriptDeltaReceived?.Invoke(delta);
        public void RaiseDone(string transcript) => TranscriptCompleted?.Invoke(transcript);
        public void RaiseStateChanged(ConnectionState newState) { State = newState; StateChanged?.Invoke(newState); }
    }

    private sealed class TestVoiceActivityDetector : IVoiceActivityDetector
    {
        public int RequiredFrameSize => 512;
        public int SampleRate => 16000;
        public float DetectSpeechProb(ReadOnlySpan<float> frame16kHz) => 0f;
        public void Reset() { }
        public void Dispose() { }
    }

    private static (TranslationPipelineService pipeline, TestRealtimeTranscriber transcriber, List<SubtitleItem> emitted) CreatePipeline()
    {
        var transcriber = new TestRealtimeTranscriber();
        var audio = new TestAudioCaptureService();
        var settings = new AppSettings
        {
            OpenAIRealtime = new OpenAIRealtimeSettings
            {
                ApiKey = "test-key",
                Endpoint = "wss://api.openai.com/v1/realtime/translations",
                Model = "gpt-realtime-translate",
                OutputLanguage = "ja"
            }
        };
        var monitor = new StubOptionsMonitor(settings);
        var settingsService = new TestSettingsService();
        var vad = new TestVoiceActivityDetector();
        var pipeline = new TranslationPipelineService(audio, transcriber, monitor, settingsService, vad);
        var emitted = new List<SubtitleItem>();
        pipeline.SubtitleGenerated += (_, item) => emitted.Add(item);
        return (pipeline, transcriber, emitted);
    }

    // アイドル確定テスト用: Overlay.DisplayDuration を短く設定して待ち時間を最小化する (下限 1 秒)。
    private static (TranslationPipelineService pipeline, TestRealtimeTranscriber transcriber, List<SubtitleItem> emitted) CreatePipelineWithDisplayDuration(double displayDurationSeconds)
    {
        var transcriber = new TestRealtimeTranscriber();
        var audio = new TestAudioCaptureService();
        var settings = new AppSettings
        {
            OpenAIRealtime = new OpenAIRealtimeSettings
            {
                ApiKey = "test-key",
                Endpoint = "wss://api.openai.com/v1/realtime/translations",
                Model = "gpt-realtime-translate",
                OutputLanguage = "ja"
            },
            Overlay = new OverlaySettings { DisplayDuration = displayDurationSeconds }
        };
        var monitor = new StubOptionsMonitor(settings);
        var settingsService = new TestSettingsService();
        var vad = new TestVoiceActivityDetector();
        var pipeline = new TranslationPipelineService(audio, transcriber, monitor, settingsService, vad);
        var emitted = new List<SubtitleItem>();
        pipeline.SubtitleGenerated += (_, item) => emitted.Add(item);
        return (pipeline, transcriber, emitted);
    }

    /// <happypath category="done-cumulative-diff" description="累積done(セッション全文)の2回目で差分だけがemitされる" expected="2回目doneのIsFinalは1件、内容は差分の『元気。』のみ" />
    [TestMethod]
    [TestCategory("Happy")]
    public void OnTranscriptCompleted_累積差分のみ_2回目doneで新規文だけemitすること()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();

        // Act: 1回目のdone = セッション累積「今日は。」
        transcriber.RaiseDone("今日は。");
        var firstFinals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, firstFinals.Count, "1回目doneで完結文1件");
        Assert.AreEqual("今日は。", firstFinals[0].TranslatedText);

        // Act: 2回目のdone = セッション累積全文「今日は。元気。」(1文目は既出)
        emitted.Clear();
        transcriber.RaiseDone("今日は。元気。");

        // Assert: 差分「元気。」だけがemitされ、「今日は。」は重複しない
        var secondFinals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, secondFinals.Count, "2回目doneは差分1件のみemitされるはず");
        Assert.AreEqual("元気。", secondFinals[0].TranslatedText, "既出『今日は。』を除いた差分のみ");
    }

    /// <happypath category="done-multi-sentence" description="1回のdoneで新規分に複数文が含まれると各文を分割emit" expected="IsFinalが3件、各文が別SegmentId、内容は『あ。』『い。』『う。』" />
    [TestMethod]
    [TestCategory("Happy")]
    public void OnTranscriptCompleted_累積全文に複数新規文_各文を別SegmentIdでemitすること()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();

        // Act: 初回doneで一気に3文の累積全文が届く
        transcriber.RaiseDone("あ。い。う。");

        // Assert: 3文がそれぞれ完結文としてemit
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(3, finals.Count, "句点3つで3文emitされるはず");
        Assert.AreEqual("あ。", finals[0].TranslatedText);
        Assert.AreEqual("い。", finals[1].TranslatedText);
        Assert.AreEqual("う。", finals[2].TranslatedText);
        Assert.AreNotEqual(finals[0].SegmentId, finals[1].SegmentId, "文1と文2は別SegmentId");
        Assert.AreNotEqual(finals[1].SegmentId, finals[2].SegmentId, "文2と文3は別SegmentId");
        Assert.AreNotEqual(finals[0].SegmentId, finals[2].SegmentId, "文1と文3は別SegmentId");
    }

    /// <happypath category="done-idempotent" description="同一累積transcriptの再送は差分ゼロでemitなし" expected="2回目doneのIsFinalは0件" />
    [TestMethod]
    [TestCategory("Happy")]
    public void OnTranscriptCompleted_確定済みと同一transcript再送_新規分なしでemitしないこと()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();

        // Act: 同一transcriptを2回
        transcriber.RaiseDone("こんばんは。");
        Assert.AreEqual(1, emitted.Count(x => x.IsFinal), "1回目で1件");

        emitted.Clear();
        transcriber.RaiseDone("こんばんは。");

        // Assert: 新規分が無いのでemitゼロ
        Assert.AreEqual(0, emitted.Count(x => x.IsFinal), "同一transcript再送は差分ゼロでemitなし");
    }

    /// <happypath category="delta-then-done" description="句点なしdeltaで蓄積→doneで確定文をemit" expected="最終的にIsFinal『おはよう。』が1件" />
    [TestMethod]
    [TestCategory("Happy")]
    public void OnTranscriptDelta_partialからdone確定への連携が正常に動くこと()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();

        // Act: まず句点なしdelta(partial蓄積のみ、完結文emitなし)
        transcriber.RaiseDelta("おはよう");
        Assert.AreEqual(0, emitted.Count(x => x.IsFinal), "句点なしdelta時点では完結文emitゼロ");

        // Act: doneで句点付き累積全文が届く
        transcriber.RaiseDone("おはよう。");

        // Assert: 確定文が1件emitされ内容が正しい
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count, "doneで確定文1件emit");
        Assert.AreEqual("おはよう。", finals[0].TranslatedText);
    }

    /// <happypath category="delta-confirmed-then-done" description="delta確定済み2文+done累積で差分のみ抽出" expected="done側のIsFinalは差分『追加文。』1件のみ" />
    [TestMethod]
    [TestCategory("Happy")]
    public void OnTranscriptCompleted_delta複数文確定後のdone累積_さらなる差分のみemitすること()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();

        // Act: delta経路で2文確定 (_lastFinalizedTranscript = "一文目。二文目。")
        transcriber.RaiseDelta("一文目。二文目。");
        Assert.AreEqual(2, emitted.Count(x => x.IsFinal), "delta経路で2文確定");

        // Act: doneでセッション累積全文(既出2文 + 新規1文)が届く
        emitted.Clear();
        transcriber.RaiseDone("一文目。二文目。追加文。");

        // Assert: 既出2文は重複せず、新規差分「追加文。」だけemit
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count, "done経路は新規差分1件のみ");
        Assert.AreEqual("追加文。", finals[0].TranslatedText, "delta確定済み2文を除いた差分のみ");
    }

    /// <happypath category="idle-finalize" description="句点なしpartialがDisplayDuration放置で確定emit" expected="待機後IsFinal『やあ』が1件" />
    [TestMethod]
    [TestCategory("Happy")]
    public async Task OnTranscriptDelta_句点なし短文をアイドル放置でIsFinal確定emitすること()
    {
        var (pipeline, transcriber, emitted) = CreatePipelineWithDisplayDuration(1);

        // Act: 句点なしdelta(完結文emitなし、アイドルタイマー起動)
        transcriber.RaiseDelta("やあ");
        Assert.AreEqual(0, emitted.Count(x => x.IsFinal), "delta直後は確定emitなし");

        // Act: DisplayDuration(下限1秒)を超えて放置 → アイドル確定タイマー発火
        await Task.Delay(1300);

        // Assert: アイドル確定で「やあ」がIsFinal=trueで1件emit
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count, "アイドル確定でIsFinal=true 1件emitされるはず");
        Assert.AreEqual("やあ", finals[0].TranslatedText);
        Assert.IsTrue(finals[0].IsFinal, "アイドル確定はIsFinal=true");
    }

    /// <happypath category="arc-raiders-split" description="アイドル確定した断片が後続doneのprefixとして扱われ差分のみemit" expected="アイドルで『何』、done差分で『であれ。』、合計IsFinal2件で重複なし" />
    [TestMethod]
    [TestCategory("Happy")]
    public async Task ARC_Raiders分断_何をアイドル確定後にdone何であれで差分であれだけemitすること()
    {
        var (pipeline, transcriber, emitted) = CreatePipelineWithDisplayDuration(1);

        // Act 1: 「何」だけがdeltaで届く(句点なし → partial蓄積)
        transcriber.RaiseDelta("何");
        Assert.AreEqual(0, emitted.Count(x => x.IsFinal), "delta直後は確定emitなし");

        // Act 2: DisplayDuration放置でアイドル確定 → 「何」がIsFinal emit + _lastFinalizedTranscriptへ取り込み
        await Task.Delay(1300);
        var afterIdle = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, afterIdle.Count, "アイドル確定で『何』が1件emit");
        Assert.AreEqual("何", afterIdle[0].TranslatedText);

        // Act 3: doneでセッション累積全文「何であれ。」が届く(『何』は既にアイドル確定済み)
        emitted.Clear();
        transcriber.RaiseDone("何であれ。");

        // Assert: 差分「であれ。」だけemit、「何」は重複しない
        var afterDone = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, afterDone.Count, "done経路は差分1件のみ(『何』重複なし)");
        Assert.AreEqual("であれ。", afterDone[0].TranslatedText, "アイドル確定済み『何』を除いた差分のみ");
    }

    /// <happypath category="done-no-terminator-trailing" description="句点なしdoneは差分全体をtrailing確定としてemit" expected="IsFinal『まいど』が1件" />
    [TestMethod]
    [TestCategory("Happy")]
    public void OnTranscriptCompleted_句点なし累積全文_trailingをIsFinalでemitすること()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();

        // Act: 句点を含まない累積全文がdoneで届く
        transcriber.RaiseDone("まいど");

        // Assert: done文脈ではtrailing「まいど」をIsFinal=trueでemit
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count, "句点なしdoneは差分全体をtrailing確定として1件emit");
        Assert.AreEqual("まいど", finals[0].TranslatedText);
        Assert.IsTrue(finals[0].IsFinal);
    }

    /// <happypath category="done-sentence-plus-trailing" description="done差分が『完結文。+未完trailing』のとき完結文とtrailingを分けてemit" expected="IsFinal2件、『はい。』と『つづき』、別SegmentId" />
    [TestMethod]
    [TestCategory("Happy")]
    public void OnTranscriptCompleted_文末trailing付き累積_完結文をemitしtrailingは未確定で保持すること()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();

        // Act: 完結文 + 句点なしtrailing の累積全文がdoneで届く
        transcriber.RaiseDone("はい。つづき");

        // Assert: 完結文「はい。」とtrailing「つづき」が両方emit(done時はtrailingも即IsFinal)
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(2, finals.Count, "完結文1 + trailing1 で2件emit");
        Assert.AreEqual("はい。", finals[0].TranslatedText, "1件目は完結文");
        Assert.AreEqual("つづき", finals[1].TranslatedText, "2件目はtrailing");
        Assert.AreNotEqual(finals[0].SegmentId, finals[1].SegmentId, "完結文とtrailingは別SegmentId");
    }
}
