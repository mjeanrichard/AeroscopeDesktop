using System.Globalization;
using System.Text;
using Aeroscope.Core.Bluetooth;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Probe;

/// <summary>One step of a scripted probe session, parsed from a command-line token.</summary>
public abstract record SessionStep(string Text)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public static SessionStep Parse(string text)
    {
        var colon = text.IndexOf(':');
        var (verb, argument) = colon < 0 ? (text, "") : (text[..colon], text[(colon + 1)..]);

        return verb.ToLowerInvariant() switch
        {
            "in" => new WriteStep(text, AeroscopeGatt.ScopeIn, Convert.FromHexString(argument)),
            "state" => new WriteStep(text, AeroscopeGatt.ScopeState, Convert.FromHexString(argument)),
            "settings" => new WriteStep(text, AeroscopeGatt.ScopeState, ParseRegisters(argument).ToPacket()),
            "wait" => new WaitStep(text, TimeSpan.FromSeconds(ParseDouble(argument))),
            "until" => ParseUntil(text, argument),
            "frames" => ParseFrames(text, argument),
            _ when colon < 0 => new WriteStep(text, AeroscopeGatt.ScopeIn, Encoding.ASCII.GetBytes(text)),
            _ => throw new FormatException($"Unknown step '{text}'."),
        };
    }

    public abstract Task ExecuteAsync(IScopeLink connection, SessionRecorder recorder);

    /// <summary>Android-app defaults with "key=value" overrides; values are decimal or 0x hex.</summary>
    public static ScopeRegisters ParseRegisters(string overrides)
    {
        var registers = new ScopeRegisters();
        foreach (var pair in overrides.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=');
            if (parts.Length != 2)
            {
                throw new FormatException($"Expected key=value, got '{pair}'.");
            }

            var value = ParseNumber(parts[1]);
            registers = parts[0].ToLowerInvariant() switch
            {
                "trig" => registers with { TriggerControl = AsByte(pair, value) },
                "level" => registers with { TriggerLevel = AsByte(pair, value) },
                "fe" => registers with { FrontEnd = AsByte(pair, value) },
                "ts" => registers with { SamplerControl = AsByte(pair, value) },
                "tpos" => registers with { TriggerPosition = AsUInt16(pair, value) },
                "rstart" => registers with { ReadStartAddress = AsUInt16(pair, value) },
                "wdepth" => registers with { WriteDepth = AsByte(pair, value) },
                "rdepth" => registers with { ReadDepth = AsByte(pair, value) },
                "dac" => registers with { OffsetDac = AsUInt16(pair, value) },
                _ => throw new FormatException($"Unknown register '{parts[0]}'."),
            };
        }

        return registers;
    }

    private static byte AsByte(string pair, int value)
        => value is >= 0 and <= byte.MaxValue ? (byte)value : throw new FormatException($"{pair} is out of range 0-255.");

    private static ushort AsUInt16(string pair, int value)
        => value is >= 0 and <= ushort.MaxValue ? (ushort)value : throw new FormatException($"{pair} is out of range 0-65535.");

    private static SessionStep ParseUntil(string text, string argument)
    {
        // until:PF or until:PF:5
        var parts = argument.Split(':');
        if (parts[0].Length == 0)
        {
            throw new FormatException("until needs the text to wait for, e.g. until:PF.");
        }

        var timeout = parts.Length > 1 ? TimeSpan.FromSeconds(ParseDouble(parts[1])) : DefaultTimeout;
        return new UntilStep(text, Encoding.ASCII.GetBytes(parts[0]), timeout);
    }

    private static SessionStep ParseFrames(string text, string argument)
    {
        var parts = argument.Split(':');
        var count = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var timeout = parts.Length > 1 ? TimeSpan.FromSeconds(ParseDouble(parts[1])) : DefaultTimeout;
        return new FramesStep(text, count, timeout);
    }

    private static double ParseDouble(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    private static int ParseNumber(string text)
        => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(text, CultureInfo.InvariantCulture);
}

public sealed record WriteStep(string Text, ushort Characteristic, byte[] Payload) : SessionStep(Text)
{
    public override async Task ExecuteAsync(IScopeLink connection, SessionRecorder recorder)
    {
        var packet = new byte[AeroscopeGatt.PacketLength];
        Payload.CopyTo(packet, 0);
        recorder.MarkWrite();
        await connection.WriteAsync(Characteristic, packet);
        recorder.RecordWrite(Characteristic, packet);
    }
}

public sealed record WaitStep(string Text, TimeSpan Duration) : SessionStep(Text)
{
    public override Task ExecuteAsync(IScopeLink connection, SessionRecorder recorder) => Task.Delay(Duration);
}

/// <summary>Waits for a Scope Out packet, received since the last write, that starts with the given bytes.</summary>
public sealed record UntilStep(string Text, byte[] Prefix, TimeSpan Timeout) : SessionStep(Text)
{
    public override Task ExecuteAsync(IScopeLink connection, SessionRecorder recorder)
    {
        var start = recorder.OutPacketCountAtLastWrite;
        return recorder.WaitForAsync(
            () => recorder.OutPacketsSince(start).Any(packet => packet.AsSpan().StartsWith(Prefix)),
            Timeout,
            $"No Scope Out packet starting with '{Encoding.ASCII.GetString(Prefix)}'");
    }
}

/// <summary>Waits until the given number of frames has been assembled since the last write.</summary>
public sealed record FramesStep(string Text, int Count, TimeSpan Timeout) : SessionStep(Text)
{
    public override Task ExecuteAsync(IScopeLink connection, SessionRecorder recorder)
    {
        var target = recorder.FrameCountAtLastWrite + Count;
        return recorder.WaitForAsync(() => recorder.FrameCount >= target, Timeout, $"Fewer than {Count} frames arrived");
    }
}
