using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// デバッグ録音 (WAV 保存) の「翻訳開始後の ON/OFF 切替が即反映される」回帰テスト。
/// 旧実装は StartCoreAsync 時に 1 回だけ DebugRecordSentAudio を見ており、 走行中に ON にしても
/// 録音が始まらなかった (ゆろさん報告)。 修正後は IOptionsMonitor.OnChange → SyncDebugRecording 経由で
/// 走行中の切替に追従する。 本テストはその挙動を spy recorder + 発火可能な options monitor で検証する。
/// </summary>
[TestClass]
public sealed class TranslationPipelineServiceDebugRecordingTests
{
    /// <summary>StartSession / StopSession の呼び出しと録音状態を記録する spy。</summary>
    private sealed class SpyDebugAudioRecorder : IDebugAudioRecorder
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool IsRecording { get; private set; }
#pragma warning disable CS0067 // WriteFailed は本テストでは発火しないが、 インターフェース実装上必要
        public event System.Action<System.Exception>? WriteFailed;
#pragma warning restore CS0067
        public void StartSession(string sessionId) { StartCount++; IsRecording = true; }
        public void WritePcm16(System.ReadOnlySpan<byte> pcm16) { }
        public void StopSession() { if (IsRecording) StopCount++; IsRecording = false; }
    }

    /// <summary>CurrentValue を共有しつつ OnChange を任意に発火できる options monitor。</summary>
    private sealed class FiringOptionsMonitor : IOptionsMonitor<AppSettings>
    {
        private readonly System.Collections.Generic.List<System.Action<AppSettings, string?>> _listeners = new();
        public AppSettings CurrentValue { get; }
        public FiringOptionsMonitor(AppSettings settings) { CurrentValue = settings; }
        public AppSettings Get(string? name) => CurrentValue;
        public IDisposable OnChange(System.Action<AppSettings, string?> listener)
        {
            _listeners.Add(listener);
            return new Sub(this, listener);
        }
        /// <summary>登録済みリスナーを発火する (settings.json 再読込相当)。</summary>
        public void Raise()
        {
            foreach (var l in _listeners.ToArray()) l(CurrentValue, null);
        }
        private sealed class Sub : IDisposable
        {
            private readonly FiringOptionsMonitor _m;
            private readonly System.Action<AppSettings, string?> _l;
            public Sub(FiringOptionsMonitor m, System.Action<AppSettings, string?> l) { _m = m; _l = l; }
            public void Dispose() => _m._listeners.Remove(_l);
        }
    }

    private static AppSettings MakeSettings(bool recordOn)
    {
        var s = new AppSettings
        {
            OpenAIRealtime = new OpenAIRealtimeSettings
            {
                ApiKey = "test-key",
                Endpoint = "wss://api.openai.com/v1/realtime/translations",
                Model = "gpt-realtime-translate",
                OutputLanguage = "ja"
            }
        };
        s.AudioCapture.DebugRecordSentAudio = recordOn;
        return s;
    }

    private static (TranslationPipelineService pipeline, FiringOptionsMonitor monitor, SpyDebugAudioRecorder recorder)
        CreatePipeline(bool recordOn)
    {
        var settings = MakeSettings(recordOn);
        var monitor = new FiringOptionsMonitor(settings);
        var recorder = new SpyDebugAudioRecorder();
        var pipeline = new TranslationPipelineService(
            new TestAudioCaptureService(),
            new TestRealtimeTranscriber(),
            monitor,
            new TestSettingsService(),
            new TestVoiceActivityDetector(),
            recorder);
        return (pipeline, monitor, recorder);
    }

    [TestMethod]
    [TestCategory("DebugRecording")]
    public async Task 翻訳開始後にONにすると録音が開始されること()
    {
        var (pipeline, monitor, recorder) = CreatePipeline(recordOn: false);
        try
        {
            await pipeline.StartAsync(CancellationToken.None);
            Assert.IsFalse(recorder.IsRecording, "OFF で開始したので録音は始まっていないはず");
            Assert.AreEqual(0, recorder.StartCount, "開始時 OFF なら StartSession は呼ばれない");

            // 走行中に WAV 保存を ON へ切替 (= settings.json 変更 → OnChange 発火相当)
            monitor.CurrentValue.AudioCapture.DebugRecordSentAudio = true;
            monitor.Raise();

            Assert.IsTrue(recorder.IsRecording, "走行中に ON にしたら録音が開始されるはず (これが直したかったバグ)");
            Assert.AreEqual(1, recorder.StartCount, "StartSession がちょうど 1 回呼ばれる");
        }
        finally
        {
            await pipeline.StopAsync();
        }
        Assert.IsFalse(recorder.IsRecording, "停止後は録音も終了しているはず");
        Assert.AreEqual(1, recorder.StopCount, "停止で StopSession がちょうど 1 回呼ばれる");
    }

    [TestMethod]
    [TestCategory("DebugRecording")]
    public async Task 走行中にOFFにすると録音が停止すること()
    {
        var (pipeline, monitor, recorder) = CreatePipeline(recordOn: true);
        try
        {
            await pipeline.StartAsync(CancellationToken.None);
            Assert.IsTrue(recorder.IsRecording, "ON で開始したので録音中のはず");
            Assert.AreEqual(1, recorder.StartCount);

            // 走行中に OFF へ切替
            monitor.CurrentValue.AudioCapture.DebugRecordSentAudio = false;
            monitor.Raise();

            Assert.IsFalse(recorder.IsRecording, "走行中に OFF にしたら録音が停止するはず");
            Assert.AreEqual(1, recorder.StopCount, "StopSession がちょうど 1 回呼ばれる");
        }
        finally
        {
            await pipeline.StopAsync();
        }
        // 既に停止済みなので停止処理が二重に StopSession を呼ばない
        Assert.AreEqual(1, recorder.StopCount, "停止後の StopAsync で二重停止しない");
    }

    [TestMethod]
    [TestCategory("DebugRecording")]
    public async Task ONで開始すると即座に録音が始まること()
    {
        var (pipeline, _, recorder) = CreatePipeline(recordOn: true);
        try
        {
            await pipeline.StartAsync(CancellationToken.None);
            Assert.IsTrue(recorder.IsRecording, "ON で開始したら即録音されるはず (既存挙動の維持)");
            Assert.AreEqual(1, recorder.StartCount);
        }
        finally
        {
            await pipeline.StopAsync();
        }
        Assert.IsFalse(recorder.IsRecording);
        Assert.AreEqual(1, recorder.StopCount);
    }

    [TestMethod]
    [TestCategory("DebugRecording")]
    public void 翻訳開始前にONにしても録音は始まらないこと()
    {
        var (pipeline, monitor, recorder) = CreatePipeline(recordOn: false);
        try
        {
            // 非走行中に ON にしても、 翻訳が走っていないので録音は開始されない
            monitor.CurrentValue.AudioCapture.DebugRecordSentAudio = true;
            monitor.Raise();

            Assert.IsFalse(recorder.IsRecording, "非走行中は録音しない (_isRunning=false ガード)");
            Assert.AreEqual(0, recorder.StartCount);
        }
        finally
        {
            pipeline.Dispose();
        }
    }
}
