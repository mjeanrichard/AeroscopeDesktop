using System.Globalization;
using System.Windows.Data;

namespace Aeroscope.App.Controls;

/// <summary>
/// Binds one radio button of a group to an enum or bool: checked when the bound value equals
/// the parameter, and checking it sets the value to the parameter.
/// </summary>
public sealed class ValueEquals : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Equals(value, parameter);

    // Unchecking is the other button of the group being checked, which sets the value itself.
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? parameter : Binding.DoNothing;
}
