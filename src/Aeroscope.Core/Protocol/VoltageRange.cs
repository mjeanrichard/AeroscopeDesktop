namespace Aeroscope.Core.Protocol;

/// <summary>
/// A vertical range of the analog front end. Values from the Android app (settings.h);
/// the DAC factors were checked on the real scope at 1 V and 100 mV/div.
/// </summary>
/// <param name="FrontEndCode">Front-end register value without the DC-coupling bit.</param>
/// <param name="DacCountsPerCode">Offset-DAC counts that move the trace by one ADC code.</param>
public sealed record VoltageRange(string Name, byte FrontEndCode, double VoltsPerDivision, double DacCountsPerCode)
{
    /// <summary>The screen is 8 divisions high and the ADC spans it with codes 0-255.</summary>
    public const int Divisions = 8;

    public static readonly VoltageRange Range100mV = new("100 mV", 0x60, 0.1, 2.048);
    public static readonly VoltageRange Range200mV = new("200 mV", 0x41, 0.2, 4.096);
    public static readonly VoltageRange Range500mV = new("500 mV", 0x20, 0.5, 10.24);
    public static readonly VoltageRange Range1V = new("1 V", 0x22, 1, 20.48);
    public static readonly VoltageRange Range2V = new("2 V", 0x03, 2, 40.96);
    public static readonly VoltageRange Range5V = new("5 V", 0x04, 5, 102.4);
    public static readonly VoltageRange Range10V = new("10 V", 0x05, 10, 204.8);

    public static IReadOnlyList<VoltageRange> All { get; } =
        [Range100mV, Range200mV, Range500mV, Range1V, Range2V, Range5V, Range10V];

    public double VoltsPerCode => VoltsPerDivision * Divisions / 255;

    public double VoltsPerDacCount => VoltsPerCode / DacCountsPerCode;

    public override string ToString() => $"{Name}/div";
}
