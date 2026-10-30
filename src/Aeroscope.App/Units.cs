using System.Globalization;

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

    /// <summary>
    /// Reads a typed voltage: a number with an optional prefix and unit, e.g. "1.2", "-0,5 V",
    /// "500 mV", "20mv". A plain number is in volts.
    /// </summary>
    public static bool TryParseVolts(string? text, out double volts)
    {
        volts = 0;
        var s = (text ?? "").Trim();
        if (s.EndsWith('V') || s.EndsWith('v'))
        {
            s = s[..^1].TrimEnd();
        }

        var scale = 1.0;
        if (s.Length > 0 && Scales.TryGetValue(s[^1], out var prefixScale))
        {
            scale = prefixScale;
            s = s[..^1].TrimEnd();
        }

        if (!double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            return false;
        }

        volts = value * scale;
        return true;
    }

    private static readonly Dictionary<char, double> Scales = new()
    {
        ['m'] = 1e-3, ['µ'] = 1e-6, ['u'] = 1e-6,
    };

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
