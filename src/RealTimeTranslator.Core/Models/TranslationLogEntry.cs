namespace RealTimeTranslator.Core.Models;

/// <summary>
/// 翻訳ログ 1 件分のエントリ。 「翻訳ログ」タブの ListBox 1 行 / ファイル 1 行に対応する。
///
/// 永続化フォーマット: TSV (タブ区切り、 1 行 1 エントリ、 UTF-8)。
/// 既存システムログ (.log) と同じく Notepad / Excel で開ける人間可読フォーマット。
/// 翻訳テキスト中の \t \n \r は空白に正規化して 1 行化することで、 改行混入で行が壊れる事故を防ぐ。
/// </summary>
/// <param name="Timestamp">翻訳が確定した時刻 (UTC ではなく Local。 ユーザーが UI で読む値)。</param>
/// <param name="Language">翻訳先言語コード (例: "ja", "en", "zh")。 AppSettings.OpenAIRealtime.OutputLanguage 由来。</param>
/// <param name="SessionId">Start ボタン押下ごとに発行される短縮 Guid (8 文字)。 セッション境界を判別するため。</param>
/// <param name="ProcessName">キャプチャ対象プロセスの表示名 (ProductName 優先、 例: "Google Chrome")。 取れなければ ProcessName。</param>
/// <param name="Text">翻訳テキスト本文 (40 文字 truncate なしのフル文字列)。 改行は空白に正規化済み。</param>
public sealed record TranslationLogEntry(
    DateTime Timestamp,
    string Language,
    string SessionId,
    string ProcessName,
    string Text)
{
    private const char TsvSeparator = '\t';

    // ISO 8601 ベースだが秒精度で UI / ファイル両方に使える形式。 タイムゾーン情報なし (Local 想定)。
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss";

    /// <summary>UI 表示用にフォーマットした「日付 + 時刻」文字列。 ADV ログのヘッダーに使う。</summary>
    public string FormattedTimestamp => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// TSV 1 行にシリアライズする。 改行・タブ・キャリッジリターンは空白に正規化して 1 行化する。
    /// </summary>
    public string ToTsvLine()
    {
        return string.Join(TsvSeparator,
            Timestamp.ToString(TimestampFormat),
            Sanitize(Language),
            Sanitize(SessionId),
            Sanitize(ProcessName),
            Sanitize(Text));
    }

    /// <summary>
    /// TSV 1 行を <see cref="TranslationLogEntry"/> にパースする。 不正な行は false で返す (skip 推奨)。
    /// </summary>
    public static bool TryParseTsvLine(string line, out TranslationLogEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        // string.Split は分割数を制限できるので、 Text 内のタブ (Sanitize で除去済みのはずだが念のため) を吸収。
        var parts = line.Split(TsvSeparator, 5);
        if (parts.Length != 5) return false;

        if (!DateTime.TryParseExact(parts[0], TimestampFormat, System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.AssumeLocal, out var timestamp))
        {
            return false;
        }

        entry = new TranslationLogEntry(
            Timestamp: timestamp,
            Language: parts[1],
            SessionId: parts[2],
            ProcessName: parts[3],
            Text: parts[4]);
        return true;
    }

    /// <summary>
    /// TSV フィールドに含めて安全な文字列に正規化する (タブ / 改行 / キャリッジリターンを半角空白に置換)。
    /// 文字数は維持され、 視覚的な変化も最小限。
    /// </summary>
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        // 個別 Replace を 3 回行うより 1 パスで置換する方が割安だが、 入力は通常 100 文字未満なので可読性優先。
        return value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }
}
