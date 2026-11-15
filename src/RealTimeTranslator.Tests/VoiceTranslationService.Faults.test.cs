using System.Reflection;
using System.Text;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

[TestClass]
public sealed class VoiceTranslationServiceFaultTests
{
    private static VoiceTranslationSettings Settings() => new()
    {
        InputDeviceId = "microphone",
        OutputDeviceId = "output",
        EnableVad = false,
        PushToTalkEnabled = false
    };

    private static OpenAIRealtimeSettings ApiSettings() => new() { ApiKey = "test-key" };

    [TestMethod]
    public async Task クライアント破棄失敗でも音声を停止して再開始できる()
    {
        var audio = new Audio();
        var client = new Client { DisposeError = new IOException("破棄失敗") };
        await using var service = new VoiceTranslationService(audio, () => client.DisposeCount == 0 ? client : new Client(), _ => false);
        await service.StartAsync(Settings(), ApiSettings());

        var error = await Assert.ThrowsExactlyAsync<IOException>(() => service.StopAsync());

        Assert.AreEqual("破棄失敗", error.Message);
        Assert.AreEqual(VoiceTranslationState.Faulted, service.State);
        Assert.AreEqual(1, audio.StopCount);
        await service.StartAsync(Settings(), ApiSettings());
        Assert.AreEqual(VoiceTranslationState.Running, service.State);
        await service.StopAsync();
        Assert.AreEqual(VoiceTranslationState.Stopped, service.State);
    }

    [TestMethod]
    public async Task クライアント破棄と音声停止の同時失敗は両方の原因を保持する()
    {
        var disposeError = new IOException("破棄失敗");
        var stopError = new IOException("音声停止失敗");
        var audio = new Audio { StopError = stopError };
        var client = new Client { DisposeError = disposeError };
        await using var service = new VoiceTranslationService(audio, () => client.DisposeCount == 0 ? client : new Client(), _ => false);
        await service.StartAsync(Settings(), ApiSettings());

        var error = await Assert.ThrowsExactlyAsync<AggregateException>(() => service.StopAsync());

        // DESIGN.md の終了順 (音声停止 → client 破棄) で両方を実行し、その順で原因を保持する。
        CollectionAssert.AreEqual(new Exception[] { stopError, disposeError }, error.Flatten().InnerExceptions.ToArray());
        Assert.AreEqual(VoiceTranslationState.Faulted, service.State);
        await service.StartAsync(Settings(), ApiSettings());
        await service.StopAsync();
    }

    [TestMethod]
    public async Task 通常停止は音声を停止してからクライアントを破棄する()
    {
        // DESIGN.md の終了順。client の破棄待ちの間にマイク・再生・ゲームPTTフックを開いたままにしない。
        var order = new List<string>();
        var audio = new Audio { Log = order };
        var client = new Client { Log = order };
        await using var service = new VoiceTranslationService(audio, () => client.DisposeCount == 0 ? client : new Client(), _ => false);
        await service.StartAsync(Settings(), ApiSettings());

        await service.StopAsync();

        CollectionAssert.AreEqual(new[] { "audio.stop", "client.dispose" }, order);
        Assert.AreEqual(1, client.DisposeCount);
        Assert.AreEqual(VoiceTranslationState.Stopped, service.State);
    }

    // 停止は _lifecycle を待たずに先にミュート・取消するため、その途中で確定点に達した操作が
    // routing を開き直さないことを、停止保留を直接立てて決定的に再現する。
    private static void SetStopPending(VoiceTranslationService service, int value) =>
        typeof(VoiceTranslationService).GetField("_stopPending", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, value);

    private static int ReadPrivateInt(VoiceTranslationService service, string field) =>
        (int)typeof(VoiceTranslationService).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    [TestMethod]
    public async Task 停止はミュートの時点で停止保留を立て完了後に下ろす()
    {
        var audio = new Audio();
        await using var service = new VoiceTranslationService(audio, () => new Client(), _ => false);
        await service.StartAsync(Settings(), ApiSettings());
        var observed = new List<int>();
        // 停止のミュート (MuteRouting) は送話出力を消去する。その時点で確定点が停止を観測できること。
        audio.OnClearOutput = () => observed.Add(ReadPrivateInt(service, "_stopPending"));

        await service.StopAsync();

        Assert.IsTrue(observed.Count > 0 && observed.All(x => x == 1), $"停止中の停止保留: {string.Join(",", observed)}");
        Assert.AreEqual(0, ReadPrivateInt(service, "_stopPending"));
    }

    [TestMethod]
    public async Task 停止中に開始の確定点へ達しても送信経路を開かない()
    {
        var audio = new Audio();
        var client = new Client();
        await using var service = new VoiceTranslationService(audio, () => client, _ => false);
        var errors = 0;
        service.ErrorReceived += _ => Interlocked.Increment(ref errors);
        audio.OnStart = () => SetStopPending(service, 1);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.StartAsync(Settings(), ApiSettings()));

        Assert.AreEqual(VoiceSendMode.Muted, service.Mode);
        Assert.AreNotEqual(VoiceTranslationState.Running, service.State);
        Assert.AreEqual(0, errors);
        audio.OnStart = null;
        SetStopPending(service, 0);
        await service.StartAsync(Settings(), ApiSettings());
        Assert.AreEqual(VoiceTranslationState.Running, service.State);
        await service.StopAsync();
    }

    [TestMethod]
    public async Task 停止中に送信モード切替の確定点へ達しても送信経路を開かず障害にしない()
    {
        var audio = new Audio();
        VoiceTranslationService? target = null;
        // 原音モードで開始し (client を作らない)、翻訳モードへの切替の接続中に停止が始まった状態を作る。
        await using var service = new VoiceTranslationService(audio,
            () => new Client { OnConnect = () => SetStopPending(target!, 1) }, _ => false);
        target = service;
        var errors = 0;
        service.ErrorReceived += _ => Interlocked.Increment(ref errors);
        var settings = Settings();
        settings.Mode = VoiceSendMode.Original;
        await service.StartAsync(settings, ApiSettings());

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.SetModeAsync(VoiceSendMode.Translated));

        // 障害停止は非同期に走るため、予約フラグを同期で確認して「障害にしない」を決定的に検証する。
        Assert.AreEqual(0, ReadPrivateInt(service, "_faultScheduled"));
        Assert.AreEqual(VoiceSendMode.Muted, service.Mode);
        // 停止へ任せるため、障害停止の予約・通知をしない。
        Assert.AreEqual(VoiceTranslationState.Running, service.State);
        Assert.AreEqual(0, errors);
        SetStopPending(service, 0);
        await service.StopAsync();
        Assert.AreEqual(VoiceTranslationState.Stopped, service.State);
    }

    [TestMethod]
    public async Task 音声停止失敗でもクライアントを破棄して再開始できる()
    {
        var audio = new Audio { StopError = new IOException("音声停止失敗") };
        var client = new Client();
        await using var service = new VoiceTranslationService(audio, () => client.DisposeCount == 0 ? client : new Client(), _ => false);
        await service.StartAsync(Settings(), ApiSettings());

        await Assert.ThrowsExactlyAsync<IOException>(() => service.StopAsync());

        Assert.AreEqual(VoiceTranslationState.Faulted, service.State);
        Assert.AreEqual(1, client.DisposeCount);
        await service.StartAsync(Settings(), ApiSettings());
        await service.StopAsync();
        Assert.AreEqual(VoiceTranslationState.Stopped, service.State);
    }

    [TestMethod]
    public async Task 開始失敗後の破棄失敗でも元の原因と終端状態を保つ()
    {
        var original = new InvalidOperationException("マイク開始失敗");
        var audio = new Audio { StartError = original };
        var client = new Client { DisposeError = new IOException("破棄失敗") };
        await using var service = new VoiceTranslationService(audio, () => client.DisposeCount == 0 ? client : new Client(), _ => false);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.StartAsync(Settings(), ApiSettings()));

        Assert.AreSame(original, error);
        Assert.AreEqual(VoiceTranslationState.Faulted, service.State);
        Assert.AreEqual(1, audio.StopCount);
        await service.StartAsync(Settings(), ApiSettings());
        await service.StopAsync();
    }

    [TestMethod]
    public async Task 接続失敗後の破棄失敗でも元の原因を保つ()
    {
        var original = new InvalidOperationException("接続失敗");
        var audio = new Audio();
        var client = new Client { ConnectError = original, DisposeError = new IOException("破棄失敗") };
        await using var service = new VoiceTranslationService(audio, () => client.DisposeCount == 0 ? client : new Client(), _ => false);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.StartAsync(Settings(), ApiSettings()));

        Assert.AreSame(original, error);
        Assert.AreEqual(VoiceTranslationState.Faulted, service.State);
        await service.StartAsync(Settings(), ApiSettings());
        await service.StopAsync();
    }

    [TestMethod]
    public async Task 実行中の障害は終了処理失敗後も元の原因を一度通知する()
    {
        var original = new IOException("接続の元の障害");
        var audio = new Audio { StopError = new IOException("音声停止失敗") };
        var client = new Client { DisposeError = new IOException("破棄失敗") };
        await using var service = new VoiceTranslationService(audio, () => client.DisposeCount == 0 ? client : new Client(), _ => false);
        var received = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        service.ErrorReceived += error => { Interlocked.Increment(ref count); received.TrySetResult(error); };
        await service.StartAsync(Settings(), ApiSettings());

        client.Fail(original);

        Assert.AreSame(original, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(VoiceTranslationState.Faulted, service.State);
        Assert.AreEqual(1, client.DisposeCount);
        await service.StartAsync(Settings(), ApiSettings());
        await service.StopAsync();
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public async Task フレーム処理の障害は終了処理失敗後も通知して再開始できる()
    {
        var audio = new Audio { StopError = new IOException("音声停止失敗") };
        var client = new Client();
        await using var service = new VoiceTranslationService(audio, () => client.DisposeCount == 0 ? client : new Client(), _ => false);
        var received = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ErrorReceived += error => received.TrySetResult(error);
        await service.StartAsync(Settings(), ApiSettings());

        audio.Emit(new VoiceMicrophoneFrame([0f], 1));

        Assert.IsInstanceOfType<InvalidDataException>(await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(VoiceTranslationState.Faulted, service.State);
        Assert.AreEqual(1, client.DisposeCount);
        await service.StartAsync(Settings(), ApiSettings());
        await service.StopAsync();
    }

    [TestMethod]
    public async Task 実行中の致命的APIエラーは汎用切断メッセージに置き換わらない()
    {
        var audio = new Audio();
        var client = new Client();
        await using var service = new VoiceTranslationService(audio, () => client.DisposeCount == 0 ? client : new Client(), _ => false);
        var received = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        service.ErrorReceived += error => { Interlocked.Increment(ref count); received.TrySetResult(error); };
        await service.StartAsync(Settings(), ApiSettings());

        // 接続だけを偽装し、実クライアントのAPIイベント解析とサービスの購読経路を通す。
        client.ReceiveApiMessage("""{"type":"error","error":{"code":"insufficient_quota","message":"Quota exceeded"}}""");

        var error = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsInstanceOfType<OpenAIApiException>(error);
        Assert.IsTrue(((OpenAIApiException)error).IsFatal);
        Assert.AreEqual(VoiceTranslationState.Faulted, service.State);
        await service.StopAsync();
        Assert.AreEqual(1, count);
    }

    private sealed class Audio : IVoiceAudioService
    {
        public long SessionId { get; private set; }
        public int StopCount { get; private set; }
        public Exception? StartError { get; set; }
        public Exception? StopError { get; set; }
        public List<string>? Log { get; init; }
        public event Action<VoiceMicrophoneFrame>? MicrophoneFrame;
#pragma warning disable CS0067
        public event Action<long, Exception>? ErrorReceived;
#pragma warning restore CS0067
        public IReadOnlyList<VoiceAudioDevice> GetMicrophones() => [];
        public IReadOnlyList<VoiceAudioDevice> GetOutputs() => [];
        public Action? OnStart { get; set; }
        public Task StartAsync(string microphoneId, string outputDeviceId, CancellationToken ct = default)
        {
            SessionId++;
            OnStart?.Invoke();
            var error = StartError;
            StartError = null;
            return error is null ? Task.CompletedTask : Task.FromException(error);
        }
        public Task StopAsync()
        {
            StopCount++;
            Log?.Add("audio.stop");
            var error = StopError;
            StopError = null;
            return error is null ? Task.CompletedTask : Task.FromException(error);
        }
        public void Emit(VoiceMicrophoneFrame frame) => MicrophoneFrame?.Invoke(frame);
        public void WriteOutput(byte[] pcm16, int sampleRate) { }
        public Action? OnClearOutput { get; set; }
        public void ClearOutput() => OnClearOutput?.Invoke();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Client : IRealtimeSpeechTranslator
    {
        private readonly OpenAIRealtimeClient _parser = new();
        public Client()
        {
            _parser.ErrorReceived += error => ErrorReceived?.Invoke(error);
            _parser.StateChanged += state => { State = state; StateChanged?.Invoke(state); };
        }
        public ConnectionState State { get; private set; }
        public int InputSampleRate => 24000;
        public int OutputSampleRate => 24000;
        public long DroppedAudioChunkCount => 0;
        public long TotalAudioInputSamples24kHz => 0;
        public long ServerReportedAudioInputTokens => 0;
        public int DisposeCount { get; private set; }
        public Exception? DisposeError { get; set; }
        public Exception? ConnectError { get; set; }
        public List<string>? Log { get; init; }
        public event Action<Exception>? ErrorReceived;
        public event Action<ConnectionState>? StateChanged;
#pragma warning disable CS0067
        public event Action<string>? TranscriptDeltaReceived;
        public event Action<string>? TranscriptCompleted;
        public event Action<byte[]>? TranslatedAudioReceived;
#pragma warning restore CS0067
        public Action? OnConnect { get; set; }
        public Task ConnectAsync(OpenAIRealtimeSettings settings, CancellationToken ct = default)
        {
            OnConnect?.Invoke();
            var error = ConnectError;
            ConnectError = null;
            if (error is not null) return Task.FromException(error);
            State = ConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }
        public void Fail(Exception error) => ErrorReceived?.Invoke(error);
        public void ReceiveApiMessage(string json)
        {
            var method = typeof(OpenAIRealtimeClient).GetMethod("ProcessMessage", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            ReadOnlyMemory<byte> bytes = Encoding.UTF8.GetBytes(json);
            method.Invoke(_parser, [bytes]);
        }
        public void SendAudio(byte[] pcm16Audio) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            Log?.Add("client.dispose");
            await _parser.DisposeAsync();
            var error = DisposeError;
            DisposeError = null;
            if (error is not null) throw error;
        }
        public void Dispose() => _parser.Dispose();
    }
}
