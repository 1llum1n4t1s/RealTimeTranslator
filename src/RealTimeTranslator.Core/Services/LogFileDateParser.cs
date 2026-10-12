using System.Globalization;

namespace RealTimeTranslator.Core.Services;

/// <summary>文化情報のない旧ログ名を誤って期限削除しないための日付判定。</summary>
internal static class LogFileDateParser
{
    private static readonly CultureInfo[] LegacyCalendarCultures = CreateLegacyCalendarCultures();

    public static bool TryParseUnambiguousDate(string value, DateTime today, out DateTime date)
    {
        if (!DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out date)) return false;

        foreach (var culture in LegacyCalendarCultures)
        {
            // 同じ数値が別の暦で現在までの新しい日付になるなら、元の暦を特定できない。
            // 保持期間外でも推測で補正・削除せず、そのファイルを保全する。
            if (DateTime.TryParseExact(value, "yyyyMMdd", culture, DateTimeStyles.None, out var legacyDate) &&
                legacyDate > date && legacyDate <= today) return false;
        }
        return true;
    }

    private static CultureInfo[] CreateLegacyCalendarCultures()
    {
        var cultures = new List<CultureInfo>();
        var calendarTypes = new HashSet<Type>();
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            foreach (var calendar in culture.OptionalCalendars)
            {
                if (calendar is GregorianCalendar || !calendarTypes.Add(calendar.GetType())) continue;
                var legacyCulture = (CultureInfo)culture.Clone();
                legacyCulture.DateTimeFormat.Calendar = calendar;
                cultures.Add(CultureInfo.ReadOnly(legacyCulture));
            }
        }
        return cultures.ToArray();
    }
}
