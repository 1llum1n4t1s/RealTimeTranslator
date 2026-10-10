using System.Net.WebSockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// OpenAIRealtimeClient の嫌がらせテスト
/// ネットワーク接続不要のユニットテスト（接続は失敗前提で状態遷移を検証）
/// </summary>
[TestClass]
public sealed class OpenAIRealtimeClientAdversarialTests
{
    // ═══════════════════════════════════════════════════════════════
    // 🔀 カテゴリ4: 状態遷移の矛盾（State Machine Abuse）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="state" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void InitialState_ShouldBeDisconnected()
    {
        using var client = new OpenAIRealtimeClient();
        Assert.AreEqual(ConnectionState.Disconnected, client.State);
    }

    /// <adversarial category="state" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task DisconnectAsync_BeforeConnect_ShouldNotCrash()
    {
        await using var client = new OpenAIRealtimeClient();
        await client.DisconnectAsync();
        Assert.AreEqual(ConnectionState.Disconnected, client.State);
    }

    /// <adversarial category="state" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task DisconnectAsync_DoubleCalled_ShouldNotCrash()
    {
        await using var client = new OpenAIRealtimeClient();
        await client.DisconnectAsync();
        await client.DisconnectAsync();
        Assert.AreEqual(ConnectionState.Disconnected, client.State);
    }

    /// <adversarial category="state" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task DisposeAsync_DoubleCalled_ShouldNotCrash()
    {
        var client = new OpenAIRealtimeClient();
        await client.DisposeAsync();
        await client.DisposeAsync();
    }

    /// <adversarial category="state" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void SendAudio_BeforeConnect_ShouldNotCrash()
    {
        using var client = new OpenAIRealtimeClient();
        client.SendAudio(new byte[4800]);
    }

    /// <adversarial category="state" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task SendAudio_AfterDisconnect_ShouldNotCrash()
    {
        await using var client = new OpenAIRealtimeClient();
        await client.DisconnectAsync();
        client.SendAudio(new byte[4800]);
    }

    /// <adversarial category="state" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public async Task ConnectAsync_WithInvalidEndpoint_ShouldFailGracefully()
    {
        await using var client = new OpenAIRealtimeClient();
        Exception? receivedError = null;
        client.ErrorReceived += ex => receivedError = ex;

        var settings = new OpenAIRealtimeSettings
        {
            ApiKey = "sk-fake-key",
            Endpoint = "wss://localhost:1",
            Model = "test"
        };

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(OpenAIRealtimeClient).GetField("_totalAudioInputSamples24kHz", flags)!.SetValue(client, 24000L);
        typeof(OpenAIRealtimeClient).GetField("_serverReportedAudioInputTokens", flags)!.SetValue(client, 100L);
        typeof(OpenAIRealtimeClient).GetField("_totalDroppedAudioChunks", flags)!.SetValue(client, 3L);
        typeof(OpenAIRealtimeClient).GetField("_totalDeltaCount", flags)!.SetValue(client, 7L);
        typeof(OpenAIRealtimeClient).GetField("_totalDoneCount", flags)!.SetValue(client, 2L);
        typeof(OpenAIRealtimeClient).GetField("_lastTranscriptResponseId", flags)!.SetValue(client, "response-previous");
        typeof(OpenAIRealtimeClient).GetField("_lastTranscriptEventGroup", flags)!.SetValue(client, "audio_transcript");

        // 例外型は環境（OS / .NET ランタイム）依存で WebSocketException 以外
        // （SocketException / HttpRequestException 等）になることがあるため、型は固定せず
        // 「失敗イベントが通知され、状態が Connected にならない」ことだけを検証する。
        try { await client.ConnectAsync(settings); }
        catch { /* 接続失敗は想定通り */ }

        Assert.AreNotEqual(ConnectionState.Connected, client.State, "失敗時に Connected 状態にはならない");

        var shouldReconnect = (bool)typeof(OpenAIRealtimeClient).GetField("_shouldReconnect", flags)!.GetValue(client)!;
        var cts = typeof(OpenAIRealtimeClient).GetField("_cts", flags)!.GetValue(client);
        var ws = typeof(OpenAIRealtimeClient).GetField("_ws", flags)!.GetValue(client);
        var capturedSettings = (OpenAIRealtimeSettings)typeof(OpenAIRealtimeClient)
            .GetField("_settings", flags)!.GetValue(client)!;

        Assert.IsFalse(shouldReconnect, "初回接続失敗後は network 復帰再接続を武装解除する");
        Assert.IsNull(cts, "初回接続失敗後は linked CTS を解放する");
        Assert.IsNull(ws, "初回接続失敗後は WebSocket を破棄する");
        Assert.AreNotSame(settings, capturedSettings, "走行中変更が再接続へ漏れないよう設定をコピーする");
        settings.ApiKey = "changed-after-connect";
        Assert.AreEqual("sk-fake-key", capturedSettings.ApiKey);
        Assert.AreEqual(0L, client.TotalAudioInputSamples24kHz, "新しい Start では前セッションの音声サンプル累積を破棄する");
        Assert.AreEqual(0L, client.ServerReportedAudioInputTokens, "新しい Start では前セッションの server token 累積を破棄する");
        Assert.AreEqual(0L, client.DroppedAudioChunkCount, "新しい Start では前セッションの drop 累積を破棄する");
        Assert.AreEqual(0L, typeof(OpenAIRealtimeClient).GetField("_totalDeltaCount", flags)!.GetValue(client));
        Assert.AreEqual(0L, typeof(OpenAIRealtimeClient).GetField("_totalDoneCount", flags)!.GetValue(client));
        Assert.IsNull(typeof(OpenAIRealtimeClient).GetField("_lastTranscriptResponseId", flags)!.GetValue(client));
        Assert.IsNull(typeof(OpenAIRealtimeClient).GetField("_lastTranscriptEventGroup", flags)!.GetValue(client));
    }

    /// <adversarial category="state" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public async Task ConnectAsync_StateChangedEvents_ShouldFireInOrder()
    {
        await using var client = new OpenAIRealtimeClient();
        // StateChanged は別スレッドから発火する可能性があるため、List<T> を lock で保護する
        var states = new List<ConnectionState>();
        var statesLock = new object();
        client.StateChanged += state =>
        {
            lock (statesLock)
            {
                states.Add(state);
            }
        };

        var settings = new OpenAIRealtimeSettings
        {
            ApiKey = "sk-fake",
            Endpoint = "wss://localhost:1",
            Model = "test"
        };

        try { await client.ConnectAsync(settings); }
        catch { /* expected */ }

        ConnectionState[] snapshot;
        lock (statesLock)
        {
            snapshot = states.ToArray();
        }

        Assert.IsTrue(snapshot.Length >= 1, $"状態変更イベントが発火すべき（実際: {snapshot.Length}）");
        Assert.AreEqual(ConnectionState.Connecting, snapshot[0], "最初の状態は Connecting");
    }

    // ═══════════════════════════════════════════════════════════════
    // 🎭 サーバーイベントのスキーマ互換性
    //   GPT Realtime API のサーバーイベント名は時期により以下のバリエーションがある:
    //   - response.output_audio_transcript.{delta,done}（現行・音声出力）
    //   - response.output_text.{delta,done}（現行・テキスト出力）
    //   - session.output_transcript.{delta,done} / output_transcript.{delta,done}（旧 Translate 専用）
    //   - response.audio_transcript.{delta,done}（旧）
    //   このテストは ProcessMessage が全バリエーションでイベントを発火することを保証する。
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Adversarial")]
    [DataRow("response.output_audio_transcript.delta")]
    [DataRow("response.output_text.delta")]
    [DataRow("session.output_transcript.delta")]
    [DataRow("response.audio_transcript.delta")]
    [DataRow("output_transcript.delta")]
    public void ProcessMessage_DeltaEvent_ShouldRaiseTranscriptDeltaReceived(string eventType)
    {
        using var client = new OpenAIRealtimeClient();
        string? receivedDelta = null;
        client.TranscriptDeltaReceived += d => receivedDelta = d;

        var json = $"{{\"type\":\"{eventType}\",\"delta\":\"こんにちは\"}}";
        InvokeProcessMessage(client, json);

        Assert.AreEqual("こんにちは", receivedDelta, $"イベント '{eventType}' でデルタが伝播するべき");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    [DataRow("response.output_audio_transcript.done", "transcript")]
    [DataRow("response.output_text.done", "text")]
    [DataRow("session.output_transcript.done", "transcript")]
    [DataRow("response.audio_transcript.done", "transcript")]
    [DataRow("output_transcript.done", "transcript")]
    public void ProcessMessage_DoneEvent_ShouldRaiseTranscriptCompleted(string eventType, string finalField)
    {
        using var client = new OpenAIRealtimeClient();
        string? receivedFinal = null;
        client.TranscriptCompleted += t => receivedFinal = t;

        var json = $"{{\"type\":\"{eventType}\",\"{finalField}\":\"完成テキスト\"}}";
        InvokeProcessMessage(client, json);

        Assert.AreEqual("完成テキスト", receivedFinal, $"イベント '{eventType}' で最終テキストが伝播するべき");
    }

    private static void InvokeProcessMessage(OpenAIRealtimeClient client, string json)
    {
        var method = typeof(OpenAIRealtimeClient).GetMethod(
            "ProcessMessage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(method, "ProcessMessage メソッドが見つからない");
        // ProcessMessage は ReadOnlyMemory<byte> を取る（UTF-16 経由を避けるためのホットパス最適化）。
        // リフレクションは厳密な型一致を要求するため、Memory<byte> ではなく ReadOnlyMemory<byte> に明示変換して box 化する。
        ReadOnlyMemory<byte> bytes = System.Text.Encoding.UTF8.GetBytes(json);
        method.Invoke(client, new object[] { bytes });
    }

    // ═══════════════════════════════════════════════════════════════
    // 🗡️ カテゴリ1: 境界値・極端入力（Boundary Assault）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void SendAudio_EmptyArray_ShouldNotCrash()
    {
        using var client = new OpenAIRealtimeClient();
        client.SendAudio([]);
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void SendAudio_LargePayload_ShouldNotCrash()
    {
        using var client = new OpenAIRealtimeClient();
        client.SendAudio(new byte[1_000_000]);
    }

    /// <adversarial category="boundary" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public async Task ConnectAsync_EmptyApiKey_ShouldFail()
    {
        await using var client = new OpenAIRealtimeClient();
        var settings = new OpenAIRealtimeSettings
        {
            ApiKey = "",
            Endpoint = "wss://localhost:1",
            Model = "test"
        };

        // 接続は失敗するが、空APIキーでもクラッシュしない
        // 例外型は環境依存（WebSocketException / SocketException / HttpRequestException 等）のため固定しない
        Exception? caught = null;
        try { await client.ConnectAsync(settings); }
        catch (Exception ex) { caught = ex; }

        Assert.IsNotNull(caught, "空APIキーまたは無効エンドポイントで接続が失敗するべき");
        Assert.AreNotEqual(ConnectionState.Connected, client.State);
    }

    /// <adversarial category="boundary" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public async Task ConnectAsync_WithCancelledToken_ShouldThrowOperationCanceled()
    {
        await using var client = new OpenAIRealtimeClient();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var settings = new OpenAIRealtimeSettings
        {
            ApiKey = "sk-test",
            Endpoint = "wss://api.openai.com/v1/realtime/translations",
            Model = "test"
        };

        // OperationCanceledException の派生型（TaskCanceledException 等）も受け入れる
        OperationCanceledException? caught = null;
        try { await client.ConnectAsync(settings, cts.Token); }
        catch (OperationCanceledException ex) { caught = ex; }

        Assert.IsNotNull(caught, "キャンセル済みトークンで OperationCanceledException またはその派生例外が投げられるべき");
    }

    // ═══════════════════════════════════════════════════════════════
    // ⚡ カテゴリ2: 並行性（Concurrency Chaos）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="concurrency" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public void SendAudio_ConcurrentCalls_ShouldNotCrash()
    {
        using var client = new OpenAIRealtimeClient();
        // 未接続状態で並行SendAudio — すべて静かにドロップされるべき
        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            for (int j = 0; j < 100; j++)
                client.SendAudio(new byte[4800]);
        })).ToArray();
        Task.WaitAll(tasks);
    }

    /// <adversarial category="concurrency" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public async Task DisconnectAsync_ConcurrentCalls_ShouldNotDeadlock()
    {
        await using var client = new OpenAIRealtimeClient();
        var tasks = Enumerable.Range(0, 5).Select(_ =>
            client.DisconnectAsync()).ToArray();
        await Task.WhenAll(tasks);
    }

    // ═══════════════════════════════════════════════════════════════
    // 💀 カテゴリ3: リソース枯渇（Resource Exhaustion）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="resource" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task CreateAndDispose_RepeatedCycles_ShouldNotLeakResources()
    {
        var before = GC.GetTotalMemory(true);
        for (int i = 0; i < 50; i++)
        {
            await using var client = new OpenAIRealtimeClient();
            client.SendAudio(new byte[4800]);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var after = GC.GetTotalMemory(true);
        var growth = after - before;
        Assert.IsTrue(growth < 32 * 1024 * 1024,
            $"50サイクル後のメモリ成長が異常: {growth / 1024.0:F0}KB");
    }

    /// <adversarial category="resource" severity="high" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public void SendAudio_FloodBeforeConnect_ShouldDropGracefully()
    {
        using var client = new OpenAIRealtimeClient();
        // 未接続状態で大量送信 — Channel未作成なので全部ドロップ
        for (int i = 0; i < 10_000; i++)
            client.SendAudio(new byte[4800]);
    }

    // ═══════════════════════════════════════════════════════════════
    // 🌪️ カテゴリ6: 環境異常（Environmental Chaos）
    // ═══════════════════════════════════════════════════════════════

    /// <adversarial category="chaos" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public async Task ConnectAsync_MaxReconnectAttemptsZero_ShouldNotRetryIndefinitely()
    {
        await using var client = new OpenAIRealtimeClient();
        var settings = new OpenAIRealtimeSettings
        {
            ApiKey = "sk-test",
            Endpoint = "wss://localhost:1",
            Model = "test",
            MaxReconnectAttempts = 0
        };

        try { await client.ConnectAsync(settings); }
        catch { /* expected */ }

        // 再接続が無限ループにならないことを確認
        await Task.Delay(500);
        Assert.AreNotEqual(ConnectionState.Connected, client.State);
    }

    /// <adversarial category="chaos" severity="medium" />
    [TestMethod]
    [TestCategory("Adversarial")]
    public void OpenAIRealtimeSettings_Defaults_ShouldBeValid()
    {
        var settings = new OpenAIRealtimeSettings();
        Assert.AreEqual("", settings.ApiKey);
        Assert.AreEqual("ja", settings.OutputLanguage);
        Assert.AreEqual("gpt-realtime-translate", settings.Model);
        Assert.IsTrue(settings.Endpoint.StartsWith("wss://"));
        Assert.IsTrue(settings.ReconnectDelayMs > 0);
        Assert.IsTrue(settings.MaxReconnectAttempts > 0);
    }
}
