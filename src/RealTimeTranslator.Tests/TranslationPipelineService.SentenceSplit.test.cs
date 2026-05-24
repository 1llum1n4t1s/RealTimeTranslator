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
    private sealed class TestRealtimeTranscriber : IRealtimeTranscriber
    {
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public long TotalAudioInputSamples24kHz => 0;
        public long ServerReportedAudioInputTokens => 0;
        public event Action<string>? TranscriptDeltaReceived;
        public event Action<string>? TranscriptCompleted;
        public event Action<Exception>? ErrorReceived;
        public event Action<ConnectionState>? StateChanged;

        public Task ConnectAsync(OpenAIRealtimeSettings settings, CancellationToken ct = default)
        {
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }
        public void SendAudio(byte[] pcm16Audio) { }
        public void SendCommit() { }
        public Task DisconnectAsync()
        {
            State = ConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }

        public void RaiseDelta(string delta) => TranscriptDeltaReceived?.Invoke(delta);
        public void RaiseDone(string transcript) => TranscriptCompleted?.Invoke(transcript);
        public void RaiseStateChanged(ConnectionState newState)
        {
            State = newState;
            StateChanged?.Invoke(newState);
        }
    }

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

    // VAD ゲート統合後、 TranslationPipelineService が IVoiceActivityDetector を要求するための
    // no-op 実装。 既存テストは ProcessAudioLoopAsync を経由しない (RaiseDelta/RaiseDone 直接呼び)
    // ので DetectSpeechProb は呼ばれない。
    private sealed class TestVoiceActivityDetector : IVoiceActivityDetector
    {
        public int RequiredFrameSize => 512;
        public int SampleRate => 16000;
        public float DetectSpeechProb(ReadOnlySpan<float> frame16kHz) => 0f;
        public void Reset() { }
        public void Dispose() { }
    }

    // TestAudioCaptureService / TestSettingsService / StubOptionsMonitor は TestDoubles.cs に共通化。

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

    /// <test rere="D-7 句読点 fallback" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnTranscriptDelta_LongMachineGunTalk_FallbackSplitOnComma()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        // 句点なし 100 文字超で「、」を含む長文
        var longText = new string('あ', 50) + "、" + new string('い', 60);
        transcriber.RaiseDelta(longText);

        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.IsTrue(finals.Count >= 1,
            "100 文字超で「、」 fallback 分割が走り完結文 emit されるはず (rere D-7)");
        // 1 文目に「、」が含まれることを確認
        StringAssert.EndsWith(finals[0].TranslatedText, "、");
    }

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
}
