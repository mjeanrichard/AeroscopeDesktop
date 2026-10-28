namespace Aeroscope.App;

/// <summary>Formats values with an SI prefix and three significant digits, e.g. "2.35 V", "500 ns".</summary>
public static class Units
{
    private static readonly (double Limit, double Scale, string Prefix)[] Prefixes =
    [
        (1, 1, ""),
        (1e-3, 1e3, "m"),
        (1e-6, 1e6, "µ"),
        (0, 1e9, "n"),
    ];

    public static string Volts(double volts) => Format(volts, "V");

    public static string Seconds(double seconds) => Format(seconds, "s");

    private static string Format(double value, string unit)
    {
        var magnitude = Math.Abs(value);
        if (magnitude < 1e-12)
        {
            return $"0 {unit}";
        }

        var (_, scale, prefix) = Prefixes.First(p => magnitude >= p.Limit);
        var scaled = value * scale;
        var format = Math.Abs(scaled) >= 100 ? "0" : Math.Abs(scaled) >= 10 ? "0.0" : "0.00";
        return $"{scaled.ToString(format)} {prefix}{unit}";
    }
}
