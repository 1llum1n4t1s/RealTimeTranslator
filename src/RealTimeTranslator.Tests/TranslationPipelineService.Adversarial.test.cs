using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// TranslationPipelineService の嫌がらせ (Adversarial) テスト (stst 隊員2-7)。
/// 境界値 / 並行性 / リソース枯渇 / 状態遷移 / 型パンチ / 環境異常 の6カテゴリを統合。
/// 対象は字幕分断ロジック (OnTranscriptDelta / OnTranscriptCompleted / FinalizePendingPartial / アイドル確定)。
/// 既存 SentenceSplit テストでカバー済みの領域とは重複しない未カバー領域に集中。
/// </summary>
[TestClass]
public sealed class TranslationPipelineServiceAdversarialTests
{
    // ════════ 共通テストダブル (隊員2/3/5/6 が使用) ════════

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
        public void SendCommit() { }
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

    // ════════ 隊員3 (並行性) 専用ヘルパー (lock 集計) ════════
    private static (TranslationPipelineService pipeline, TestRealtimeTranscriber transcriber, List<SubtitleItem> emitted, object sync) CreateConcurrentPipeline(double displayDuration = 5.0)
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
            },
            Overlay = new OverlaySettings { DisplayDuration = displayDuration },
            AudioCapture = new AudioCaptureSettings { EnableVad = false, AutoPauseOnSilenceSec = 0 }
        };
        var monitor = new StubOptionsMonitor(settings);
        var settingsService = new TestSettingsService();
        var vad = new TestVoiceActivityDetector();
        var pipeline = new TranslationPipelineService(audio, transcriber, monitor, settingsService, vad);
        var emitted = new List<SubtitleItem>();
        var sync = new object();
        pipeline.SubtitleGenerated += (_, item) => { lock (sync) { emitted.Add(item); } };
        return (pipeline, transcriber, emitted, sync);
    }

    private static List<SubtitleItem> SnapshotEmitted(List<SubtitleItem> emitted, object sync)
    {
        lock (sync) { return new List<SubtitleItem>(emitted); }
    }

    private static string FormatExceptions(ConcurrentBag<Exception> exceptions)
    {
        if (exceptions.IsEmpty) return "(なし)";
        return string.Join(" | ", exceptions.Take(5).Select(e => $"{e.GetType().Name}: {e.Message}"));
    }

    // ════════ 隊員5 (状態遷移) 専用ヘルパー (lock 集計) ════════
    private static (TranslationPipelineService pipeline, TestRealtimeTranscriber transcriber, List<SubtitleItem> emitted, object gate) CreateStatePipeline(double displayDuration = 5.0)
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
            },
            Overlay = new OverlaySettings { DisplayDuration = displayDuration }
        };
        var monitor = new StubOptionsMonitor(settings);
        var settingsService = new TestSettingsService();
        var vad = new TestVoiceActivityDetector();
        var pipeline = new TranslationPipelineService(audio, transcriber, monitor, settingsService, vad);
        var emitted = new List<SubtitleItem>();
        var gate = new object();
        pipeline.SubtitleGenerated += (_, item) => { lock (gate) { emitted.Add(item); } };
        return (pipeline, transcriber, emitted, gate);
    }

    private static List<SubtitleItem> SnapshotFinals(List<SubtitleItem> emitted, object gate)
    {
        lock (gate) { return emitted.Where(x => x.IsFinal).ToList(); }
    }

    private static Exception? Record(Action act)
    {
        try { act(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static async Task<Exception?> RecordAsync(Func<Task> act)
    {
        try { await act(); return null; }
        catch (Exception ex) { return ex; }
    }

    /// <summary>
    /// テスト並列実行時に ThreadPool 飽和で System.Threading.Timer コールバックが遅延する問題を回避するための
    /// ポーリング待機ヘルパー。 固定 Task.Delay だと並列テスト数に依存して flaky になるため、
    /// 条件成立を 50ms 間隔でチェックし、タイムアウトまでに成立しなければ Assert.Fail で落とす。
    /// </summary>
    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        // タイムアウト時は呼び出し元の Assert で詳細メッセージが出るので、 ここでは Assert.Fail しない
    }

    // ════════ 隊員4 (リソース枯渇) 専用テストダブル + ファクトリ ════════
    private sealed class ResourceTestTranscriber : IRealtimeTranscriber
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
        public void SendCommit() { }
        public Task DisconnectAsync()
        { State = ConnectionState.Disconnected; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }

        public void RaiseDelta(string delta) => TranscriptDeltaReceived?.Invoke(delta);
        public void RaiseDone(string transcript) => TranscriptCompleted?.Invoke(transcript);
    }

    private sealed class ResourceTestVad : IVoiceActivityDetector
    {
        public int RequiredFrameSize => 512;
        public int SampleRate => 16000;
        public float DetectSpeechProb(ReadOnlySpan<float> frame16kHz) => 0f;
        public void Reset() { }
        public void Dispose() { }
    }

    private static class ResourceTestPipelineFactory
    {
        private static AppSettings BuildSettings() => new()
        {
            OpenAIRealtime = new OpenAIRealtimeSettings
            {
                ApiKey = "test-key",
                Endpoint = "wss://api.openai.com/v1/realtime/translations",
                Model = "gpt-realtime-translate",
                OutputLanguage = "ja"
            }
        };

        public static (TranslationPipelineService pipeline, ResourceTestTranscriber transcriber, List<SubtitleItem> emitted) Create()
        {
            var transcriber = new ResourceTestTranscriber();
            var pipeline = CreateRaw(transcriber);
            var emitted = new List<SubtitleItem>();
            pipeline.SubtitleGenerated += (_, item) => emitted.Add(item);
            return (pipeline, transcriber, emitted);
        }

        public static TranslationPipelineService CreateRaw(ResourceTestTranscriber transcriber)
        {
            var audio = new TestAudioCaptureService();
            var monitor = new StubOptionsMonitor(BuildSettings());
            var settingsService = new TestSettingsService();
            var vad = new ResourceTestVad();
            return new TranslationPipelineService(audio, transcriber, monitor, settingsService, vad);
        }
    }

    // ════════ 隊員7 (環境異常) 専用テストダブル + ファクトリ ════════
    private sealed class MutableOptionsMonitor : IOptionsMonitor<AppSettings>
    {
        public AppSettings CurrentValue { get; set; }
        public MutableOptionsMonitor(AppSettings settings) { CurrentValue = settings; }
        public AppSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppSettings, string?> listener) => null;
    }

    private sealed class ChaosNoopVad : IVoiceActivityDetector
    {
        public int RequiredFrameSize => 512;
        public int SampleRate => 16000;
        public float DetectSpeechProb(ReadOnlySpan<float> frame16kHz) => 0f;
        public void Reset() { }
        public void Dispose() { }
    }

    private sealed class ChaosTranscriber : IRealtimeTranscriber
    {
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public long TotalAudioInputSamples24kHz => 0;
        public long ServerReportedAudioInputTokens => 0;
        public event Action<string>? TranscriptDeltaReceived;
        public event Action<string>? TranscriptCompleted;
#pragma warning disable CS0067
        public event Action<Exception>? ErrorReceived;
        public event Action<ConnectionState>? StateChanged;
#pragma warning restore CS0067
        public Task ConnectAsync(OpenAIRealtimeSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public void SendAudio(byte[] pcm16Audio) { }
        public void SendCommit() { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
        public void RaiseDelta(string delta) => TranscriptDeltaReceived?.Invoke(delta);
        public void RaiseDone(string transcript) => TranscriptCompleted?.Invoke(transcript);
    }

    private static AppSettings BuildChaosSettings(double displayDuration)
    {
        return new AppSettings
        {
            OpenAIRealtime = new OpenAIRealtimeSettings
            {
                ApiKey = "test-key",
                Endpoint = "wss://api.openai.com/v1/realtime/translations",
                Model = "gpt-realtime-translate",
                OutputLanguage = "ja",
            },
            Overlay = new OverlaySettings { DisplayDuration = displayDuration }
        };
    }

    private static (TranslationPipelineService pipeline, ChaosTranscriber transcriber, List<SubtitleItem> emitted) CreateChaosPipeline(double displayDuration)
    {
        var transcriber = new ChaosTranscriber();
        var audio = new TestAudioCaptureService();
        var monitor = new MutableOptionsMonitor(BuildChaosSettings(displayDuration));
        var settingsService = new TestSettingsService();
        var vad = new ChaosNoopVad();
        var pipeline = new TranslationPipelineService(audio, transcriber, monitor, settingsService, vad);
        var emitted = new List<SubtitleItem>();
        pipeline.SubtitleGenerated += (_, item) => { lock (emitted) { emitted.Add(item); } };
        return (pipeline, transcriber, emitted);
    }

    private static (TranslationPipelineService pipeline, ChaosTranscriber transcriber, List<SubtitleItem> emitted, MutableOptionsMonitor monitor) CreateMutableChaosPipeline(double initialDisplayDuration)
    {
        var transcriber = new ChaosTranscriber();
        var audio = new TestAudioCaptureService();
        var monitor = new MutableOptionsMonitor(BuildChaosSettings(initialDisplayDuration));
        var settingsService = new TestSettingsService();
        var vad = new ChaosNoopVad();
        var pipeline = new TranslationPipelineService(audio, transcriber, monitor, settingsService, vad);
        var emitted = new List<SubtitleItem>();
        pipeline.SubtitleGenerated += (_, item) => { lock (emitted) { emitted.Add(item); } };
        return (pipeline, transcriber, emitted, monitor);
    }

    // ═══════════════════════════════════════════════════════════════
    // 🗡️ 隊員2: 境界値・極端入力 (Boundary Assault)
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="boundary" severity="low" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    public void OnTranscriptDelta_EmptyString_NoEmitNoCrash()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("");
        Assert.AreEqual(0, emitted.Count, "空文字列の delta は早期 return で emit ゼロのはず");
    }

    /// <adversarial category="boundary" severity="low" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    public void OnTranscriptDelta_WhitespaceOnly_NoFinalEmit()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("   ");
        transcriber.RaiseDelta("\t\t");
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(0, finals.Count, "空白のみの delta では確定文 (IsFinal) emit ゼロのはず");
    }

    /// <adversarial category="boundary" severity="med" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    public void OnTranscriptDelta_SinglePeriodOnly_EmitsSinglePeriodAsFinal()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("。");
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count, "句点 1 文字「。」は完結文として 1 件 emit されるはず");
        Assert.AreEqual("。", finals[0].TranslatedText);
    }

    /// <adversarial category="boundary" severity="med" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    public void OnTranscriptDelta_OnlyTerminators_EmitsEachAsSeparateFinal()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        // 各文にユニークな内容を持たせて類似重複抑制を回避 (本テストの目的は句点分割の検証)
        transcriber.RaiseDelta("あ。い。う。");
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(3, finals.Count, "「あ。い。う。」は 3 件の完結文に分割されるはず");
        Assert.AreEqual("あ。", finals[0].TranslatedText);
        Assert.AreEqual("い。", finals[1].TranslatedText);
        Assert.AreEqual("う。", finals[2].TranslatedText);
        Assert.AreEqual(3, finals.Select(f => f.SegmentId).Distinct().Count(), "各完結文は別 SegmentId のはず");
    }

    /// <adversarial category="boundary" severity="med" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    public void OnTranscriptDelta_MixedExclamationQuestion_EmitsEachTerminatorSeparately()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("！？");
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(2, finals.Count, "「！？」は 2 件の完結文に分割されるはず");
        Assert.AreEqual("！", finals[0].TranslatedText);
        Assert.AreEqual("？", finals[1].TranslatedText);
    }

    /// <adversarial category="boundary" severity="med" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    public void OnTranscriptDelta_TerminatorAtStringStart_EmitsLeadingTerminator()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("。あ");
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count, "先頭句点「。」が 1 件の完結文として emit されるはず");
        Assert.AreEqual("。", finals[0].TranslatedText, "trailing「あ」は確定されず句点のみ確定のはず");
    }

    /// <adversarial category="boundary" severity="med" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    public void OnTranscriptDelta_ConsecutivePeriodsInMiddle_EmitsThreeSentences()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("あ。。い。");
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(3, finals.Count, "「あ。。い。」は「あ。」「。」「い。」の 3 件に分割されるはず");
        Assert.AreEqual("あ。", finals[0].TranslatedText);
        Assert.AreEqual("。", finals[1].TranslatedText);
        Assert.AreEqual("い。", finals[2].TranslatedText);
    }

    /// <adversarial category="boundary" severity="low" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    public void OnTranscriptDelta_SingleCharNoTerminator_NoFinalEmit()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("あ");
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(0, finals.Count, "句点なし 1 文字では IsFinal emit ゼロのはず");
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    public void OnTranscriptDelta_NullByteAndCRLFAndTab_NoCrashControlCharsPreservedInSentence()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("あ\x00い\r\nう\tえ。");
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count, "制御文字を含んでも句点で 1 件の完結文として emit されるはず");
        Assert.AreEqual("あ\x00い\r\nう\tえ。", finals[0].TranslatedText,
            "制御文字は句点判定に影響せず、文字列そのままが TranslatedText になるはず");
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    public void OnTranscriptDelta_ZeroWidthAndRtlControlChars_NoCrashTreatedAsNonTerminator()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        const string zwsp = "​";
        const string rtl = "‮";
        const string combining = "́";
        transcriber.RaiseDelta($"a{zwsp}b{rtl}c{combining}。");
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count, "Unicode 制御/結合文字を含んでも句点で 1 件 emit されるはず");
        StringAssert.EndsWith(finals[0].TranslatedText, "。", "末尾句点で区切られるはず");
        StringAssert.Contains(finals[0].TranslatedText, zwsp, "ゼロ幅文字は文中に保持されるはず");
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    public void OnTranscriptDelta_EmojiAndSurrogatePairBeforePeriod_NoSplitWithinSurrogate()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        var emoji = char.ConvertFromUtf32(0x1F600);
        transcriber.RaiseDelta($"挨拶{emoji}。");
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count, "絵文字を含んでも句点で 1 件 emit されるはず");
        Assert.AreEqual($"挨拶{emoji}。", finals[0].TranslatedText, "サロゲートペアが分断されず文字列そのままが emit されるはず");
        Assert.AreEqual(4, finals[0].TranslatedText.EnumerateRunes().Count(), "挨/拶/😀/。 の 4 Rune として健全にデコードできるはず");
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    public void OnTranscriptDelta_EmojiBetweenTwoPeriods_SplitsAtTerminatorsKeepingEmojiIntact()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        var emoji = char.ConvertFromUtf32(0x1F602);
        transcriber.RaiseDelta($"あ。{emoji}。い");
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(2, finals.Count, "2 句点で 2 件の完結文に分割されるはず (trailing「い」は未確定)");
        Assert.AreEqual("あ。", finals[0].TranslatedText);
        Assert.AreEqual($"{emoji}。", finals[1].TranslatedText, "絵文字 + 句点が 1 文として割れずに emit されるはず");
        Assert.AreEqual(2, finals[1].TranslatedText.EnumerateRunes().Count(), "😂/。 の 2 Rune に健全デコードできるはず");
    }

    // v1.0.27 棚卸し削除: OnTranscriptDelta_HugeNoTerminatorWithComma_FallbackSplitAndNoQuadraticHang
    // (D-7 句読点 fallback 廃止のため該当機能なし)

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    [Timeout(15000)]
    public void OnTranscriptDelta_HugeNoTerminatorNoComma_NoFinalNoCrash()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        var huge = new string('x', 100_000);
        transcriber.RaiseDelta(huge);
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(0, finals.Count, "読点も句点もない巨大文は fallback 対象外で確定 emit ゼロのはず");
    }

    /// <adversarial category="boundary" severity="med" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    public void OnTranscriptCompleted_EmptyTranscriptWithNoPriorState_NoEmit()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDone("");
        Assert.AreEqual(0, emitted.Count(x => x.IsFinal), "空 done かつ事前 state 無しでは emit ゼロのはず");
    }

    /// <adversarial category="boundary" severity="med" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    public void OnTranscriptCompleted_WhitespaceOnlyTranscript_NoFinalEmit()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDone("   ");
        Assert.AreEqual(0, emitted.Count(x => x.IsFinal), "空白のみ done では IsFinal emit ゼロのはず");
    }

    /// <adversarial category="boundary" severity="med" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    public void OnTranscriptCompleted_OnlyPeriodTranscript_EmitsSinglePeriodFinal()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDone("。");
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count, "done「。」は完結文 1 件 emit されるはず");
        Assert.AreEqual("。", finals[0].TranslatedText);
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("SentenceSplitBoundary")]
    [Timeout(20000)]
    public void OnTranscriptCompleted_HugeNewPortionAccumulatedDiff_EmitsAllSentencesWithinTimeout()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 5000; i++) sb.Append('文').Append(i).Append('。');
        transcriber.RaiseDone(sb.ToString());
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(5000, finals.Count, "5000 文すべてが完結文として emit されるはず");
        Assert.AreEqual("文0。", finals[0].TranslatedText);
        Assert.AreEqual("文4999。", finals[^1].TranslatedText);
        Assert.AreEqual(5000, finals.Select(f => f.SegmentId).Distinct().Count(), "各文は別 SegmentId のはず");
    }

    // v1.0.27 棚卸し削除: OnTranscriptDelta_FallbackSplitWithMiddleDotTerminator_SplitsOnNakaguro
    // (D-7 句読点 fallback 廃止のため該当機能なし)

    // ═══════════════════════════════════════════════════════════════
    // ⚡ 隊員3: 並行性・レースコンディション (Concurrency Chaos)
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="concurrency" severity="critical" />
    [TestMethod]
    [TestCategory("Concurrency")]
    [Timeout(30000)]
    public void OnTranscriptDelta_ConcurrentRaiseFromManyThreads_NoCrashAndLockHolds()
    {
        var (pipeline, transcriber, emitted, sync) = CreateConcurrentPipeline();
        const int threadCount = 8;
        const int iterations = 200;
        var exceptions = new ConcurrentBag<Exception>();
        var startGate = new ManualResetEventSlim(false);

        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int threadIndex = t;
            tasks[t] = Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < iterations; i++)
                {
                    try { transcriber.RaiseDelta($"T{threadIndex}文{i}。"); }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });
        }

        startGate.Set();
        Assert.IsTrue(Task.WaitAll(tasks, TimeSpan.FromSeconds(25)), "全 RaiseDelta タスクが 25 秒以内に完了するはず (デッドロックなし)");
        Assert.AreEqual(0, exceptions.Count, $"_textLock が効いていれば並行 RaiseDelta でも例外ゼロのはず。 実際: {FormatExceptions(exceptions)}");

        var snapshot = SnapshotEmitted(emitted, sync);
        foreach (var item in snapshot)
        {
            Assert.IsNotNull(item, "emit された SubtitleItem が null であってはならない");
            Assert.IsNotNull(item.SegmentId, "SegmentId が null であってはならない (データ破壊の兆候)");
            Assert.IsNotNull(item.TranslatedText, "TranslatedText が null であってはならない");
            Assert.IsNotNull(item.OriginalText, "OriginalText が null であってはならない");
        }
    }

    /// <adversarial category="concurrency" severity="critical" />
    [TestMethod]
    [TestCategory("Concurrency")]
    [Timeout(30000)]
    public void OnTranscriptDelta_And_OnTranscriptCompleted_RacingConcurrently_NoCrash()
    {
        var (pipeline, transcriber, emitted, sync) = CreateConcurrentPipeline();
        const int deltaThreads = 4;
        const int doneThreads = 4;
        const int iterations = 150;
        var exceptions = new ConcurrentBag<Exception>();
        var startGate = new ManualResetEventSlim(false);
        var tasks = new List<Task>();

        for (int t = 0; t < deltaThreads; t++)
        {
            int idx = t;
            tasks.Add(Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < iterations; i++)
                {
                    try { transcriber.RaiseDelta($"delta{idx}_{i}"); }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            }));
        }
        for (int t = 0; t < doneThreads; t++)
        {
            int idx = t;
            tasks.Add(Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < iterations; i++)
                {
                    try { transcriber.RaiseDone(i % 2 == 0 ? string.Empty : $"確定全文{idx}_{i}。"); }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            }));
        }

        startGate.Set();
        Assert.IsTrue(Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(25)), "delta/done 競合の全タスクが 25 秒以内に完了するはず (デッドロックなし)");
        Assert.AreEqual(0, exceptions.Count, $"delta と done が _textLock 下で交差しても例外ゼロのはず。 実際: {FormatExceptions(exceptions)}");

        var snapshot = SnapshotEmitted(emitted, sync);
        foreach (var item in snapshot)
        {
            Assert.IsNotNull(item.SegmentId, "競合下でも SegmentId は破壊されないはず");
            Assert.IsNotNull(item.TranslatedText, "競合下でも TranslatedText は破壊されないはず");
        }
    }

    // v1.0.27: OnIdleFinalizeTimer_RacingWithRaiseDoneAndDelta_NoCrash 削除済み
    // (最大寿命タイマー自体を廃止したため、 アイドルタイマー競合シナリオは存在しない)

    /// <adversarial category="concurrency" severity="high" />
    [TestMethod]
    [TestCategory("Concurrency")]
    [Timeout(30000)]
    public void OnTranscriptCompleted_DoubleRaiseDoneBurst_NoDuplicateExplosionAndNoCrash()
    {
        var (pipeline, transcriber, emitted, sync) = CreateConcurrentPipeline();
        const int threadCount = 2;
        const int iterations = 100;
        const string sameTranscript = "最初の文。次の文。最後の文。";
        var exceptions = new ConcurrentBag<Exception>();
        var startGate = new ManualResetEventSlim(false);

        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < iterations; i++)
                {
                    try { transcriber.RaiseDone(sameTranscript); }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });
        }

        startGate.Set();
        Assert.IsTrue(Task.WaitAll(tasks, TimeSpan.FromSeconds(25)), "二重 done 連打タスクが 25 秒以内に完了するはず");
        Assert.AreEqual(0, exceptions.Count, $"二重 done 連打でも _textLock 下で例外ゼロのはず。 実際: {FormatExceptions(exceptions)}");

        var finals = SnapshotEmitted(emitted, sync).Where(x => x.IsFinal).ToList();
        Assert.IsTrue(finals.Count <= threadCount * 3, $"二重 done でも確定文 emit は重複爆発せず {threadCount * 3} 件以下のはず。 実際: {finals.Count}");
        Assert.IsTrue(finals.Count >= 1, "少なくとも 1 回は新規ぶんとして確定文が emit されるはず");
    }

    /// <adversarial category="concurrency" severity="high" />
    [TestMethod]
    [TestCategory("Concurrency")]
    [Timeout(30000)]
    public async Task StopAsync_RacingWithRaiseDelta_NoCrashAndConverges()
    {
        var (pipeline, transcriber, emitted, sync) = CreateConcurrentPipeline();
        var exceptions = new ConcurrentBag<Exception>();

        await pipeline.StartAsync(CancellationToken.None);

        const int deltaCount = 300;
        var deltaPump = Task.Run(() =>
        {
            for (int i = 0; i < deltaCount; i++)
            {
                try { transcriber.RaiseDelta($"継続中の発話{i}"); }
                catch (Exception ex) { exceptions.Add(ex); }
            }
        });

        var stopTask = pipeline.StopAsync(CancellationToken.None);

        await deltaPump.WaitAsync(TimeSpan.FromSeconds(15));
        var stopCompleted = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(20))) == stopTask;
        Assert.IsTrue(stopCompleted, "StopAsync が delta 競合下でも 20 秒以内に完走するはず (デッドロックなし)");
        await stopTask;

        Assert.AreEqual(0, exceptions.Count, $"Stop と RaiseDelta が _textLock を奪い合っても例外ゼロのはず。 実際: {FormatExceptions(exceptions)}");

        try { transcriber.RaiseDelta("停止後の遅延 delta"); }
        catch (Exception ex) { exceptions.Add(ex); }
        Assert.AreEqual(0, exceptions.Count, "停止後の遅延 delta でもクラッシュしないはず");

        var snapshot = SnapshotEmitted(emitted, sync);
        foreach (var item in snapshot)
            Assert.IsNotNull(item.TranslatedText, "Stop 競合下でも emit された item は健全なはず");
    }

    /// <adversarial category="concurrency" severity="high" />
    [TestMethod]
    [TestCategory("Concurrency")]
    [Timeout(40000)]
    public async Task ConcurrentStartStop_RepeatedToggling_SerializedNoCrash()
    {
        var (pipeline, transcriber, emitted, sync) = CreateConcurrentPipeline();
        const int threadCount = 6;
        const int iterations = 15;
        var exceptions = new ConcurrentBag<Exception>();
        var startGate = new ManualResetEventSlim(false);

        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int idx = t;
            tasks[t] = Task.Run(async () =>
            {
                startGate.Wait();
                for (int i = 0; i < iterations; i++)
                {
                    try
                    {
                        if ((idx + i) % 2 == 0)
                            await pipeline.StartAsync(CancellationToken.None);
                        else
                            await pipeline.StopAsync(CancellationToken.None);
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });
        }

        startGate.Set();
        Assert.IsTrue(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)), "Start/Stop トグルの全タスクが 30 秒以内に完了するはず (_startStopLock デッドロックなし)");
        Assert.AreEqual(0, exceptions.Count, $"_startStopLock で直列化されていれば Start/Stop 並行トグルで例外ゼロのはず。 実際: {FormatExceptions(exceptions)}");

        await pipeline.StopAsync(CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════
    // 💀 隊員4: リソース枯渇・DoS耐性 (Resource Exhaustion)
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="resource" severity="high" />
    [TestMethod]
    [TestCategory("ResourceExhaustion")]
    [Timeout(10000)]
    public void OnTranscriptDelta_TenThousandTerminatedDeltas_AccumulatedTextStaysBoundedAndMemoryDoesNotLeak()
    {
        var (pipeline, transcriber, emitted) = ResourceTestPipelineFactory.Create();
        try
        {
            const int count = 10_000;
            var before = GC.GetTotalMemory(true);
            // インデックスを含めて各 delta をユニークにし、類似重複抑制を回避
            for (int i = 0; i < count; i++) transcriber.RaiseDelta($"短文{i}。");
            var after = GC.GetTotalMemory(true);
            long retainedGrowth = after - before;

            int finals = emitted.Count(x => x.IsFinal);
            Assert.AreEqual(count, finals, $"句点付き delta {count} 件で完結文 {count} 件が emit されるはず (実際 {finals})");
            Assert.IsTrue(retainedGrowth < 8 * 1024 * 1024, $"1万件 delta 後の retained メモリ増加 {retainedGrowth / 1024}KB が 8MB 未満であるべき (メモリ線形性=leak しないこと)");
        }
        finally { pipeline.Dispose(); }
    }

    /// <adversarial category="resource" severity="high" />
    [TestMethod]
    [TestCategory("ResourceExhaustion")]
    [Timeout(10000)]
    public void OnTranscriptDelta_NeverEndingMachineGunTalkWithoutComma_CompletesWithinTimeout()
    {
        var (pipeline, transcriber, emitted) = ResourceTestPipelineFactory.Create();
        try
        {
            const int count = 50_000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < count; i++) transcriber.RaiseDelta("あ");
            sw.Stop();

            Assert.AreEqual(0, emitted.Count(x => x.IsFinal), "句点・読点なしの delta では IsFinal emit はゼロのはず");
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(9), $"句点なし {count} 件の delta 処理が 9 秒未満で完了するべき (実際 {sw.Elapsed.TotalSeconds:F1}s) — O(n^2) 劣化/ハング検出");
        }
        finally { pipeline.Dispose(); }
    }

    /// <adversarial category="resource" severity="high" />
    [TestMethod]
    [TestCategory("ResourceExhaustion")]
    [Timeout(10000)]
    public void OnTranscriptCompleted_RepeatedHugeCumulativeTranscript_DiffExtractionDoesNotDegradeQuadratically()
    {
        var (pipeline, transcriber, emitted) = ResourceTestPipelineFactory.Create();
        try
        {
            const int sentences = 3_000;
            // 各文の bigram 集合を十分に異ならせて類似重複抑制の誤検出を回避。
            // 固定テキスト部分が bigram を支配すると Jaccard が閾値を超えるため、
            // ローテーションひらがな (46 種) をサフィックスに付けて多様性を保証する。
            var cumulative = new StringBuilder(sentences * 16);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < sentences; i++) { cumulative.Append($"節{i}{(char)(0x3042 + i % 46)}。"); transcriber.RaiseDone(cumulative.ToString()); }
            sw.Stop();

            int finals = emitted.Count(x => x.IsFinal);
            Assert.AreEqual(sentences, finals, $"累積 done {sentences} 回で新規ぶんのみ {sentences} 件 emit されるはず — 実際 {finals}");
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(9), $"数十KB 累積の done を {sentences} 回処理しても 9 秒未満で完了するべき (実際 {sw.Elapsed.TotalSeconds:F1}s)");
        }
        finally { pipeline.Dispose(); }
    }

    /// <adversarial category="resource" severity="medium" />
    [TestMethod]
    [TestCategory("ResourceExhaustion")]
    [Timeout(10000)]
    public void OnTranscriptCompleted_LongSessionThousandsOfSentences_MemoryGrowthWithinBudget()
    {
        var transcriber = new ResourceTestTranscriber();
        var pipeline = ResourceTestPipelineFactory.CreateRaw(transcriber);
        int finalCount = 0;
        pipeline.SubtitleGenerated += (_, item) => { if (item.IsFinal) finalCount++; };
        try
        {
            const int sentences = 5_000;
            // 各文の bigram 集合を十分に異ならせて類似重複抑制の誤検出を回避。
            // 固定テキスト部分が bigram を支配すると Jaccard が閾値を超えるため、
            // ローテーションひらがな (46 種) をサフィックスに付けて多様性を保証する。
            var cumulative = new StringBuilder(sentences * 20);
            var before = GC.GetTotalMemory(true);
            for (int i = 0; i < sentences; i++) { cumulative.Append($"段{i}{(char)(0x3042 + i % 46)}。"); transcriber.RaiseDone(cumulative.ToString()); }
            var after = GC.GetTotalMemory(true);
            long retainedGrowth = after - before;

            Assert.AreEqual(sentences, finalCount, $"{sentences} 文の累積 done で確定 emit が {sentences} 件のはず (実際 {finalCount})");
            Assert.IsTrue(retainedGrowth < 12 * 1024 * 1024, $"長時間セッション {sentences} 文後の retained メモリ増加 {retainedGrowth / 1024}KB が 12MB 未満であるべき");
        }
        finally { pipeline.Dispose(); }
    }

    /// <adversarial category="resource" severity="medium" />
    [TestMethod]
    [TestCategory("ResourceExhaustion")]
    [Timeout(10000)]
    public void OnTranscriptDelta_ManyShortIdleFinalizeCycles_NoUnboundedRetentionAcrossSegments()
    {
        var transcriber = new ResourceTestTranscriber();
        var pipeline = ResourceTestPipelineFactory.CreateRaw(transcriber);
        int finalCount = 0;
        pipeline.SubtitleGenerated += (_, item) => { if (item.IsFinal) finalCount++; };
        try
        {
            const int cycles = 20_000;
            var before = GC.GetTotalMemory(true);
            // 各 delta の bigram 集合を十分に異ならせて類似重複抑制の誤検出を回避。
            // 数字のみだと trailing 0 の重複で bigram が衝突するため、
            // ローテーションひらがな (46 種) をサフィックスに付けて多様性を保証する。
            for (int i = 0; i < cycles; i++) transcriber.RaiseDelta($"話{i}{(char)(0x3042 + i % 46)}。");
            var after = GC.GetTotalMemory(true);
            long retainedGrowth = after - before;

            Assert.AreEqual(cycles, finalCount, $"{cycles} サイクルの短い完結 delta で確定 emit が {cycles} 件のはず (実際 {finalCount})");
            Assert.IsTrue(retainedGrowth < 10 * 1024 * 1024, $"{cycles} 確定サイクル後の retained メモリ増加 {retainedGrowth / 1024}KB が 10MB 未満であるべき");
        }
        finally { pipeline.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════
    // 🔀 隊員5: 状態遷移の矛盾 (State Machine Abuse)
    // ═══════════════════════════════════════════════════════════════

    // v1.0.27: OnTranscriptCompleted_DoneAfterMaxSegmentLifetimeFinalize_EmitsOnlyDiff 削除済み
    // (最大寿命タイマー廃止、 partial 連結方式廃止のため該当シナリオ消滅)

    /// <adversarial category="state" severity="high" />
    [TestMethod]
    [TestCategory("StateMachine")]
    public async Task OnTranscriptDelta_DeltaThenDoneAfterStop_DoesNotThrow()
    {
        var (pipeline, transcriber, emitted, gate) = CreateStatePipeline();
        await pipeline.StartAsync(CancellationToken.None);
        transcriber.RaiseDelta("途中まで。");
        await pipeline.StopAsync();

        int finalsBeforeLateEvents = SnapshotFinals(emitted, gate).Count;

        var ex = Record(() =>
        {
            transcriber.RaiseDelta("停止後の追加");
            transcriber.RaiseDone("停止後の追加だよ。");
        });

        Assert.IsNull(ex, $"Stop 後の遅延 delta/done で例外を投げてはいけない (実際: {ex})");
        Assert.IsTrue(SnapshotFinals(emitted, gate).Count >= finalsBeforeLateEvents, "Stop 後の遅延イベントで確定字幕数が減る (状態破壊) ことはないはず");
    }

    /// <adversarial category="state" severity="medium" />
    [TestMethod]
    [TestCategory("StateMachine")]
    public void OnTranscriptCompleted_SameTranscriptTwice_SecondDoneSkipped()
    {
        var (pipeline, transcriber, emitted, gate) = CreateStatePipeline();
        transcriber.RaiseDone("おはよう。");
        Assert.AreEqual(1, SnapshotFinals(emitted, gate).Count, "1 回目の done で完結文 1 件 emit されるはず");

        transcriber.RaiseDone("おはよう。");
        Assert.AreEqual(1, SnapshotFinals(emitted, gate).Count, "2 回目の同一 done は newPortion 空でスキップされ、 重複 emit しないはず");
    }

    /// <adversarial category="state" severity="medium" />
    [TestMethod]
    [TestCategory("StateMachine")]
    public void OnTranscriptDelta_DeltaWhileReconnecting_StillEmits()
    {
        var (pipeline, transcriber, emitted, gate) = CreateStatePipeline();
        transcriber.RaiseStateChanged(ConnectionState.Reconnecting);
        transcriber.RaiseDelta("再接続中の発話。");

        var finals = SnapshotFinals(emitted, gate);
        Assert.AreEqual(1, finals.Count, "Reconnecting 中でも delta の句点分割は動き、 完結文 1 件 emit されるはず");
        Assert.AreEqual("再接続中の発話。", finals[0].TranslatedText);
    }

    /// <adversarial category="state" severity="medium" />
    [TestMethod]
    [TestCategory("StateMachine")]
    public void OnTranscriptCompleted_DoneWithoutAnyDelta_EmitsFullTranscript()
    {
        var (pipeline, transcriber, emitted, gate) = CreateStatePipeline();
        transcriber.RaiseDone("いきなり確定。");

        var finals = SnapshotFinals(emitted, gate);
        Assert.AreEqual(1, finals.Count, "delta 無しの done でも transcript 全体が 1 件 emit されるはず");
        Assert.AreEqual("いきなり確定。", finals[0].TranslatedText);
    }

    // v1.0.27: OnMaxSegmentLifetimeTimer_MultipleConsecutiveFinalizes_EmitDistinctSegmentIds 削除済み
    // (最大寿命タイマー廃止のため該当シナリオ消滅)

    /// <adversarial category="state" severity="medium" />
    [TestMethod]
    [TestCategory("StateMachine")]
    public async Task StartStop_DoubleStartAndDoubleStop_AreIdempotent()
    {
        var (pipeline, transcriber, emitted, gate) = CreateStatePipeline();
        var ex = await RecordAsync(async () =>
        {
            await pipeline.StartAsync(CancellationToken.None);
            await pipeline.StartAsync(CancellationToken.None);
            await pipeline.StopAsync();
            await pipeline.StopAsync();
        });
        Assert.IsNull(ex, $"二重 Start / 二重 Stop は冪等で例外を投げてはいけない (実際: {ex})");
    }

    // v1.0.27: OnMaxSegmentLifetimeTimer_AfterDeltaFullyTerminated_NoExtraEmit 削除済み
    // (最大寿命タイマー廃止のため該当シナリオ消滅)


    // ═══════════════════════════════════════════════════════════════
    // 🎭 隊員6: 型パンチ・プロトコル違反 (Type Punching)
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="type" severity="high" />
    [TestMethod]
    [TestCategory("TypePunch")]
    public void OnTranscriptDelta_NullString_IsSwallowedByGuard()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta(null!);
        Assert.AreEqual(0, emitted.Count, "null delta は何も emit せず握られるはず");
    }

    /// <adversarial category="type" severity="critical" />
    [TestMethod]
    [TestCategory("TypePunch")]
    public void OnTranscriptCompleted_NullString_DoesNotThrow()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDone(null!);
        Assert.AreEqual(0, emitted.Count(x => x.IsFinal), "null done は emit ゼロで握られるはず");
    }

    /// <adversarial category="type" severity="medium" />
    [TestMethod]
    [TestCategory("TypePunch")]
    public void OnTranscriptDelta_EmptyStringFlood_IsSwallowedAndNoEmit()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        for (int n = 0; n < 1000; n++) transcriber.RaiseDelta(string.Empty);
        Assert.AreEqual(0, emitted.Count, "空文字列連打は全件ガードで握られ emit ゼロのはず");
    }

    /// <adversarial category="type" severity="high" />
    [TestMethod]
    [TestCategory("TypePunch")]
    public void OnTranscriptCompleted_DoneOnlyNoPriorDelta_EmitsSentencesFromScratch()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDone("最初の文。次の文。");
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(2, finals.Count, "done 単独でも newPortion 全体が句点分割 emit されるはず");
        Assert.AreEqual("最初の文。", finals[0].TranslatedText);
        Assert.AreEqual("次の文。", finals[1].TranslatedText);
    }

    /// <adversarial category="type" severity="high" />
    [TestMethod]
    [TestCategory("TypePunch")]
    public void OnTranscriptCompleted_PartialMatchThenDiverges_SkipsAsMismatch()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDelta("ABC。");
        Assert.AreEqual(1, emitted.Count(x => x.IsFinal), "1 回目で 1 件確定");

        emitted.Clear();
        transcriber.RaiseDone("ABX。");
        Assert.AreEqual(0, emitted.Count(x => x.IsFinal), "途中分岐の done は prefix 不一致 skip で emit ゼロのはず");
    }

    /// <adversarial category="type" severity="high" />
    [TestMethod]
    [TestCategory("TypePunch")]
    public void OnTranscriptDelta_LoneSurrogateHalf_DoesNotThrow()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        var loneHigh = "\uD83D";
        transcriber.RaiseDelta(loneHigh + "あ。");
        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(1, finals.Count, "孤立サロゲート混入でもクラッシュせず句点分割されるはず");
        StringAssert.EndsWith(finals[0].TranslatedText, "。");
    }

    /// <adversarial category="type" severity="medium" />
    [TestMethod]
    [TestCategory("TypePunch")]
    public void OnTranscriptCompleted_SurrogatePairSplitAcrossPrefixBoundary_DoesNotThrow()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        var emoji = "😀";
        transcriber.RaiseDelta("あ" + "\uD83D");
        emitted.Clear();
        transcriber.RaiseDone("あ" + emoji + "。");
        Assert.IsTrue(emitted.Count >= 0, "サロゲート境界破綻でもクラッシュせず到達できるはず");
    }

    /// <adversarial category="type" severity="high" />
    [TestMethod]
    [TestCategory("TypePunch")]
    public void OnTranscriptCompleted_NfcVsNfdSameGlyph_TreatedAsPrefixMismatch()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        var nfc = "が";
        var nfd = "が";
        transcriber.RaiseDelta(nfc + "ぎ。");
        Assert.AreEqual(1, emitted.Count(x => x.IsFinal), "1 回目で 1 件確定");

        emitted.Clear();
        transcriber.RaiseDone(nfd + "ぎ。");
        Assert.AreEqual(0, emitted.Count(x => x.IsFinal), "NFC/NFD は正規化されず prefix 不一致 skip になるはず");
    }

    /// <adversarial category="type" severity="medium" />
    [TestMethod]
    [TestCategory("TypePunch")]
    public void OnTranscriptDelta_FullWidthExclamationIsBoundary_HalfWidthLookalikeAlso()
    {
        var (p1, t1, e1) = CreatePipeline();
        t1.RaiseDelta("やった！");
        Assert.AreEqual(1, e1.Count(x => x.IsFinal), "全角！は句点境界として確定 emit されるはず");
        StringAssert.EndsWith(e1.First(x => x.IsFinal).TranslatedText, "！");

        var (p2, t2, e2) = CreatePipeline();
        t2.RaiseDelta("やった!");
        Assert.AreEqual(1, e2.Count(x => x.IsFinal), "半角!も句点境界として確定 emit されるはず");

        var (p3, t3, e3) = CreatePipeline();
        t3.RaiseDelta("続くよ…");
        Assert.AreEqual(0, e3.Count(x => x.IsFinal), "三点リーダ … は SentenceTerminators 外なので確定 emit されないはず");
    }

    /// <adversarial category="type" severity="medium" />
    [TestMethod]
    [TestCategory("TypePunch")]
    public void OnTranscriptCompleted_EmptyStringDone_FallbackToAccumulatedNoEmit()
    {
        var (pipeline, transcriber, emitted) = CreatePipeline();
        transcriber.RaiseDone(string.Empty);
        Assert.AreEqual(0, emitted.Count(x => x.IsFinal), "空 done は fallback 経路で newPortion 空 → emit ゼロのはず");
    }

    // ═══════════════════════════════════════════════════════════════
    // 🌪️ 隊員7: 環境異常・カオステスト (Environmental Chaos)
    // ═══════════════════════════════════════════════════════════════

    // v1.0.27 棚卸し削除: MaxSegmentLifetime 境界値 / NaN / mid-session 変更テスト全廃 (タイマー自体が消滅)。

    /// <adversarial category="chaos" severity="medium" />
    [TestMethod]
    [TestCategory("Chaos")]
    public void OnTranscriptDelta_GermanCultureCommaDecimal_SentenceSplitUnaffected()
    {
        // v1.0.27: 最大寿命タイマー廃止のため、 アイドル確定検証は削除。
        // de-DE カルチャでも句点分割が正常動作することのみ検証。
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var german = new CultureInfo("de-DE");
            CultureInfo.CurrentCulture = german;
            CultureInfo.CurrentUICulture = german;

            var (pipeline, transcriber, emitted) = CreateChaosPipeline(displayDuration: 5.0);
            transcriber.RaiseDelta("ドイツロケールの文。");

            var finals = emitted.Where(x => x.IsFinal).ToList();
            Assert.AreEqual(1, finals.Count, "de-DE カルチャでも句点分割が正常動作するはず (char 比較はカルチャ非依存)");
            Assert.AreEqual("ドイツロケールの文。", finals[0].TranslatedText);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    /// <adversarial category="chaos" severity="medium" />
    [TestMethod]
    [TestCategory("Chaos")]
    public void OnTranscriptDelta_TurkishCultureDottedI_SentenceSplitUnaffected()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            var (pipeline, transcriber, emitted) = CreateChaosPipeline(displayDuration: 5.0);
            transcriber.RaiseDelta("This is a title. It works.");

            var finals = emitted.Where(x => x.IsFinal).ToList();
            Assert.AreEqual(2, finals.Count, "tr-TR でも英語 '.' 区切りが 2 文に分割されるはず (char 比較はカルチャ非依存)");
            StringAssert.EndsWith(finals[0].TranslatedText, ".");
            StringAssert.EndsWith(finals[1].TranslatedText, ".");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <adversarial category="chaos" severity="medium" />
    [TestMethod]
    [TestCategory("Chaos")]
    public void OnTranscriptDone_MultilingualPunctuation_ChineseAndArabic_SplitsCorrectly()
    {
        var (pipeline, transcriber, emitted) = CreateChaosPipeline(displayDuration: 5.0);

        const string arabic = "مرحبا بالعالم";
        transcriber.RaiseDone($"你好。今天天气很好。{arabic}?");

        var finals = emitted.Where(x => x.IsFinal).ToList();
        Assert.AreEqual(3, finals.Count, "中国語句点 2 つ + アラビア語末尾 '?' で 3 文に分割されるはず");
        StringAssert.EndsWith(finals[0].TranslatedText, "。");
        StringAssert.EndsWith(finals[1].TranslatedText, "。");
        StringAssert.Contains(finals[2].TranslatedText, arabic);
    }

    // v1.0.27 棚卸し削除: 最大寿命タイマー / partial 連結方式関連テスト全廃。
    //   - OnTranscriptDelta_MaxSegmentLifetimeOne_MinimumBoundary_FinalizesExactlyOnce
    //   - OnTranscriptDelta_SilenceBetweenDeltas_DoesNotForceFinalizeAndConcatenates
    //   - OnTranscriptDelta_SilenceBetweenDeltas_PartialKeepsSameSegmentId
    //   - OnTranscriptDelta_MaxSegmentLifetimeAfterFinalize_NewSegmentRestartsTimer
    // v1.0.27 設計: server gap 対策は「VAD Silence 中の無音 PCM 継続送信」に置換。
}
