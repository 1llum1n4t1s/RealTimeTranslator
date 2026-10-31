using Microsoft.VisualStudio.TestTools.UnitTesting;
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
        => TranslationPipelineTestFixture.Create();

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

    /// <summary>delta のない初回 done は、全文を文順に分割して別 SegmentId で確定する。</summary>
    [TestMethod]
    [TestCategory("Happy")]
    [TestCategory("StateMachine")]
    [TestCategory("TypePunch")]
    [DataRow("いきなり確定。", new string[] { "いきなり確定。" })]
    [DataRow("最初の文。次の文。", new string[] { "最初の文。", "次の文。" })]
    [DataRow("あ。い。う。", new string[] { "あ。", "い。", "う。" })]
    public void OnTranscriptCompleted_delta無し初回done_各文を順番に別SegmentIdでemitすること(string transcript, string[] expectedSentences)
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDone(transcript);

        var finals = emitted.Where(x => x.IsFinal).ToList();
        CollectionAssert.AreEqual(expectedSentences, finals.Select(x => x.TranslatedText).ToArray());
        Assert.AreEqual(expectedSentences.Length, finals.Select(x => x.SegmentId).Distinct().Count(), "全ての文が別 SegmentId を持つ");
    }

    /// <happypath category="done-idempotent" description="同一累積transcriptの再送は差分ゼロでemitなし" expected="2回目doneの追加通知は0件" />
    [TestMethod]
    [TestCategory("Happy")]
    [TestCategory("StateMachine")]
    public void OnTranscriptCompleted_確定済みと同一transcript再送_新規分なしでemitしないこと()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();

        // Act: 同一transcriptを2回
        transcriber.RaiseDone("こんばんは。");
        Assert.AreEqual(1, emitted.Count(x => x.IsFinal), "1回目で1件");

        emitted.Clear();
        transcriber.RaiseDone("こんばんは。");

        // Assert: 新規分が無いのでemitゼロ
        Assert.AreEqual(0, emitted.Count, "同一transcript再送は追加通知なし");
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

    // 非空の累積 done の末尾は partial に保ち、句読点・空 done・idle・停止で確定する。
    /// <happypath category="done-no-terminator-trailing" description="句点なし累積doneはpartialとしてemit" expected="未確定『まいど』が1件" />
    [TestMethod]
    [TestCategory("Happy")]
    public void OnTranscriptCompleted_句点なし累積全文_trailingをpartialでemitすること()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();

        // Act: 句点を含まない累積全文がdoneで届く
        transcriber.RaiseDone("まいど");

        // Assert: 未完結の末尾は表示できるpartialとし、確定ログへ流さない。
        Assert.AreEqual(0, emitted.Count(x => x.IsFinal));
        var partial = emitted.Single(x => !x.IsFinal);
        Assert.AreEqual("まいど", partial.OriginalText);
        Assert.AreEqual("まいど", partial.DisplayText);
        Assert.AreEqual(string.Empty, partial.TranslatedText);
    }

    /// <happypath category="done-sentence-plus-trailing" description="完結文と未完末尾を分けてemit" expected="確定『はい。』とpartial『つづき』、別SegmentId" />
    [TestMethod]
    [TestCategory("Happy")]
    public void OnTranscriptCompleted_文末trailing付き累積_完結文をemitしtrailingは未確定で保持すること()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();

        // Act: 完結文 + 句点なしtrailing の累積全文がdoneで届く
        transcriber.RaiseDone("はい。つづき");

        // Assert: 完結文だけを確定し、末尾は同じSegmentIdで続けられるpartialにする。
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count, "完結文だけを確定する");
        Assert.AreEqual("はい。", finals[0].TranslatedText, "1件目は完結文");
        var partial = emitted.Single(x => !x.IsFinal);
        Assert.AreEqual("つづき", partial.DisplayText, "2件目は未完末尾");
        Assert.AreNotEqual(finals[0].SegmentId, partial.SegmentId, "完結文とtrailingは別SegmentId");
    }
}
