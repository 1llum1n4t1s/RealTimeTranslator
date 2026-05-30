using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace RealTimeTranslator.UI.Converters;

/// <summary>
/// レベルメーターの「未点灯部分(右側マスク)」の幅を算出する MultiValueConverter。
///
/// メーターは「緑→黄→赤の固定グラデ帯(トラック全幅)」の上に、 現在レベルより右側を覆う
/// 半透明マスクを重ねて表現する (OBS 風に色ゾーンが固定される)。 マスク幅 = (1 - norm) * トラック幅。
/// 入力: [0]=正規化レベル norm (0..1)、 [1]=トラックの実幅 (Bounds.Width, double)。
/// </summary>
public sealed class LevelMeterMaskWidthConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not double norm || values[1] is not double trackWidth || trackWidth <= 0)
        {
            return 0d;
        }

        double clamped = norm < 0 ? 0 : norm > 1 ? 1 : norm;
        double maskWidth = (1.0 - clamped) * trackWidth;
        return maskWidth < 0 ? 0d : maskWidth;
    }
}
