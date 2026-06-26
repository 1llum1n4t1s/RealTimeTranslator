using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// Soniox / Speechmatics / Azure クライアントの嫌がらせ + プロトコル互換テスト。
/// ネットワーク接続不要 (状態遷移 + 受信パースを ProcessMessage 直叩き / 静的メソッドで検証)。
/// GeminiLiveClient.adversarial.test.cs と同じ方針。
/// </summary>
[TestClass]
public sealed class NewProvidersClientTests
{
    // ═══════════════════════════════════════════════════════════════
    // 🟢 Soniox
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Soniox_InitialState_ShouldBeDisconnected()
    {
        using var client = new SonioxRealtimeClient();
        Assert.AreEqual(ConnectionState.Disconnected, client.State);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Soniox_InputSampleRate_ShouldBe16000()
    {
        using var client = new SonioxRealtimeClient();
        Assert.AreEqual(16000, client.InputSampleRate);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task Soniox_DisconnectAndDispose_BeforeConnect_ShouldNotCrash()
    {
        var client = new SonioxRealtimeClient();
        await client.DisconnectAsync();
        await client.DisconnectAsync();
        await client.DisposeAsync();
        await client.DisposeAsync();
        Assert.AreEqual(ConnectionState.Disconnected, client.State);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Soniox_ProcessMessage_FinalTranslationToken_ShouldRaiseDelta()
    {
        using var client = new SonioxRealtimeClient();
        string? delta = null;
        client.TranscriptDeltaReceived += d => delta = d;

        // translation_status=="translation" かつ is_final==true のトークンのみ連結して delta。
        InvokeProcessMessage(client,
            "{\"tokens\":[{\"text\":\"こんにちは\",\"is_final\":true,\"translation_status\":\"translation\"}]}");

        Assert.AreEqual("こんにちは", delta);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Soniox_ProcessMessage_NonFinalOrOriginalTokens_ShouldNotRaiseDelta()
    {
        using var client = new SonioxRealtimeClient();
        string? delta = null;
        client.TranscriptDeltaReceived += d => delta = d;

        // 非確定の翻訳トークン (差し替わるので採用しない) + 原文トークン (字幕に出さない)。
        InvokeProcessMessage(client,
            "{\"tokens\":[" +
            "{\"text\":\"とちゅう\",\"is_final\":false,\"translation_status\":\"translation\"}," +
            "{\"text\":\"original\",\"is_final\":true,\"translation_status\":\"original\"}]}");

        Assert.IsNull(delta);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Soniox_ProcessMessage_Finished_ShouldRaiseEmptyCompleted()
    {
        using var client = new SonioxRealtimeClient();
        bool completed = false;
        client.TranscriptCompleted += _ => completed = true;

        InvokeProcessMessage(client, "{\"tokens\":[],\"finished\":true}");

        Assert.IsTrue(completed, "finished で TranscriptCompleted が発火するべき");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Soniox_ProcessMessage_Error_ShouldNotCrash()
    {
        using var client = new SonioxRealtimeClient();
        Exception? err = null;
        client.ErrorReceived += e => err = e;

        InvokeProcessMessage(client, "{\"error_code\":401,\"error_message\":\"invalid api key\"}");

        Assert.IsNotNull(err, "error_code で ErrorReceived が発火するべき");
    }

    // ═══════════════════════════════════════════════════════════════
    // 🟦 Speechmatics
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Speechmatics_InitialState_ShouldBeDisconnected()
    {
        using var client = new SpeechmaticsRealtimeClient();
        Assert.AreEqual(ConnectionState.Disconnected, client.State);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Speechmatics_InputSampleRate_ShouldBe16000()
    {
        using var client = new SpeechmaticsRealtimeClient();
        Assert.AreEqual(16000, client.InputSampleRate);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task Speechmatics_DisconnectAndDispose_BeforeConnect_ShouldNotCrash()
    {
        var client = new SpeechmaticsRealtimeClient();
        await client.DisconnectAsync();
        await client.DisconnectAsync();
        await client.DisposeAsync();
        await client.DisposeAsync();
        Assert.AreEqual(ConnectionState.Disconnected, client.State);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Speechmatics_ProcessMessage_AddTranslation_ShouldRaiseJoinedDelta()
    {
        using var client = new SpeechmaticsRealtimeClient();
        string? delta = null;
        client.TranscriptDeltaReceived += d => delta = d;

        // 確定翻訳 results[].content を連結して delta。
        InvokeProcessMessageSm(client,
            "{\"message\":\"AddTranslation\",\"language\":\"ja\",\"results\":[" +
            "{\"content\":\"こんにちは\"},{\"content\":\"世界\"}]}");

        Assert.AreEqual("こんにちは世界", delta);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Speechmatics_ProcessMessage_PartialTranslation_ShouldNotRaiseDelta()
    {
        using var client = new SpeechmaticsRealtimeClient();
        string? delta = null;
        client.TranscriptDeltaReceived += d => delta = d;

        // partial は破棄 (確定だけ採用)。
        InvokeProcessMessageSm(client,
            "{\"message\":\"AddPartialTranslation\",\"language\":\"ja\",\"results\":[{\"content\":\"とちゅう\"}]}");

        Assert.IsNull(delta);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Speechmatics_ProcessMessage_EndOfTranscript_ShouldRaiseCompleted()
    {
        using var client = new SpeechmaticsRealtimeClient();
        bool completed = false;
        client.TranscriptCompleted += _ => completed = true;

        InvokeProcessMessageSm(client, "{\"message\":\"EndOfTranscript\"}");

        Assert.IsTrue(completed, "EndOfTranscript で TranscriptCompleted が発火するべき");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Speechmatics_ProcessMessage_RecognitionStarted_ShouldNotCrash()
    {
        using var client = new SpeechmaticsRealtimeClient();
        InvokeProcessMessageSm(client, "{\"message\":\"RecognitionStarted\",\"id\":\"abc\"}");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Speechmatics_ProcessMessage_TranslationDisablingWarning_ShouldRaiseError()
    {
        using var client = new SpeechmaticsRealtimeClient();
        Exception? err = null;
        client.ErrorReceived += e => err = e;

        // 翻訳ペア非対応 Warning は握りつぶさず ErrorReceived で通知 (字幕が出ない原因をユーザーに知らせる)。
        InvokeProcessMessageSm(client,
            "{\"message\":\"Warning\",\"type\":\"unsupported_translation_pair\",\"reason\":\"ja->xx not supported\"}");

        Assert.IsNotNull(err, "翻訳無効 Warning で ErrorReceived が発火するべき");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Speechmatics_ProcessMessage_BenignWarning_ShouldNotRaiseError()
    {
        using var client = new SpeechmaticsRealtimeClient();
        Exception? err = null;
        client.ErrorReceived += e => err = e;

        // 翻訳に影響しない Warning は通知しない (ノイズを増やさない)。
        InvokeProcessMessageSm(client,
            "{\"message\":\"Warning\",\"type\":\"duration_limit_exceeded\",\"reason\":\"info only\"}");

        Assert.IsNull(err, "翻訳に影響しない Warning では ErrorReceived を発火しないべき");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Speechmatics_IsTranslationDisablingWarning_ShouldClassify()
    {
        Assert.IsTrue(SpeechmaticsRealtimeClient.IsTranslationDisablingWarning("unsupported_translation_pair"));
        Assert.IsTrue(SpeechmaticsRealtimeClient.IsTranslationDisablingWarning("empty_translation_target_list"));
        Assert.IsFalse(SpeechmaticsRealtimeClient.IsTranslationDisablingWarning("duration_limit_exceeded"));
        Assert.IsFalse(SpeechmaticsRealtimeClient.IsTranslationDisablingWarning(null));
    }

    // ═══════════════════════════════════════════════════════════════
    // 🟪 Azure (Speech SDK ラッパ)
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Azure_InitialState_ShouldBeDisconnected()
    {
        using var client = new AzureSpeechTranslationClient();
        Assert.AreEqual(ConnectionState.Disconnected, client.State);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Azure_InputSampleRate_ShouldBe16000()
    {
        using var client = new AzureSpeechTranslationClient();
        Assert.AreEqual(16000, client.InputSampleRate);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task Azure_DisconnectAndDispose_BeforeConnect_ShouldNotCrash()
    {
        var client = new AzureSpeechTranslationClient();
        await client.DisconnectAsync();
        await client.DisconnectAsync();
        await client.DisposeAsync();
        await client.DisposeAsync();
        Assert.AreEqual(ConnectionState.Disconnected, client.State);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Azure_MapToAzureTargetLanguage_ShouldNormalize()
    {
        // zh は variant (簡体字) を既定に。 明確なコードはそのまま。 空/null は ja。
        Assert.AreEqual("zh-Hans", AzureSpeechTranslationClient.MapToAzureTargetLanguage("zh"));
        Assert.AreEqual("en", AzureSpeechTranslationClient.MapToAzureTargetLanguage("en"));
        Assert.AreEqual("ja", AzureSpeechTranslationClient.MapToAzureTargetLanguage(""));
        Assert.AreEqual("ja", AzureSpeechTranslationClient.MapToAzureTargetLanguage(null));
    }

    // ═══════════════════════════════════════════════════════════════
    // 💰 CostEstimator レート解決 (新 provider)
    // ═══════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Adversarial")]
    public void CostEstimator_NewProviders_ResolveDistinctRates()
    {
        // Soniox (最安) < Speechmatics < Azure の順。 OpenAI フル ($32/1M) より全部安い。
        var soniox = CostEstimator.ResolveRatePerMillion("stt-rt-v5");
        var sonioxByName = CostEstimator.ResolveRatePerMillion("soniox");
        var speechmatics = CostEstimator.ResolveRatePerMillion("speechmatics-rt");
        var azure = CostEstimator.ResolveRatePerMillion("azure-translation");

        Assert.AreEqual(soniox, sonioxByName, "model 名 / プロバイダ名どちらでも同レート");
        Assert.IsTrue(soniox < speechmatics, "Soniox は Speechmatics より安い");
        Assert.IsTrue(speechmatics < azure, "Speechmatics は Azure より安い");
        Assert.IsTrue(azure < 32m, "新 provider は OpenAI フルレートより安い");
    }

    // ═══════════════════════════════════════════════════════════════
    // helper
    // ═══════════════════════════════════════════════════════════════

    private static void InvokeProcessMessage(SonioxRealtimeClient client, string json)
    {
        var method = typeof(SonioxRealtimeClient).GetMethod(
            "ProcessMessage", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "Soniox ProcessMessage メソッドが見つからない");
        ReadOnlyMemory<byte> bytes = Encoding.UTF8.GetBytes(json);
        method!.Invoke(client, new object[] { bytes });
    }

    private static void InvokeProcessMessageSm(SpeechmaticsRealtimeClient client, string json)
    {
        var method = typeof(SpeechmaticsRealtimeClient).GetMethod(
            "ProcessMessage", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "Speechmatics ProcessMessage メソッドが見つからない");
        ReadOnlyMemory<byte> bytes = Encoding.UTF8.GetBytes(json);
        method!.Invoke(client, new object[] { bytes });
    }
}
