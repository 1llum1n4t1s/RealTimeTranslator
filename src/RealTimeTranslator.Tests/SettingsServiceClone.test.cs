using Microsoft.VisualStudio.TestTools.UnitTesting;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// <see cref="SettingsService.CloneWithEncryptedSecrets"/> のコピー漏れ回帰テスト。
///
/// <para>
/// 背景: このメソッドは各 *Settings を新インスタンスへ手書きコピーするため、 プロパティを追加したときに
/// ここへの追記を忘れると「settings.json に default が書き戻される」= ユーザーが手編集した値が
/// UI の autosave のたびに静かに消えるバグになる。 rere C2-001 (SilencePaddingMs / MaxPartialChars) に続き、
/// v1.0.49 で追加した DeltaIdleFinalizeMs でも同じ漏れが起きていた (Grok audit 指摘)。
/// </para>
/// </summary>
[TestClass]
public sealed class SettingsServiceCloneTests
{
    /// <summary>OpenAI の非 default 値を詰めた AppSettings を作る。</summary>
    private static AppSettings BuildNonDefaultSettings() => new()
    {
        OpenAIRealtime = new OpenAIRealtimeSettings
        {
            OutputLanguage = "en", SilencePaddingMs = 7000, MaxPartialChars = 120, DeltaIdleFinalizeMs = 9000,
        },
    };

    /// <summary>
    /// DeltaIdleFinalizeMs がクローンに引き継がれる (= 保存しても default 6000 に戻らない)。
    /// </summary>
    [TestMethod]
    public void CloneWithEncryptedSecrets_PreservesDeltaIdleFinalizeMs()
    {
        var source = BuildNonDefaultSettings();

        var clone = SettingsService.CloneWithEncryptedSecrets(source);

        Assert.AreEqual(9000, clone.OpenAIRealtime.DeltaIdleFinalizeMs, "OpenAI の DeltaIdleFinalizeMs が失われている");
    }

    /// <summary>DeltaIdleFinalizeMs=0 (アイドル確定を無効化する設定) も 0 のまま保たれる。</summary>
    [TestMethod]
    public void CloneWithEncryptedSecrets_PreservesZeroDeltaIdleFinalizeMs()
    {
        var source = BuildNonDefaultSettings();
        source.OpenAIRealtime.DeltaIdleFinalizeMs = 0;

        var clone = SettingsService.CloneWithEncryptedSecrets(source);

        Assert.AreEqual(0, clone.OpenAIRealtime.DeltaIdleFinalizeMs,
            "0 (無効化) が default 6000 に戻されると、 無効化した意図が autosave で消える");
    }

    /// <summary>既に回帰実績のある SilencePaddingMs / MaxPartialChars も同時に守られていることを固定する。</summary>
    [TestMethod]
    public void CloneWithEncryptedSecrets_PreservesSilencePaddingAndMaxPartialChars()
    {
        var source = BuildNonDefaultSettings();

        var clone = SettingsService.CloneWithEncryptedSecrets(source);

        Assert.AreEqual(7000, clone.OpenAIRealtime.SilencePaddingMs);
        Assert.AreEqual(120, clone.OpenAIRealtime.MaxPartialChars);
    }
}
