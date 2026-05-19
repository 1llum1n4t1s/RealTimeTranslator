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
    public void ToTsvLine_StandardEntry_ProducesCorrectFormat()
    {
        var entry = new TranslationLogEntry(SampleTime, "ja", "abc12345", "Google Chrome", "こんにちは、 今日は天気がいいですね。");
        var line = entry.ToTsvLine();
        Assert.AreEqual("2026-05-19T12:34:56\tja\tabc12345\tGoogle Chrome\tこんにちは、 今日は天気がいいですね。", line);
    }

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
    public void ToTsvLine_TextWithNewline_NormalizesToSpace()
    {
        // 翻訳テキスト中の \n は半角空白に置換され、 ファイルの行が崩れない
        var entry = new TranslationLogEntry(SampleTime, "ja", "s1", "VLC", "1行目\n2行目");
        var line = entry.ToTsvLine();
        Assert.IsFalse(line.Contains('\n'), "改行は除去されている");
        Assert.IsTrue(line.EndsWith("1行目 2行目"));
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
    public void TryParseTsvLine_StandardLine_ParsesCorrectly()
    {
        var line = "2026-05-19T12:34:56\ten\txyz98765\tDiscord\tHello world.";
        var ok = TranslationLogEntry.TryParseTsvLine(line, out var entry);
        Assert.IsTrue(ok);
        Assert.IsNotNull(entry);
        Assert.AreEqual(new DateTime(2026, 5, 19, 12, 34, 56), entry!.Timestamp);
        Assert.AreEqual("en", entry.Language);
        Assert.AreEqual("xyz98765", entry.SessionId);
        Assert.AreEqual("Discord", entry.ProcessName);
        Assert.AreEqual("Hello world.", entry.Text);
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
    public void TryParseTsvLine_TextContainsLiteralSpaceInsteadOfTab_ParsesAllFields()
    {
        // タブが半角空白に正規化された text フィールド (5 列目以降) は最後にまとめられる
        var line = "2026-05-19T12:34:56\tja\ts1\tChrome\tfoo bar baz";
        var ok = TranslationLogEntry.TryParseTsvLine(line, out var entry);
        Assert.IsTrue(ok);
        Assert.AreEqual("foo bar baz", entry!.Text);
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
}
