using System.Globalization;
using System.Windows.Data;
using Aeroscope.Core.Protocol;

namespace Aeroscope.App.Controls;

/// <summary>
/// Shows a time base as what it sets, the sample rate, with its 1:1 scale: "100 MS/s (500 ns/div)",
/// or just one of the two parts ("100 MS/s", "500 ns/div").
/// </summary>
public sealed class TimeBaseText : IValueConverter
{
    public enum Parts { Both, Rate, Scale }

    public Parts Part { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not TimeBase timeBase ? value : Part switch
        {
            Parts.Rate => Units.SampleRate(1 / timeBase.SampleInterval),
            Parts.Scale => Units.TimeBaseScale(timeBase),
            _ => Units.TimeBaseText(timeBase),
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
