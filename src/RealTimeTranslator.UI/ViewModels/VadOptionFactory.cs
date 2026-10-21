using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace RealTimeTranslator.UI.ViewModels;

internal static class VadOptionFactory
{
    internal static ReadOnlyCollection<VadThresholdOption> CreateThresholdOptions(float current)
        => new(new[] { 0.6f, 0.8f, 1.0f, current }
            .Distinct().Order().Select(value => new VadThresholdOption(value,
                value == 0.8f ? "0.8（既定）" : value.ToString("G", CultureInfo.CurrentCulture))).ToArray());

    internal static ReadOnlyCollection<VadDurationOption> CreatePreRollOptions(int current)
        => CreateDurationOptions(current, 50, [0, 50, 100, 200, 500, 700, 1000, 1200, 2000]);

    internal static ReadOnlyCollection<VadDurationOption> CreateHangoverOptions(int current)
        => CreateDurationOptions(current, 200, [0, 100, 200, 350, 500, 800, 1000, 2000]);

    private static ReadOnlyCollection<VadDurationOption> CreateDurationOptions(
        int current, int defaultValue, int[] values)
        => new(values.Append(current).Distinct().Order()
            .Select(value => new VadDurationOption(value,
                value == defaultValue ? $"{value} ms（既定）" : $"{value} ms")).ToArray());
}
