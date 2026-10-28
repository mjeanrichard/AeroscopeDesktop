namespace Aeroscope.Core.Bluetooth;

/// <summary>
/// GATT identifiers from the Aeroscope BTLE Protocol Specification v1.1.
/// </summary>
public static class AeroscopeGatt
{
    public static readonly Guid ServiceUuid = new("F9541234-91B3-BD9A-F077-80F2A6E57D00");

    /// <summary>Notify: waveform frame packets.</summary>
    public const ushort ScopeData = 0x1235;

    /// <summary>Write: MCU commands ("R", "S", "QTI", ...).</summary>
    public const ushort ScopeIn = 0x1236;

    /// <summary>Write: FPGA register block.</summary>
    public const ushort ScopeState = 0x1237;

    /// <summary>Notify: telemetry, version, power state, button press, errors.</summary>
    public const ushort ScopeOut = 0x1239;

    /// <summary>Every packet in both directions is 20 bytes, zero padded.</summary>
    public const int PacketLength = 20;

    /// <summary>
    /// The 16-bit id embedded in a 128-bit UUID (bits 0-15 of the first field).
    /// The spec only gives 16-bit ids, and the Android app assumed the Bluetooth
    /// base UUID; matching on this part also works if they use the vendor base.
    /// </summary>
    public static ushort ShortId(Guid uuid)
    {
        Span<byte> bytes = stackalloc byte[16];
        uuid.TryWriteBytes(bytes);
        return (ushort)(bytes[0] | bytes[1] << 8);
    }

    public static string RoleOf(ushort shortId) => shortId switch
    {
        ScopeData => "Scope Data",
        ScopeIn => "Scope In",
        ScopeState => "Scope State",
        ScopeOut => "Scope Out",
        _ => "Unknown",
    };
}
