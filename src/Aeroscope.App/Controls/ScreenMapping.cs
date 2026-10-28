using Aeroscope.Core.Protocol;

namespace Aeroscope.App.Controls;

/// <summary>
/// Maps volts and seconds to pixels on a 10 x 8 division screen. <paramref name="CentreSeconds"/>
/// (normally 0, the trigger) is the horizontal centre; <paramref name="CentreVolts"/> the vertical one.
/// </summary>
public readonly record struct ScreenMapping(double Width, double Height, double VoltsPerDivision, double SecondsPerDivision, double CentreVolts, double CentreSeconds = 0)
{
    public double DivisionWidth => Width / TimeBase.Divisions;

    public double DivisionHeight => Height / VoltageRange.Divisions;

    public double X(double seconds) => Width / 2 + (seconds - CentreSeconds) / SecondsPerDivision * DivisionWidth;

    public double Y(double volts) => Height / 2 - (volts - CentreVolts) / VoltsPerDivision * DivisionHeight;
}
