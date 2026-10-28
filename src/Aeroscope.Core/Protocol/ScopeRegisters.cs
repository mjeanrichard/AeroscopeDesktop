namespace Aeroscope.Core.Protocol;

/// <summary>
/// The FPGA register block written to the Scope State characteristic (spec Table 1 and 5).
/// Byte n of the packet is register n; the rest are zero. The layout follows the
/// Android app (scope.cpp update_fpga), which is what the shipped firmware was used with.
/// </summary>
public sealed record ScopeRegisters
{
    /// <summary>Bit 5 noise reject, bit 2 falling edge, bit 1 rising edge, bit 0 auto trigger.</summary>
    public byte TriggerControl { get; init; } = 0x03;

    /// <summary>ADC code (0-255) the trigger compares against.</summary>
    public byte TriggerLevel { get; init; } = 0x80;

    /// <summary>Bit 7 DC coupling, lower bits select the range (e.g. 0x22 = 1 V/div).</summary>
    public byte FrontEnd { get; init; } = 0x80 | 0x22;

    /// <summary>Sample clock divider code; 0x00 = 100 MS/s (500 ns/div).</summary>
    public byte SamplerControl { get; init; } = 0x00;

    /// <summary>12-bit position of the trigger within the 4096-sample memory.</summary>
    public ushort TriggerPosition { get; init; } = 0x800;

    /// <summary>Memory address of the first sample read back.</summary>
    public ushort ReadStartAddress { get; init; } = 0x700;

    /// <summary>Size code: 1 = 16, 6 = 512, 9 = 4096 samples.</summary>
    public byte WriteDepth { get; init; } = 0x09;

    /// <summary>Size code: 1 = 16, 6 = 512, 9 = 4096 samples.</summary>
    public byte ReadDepth { get; init; } = 0x06;

    /// <summary>16-bit vertical offset DAC; 0x8000 is mid scale (0 V).</summary>
    public ushort OffsetDac { get; init; } = 0x8000;

    public byte[] ToPacket()
    {
        var packet = new byte[20];
        packet[0] = TriggerControl;
        packet[1] = TriggerLevel;
        packet[2] = 0x00; // PLL control, not used
        packet[3] = FrontEnd;
        packet[4] = SamplerControl;
        packet[5] = (byte)((TriggerPosition >> 8) & 0x0F);
        packet[6] = (byte)TriggerPosition;
        packet[7] = (byte)((ReadStartAddress >> 8) & 0x0F);
        packet[8] = (byte)ReadStartAddress;
        packet[9] = WriteDepth;
        packet[10] = ReadDepth;
        packet[11] = (byte)(OffsetDac >> 8);
        packet[12] = (byte)OffsetDac;
        return packet;
    }
}
