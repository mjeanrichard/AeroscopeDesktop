using Aeroscope.Core.Protocol;

namespace Aeroscope.App.Controls;

/// <summary>
/// Maps volts and seconds to pixels on a 10 x 8 division screen. Time 0 (the trigger)
/// is the horizontal centre; <paramref name="CentreVolts"/> is the vertical centre.
/// </summary>
public readonly record struct ScreenMapping(double Width, double Height, double VoltsPerDivision, double SecondsPerDivision, double CentreVolts)
{
    public double DivisionWidth => Width / TimeBase.Divisions;

    public double DivisionHeight => Height / VoltageRange.Divisions;

    public double X(double seconds) => Width / 2 + seconds / SecondsPerDivision * DivisionWidth;

    public double Y(double volts) => Height / 2 - (volts - CentreVolts) / VoltsPerDivision * DivisionHeight;
}
