using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace RealTimeTranslator.UI.Converters;

/// <summary>
/// bool を反転するコンバーター（true → false, false → true）
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return value;
    }
}
