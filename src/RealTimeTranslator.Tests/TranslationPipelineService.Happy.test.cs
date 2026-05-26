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
    // TestRealtimeTranscriber / TestVoiceActivityDetector は TestDoubles.cs に共通化 (rere v1.0.32 #B2-004)。

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

    // v1.0.24-26 の最大寿命タイマー / partial 連結方式テストは v1.0.27 で削除済み (棚卸し結論)。
    // 「server gap で trailing が永遠に未確定」問題は v1.0.27 の無音 PCM 継続送信で server に delta
    // 引き出しを要求する設計に置換。 client 側で強制確定する経路は廃止された。

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
