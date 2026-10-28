using System.Globalization;

namespace Aeroscope.Core.Bluetooth;

/// <summary>Converts between Windows' 48-bit ulong addresses and "AA:BB:CC:DD:EE:FF".</summary>
public static class BluetoothAddress
{
    public static string Format(ulong address)
        => string.Join(":", Enumerable.Range(0, 6).Reverse().Select(i => ((address >> (i * 8)) & 0xFF).ToString("X2")));

    public static ulong Parse(string text)
    {
        var parts = text.Split(':', '-');
        if (parts.Length != 6 || parts.Any(p => p.Length != 2))
        {
            throw new FormatException($"'{text}' is not a Bluetooth address like AA:BB:CC:DD:EE:FF.");
        }

        return parts.Aggregate(0UL, (address, part) => address << 8 | byte.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}
