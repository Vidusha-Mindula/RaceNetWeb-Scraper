using System.Globalization;
using System.Windows.Data;

namespace RaceNetScraper.App.Converters;

/// <summary>Appends " (not installed)" to a browser's label when its availability check came
/// back false, so an unusable RadioButton (already disabled via IsEnabled) also explains why
/// rather than just looking greyed out for no visible reason. ConverterParameter is the plain
/// label (e.g. "Firefox"); the bound value is the corresponding IsXInstalled bool.</summary>
public sealed class BrowserLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var label = parameter as string ?? "";
        return value is true ? label : $"{label} (not installed)";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
