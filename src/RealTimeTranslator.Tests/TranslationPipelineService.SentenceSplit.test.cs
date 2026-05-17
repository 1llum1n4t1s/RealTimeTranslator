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

    private sealed class TestAudioCaptureService : IAudioCaptureService
    {
        public bool IsCapturing => false;
        public bool HasReceivedNonSilentDataSinceStart => false;
        public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
        public event EventHandler<CaptureStatusEventArgs>? CaptureStatusChanged;
        public void StartCapture(int processId) { }
        public Task<bool> StartCaptureWithRetryAsync(int processId, CancellationToken cancellationToken, SynchronizationContext? captureCreationContext = null) => Task.FromResult(true);
        public void StopCapture() { }
        public void ApplySettings(AudioCaptureSettings settings) { }
        public void Dispose() { }
    }

    private sealed class TestSettingsService : ISettingsService
    {
        public Task SaveAsync(AppSettings settings) => Task.CompletedTask;
        public void DecryptApiKey(AppSettings settings) { /* テストでは復号不要 */ }
    }

    private sealed class StubOptionsMonitor : IOptionsMonitor<AppSettings>
    {
        public AppSettings CurrentValue { get; }
        public StubOptionsMonitor(AppSettings settings) { CurrentValue = settings; }
        public AppSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppSettings, string?> listener) => null;
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
        var pipeline = new TranslationPipelineService(audio, transcriber, monitor, settingsService);
        var emitted = new List<SubtitleItem>();
        pipeline.SubtitleGenerated += (_, item) => emitted.Add(item);
        return (pipeline, transcriber, emitted);
    }

    /// <test description="SentencesPerSegment=2: 1 句点だけでは pending に蓄積されるのみで確定 emit しない" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnTranscriptDelta_SingleSentence_AccumulatesInPendingWithoutFinalEmit()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("こんにちは。");

        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(0, finals.Count,
            "SentencesPerSegment=2 のため、1 句点では確定 emit されず pending に蓄積されるはず");
    }

    /// <test description="SentencesPerSegment=2: 2 句点で 1 セグメントとして連結 emit (細切れ抑制)" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnTranscriptDelta_TwoSentences_EmitsCombinedAsOneSegment()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("こんにちは。お元気で。");

        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count,
            "SentencesPerSegment=2 で 2 句点ヒット → 1 セグメントとして 1 件 emit");
        Assert.AreEqual("こんにちは。お元気で。", finals[0].TranslatedText,
            "2 文が連結されて 1 件として emit されるはず");
    }

    /// <test description="SentencesPerSegment=2: 4 句点で 2 セグメントに分割" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnTranscriptDelta_FourSentences_EmitsTwoSeparateSegments()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("こんにちは。お元気で。今日は晴れ。明日も晴れ。");

        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(2, finals.Count, "4 句点で 2 セグメントが emit されるはず");
        Assert.AreEqual("こんにちは。お元気で。", finals[0].TranslatedText);
        Assert.AreEqual("今日は晴れ。明日も晴れ。", finals[1].TranslatedText);
        Assert.AreNotEqual(finals[0].SegmentId, finals[1].SegmentId,
            "各 2 文セグメントは別 SegmentId で emit されるはず");
    }

    /// <test rere="P0 #1 (B2-C1 / D-1)" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnTranscriptCompleted_PrefixMismatch_SkipsWithoutEmit()
    {
        // 1 回目の delta で 2 文 → 1 セグメント emit、 _lastFinalizedTranscript = "今日は良い天気です。本当に。"
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("今日は良い天気です。本当に。");
        Assert.AreEqual(1, emitted.Count(x => x.IsFinal), "1 セグメント (2 文連結) で 1 件 emit");

        // 2 回目: 新セッション風の done (prefix が一致しない) を投げる
        emitted.Clear();
        transcriber.RaiseDone("全く違う内容の文。本当に違う。");

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
        transcriber.RaiseDelta("旧セッションの文。これは旧。");
        Assert.AreEqual(1, emitted.Count(x => x.IsFinal), "旧セッションで 1 件 emit (2 文連結)");

        // 再接続シミュレート: Connected → Reconnecting → Connected
        transcriber.RaiseStateChanged(ConnectionState.Reconnecting);
        transcriber.RaiseStateChanged(ConnectionState.Connected);

        // 新セッションで新規 transcript を受領
        emitted.Clear();
        transcriber.RaiseDelta("新セッションの文。これは新。");

        // リセットされていれば prefix mismatch にならず、 そのまま 1 件 emit されるはず
        Assert.AreEqual(1, emitted.Count(x => x.IsFinal),
            "再接続後は _lastFinalizedTranscript リセットで新セッションが正常に動くはず");
        Assert.AreEqual("新セッションの文。これは新。", emitted.First(x => x.IsFinal).TranslatedText);
    }

    /// <test rere="D-7 句読点 fallback" />
    [TestMethod]
    [TestCategory("SentenceSplit")]
    public void OnTranscriptDelta_LongMachineGunTalk_FallbackSplitOnComma()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        // 句点なし 150 文字超 (FallbackSplitThreshold = 150) で「、」を含む長文
        var longText = new string('あ', 80) + "、" + new string('い', 80);
        transcriber.RaiseDelta(longText);

        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.IsTrue(finals.Count >= 1,
            "150 文字超で「、」 fallback 分割が走り完結文 emit されるはず (D-7 + SentencesPerSegment 未達でも強制 emit)");
        // 1 文目に「、」が含まれることを確認
        StringAssert.EndsWith(finals[0].TranslatedText, "、");
    }

    /// <test description="句点なし短文では emit ゼロ (D-7 閾値未満 + SentencesPerSegment 未達)" />
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
