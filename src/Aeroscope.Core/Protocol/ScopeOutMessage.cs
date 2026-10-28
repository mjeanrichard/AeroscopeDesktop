using System.Buffers.Binary;

namespace Aeroscope.Core.Protocol;

/// <summary>
/// A packet from the Scope Out characteristic (spec section "Scope Out Characteristic", Table 4).
/// </summary>
public abstract record ScopeOutMessage
{
    public static ScopeOutMessage Parse(byte[] packet)
    {
        if (packet.Length == 0)
        {
            return new UnknownMessage(packet);
        }

        return (char)packet[0] switch
        {
            'T' when packet.Length >= 5 => new TelemetryMessage(
                ChargerConnected: (packet[1] & 0x80) != 0,
                Charging: (packet[1] & 0x40) != 0,
                BatteryLevel: packet[2],
                TemperatureCelsius: BinaryPrimitives.ReadInt16BigEndian(packet.AsSpan(3)) / 10.0),
            'V' when packet.Length >= 8 => new VersionMessage(
                HardwareId: packet[1],
                FpgaRevision: packet[2],
                FirmwareRevision: packet[3],
                SerialNumber: BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4))),
            'P' when packet.Length >= 2 => new PowerStateMessage(FullyOn: packet[1] == 'F'),
            // The spec text says "BP", its table says "BD"; the Android app only checks the 'B'.
            'B' => new ButtonPressedMessage(),
            'E' when packet.Length >= 3 && packet[1] == 'C' => new CriticalErrorMessage(packet[2]),
            'E' => new ErrorLogMessage(packet.Skip(1).Where(code => code != 0).ToArray()),
            'C' when packet.Length >= 16 && packet[1] == 'B' => new CalibrationMessage(
                Enumerable.Range(0, 7).Select(i => BinaryPrimitives.ReadInt16BigEndian(packet.AsSpan(2 + i * 2))).ToArray()),
            _ => new UnknownMessage(packet),
        };
    }
}

/// <param name="BatteryLevel">Raw level: above 238 full, 226-238 partial, 220-226 low.</param>
public sealed record TelemetryMessage(bool ChargerConnected, bool Charging, byte BatteryLevel, double TemperatureCelsius) : ScopeOutMessage
{
    public override string ToString()
        => $"Telemetry: battery {BatteryLevel}, charger {(ChargerConnected ? "connected" : "absent")}{(Charging ? ", charging" : "")}, {TemperatureCelsius:0.0} °C";
}

public sealed record VersionMessage(byte HardwareId, byte FpgaRevision, byte FirmwareRevision, uint SerialNumber) : ScopeOutMessage
{
    public override string ToString()
        => $"Version: FPGA rev {FpgaRevision}, firmware rev {FirmwareRevision}, hardware id {HardwareId}, serial {SerialNumber}";
}

/// <summary>"PF": FPGA configured and ready for frames. "PO": not ready yet.</summary>
public sealed record PowerStateMessage(bool FullyOn) : ScopeOutMessage
{
    public override string ToString() => FullyOn ? "Power: fully on (FPGA ready)" : "Power: FPGA off / still configuring";
}

public sealed record ButtonPressedMessage : ScopeOutMessage
{
    public override string ToString() => "Button pressed";
}

public sealed record CriticalErrorMessage(byte Code) : ScopeOutMessage
{
    public override string ToString() => $"Critical error 0x{Code:X2}";
}

public sealed record ErrorLogMessage(byte[] Codes) : ScopeOutMessage
{
    public override string ToString()
        => Codes.Length == 0 ? "Error log: empty" : $"Error log: {string.Join(" ", Codes.Select(c => $"0x{c:X2}"))}";
}

/// <summary>
/// DAC offset corrections from the calibration routine, one per range from 10 V down to 100 mV.
/// Signedness is not stated in the spec; read as signed since they are corrections.
/// </summary>
public sealed record CalibrationMessage(short[] Offsets) : ScopeOutMessage
{
    private static readonly string[] Ranges = ["10V", "5V", "2V", "1V", "500mV", "200mV", "100mV"];

    public override string ToString()
        => "Calibration: " + string.Join(", ", Ranges.Zip(Offsets, (range, offset) => $"{range} {offset}"));
}

public sealed record UnknownMessage(byte[] Packet) : ScopeOutMessage
{
    public override string ToString()
        => Packet.Length > 0 && Packet[0] is >= 0x20 and < 0x7F ? $"Unrecognised '{(char)Packet[0]}' message" : "Unrecognised message";
}
