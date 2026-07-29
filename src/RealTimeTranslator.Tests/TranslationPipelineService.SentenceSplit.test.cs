using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// TranslationPipelineService の文区切りロジック単体テスト (rere レビュー B3-010 修正)。
/// OnTranscriptDelta / OnTranscriptCompleted の句点分割 + 再接続リセット + D-7 fallback の挙動を
/// IRealtimeTranscriber / IAudioCaptureService をモック化して検証する。
/// </summary>
[TestClass]
public sealed class TranslationPipelineServiceSentenceSplitTests
{
    // TestRealtimeTranscriber / TestVoiceActivityDetector / TestAudioCaptureService /
    // TestSettingsService / StubOptionsMonitor は TestDoubles.cs に共通化 (rere v1.0.32 #B2-004)。

    // ═══════════════════════════════════════════════════════════════
    // IsSentenceBoundaryAt の単体テスト (数字小数点の誤分割対策)
    // ═══════════════════════════════════════════════════════════════
    //
    // 経緯 (2026-05-18 ゆろさん報告):
    // 「6.3インチ」「3.14は円周率」のような小数点入り数字が「6.」「3インチ」と
    // 2 つの字幕に分割される問題。 SentenceTerminators に '.' が含まれているため、
    // 半角ピリオドは何でも句点扱いされていた。 IsSentenceBoundaryAt で前後文字を見て
    // 小数点を保護する + delta 中の末尾ピリオドは保留 (続きを待つ) ように対策。

    [TestMethod]
    [TestCategory("SentenceBoundary")]
    public void IsSentenceBoundaryAt_FullWidthPeriod_IsBoundary()
    {
        Assert.IsTrue(TranslationPipelineService.IsSentenceBoundaryAt("こんにちは。", 5, isFinalContext: false));
        Assert.IsTrue(TranslationPipelineService.IsSentenceBoundaryAt("こんにちは。", 5, isFinalContext: true));
    }

    [TestMethod]
    [TestCategory("SentenceBoundary")]
    public void IsSentenceBoundaryAt_AsciiQuestionMark_IsBoundary()
    {
        Assert.IsTrue(TranslationPipelineService.IsSentenceBoundaryAt("hello?", 5, isFinalContext: false));
    }

    [TestMethod]
    [TestCategory("SentenceBoundary")]
    public void IsSentenceBoundaryAt_DecimalPointBetweenDigits_NotBoundary()
    {
        // 「6.3インチ」: index 1 の '.' は前=6 後=3 → 小数点として保護
        Assert.IsFalse(TranslationPipelineService.IsSentenceBoundaryAt("6.3インチ", 1, isFinalContext: false));
        Assert.IsFalse(TranslationPipelineService.IsSentenceBoundaryAt("6.3インチ", 1, isFinalContext: true));
    }

    [TestMethod]
    [TestCategory("SentenceBoundary")]
    public void IsSentenceBoundaryAt_PiNumber_NotBoundary()
    {
        // 「3.14は円周率」: index 1 の '.' は前=3 後=1 → 小数点
        Assert.IsFalse(TranslationPipelineService.IsSentenceBoundaryAt("3.14は円周率", 1, isFinalContext: false));
    }

    [TestMethod]
    [TestCategory("SentenceBoundary")]
    public void IsSentenceBoundaryAt_TrailingPeriodAfterDigit_DeltaContext_IsHeld()
    {
        // delta 受信中の「画面サイズは6.」: 末尾ピリオド + 直前数字 → 次 delta を待つため保留
        Assert.IsFalse(TranslationPipelineService.IsSentenceBoundaryAt("画面サイズは6.", 7, isFinalContext: false));
    }

    [TestMethod]
    [TestCategory("SentenceBoundary")]
    public void IsSentenceBoundaryAt_TrailingPeriodAfterDigit_FinalContext_IsBoundary()
    {
        // done 受信時の「画面サイズは6.」: もう続きは来ないので区切る
        Assert.IsTrue(TranslationPipelineService.IsSentenceBoundaryAt("画面サイズは6.", 7, isFinalContext: true));
    }

    [TestMethod]
    [TestCategory("SentenceBoundary")]
    public void IsSentenceBoundaryAt_PeriodAfterDigitBeforeNonDigit_IsBoundary()
    {
        // 「価格は500.です」: index 6 の '.' は前=0 後='で' (非数字) → 通常の句点扱い (区切る)
        Assert.IsTrue(TranslationPipelineService.IsSentenceBoundaryAt("価格は500.です", 6, isFinalContext: false));
    }

    [TestMethod]
    [TestCategory("SentenceBoundary")]
    public void IsSentenceBoundaryAt_EnglishSentenceEnd_IsBoundary()
    {
        // 「This is a test. Next.」: index 14 の '.' は前='t' (非数字) → 通常の句点扱い
        Assert.IsTrue(TranslationPipelineService.IsSentenceBoundaryAt("This is a test. Next.", 14, isFinalContext: false));
    }

    [TestMethod]
    [TestCategory("SentenceBoundary")]
    public void IsSentenceBoundaryAt_OutOfRange_ReturnsFalse()
    {
        Assert.IsFalse(TranslationPipelineService.IsSentenceBoundaryAt("abc", -1, isFinalContext: false));
        Assert.IsFalse(TranslationPipelineService.IsSentenceBoundaryAt("abc", 3, isFinalContext: false));
        Assert.IsFalse(TranslationPipelineService.IsSentenceBoundaryAt("abc", 100, isFinalContext: false));
    }

    [TestMethod]
    [TestCategory("SentenceBoundary")]
    public void IsSentenceBoundaryAt_DecimalAtEndOfFinalContext_IsBoundaryOnLastDigit()
    {
        // 「価格は5.」 done 文脈: index 4 の '.' 前=5 末尾 → done なので区切る
        Assert.IsTrue(TranslationPipelineService.IsSentenceBoundaryAt("価格は5.", 4, isFinalContext: true));
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
                OutputLanguage = "ja",
                // 既存テストは MaxPartialChars=80 ベースの境界値で書かれているため、
                // default 変更 (v1.0.31 で 80→50) に追従させず specific value で固定する。
                MaxPartialChars = 80,
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

    /// <test rere="B3-010" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnTranscriptDelta_SingleSentenceWithPeriod_EmitsFinalOnce()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("こんにちは。");

        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count, "句点ありの delta で IsFinal=true が 1 件 emit されるはず");
        Assert.AreEqual("こんにちは。", finals[0].TranslatedText);
    }

    /// <test rere="B3-010" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnTranscriptDelta_MultipleSentences_EmitsSeparateSegmentIds()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("こんにちは。お元気で。");

        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(2, finals.Count, "句点 2 つで完結文が 2 件 emit されるはず");
        Assert.AreEqual("こんにちは。", finals[0].TranslatedText);
        Assert.AreEqual("お元気で。", finals[1].TranslatedText);
        Assert.AreNotEqual(finals[0].SegmentId, finals[1].SegmentId, "各完結文は別 SegmentId で emit されるはず");
    }

    /// <test rere="P0 #1 (B2-C1 / D-1)" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnTranscriptCompleted_PrefixMismatch_SkipsWithoutEmit()
    {
        // 1 回目の delta で _lastFinalizedTranscript = "今日は良い天気です。"
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("今日は良い天気です。");
        Assert.AreEqual(1, emitted.Count(x => x.IsFinal), "1 回目で 1 件");

        // 2 回目: 新セッション風の done (prefix が一致しない) を投げる
        emitted.Clear();
        transcriber.RaiseDone("全く違う内容の文。");

        // 旧設計だと "全く違う内容の文。" が IsFinal=true で再 emit されていた (重複)。
        // P0 #1 修正後は skip + Warn ログのみ
        Assert.AreEqual(0, emitted.Count(x => x.IsFinal),
            "prefix 不一致時は skip され、 IsFinal emit はゼロのはず (rere P0 #1)");
    }

    /// <test rere="P0 #1 (再接続時リセット)" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnConnectionStateChanged_ReconnectingToConnected_ResetsFinalizedTranscript()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("旧セッションの文。");
        Assert.AreEqual(1, emitted.Count(x => x.IsFinal), "旧セッションで 1 件 emit");

        // 再接続シミュレート: Connected → Reconnecting → Connected
        transcriber.RaiseStateChanged(ConnectionState.Reconnecting);
        transcriber.RaiseStateChanged(ConnectionState.Connected);

        // 新セッションで新規 transcript を受領
        emitted.Clear();
        transcriber.RaiseDelta("新セッションの文。");

        // リセットされていれば prefix mismatch にならず、 そのまま 1 件 emit されるはず
        Assert.AreEqual(1, emitted.Count(x => x.IsFinal),
            "再接続後は _lastFinalizedTranscript リセットで新セッションが正常に動くはず");
        Assert.AreEqual("新セッションの文。", emitted.First(x => x.IsFinal).TranslatedText);
    }

    /// <test rere="#D-002 (v1.0.28 拡張)" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnConnectionStateChanged_DisconnectedToConnected_ResetsFinalizedTranscript()
    {
        // /rere #D-002: NW スタック種別によっては Reconnecting を経由せず
        // Disconnected → Connected の直接遷移が発生する。 旧 v1.0.27 はこの経路で
        // _lastFinalizedTranscript が残り字幕完全停止していた (再起動以外復旧不能)。
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("旧セッションの文。");
        Assert.AreEqual(1, emitted.Count(x => x.IsFinal), "旧セッションで 1 件 emit");

        // Disconnected → Connected 直接遷移 (Reconnecting を経由しない)
        transcriber.RaiseStateChanged(ConnectionState.Disconnected);
        transcriber.RaiseStateChanged(ConnectionState.Connected);

        emitted.Clear();
        transcriber.RaiseDelta("新セッションの文。");

        Assert.AreEqual(1, emitted.Count(x => x.IsFinal),
            "Disconnected → Connected 直接遷移でも _lastFinalizedTranscript はリセットされるべき (#D-002)");
        Assert.AreEqual("新セッションの文。", emitted.First(x => x.IsFinal).TranslatedText);
    }

    /// <test rere="#D-002 (v1.0.28 拡張)" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnConnectionStateChanged_FailedToConnected_ResetsFinalizedTranscript()
    {
        // /rere #D-002: Failed → Connected の経路 (NW 復帰ハンドラからの直接再接続) でもリセット必須
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("旧セッションの文。");
        Assert.AreEqual(1, emitted.Count(x => x.IsFinal));

        transcriber.RaiseStateChanged(ConnectionState.Failed);
        transcriber.RaiseStateChanged(ConnectionState.Connected);

        emitted.Clear();
        transcriber.RaiseDelta("新セッションの文。");

        Assert.AreEqual(1, emitted.Count(x => x.IsFinal),
            "Failed → Connected でも _lastFinalizedTranscript はリセットされるべき (#D-002)");
        Assert.AreEqual("新セッションの文。", emitted.First(x => x.IsFinal).TranslatedText);
    }

    /// <test rere="#D-002 (v1.0.28 拡張)" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnConnectionStateChanged_ConnectingToConnected_ResetsFinalizedTranscript()
    {
        // /rere #D-002: 初回接続 (Disconnected → Connecting → Connected) もリセットを通す。
        // 初回は _lastFinalizedTranscript が空のため Clear() は no-op だが、 後続の挙動を統一する。
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("旧セッションの文。");
        Assert.AreEqual(1, emitted.Count(x => x.IsFinal));

        transcriber.RaiseStateChanged(ConnectionState.Connecting);
        transcriber.RaiseStateChanged(ConnectionState.Connected);

        emitted.Clear();
        transcriber.RaiseDelta("新セッションの文。");

        Assert.AreEqual(1, emitted.Count(x => x.IsFinal),
            "Connecting → Connected でも _lastFinalizedTranscript はリセットされるべき (#D-002)");
        Assert.AreEqual("新セッションの文。", emitted.First(x => x.IsFinal).TranslatedText);
    }

    /// <test rere="#D-002 (v1.0.28 拡張)" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnConnectionStateChanged_ConnectedToConnected_DoesNotReset()
    {
        // Connected → Connected (idle 再通知) では Clear しない (リセット過剰防止)
        var (pipeline, transcriber, emitted) = CreatePipeline();
        // 初回接続トリガー
        transcriber.RaiseStateChanged(ConnectionState.Connected);
        transcriber.RaiseDelta("文1。");
        Assert.AreEqual(1, emitted.Count(x => x.IsFinal));

        // 同一 Connected 再通知 (StateChanged ハンドラで idle イベント発火するケース想定)
        transcriber.RaiseStateChanged(ConnectionState.Connected);
        // 続きの delta が prefix mismatch にならず emit されるはず
        emitted.Clear();
        transcriber.RaiseDelta("文2。");

        Assert.AreEqual(1, emitted.Count(x => x.IsFinal),
            "Connected → Connected (idle) ではリセットしないため、 連続した delta は正常に emit されるはず");
        Assert.AreEqual("文2。", emitted.First(x => x.IsFinal).TranslatedText);
    }

    // v1.0.27 棚卸しで一度削除されたが、 2026-05-24 ARC Raiders 実機ログで
    // 「partial が 127 文字まで育って完結文 emit=0」が観測されたため v1.0.28 で復活。
    // /rere レビュー #D-005 (句点なしコンテンツで _accumulatedText 無限成長) の安全弁。

    /// <test rere="B3-010" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnTranscriptDelta_NoTerminator_EmitsPartialOnly()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("句点なし短文");

        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(0, finals.Count, "句点なし短文では IsFinal emit ゼロのはず");
        // partial は throttle で発火するため、 即時には emit されないこともある
    }

    // ═══════════════════════════════════════════════════════════════
    // FindForcedSplitIndex の単体テスト (v1.0.28 D-7 fallback 復活、 /rere #D-005)
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("ForcedSplit")]
    public void FindForcedSplitIndex_BelowThreshold_Returns0()
    {
        var sb = new System.Text.StringBuilder("短い文");
        Assert.AreEqual(0, TranslationPipelineService.FindForcedSplitIndex(sb, maxChars: 80));
    }

    [TestMethod]
    [TestCategory("ForcedSplit")]
    public void FindForcedSplitIndex_MaxCharsZeroOrNegative_Returns0()
    {
        var sb = new System.Text.StringBuilder(new string('a', 200));
        Assert.AreEqual(0, TranslationPipelineService.FindForcedSplitIndex(sb, maxChars: 0));
        Assert.AreEqual(0, TranslationPipelineService.FindForcedSplitIndex(sb, maxChars: -5));
    }

    [TestMethod]
    [TestCategory("ForcedSplit")]
    public void FindForcedSplitIndex_FindsJapaneseCommaInLookback()
    {
        // 全角 70 文字 + 「、」+ 全角 15 文字 (合計 86 文字、 maxChars=80)
        // 末尾 30 文字以内 (= index 50 以降) に「、」が index 70 にある → splitIdx = 71
        var sb = new System.Text.StringBuilder(new string('あ', 70) + "、" + new string('い', 15));
        int idx = TranslationPipelineService.FindForcedSplitIndex(sb, maxChars: 80);
        Assert.AreEqual(71, idx, "「、」の直後 (71) で分割されるはず");
        Assert.AreEqual('、', sb[idx - 1]);
    }

    [TestMethod]
    [TestCategory("ForcedSplit")]
    public void FindForcedSplitIndex_FindsAsciiCommaInLookback()
    {
        // 60 文字 + 半角 ',' + 25 文字 (合計 86 文字)
        // 末尾 30 文字以内に ',' があるので index 60+1=61 で分割
        var sb = new System.Text.StringBuilder(new string('a', 60) + "," + new string('b', 25));
        int idx = TranslationPipelineService.FindForcedSplitIndex(sb, maxChars: 80);
        Assert.AreEqual(61, idx, "半角 ',' の直後 (61) で分割");
    }

    [TestMethod]
    [TestCategory("ForcedSplit")]
    public void FindForcedSplitIndex_FindsHalfWidthSpace()
    {
        var sb = new System.Text.StringBuilder(new string('x', 60) + " " + new string('y', 25));
        int idx = TranslationPipelineService.FindForcedSplitIndex(sb, maxChars: 80);
        Assert.AreEqual(61, idx, "半角空白の直後 (61) で分割");
    }

    [TestMethod]
    [TestCategory("ForcedSplit")]
    public void FindForcedSplitIndex_FindsFullWidthSpace()
    {
        var sb = new System.Text.StringBuilder(new string('あ', 60) + "　" + new string('い', 25));
        int idx = TranslationPipelineService.FindForcedSplitIndex(sb, maxChars: 80);
        Assert.AreEqual(61, idx, "全角空白の直後 (61) で分割");
    }

    [TestMethod]
    [TestCategory("ForcedSplit")]
    public void FindForcedSplitIndex_NoCandidate_ReturnsMaxChars()
    {
        // 句読点も空白も無い 100 文字 → maxChars (80) で強制切断
        var sb = new System.Text.StringBuilder(new string('a', 100));
        Assert.AreEqual(80, TranslationPipelineService.FindForcedSplitIndex(sb, maxChars: 80));
    }

    [TestMethod]
    [TestCategory("ForcedSplit")]
    public void FindForcedSplitIndex_CommaOutsideLookback_ReturnsMaxChars()
    {
        // 「、」は index 10 (lookback 範囲 50- から外れる) → 候補にならない → maxChars (80) で強制切断
        var sb = new System.Text.StringBuilder(new string('a', 10) + "、" + new string('b', 90));
        Assert.AreEqual(80, TranslationPipelineService.FindForcedSplitIndex(sb, maxChars: 80));
    }

    [TestMethod]
    [TestCategory("ForcedSplit")]
    public void FindForcedSplitIndex_CommaPreferredOverSpace()
    {
        // 「、」は index 55, 空白は index 70 → 「、」(より新しいループは「、」を先に探す→空白は探索しない)
        var sb = new System.Text.StringBuilder(new string('a', 55) + "、" + new string('b', 14) + " " + new string('c', 15));
        int idx = TranslationPipelineService.FindForcedSplitIndex(sb, maxChars: 80);
        Assert.AreEqual(56, idx, "「、」(index 55) が空白 (index 70) より優先される");
    }

    [TestMethod]
    [TestCategory("ForcedSplit")]
    public void FindForcedSplitIndex_NullStringBuilder_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            TranslationPipelineService.FindForcedSplitIndex(null!, maxChars: 80));
    }

    // ═══════════════════════════════════════════════════════════════
    // OnTranscriptDelta + D-7 fallback の統合テスト
    // ═══════════════════════════════════════════════════════════════

    /// <test rere="#D-005 (v1.0.28 復活)" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnTranscriptDelta_NoTerminatorButCommaAt80_ForcedSplitOnComma()
    {
        // ARC Raiders 実機シナリオ再現: 句点なし、 80 文字超で「、」がある場所で強制分割
        // 70 文字 + 「、」+ 残り 15 文字 = 86 文字 (>= 80) → D-7 fallback で「、」直後 (71) 分割
        var (pipeline, transcriber, emitted) = CreatePipeline();
        var input = new string('あ', 70) + "、" + new string('い', 15);
        transcriber.RaiseDelta(input);

        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count, "D-7 fallback で 1 件強制分割 emit されるはず");
        Assert.AreEqual(new string('あ', 70) + "、", finals[0].TranslatedText, "「、」直後で分割");
    }

    /// <test rere="#D-005 (v1.0.28 復活)" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnTranscriptDelta_NoTerminatorNoComma_ForcedSplitAtMaxChars()
    {
        // 句読点も空白も無い 100 文字 → maxChars (80) 位置で強制切断
        var (pipeline, transcriber, emitted) = CreatePipeline();
        var input = new string('あ', 100);
        transcriber.RaiseDelta(input);

        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count, "強制切断で 1 件 emit");
        Assert.AreEqual(80, finals[0].TranslatedText.Length, "maxChars (80) 位置で切断");
    }

    /// <test rere="#D-005 (v1.0.28 復活)" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnTranscriptDelta_BelowThreshold_NoForcedSplit()
    {
        // 79 文字 (< 80) → D-7 fallback 発火しない
        var (pipeline, transcriber, emitted) = CreatePipeline();
        var input = new string('あ', 79);
        transcriber.RaiseDelta(input);

        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(0, finals.Count, "閾値未満では強制分割しない");
    }

    // ═══════════════════════════════════════════════════════════════
    // delta アイドル確定 (DeltaIdleFinalizeMs) の統合テスト
    // ═══════════════════════════════════════════════════════════════
    //
    // 経緯 (2026-07-29 ゆろさん実機ログ):
    // ARC Raiders で 4 人の別発話が 1 SegmentId に 112 秒間連結した
    // 「この格好ねオーケー、ありがとうこんにちは、ハロー目的地へ進んでください収縮した店...」を観測。
    // server が句点を返さず (読点のみ)、 累積 46 文字で MaxPartialChars=50 に未到達、
    // transcript.done も来ないという三重の取りこぼしで確定 emit が 0 件だった。
    // 発話間には 24.7 / 9.2 / 48.4 / 25.7 秒の無音があったので、 delta 途絶を境界として使う。

    /// <summary>
    /// アイドル確定のテスト用パイプライン。 タイマー発火を待つのでテスト時間を詰めるため
    /// 閾値を ms 単位まで下げ、 D-7 fallback (MaxPartialChars) は十分大きくして
    /// 「アイドル確定だけが効いている」ことを保証する。
    /// <para>
    /// <c>SilencePaddingMs = 0</c> にしているのは <see cref="TranslationPipelineService.ResolveIdleFinalizeMs"/>
    /// のクランプ (padding + 1000ms への引き上げ) を外して、 短い閾値をそのまま効かせるため。
    /// クランプ自体は <c>ResolveIdleFinalizeMs_*</c> の単体テストで別途検証する。
    /// </para>
    /// </summary>
    private static (TranslationPipelineService pipeline, TestRealtimeTranscriber transcriber, Func<List<SubtitleItem>> snapshot)
        CreateIdlePipeline(int idleMs)
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
                OutputLanguage = "ja",
                // D-7 fallback を実質無効化して、 確定要因をアイドルだけに限定する。
                MaxPartialChars = 10000,
                SilencePaddingMs = 0,
                DeltaIdleFinalizeMs = idleMs,
            }
        };
        var monitor = new StubOptionsMonitor(settings);
        var settingsService = new TestSettingsService();
        var vad = new TestVoiceActivityDetector();
        var pipeline = new TranslationPipelineService(audio, transcriber, monitor, settingsService, vad);

        // emit はタイマーのスレッドプールスレッドからも来るので lock で保護する。
        var gate = new object();
        var emitted = new List<SubtitleItem>();
        pipeline.SubtitleGenerated += (_, item) =>
        {
            lock (gate) emitted.Add(item);
        };
        List<SubtitleItem> Snapshot()
        {
            lock (gate) return [.. emitted];
        }
        return (pipeline, transcriber, Snapshot);
    }

    /// <summary>確定字幕が expected 件に達するまで待つ (タイムアウトしたら現状のまま返す)。</summary>
    private static async Task<List<SubtitleItem>> WaitForFinalsAsync(
        Func<List<SubtitleItem>> snapshot, int expected, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        List<SubtitleItem> finals;
        do
        {
            finals = snapshot().Where(x => x.IsFinal).ToList();
            if (finals.Count >= expected) return finals;
            await Task.Delay(20);
        } while (Environment.TickCount64 < deadline);
        return finals;
    }

    [TestMethod]
    [TestCategory("SentenceSplit")]
    public async Task OnTranscriptDelta_IdleBetweenUtterances_SplitsIntoSeparateSegments()
    {
        // 実機ログ再現: 句点なし・50 文字未満の 4 発話が無音を挟んで届く。
        // 旧実装では 1 セグメントに連結していた。
        const int idleMs = 120;
        var (pipeline, transcriber, snapshot) = CreateIdlePipeline(idleMs);

        var utterances = new[] { "この格好ねオーケー、", "ありがとう", "こんにちは、ハロー", "目的地へ進んでください" };
        foreach (var u in utterances)
        {
            transcriber.RaiseDelta(u);
            // 発話間の無音 (実機では 9〜48 秒) を閾値超えの待機で模擬する。
            await Task.Delay(idleMs * 4);
        }

        var finals = await WaitForFinalsAsync(snapshot, utterances.Length);
        Assert.AreEqual(utterances.Length, finals.Count,
            "発話ごとに delta が途絶えるので、 発話数と同じ件数の確定字幕になるはず");
        CollectionAssert.AreEqual(utterances, finals.Select(x => x.TranslatedText).ToArray(),
            "各確定字幕は発話単位のテキストになるはず (連結しない)");
        Assert.AreEqual(utterances.Length, finals.Select(x => x.SegmentId).Distinct().Count(),
            "確定のたびに新しい SegmentId が発行されるはず");
    }

    [TestMethod]
    [TestCategory("SentenceSplit")]
    public async Task OnTranscriptDelta_ContinuousDeltas_DoesNotSplitMidUtterance()
    {
        // 生成が続いている間 (delta 間隔 < 閾値) は貼り直され続けるので、 文の途中で切れないこと。
        // v1.0.24 の「セグメント最大寿命タイマー」が起こした分断の再発防止。
        const int idleMs = 300;
        var (pipeline, transcriber, snapshot) = CreateIdlePipeline(idleMs);

        foreach (var chunk in new[] { "目的", "地へ", "進んで", "ください" })
        {
            transcriber.RaiseDelta(chunk);
            await Task.Delay(idleMs / 5);
        }

        // まだ閾値に達していないので確定は 0 件のはず。
        Assert.AreEqual(0, snapshot().Count(x => x.IsFinal),
            "delta が続いている間はアイドル確定が発火しないはず");

        // 途絶えたら 1 件だけ、 連結された 1 発話として確定する。
        var finals = await WaitForFinalsAsync(snapshot, 1);
        Assert.AreEqual(1, finals.Count, "途絶後に 1 件だけ確定するはず");
        Assert.AreEqual("目的地へ進んでください", finals[0].TranslatedText,
            "連続 delta は 1 発話としてまとまるはず");
    }

    [TestMethod]
    [TestCategory("SentenceSplit")]
    public async Task DeltaIdleFinalizeMs_Zero_DisablesIdleFinalize()
    {
        // 0 以下で無効 (v1.0.48 以前の挙動)。 緊急時の逃げ道として維持する。
        var (pipeline, transcriber, snapshot) = CreateIdlePipeline(idleMs: 0);
        transcriber.RaiseDelta("句点のない未確定テキスト");

        await Task.Delay(300);

        Assert.AreEqual(0, snapshot().Count(x => x.IsFinal),
            "DeltaIdleFinalizeMs=0 ではアイドル確定しないはず");
    }

    // ───────── ResolveIdleFinalizeMs のクランプ (ロケット鉛筆方式との整合) ─────────
    //
    // 不変条件: アイドル確定は **無音 padding の外側**で起きる。
    // OpenAI Realtime は発音後に無音を付けないと後続音声に押し出されるまで出力が止まる
    // (ロケット鉛筆方式)。 padding 中に確定すると、 押し出しで後から出てくる残りが別 SegmentId に
    // 飛んで v1.0.26 の「戦略 A: 分断」を再発させる。

    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void ResolveIdleFinalizeMs_ShorterThanPadding_ClampedOutsidePadding()
    {
        // padding 内側の値 (1500ms < 5000ms) は padding + margin まで引き上げる
        Assert.AreEqual(6000, TranslationPipelineService.ResolveIdleFinalizeMs(1500, silencePaddingMs: 5000));
    }

    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void ResolveIdleFinalizeMs_DefaultWithDefaultPadding_StaysOutsidePadding()
    {
        // 既定同士 (6000 / 5000) では既定値がそのまま通り、 かつ padding より必ず長い
        var resolved = TranslationPipelineService.ResolveIdleFinalizeMs(6000, silencePaddingMs: 5000);
        Assert.AreEqual(6000, resolved);
        Assert.IsTrue(resolved > 5000, "実効閾値は無音 padding より長いはず");
    }

    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void ResolveIdleFinalizeMs_LongerPadding_FollowsPadding()
    {
        // padding を 8000ms に戻した場合 (v1.0.33-35 の旧 default) も自動追従する
        Assert.AreEqual(9000, TranslationPipelineService.ResolveIdleFinalizeMs(6000, silencePaddingMs: 8000));
    }

    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void ResolveIdleFinalizeMs_Disabled_StaysDisabled()
    {
        // 0 以下 = 明示的な無効化。 クランプで復活させない
        Assert.AreEqual(0, TranslationPipelineService.ResolveIdleFinalizeMs(0, silencePaddingMs: 5000));
        Assert.AreEqual(0, TranslationPipelineService.ResolveIdleFinalizeMs(-1, silencePaddingMs: 5000));
    }

    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void ResolveIdleFinalizeMs_PaddingDisabled_UsesConfiguredValue()
    {
        // padding 無効時はクランプ不要 (押し出し待ち時間が存在しない)
        Assert.AreEqual(1500, TranslationPipelineService.ResolveIdleFinalizeMs(1500, silencePaddingMs: 0));
    }
}
