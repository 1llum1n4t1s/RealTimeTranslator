using System.Reflection;
using System.Text;
using System.Text.Json;
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
    public void Soniox_ProcessMessage_EndpointToken_ShouldRaiseCompleted()
    {
        using var client = new SonioxRealtimeClient();
        bool completed = false;
        client.TranscriptCompleted += _ => completed = true;

        // endpoint detection の "<end>" トークンで文を確定 (TranscriptCompleted 発火) するべき。
        InvokeProcessMessage(client,
            "{\"tokens\":[{\"text\":\"<end>\",\"is_final\":true}]}");

        Assert.IsTrue(completed, "<end> トークンで TranscriptCompleted が発火するべき");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Soniox_ClassifyError_QuotaAndAuthAreFatal()
    {
        // 402 / balance 系は QuotaExceeded、 401 は InvalidApiKey (どちらも IsFatal で再接続ループを止める)。
        Assert.AreEqual(OpenAIApiErrorKind.QuotaExceeded, SonioxRealtimeClient.ClassifySonioxError("402", "Organization balance exhausted"));
        Assert.AreEqual(OpenAIApiErrorKind.QuotaExceeded, SonioxRealtimeClient.ClassifySonioxError("", "monthly budget exhausted"));
        Assert.AreEqual(OpenAIApiErrorKind.InvalidApiKey, SonioxRealtimeClient.ClassifySonioxError("401", "bad key"));
        Assert.AreEqual(OpenAIApiErrorKind.RateLimit, SonioxRealtimeClient.ClassifySonioxError("429", "slow down"));
        // 種別が実際に IsFatal であることまで確認 (再接続ループ防止の核心。 fatal 判定が変わったら検知する)。
        Assert.IsTrue(new OpenAIApiException(OpenAIApiErrorKind.QuotaExceeded, "", "").IsFatal);
        Assert.IsTrue(new OpenAIApiException(OpenAIApiErrorKind.InvalidApiKey, "", "").IsFatal);
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
    public void Speechmatics_ProcessMessage_AddTranslation_ShouldFinalizeSegment()
    {
        using var client = new SpeechmaticsRealtimeClient();
        string? delta = null;
        bool completed = false;
        client.TranscriptDeltaReceived += d => delta = d;
        client.TranscriptCompleted += _ => completed = true;

        // AddTranslation は pause 区切りの確定セグメント → delta 後に空 done で確定させる
        // (句読点なしの短い発話が次発話と融合するのを防ぐ)。
        InvokeProcessMessageSm(client,
            "{\"message\":\"AddTranslation\",\"language\":\"en\",\"results\":[{\"content\":\"Yes\"}]}");

        Assert.AreEqual("Yes", delta);
        Assert.IsTrue(completed, "AddTranslation はセグメントごとに TranscriptCompleted を発火するべき");
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

        // 翻訳に影響せず終了でもない Warning は通知しない (ノイズを増やさない)。
        // ※ duration_limit_exceeded は terminal warning なので別テストで通知を検証する。
        InvokeProcessMessageSm(client,
            "{\"message\":\"Warning\",\"type\":\"low_quality_audio\",\"reason\":\"info only\"}");

        Assert.IsNull(err, "良性 Warning では ErrorReceived を発火しないべき");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Speechmatics_ProcessMessage_TerminalWarning_ShouldRaiseError()
    {
        using var client = new SpeechmaticsRealtimeClient();
        Exception? err = null;
        client.ErrorReceived += e => err = e;

        // duration_limit_exceeded は以降の音声が無視される terminal warning → 通知する。
        InvokeProcessMessageSm(client,
            "{\"message\":\"Warning\",\"type\":\"duration_limit_exceeded\",\"reason\":\"limit reached\"}");

        Assert.IsNotNull(err, "terminal warning で ErrorReceived が発火するべき");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Speechmatics_ClassifyError_FatalVsRecoverable()
    {
        // not_authorised (4001) と timelimit_exceeded (契約使用量クォータ到達=4006) は回復不能 → fatal。
        Assert.AreEqual(OpenAIApiErrorKind.InvalidApiKey, SpeechmaticsRealtimeClient.ClassifySpeechmaticsError("not_authorised", "bad key"));
        Assert.AreEqual(OpenAIApiErrorKind.QuotaExceeded, SpeechmaticsRealtimeClient.ClassifySpeechmaticsError("timelimit_exceeded", "usage quota reached"));
        Assert.IsTrue(new OpenAIApiException(OpenAIApiErrorKind.InvalidApiKey, "", "").IsFatal);
        Assert.IsTrue(new OpenAIApiException(OpenAIApiErrorKind.QuotaExceeded, "", "").IsFatal);

        // ⚠️ quota_exceeded (4005) は公式には「同時接続数上限」= 一時的。 RateLimit (非 fatal) でなければ、
        // 同時接続上限に一瞬当たっただけで回復可能なセッションを永久に殺してしまう (実機監査で誤分類を発見・修正)。
        Assert.AreEqual(OpenAIApiErrorKind.RateLimit, SpeechmaticsRealtimeClient.ClassifySpeechmaticsError("quota_exceeded", "concurrent connections"));
        Assert.IsFalse(new OpenAIApiException(OpenAIApiErrorKind.RateLimit, "", "").IsFatal);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Speechmatics_JoinResults_SpaceDelimitedInsertsSpaces_CjkConcatenates()
    {
        using var doc = JsonDocument.Parse("{\"results\":[{\"content\":\"Hello\"},{\"content\":\"world\"},{\"content\":\".\"}]}");
        // 空白区切り言語: セグメント間に空白、 ただし句読点 "." の前は入れない。
        Assert.AreEqual("Hello world.", SpeechmaticsRealtimeClient.JoinResults(doc.RootElement, spaceDelimited: true));
        // CJK: 直結 (従来動作)。
        Assert.AreEqual("Helloworld.", SpeechmaticsRealtimeClient.JoinResults(doc.RootElement, spaceDelimited: false));
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Speechmatics_IsCjkOutputLanguage_ShouldClassify()
    {
        Assert.IsTrue(SpeechmaticsRealtimeClient.IsCjkOutputLanguage("ja"));
        Assert.IsTrue(SpeechmaticsRealtimeClient.IsCjkOutputLanguage("zh-Hans"));
        Assert.IsTrue(SpeechmaticsRealtimeClient.IsCjkOutputLanguage("ko"));
        Assert.IsFalse(SpeechmaticsRealtimeClient.IsCjkOutputLanguage("en"));
        Assert.IsFalse(SpeechmaticsRealtimeClient.IsCjkOutputLanguage("de"));
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Speechmatics_IsTerminalWarning_ShouldClassify()
    {
        Assert.IsTrue(SpeechmaticsRealtimeClient.IsTerminalWarning("duration_limit_exceeded"));
        Assert.IsFalse(SpeechmaticsRealtimeClient.IsTerminalWarning("unsupported_translation_pair"));
        Assert.IsFalse(SpeechmaticsRealtimeClient.IsTerminalWarning(null));
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
