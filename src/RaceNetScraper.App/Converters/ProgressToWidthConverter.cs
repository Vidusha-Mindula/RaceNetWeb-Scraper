using System.Globalization;
using System.Windows.Data;

namespace RaceNetScraper.App.Converters;

/// <summary>Computes a determinate ProgressBar's fill width directly. The custom ControlTemplate
/// in App.xaml has no "PART_Track" element for WPF's usual automatic indicator-sizing to key off,
/// so without this the indicator silently stays at its XAML-declared Width="0" no matter what
/// Value is set to — the percentage text next to it can be completely correct while the visual
/// bar never fills. Expects [Value, Minimum, Maximum, ActualWidth] as the four binding values.</summary>
public sealed class ProgressToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 4) return 0.0;
        if (values[0] is not double value || values[1] is not double min ||
            values[2] is not double max || values[3] is not double actualWidth)
        {
            return 0.0;
        }

        if (max <= min || actualWidth <= 0) return 0.0;

        var fraction = Math.Clamp((value - min) / (max - min), 0.0, 1.0);
        return actualWidth * fraction;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
