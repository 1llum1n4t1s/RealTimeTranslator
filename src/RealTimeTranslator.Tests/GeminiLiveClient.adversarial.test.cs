using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// GeminiLiveClient の嫌がらせ + プロトコル互換テスト。
/// ネットワーク接続不要 (接続は失敗前提で状態遷移を検証、 受信は ProcessMessage を直接叩く)。
/// </summary>
[TestClass]
public sealed class GeminiLiveClientAdversarialTests
{
    // ═══════════════════════════════════════════════════════════════
    // 🔀 状態遷移
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Adversarial")]
    public void InitialState_ShouldBeDisconnected()
    {
        using var client = new GeminiLiveClient();
        Assert.AreEqual(ConnectionState.Disconnected, client.State);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void InputSampleRate_ShouldBe16000()
    {
        using var client = new GeminiLiveClient();
        // Gemini Live は入力 16kHz 固定。 TranslationPipelineService の送信レート分岐がこの値を見る。
        Assert.AreEqual(16000, client.InputSampleRate);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task DisconnectAsync_BeforeConnect_ShouldNotCrash()
    {
        await using var client = new GeminiLiveClient();
        await client.DisconnectAsync();
        Assert.AreEqual(ConnectionState.Disconnected, client.State);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task DisconnectAsync_DoubleCalled_ShouldNotCrash()
    {
        await using var client = new GeminiLiveClient();
        await client.DisconnectAsync();
        await client.DisconnectAsync();
        Assert.AreEqual(ConnectionState.Disconnected, client.State);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task DisposeAsync_DoubleCalled_ShouldNotCrash()
    {
        var client = new GeminiLiveClient();
        await client.DisposeAsync();
        await client.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void SendAudio_BeforeConnect_ShouldNotCrash()
    {
        using var client = new GeminiLiveClient();
        client.SendAudio(new byte[3200]); // 16k/100ms 相当
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void SendAudio_EmptyAndLarge_ShouldNotCrash()
    {
        using var client = new GeminiLiveClient();
        client.SendAudio([]);
        client.SendAudio(new byte[1_000_000]);
    }

    // ═══════════════════════════════════════════════════════════════
    // 🛡️ エンドポイント検証 (host allowlist = generativelanguage.googleapis.com)
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public async Task ConnectAsync_InvalidHost_ShouldFailGracefully()
    {
        await using var client = new GeminiLiveClient();
        Exception? receivedError = null;
        client.ErrorReceived += ex => receivedError = ex;

        // 許可リスト外ホストへ API キーを送る経路を ValidateEndpoint が塞ぐ。
        var settings = new GeminiLiveSettings
        {
            ApiKey = "fake-key",
            Endpoint = "wss://evil.example.com/ws",
        };
        try { await client.ConnectAsync(settings); }
        catch { /* 失敗は想定通り */ }

        // ValidateEndpoint が try 内で投げ、 catch(Exception) が Disconnected + ErrorReceived に倒すことまで固定
        // (Connecting に取り残される回帰を検知 — CodeRabbit nitpick)。
        Assert.AreEqual(ConnectionState.Disconnected, client.State, "許可リスト外ホストでは Disconnected に倒れる");
        Assert.IsNotNull(receivedError, "失敗時は ErrorReceived が発火する");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public async Task TestConnectionAsync_InvalidHost_ShouldReturnFalse()
    {
        var settings = new GeminiLiveSettings
        {
            ApiKey = "fake-key",
            Endpoint = "wss://evil.example.com/ws",
        };
        var (success, message) = await GeminiLiveClient.TestConnectionAsync(settings);
        Assert.IsFalse(success, "許可リスト外ホストは接続テストで false");
        Assert.IsFalse(string.IsNullOrEmpty(message));
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public async Task TestConnectionAsync_EmptyApiKey_ShouldReturnFalse()
    {
        var settings = new GeminiLiveSettings { ApiKey = "" };
        var (success, _) = await GeminiLiveClient.TestConnectionAsync(settings);
        Assert.IsFalse(success);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    [Timeout(10000)]
    public async Task ConnectAsync_WithCancelledToken_ShouldThrowOperationCanceled()
    {
        await using var client = new GeminiLiveClient();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var settings = new GeminiLiveSettings { ApiKey = "k" };
        OperationCanceledException? caught = null;
        try { await client.ConnectAsync(settings, cts.Token); }
        catch (OperationCanceledException ex) { caught = ex; }

        Assert.IsNotNull(caught, "キャンセル済みトークンで OperationCanceledException が投げられるべき");
    }

    // ═══════════════════════════════════════════════════════════════
    // 🎭 受信メッセージのスキーマ (serverContent.outputTranscription 等)
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Adversarial")]
    public void ProcessMessage_OutputTranscription_ShouldRaiseDelta()
    {
        using var client = new GeminiLiveClient();
        string? receivedDelta = null;
        client.TranscriptDeltaReceived += d => receivedDelta = d;

        // 翻訳テキストは outputTranscription.text。 常に delta として流す設計。
        InvokeProcessMessage(client, "{\"serverContent\":{\"outputTranscription\":{\"text\":\"こんにちは\"}}}");

        Assert.AreEqual("こんにちは", receivedDelta);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void ProcessMessage_TurnComplete_ShouldRaiseEmptyCompleted()
    {
        using var client = new GeminiLiveClient();
        bool completed = false;
        string? final = "未設定";
        client.TranscriptCompleted += t => { completed = true; final = t; };

        // turnComplete は trailing 確定の合図。 空 done を流して Pipeline 側に未確定分を確定させる。
        InvokeProcessMessage(client, "{\"serverContent\":{\"turnComplete\":true}}");

        Assert.IsTrue(completed, "turnComplete で TranscriptCompleted が発火するべき");
        Assert.AreEqual("", final, "Gemini の done は空文字 (累積全文を渡さない)");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void ProcessMessage_InputTranscriptionOnly_ShouldNotRaiseDelta()
    {
        using var client = new GeminiLiveClient();
        string? receivedDelta = null;
        client.TranscriptDeltaReceived += d => receivedDelta = d;

        // inputTranscription (原文) は字幕に使わない。 delta は発火しない。
        InvokeProcessMessage(client, "{\"serverContent\":{\"inputTranscription\":{\"text\":\"original\"}}}");

        Assert.IsNull(receivedDelta);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void ProcessMessage_SetupComplete_ShouldNotCrash()
    {
        using var client = new GeminiLiveClient();
        InvokeProcessMessage(client, "{\"setupComplete\":{}}");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void ProcessMessage_ModelTurnAudioOnly_ShouldNotRaiseDelta()
    {
        using var client = new GeminiLiveClient();
        string? receivedDelta = null;
        client.TranscriptDeltaReceived += d => receivedDelta = d;

        // 出力音声 (modelTurn の inlineData) は破棄。 字幕 delta は発火しない。
        InvokeProcessMessage(client,
            "{\"serverContent\":{\"modelTurn\":{\"parts\":[{\"inlineData\":{\"data\":\"AAAA\"}}]}}}");

        Assert.IsNull(receivedDelta);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void ProcessMessage_GarbageJson_ShouldNotThrow()
    {
        using var client = new GeminiLiveClient();
        InvokeProcessMessage(client, "{not valid json");
        InvokeProcessMessage(client, "{}");
        InvokeProcessMessage(client, "[]");
    }

    // ═══════════════════════════════════════════════════════════════
    // 🌪️ 設定デフォルト + リソース
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Adversarial")]
    public void GeminiLiveSettings_Defaults_ShouldBeValid()
    {
        var s = new GeminiLiveSettings();
        Assert.AreEqual("", s.ApiKey);
        Assert.AreEqual("ja", s.OutputLanguage);
        Assert.IsTrue(s.Model.Contains("gemini"), "既定モデルは gemini 系");
        Assert.IsTrue(s.Endpoint.StartsWith("wss://"));
        Assert.IsTrue(s.Endpoint.Contains("generativelanguage.googleapis.com"));
        Assert.IsTrue(s.EchoTargetLanguage);
        Assert.IsTrue(s.MaxReconnectAttempts > 0);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void EventHandlers_ShouldBeUnsubscribable()
    {
        using var client = new GeminiLiveClient();
        Action<string> deltaHandler = _ => { };
        Action<ConnectionState> stateHandler = _ => { };
        client.TranscriptDeltaReceived += deltaHandler;
        client.StateChanged += stateHandler;
        client.TranscriptDeltaReceived -= deltaHandler;
        client.StateChanged -= stateHandler;
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task CreateAndDispose_RepeatedCycles_ShouldNotLeakResources()
    {
        var before = GC.GetTotalMemory(true);
        for (int i = 0; i < 50; i++)
        {
            await using var client = new GeminiLiveClient();
            client.SendAudio(new byte[3200]);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var after = GC.GetTotalMemory(true);
        Assert.IsTrue(after - before < 32 * 1024 * 1024, $"50 サイクル後のメモリ成長が異常: {(after - before) / 1024.0:F0}KB");
    }

    // ═══════════════════════════════════════════════════════════════
    // 🌐 言語コードマッピング (provider 非依存 UI コード → Gemini BCP-47)
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Adversarial")]
    public void MapToGeminiLanguageCode_AmbiguousCodes_MapToBcp47Variants()
    {
        // zh / pt は generic すぎて Gemini が variant を要求するため明示マップ (Codex 指摘 P2)。
        Assert.AreEqual("zh-Hans", GeminiLiveClient.MapToGeminiLanguageCode("zh"));
        Assert.AreEqual("pt-BR", GeminiLiveClient.MapToGeminiLanguageCode("pt"));
        // 明確なコードはそのまま通す。
        Assert.AreEqual("en", GeminiLiveClient.MapToGeminiLanguageCode("en"));
        Assert.AreEqual("ko", GeminiLiveClient.MapToGeminiLanguageCode("ko"));
        // 空/null は ja 既定。
        Assert.AreEqual("ja", GeminiLiveClient.MapToGeminiLanguageCode(""));
        Assert.AreEqual("ja", GeminiLiveClient.MapToGeminiLanguageCode(null));
    }

    private static void InvokeProcessMessage(GeminiLiveClient client, string json)
    {
        var method = typeof(GeminiLiveClient).GetMethod(
            "ProcessMessage", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "ProcessMessage メソッドが見つからない");
        ReadOnlyMemory<byte> bytes = Encoding.UTF8.GetBytes(json);
        method.Invoke(client, new object[] { bytes });
    }
}
