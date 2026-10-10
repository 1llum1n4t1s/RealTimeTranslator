using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Tests;

/// <summary>
/// TranslationLogEntry の TSV シリアライズ / パースの round-trip と異常入力 fallback を検証する。
/// 翻訳テキストに改行・タブ・キャリッジリターンが混入しても 1 行 1 エントリが崩れないことが重要。
/// </summary>
[TestClass]
public class TranslationLogEntryTests
{
    private static readonly DateTime SampleTime = new(2026, 5, 19, 12, 34, 56);

    [TestMethod]
    public void ToTsvLine_TextWithTab_NormalizesToSpace()
    {
        // 翻訳テキスト中のタブは半角空白に置換されて行が崩れない
        var entry = new TranslationLogEntry(SampleTime, "ja", "s1", "VLC", "前半\t後半");
        var line = entry.ToTsvLine();
        var parts = line.Split('\t');
        Assert.AreEqual(5, parts.Length, "TSV フィールド数は 5 のまま");
        Assert.AreEqual("前半 後半", parts[4]);
    }

    [TestMethod]
    public void ToTsvLine_TextWithCarriageReturn_NormalizesToSpace()
    {
        // \r も同様に置換
        var entry = new TranslationLogEntry(SampleTime, "ja", "s1", "VLC", "a\rb");
        var line = entry.ToTsvLine();
        Assert.IsFalse(line.Contains('\r'));
        Assert.IsTrue(line.EndsWith("a b"));
    }

    [TestMethod]
    public void TryParseTsvLine_EmptyLine_ReturnsFalse()
    {
        var ok = TranslationLogEntry.TryParseTsvLine(string.Empty, out var entry);
        Assert.IsFalse(ok);
        Assert.IsNull(entry);
    }

    [TestMethod]
    public void TryParseTsvLine_TooFewFields_ReturnsFalse()
    {
        // フィールド 4 つしかない壊れた行は skip
        var line = "2026-05-19T12:34:56\tja\ts1\tChrome";
        var ok = TranslationLogEntry.TryParseTsvLine(line, out var entry);
        Assert.IsFalse(ok);
        Assert.IsNull(entry);
    }

    [TestMethod]
    public void TryParseTsvLine_InvalidTimestamp_ReturnsFalse()
    {
        var line = "not-a-date\tja\ts1\tChrome\thi";
        var ok = TranslationLogEntry.TryParseTsvLine(line, out var entry);
        Assert.IsFalse(ok);
        Assert.IsNull(entry);
    }

    [TestMethod]
    public void RoundTrip_PreservesAllFields()
    {
        var original = new TranslationLogEntry(SampleTime, "zh", "sess0001", "Brave Browser", "你好,世界。");
        var line = original.ToTsvLine();
        var ok = TranslationLogEntry.TryParseTsvLine(line, out var parsed);

        Assert.IsTrue(ok);
        Assert.AreEqual(original.Timestamp, parsed!.Timestamp);
        Assert.AreEqual(original.Language, parsed.Language);
        Assert.AreEqual(original.SessionId, parsed.SessionId);
        Assert.AreEqual(original.ProcessName, parsed.ProcessName);
        Assert.AreEqual(original.Text, parsed.Text);
    }

    [TestMethod]
    public void RoundTrip_TextWithSpecialChars_NormalizesBeforeWrite()
    {
        // ToTsvLine で改行が空白に変わるため round-trip は完全一致しない (これは設計通り)
        var original = new TranslationLogEntry(SampleTime, "ja", "s1", "Chrome", "line1\nline2");
        var line = original.ToTsvLine();
        TranslationLogEntry.TryParseTsvLine(line, out var parsed);

        Assert.IsNotNull(parsed);
        Assert.AreEqual("line1 line2", parsed!.Text);
    }

    [TestMethod]
    public void FormattedTimestamp_HasExpectedDisplayFormat()
    {
        // UI 表示用のフォーマット (T 区切りではなく半角空白区切り)
        var entry = new TranslationLogEntry(SampleTime, "ja", "s1", "Chrome", "test");
        Assert.AreEqual("2026-05-19 12:34:56", entry.FormattedTimestamp);
    }

    [TestMethod]
    public void ToTsvLine_EmptyFields_OutputsEmptyTabs()
    {
        // 空文字フィールドは TSV 上もそのまま空 (タブ区切りで認識可能)
        var entry = new TranslationLogEntry(SampleTime, string.Empty, string.Empty, string.Empty, "only text");
        var line = entry.ToTsvLine();
        Assert.AreEqual("2026-05-19T12:34:56\t\t\t\tonly text", line);
    }

    [TestMethod]
    public void TryParseTsvLine_EmptyFieldsBetweenTabs_ParsesAsEmptyStrings()
    {
        var line = "2026-05-19T12:34:56\t\t\t\tjust text";
        var ok = TranslationLogEntry.TryParseTsvLine(line, out var entry);
        Assert.IsTrue(ok);
        Assert.AreEqual(string.Empty, entry!.Language);
        Assert.AreEqual(string.Empty, entry.SessionId);
        Assert.AreEqual(string.Empty, entry.ProcessName);
        Assert.AreEqual("just text", entry.Text);
    }

    // ═══════════════════════════════════════════════════════════════
    // Excel CSV インジェクション対策 (CWE-1236、 /rere #A2-001 / #A2-002)
    // ═══════════════════════════════════════════════════════════════
    //
    // 攻撃シナリオ: 翻訳テキスト or ProcessName が「=」「+」「-」「@」で始まる場合、
    // ユーザーが TSV ログを Excel で開いた瞬間に数式 / DDE / HYPERLINK が発火する。
    // OWASP 推奨パターン: 先頭に `'` (シングルクォート) を付与して数式評価を抑止する。

    [TestMethod]
    [TestCategory("CsvInjection")]
    public void ToTsvLine_TextStartsWithEquals_PrefixesQuote()
    {
        var entry = new TranslationLogEntry(SampleTime, "ja", "s1", "Chrome", "=cmd|'/C calc'!A0");
        var line = entry.ToTsvLine();
        Assert.IsTrue(line.EndsWith("\t'=cmd|'/C calc'!A0"), $"先頭の '=' に対して ' が付与されるはず: {line}");
    }

    [TestMethod]
    [TestCategory("CsvInjection")]
    public void ToTsvLine_TextStartsWithPlus_PrefixesQuote()
    {
        var entry = new TranslationLogEntry(SampleTime, "ja", "s1", "Chrome", "+SUM(1,2)");
        var line = entry.ToTsvLine();
        Assert.IsTrue(line.EndsWith("\t'+SUM(1,2)"), $"先頭の '+' に対して ' が付与されるはず: {line}");
    }

    [TestMethod]
    [TestCategory("CsvInjection")]
    public void ToTsvLine_TextStartsWithMinus_PrefixesQuote()
    {
        var entry = new TranslationLogEntry(SampleTime, "ja", "s1", "Chrome", "-1+1");
        var line = entry.ToTsvLine();
        Assert.IsTrue(line.EndsWith("\t'-1+1"), $"先頭の '-' に対して ' が付与されるはず: {line}");
    }

    [TestMethod]
    [TestCategory("CsvInjection")]
    public void ToTsvLine_TextStartsWithAt_PrefixesQuote()
    {
        var entry = new TranslationLogEntry(SampleTime, "ja", "s1", "Chrome", "@DDEAUTO");
        var line = entry.ToTsvLine();
        Assert.IsTrue(line.EndsWith("\t'@DDEAUTO"), $"先頭の '@' に対して ' が付与されるはず: {line}");
    }

    [TestMethod]
    [TestCategory("CsvInjection")]
    public void ToTsvLine_ProcessNameStartsWithEquals_AlsoPrefixed()
    {
        // #A2-002: ProcessName (FileVersionInfo.ProductName) は攻撃者制御可能
        // (悪意ある MOD exe が ProductName に「=HYPERLINK(...)」を設定可能)
        var entry = new TranslationLogEntry(SampleTime, "ja", "s1", "=HYPERLINK(\"http://evil\",X)", "ok");
        var line = entry.ToTsvLine();
        Assert.IsTrue(line.Contains("\t'=HYPERLINK(\"http://evil\",X)\t"),
            $"ProcessName の先頭 '=' にも ' が付与されるはず: {line}");
    }

    // /rere 第2R #B2-R2-001: TryParseTsvLine で `'` prefix を剥がして round-trip 対称化を検証

    [TestMethod]
    [TestCategory("CsvInjection")]
    public void RoundTrip_CsvInjectionPrefix_RestoresOriginalText()
    {
        var original = new TranslationLogEntry(SampleTime, "ja", "s1", "Chrome", "=cmd|'/C calc'!A0");
        var line = original.ToTsvLine();
        Assert.IsTrue(line.Contains("\t'=cmd|"), "書き込み時は ' で無害化");

        var ok = TranslationLogEntry.TryParseTsvLine(line, out var parsed);
        Assert.IsTrue(ok);
        Assert.AreEqual("=cmd|'/C calc'!A0", parsed!.Text, "UnescapeCsvInjectionPrefix で元値復元");
    }

    [TestMethod]
    [TestCategory("CsvInjection")]
    public void RoundTrip_ProcessNamePrefix_RestoresOriginal()
    {
        var original = new TranslationLogEntry(SampleTime, "ja", "s1", "=HYPERLINK(\"http://evil\",X)", "ok");
        var line = original.ToTsvLine();
        var ok = TranslationLogEntry.TryParseTsvLine(line, out var parsed);
        Assert.IsTrue(ok);
        Assert.AreEqual("=HYPERLINK(\"http://evil\",X)", parsed!.ProcessName, "ProcessName も復元");
        Assert.AreEqual("ok", parsed.Text);
    }

    [TestMethod]
    [TestCategory("CsvInjection")]
    public void RoundTrip_SingleQuoteFollowedBySafeChar_NotStripped()
    {
        // 翻訳の「'hello'」のように `'` で始まり 2 文字目が `=+-@` 以外なら剥がさない
        var original = new TranslationLogEntry(SampleTime, "ja", "s1", "Chrome", "'hello'");
        var line = original.ToTsvLine();
        var ok = TranslationLogEntry.TryParseTsvLine(line, out var parsed);
        Assert.IsTrue(ok);
        Assert.AreEqual("'hello'", parsed!.Text, "2 文字目が =+-@ でなければ ' は剥がさない");
    }
}
