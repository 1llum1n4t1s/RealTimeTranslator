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
