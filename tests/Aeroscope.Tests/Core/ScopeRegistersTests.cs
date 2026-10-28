using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.Core;

public class ScopeRegistersTests
{
    [Fact]
    public void Defaults_match_the_packet_the_real_scope_accepted()
    {
        // The scope produced frames after this exact register write (capture 02).
        var recorded = Captures.Load(Captures.FramesDefault).Single(p => p.Channel == CaptureChannel.State).Bytes;

        Assert.Equal(recorded, new ScopeRegisters().ToPacket());
    }

    [Fact]
    public void Full_depth_settings_match_the_packet_the_real_scope_accepted()
    {
        var recorded = Captures.Load(Captures.RunFullDepth).Single(p => p.Channel == CaptureChannel.State).Bytes;

        Assert.Equal(recorded, new ScopeRegisters { ReadDepth = 9, ReadStartAddress = 0 }.ToPacket());
    }

    [Fact]
    public void Defaults_follow_the_android_app()
    {
        var registers = new ScopeRegisters();

        Assert.Equal(0x03, registers.TriggerControl); // rising edge + auto
        Assert.Equal(0x80, registers.TriggerLevel);
        Assert.Equal(0xA2, registers.FrontEnd); // DC, 1 V/div
        Assert.Equal(0x00, registers.SamplerControl); // 100 MS/s
        Assert.Equal(0x800, registers.TriggerPosition);
        Assert.Equal(0x700, registers.ReadStartAddress);
        Assert.Equal(0x09, registers.WriteDepth);
        Assert.Equal(0x06, registers.ReadDepth);
        Assert.Equal(0x8000, registers.OffsetDac);
    }

    [Fact]
    public void Every_field_lands_in_its_register_byte()
    {
        var packet = new ScopeRegisters
        {
            TriggerControl = 0x25,
            TriggerLevel = 0x11,
            FrontEnd = 0x60,
            SamplerControl = 0x13,
            TriggerPosition = 0xABC,
            ReadStartAddress = 0x123,
            WriteDepth = 0x01,
            ReadDepth = 0x09,
            OffsetDac = 0xBEEF,
        }.ToPacket();

        Assert.Equal(Captures.Packet("25 11 00 60 13 0A BC 01 23 01 09 BE EF"), packet);
    }

    [Fact]
    public void Twelve_bit_addresses_drop_the_upper_nibble()
    {
        var packet = new ScopeRegisters { TriggerPosition = 0xFFFF, ReadStartAddress = 0xF123 }.ToPacket();

        Assert.Equal(0x0F, packet[5]);
        Assert.Equal(0xFF, packet[6]);
        Assert.Equal(0x01, packet[7]);
        Assert.Equal(0x23, packet[8]);
    }

    [Fact]
    public void Packet_is_twenty_bytes_with_unused_registers_zero()
    {
        var packet = new ScopeRegisters().ToPacket();

        Assert.Equal(20, packet.Length);
        Assert.Equal(0, packet[2]);
        Assert.All(packet[13..], b => Assert.Equal(0, b));
    }
}
