using System.Globalization;

namespace Aeroscope.Core.Diagnostics;

/// <summary>Which characteristic a captured packet went through.</summary>
public enum CaptureChannel { In, State, Out, Data }

public sealed record CapturedPacket(TimeSpan Time, CaptureChannel Channel, byte[] Bytes);

/// <summary>
/// Plain-text recording of BLE traffic, one packet per line, so captures from the
/// real scope can be read by people, diffed, and replayed in tests:
/// <code>
/// # comment
/// 0.412 OUT   50 46 00 00 ...
/// </code>
/// </summary>
public static class CaptureFile
{
    public static string FormatLine(CapturedPacket packet)
        => string.Create(CultureInfo.InvariantCulture,
            $"{packet.Time.TotalSeconds:0.000} {packet.Channel.ToString().ToUpperInvariant(),-5} {BitConverter.ToString(packet.Bytes).Replace('-', ' ')}");

    public static CapturedPacket ParseLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new FormatException($"Expected '<seconds> <channel> <hex bytes>', got '{line}'.");
        }

        var time = TimeSpan.FromSeconds(double.Parse(parts[0], CultureInfo.InvariantCulture));
        var channel = Enum.Parse<CaptureChannel>(parts[1], ignoreCase: true);
        var bytes = parts.Skip(2).Select(hex => Convert.ToByte(hex, 16)).ToArray();
        return new CapturedPacket(time, channel, bytes);
    }

    /// <summary>Reads all packets, skipping blank lines and '#' comments.</summary>
    public static IReadOnlyList<CapturedPacket> Read(TextReader reader)
    {
        var packets = new List<CapturedPacket>();
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                packets.Add(ParseLine(line));
            }
        }

        return packets;
    }

    public static IReadOnlyList<CapturedPacket> Read(string path)
    {
        using var reader = File.OpenText(path);
        return Read(reader);
    }
}
