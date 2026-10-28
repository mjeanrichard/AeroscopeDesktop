using System.Text;
using Aeroscope.Core.Bluetooth;
using Aeroscope.Core.Protocol;
using Aeroscope.Probe;

namespace Aeroscope.Tests.Probe;

public class SessionStepTests
{
    [Theory]
    [InlineData("QTI")]
    [InlineData("R")]
    [InlineData("ZR")]
    public void Plain_token_is_an_ascii_command(string text)
    {
        var step = Assert.IsType<WriteStep>(SessionStep.Parse(text));

        Assert.Equal(AeroscopeGatt.ScopeIn, step.Characteristic);
        Assert.Equal(Encoding.ASCII.GetBytes(text), step.Payload);
        Assert.Equal(text, step.Text);
    }

    [Fact]
    public void Raw_hex_to_scope_in()
    {
        var step = Assert.IsType<WriteStep>(SessionStep.Parse("in:515449"));

        Assert.Equal(AeroscopeGatt.ScopeIn, step.Characteristic);
        Assert.Equal([0x51, 0x54, 0x49], step.Payload);
    }

    [Fact]
    public void Raw_hex_to_scope_state()
    {
        var step = Assert.IsType<WriteStep>(SessionStep.Parse("state:0380"));

        Assert.Equal(AeroscopeGatt.ScopeState, step.Characteristic);
        Assert.Equal([0x03, 0x80], step.Payload);
    }

    [Fact]
    public void Settings_without_overrides_writes_defaults()
    {
        var step = Assert.IsType<WriteStep>(SessionStep.Parse("settings"));

        Assert.Equal(AeroscopeGatt.ScopeState, step.Characteristic);
        Assert.Equal(new ScopeRegisters().ToPacket(), step.Payload);
    }

    [Fact]
    public void Settings_overrides_accept_decimal_and_hex()
    {
        var step = Assert.IsType<WriteStep>(SessionStep.Parse("settings:level=139,fe=0x60,TS=0x13"));

        Assert.Equal(new ScopeRegisters { TriggerLevel = 139, FrontEnd = 0x60, SamplerControl = 0x13 }.ToPacket(), step.Payload);
    }

    [Fact]
    public void Every_register_key_is_recognised()
    {
        var registers = SessionStep.ParseRegisters("trig=1,level=2,fe=3,ts=4,tpos=5,rstart=6,wdepth=7,rdepth=8,dac=9");

        Assert.Equal(new ScopeRegisters
        {
            TriggerControl = 1, TriggerLevel = 2, FrontEnd = 3, SamplerControl = 4, TriggerPosition = 5,
            ReadStartAddress = 6, WriteDepth = 7, ReadDepth = 8, OffsetDac = 9,
        }, registers);
    }

    [Fact]
    public void Wait_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(1.5), Assert.IsType<WaitStep>(SessionStep.Parse("wait:1.5")).Duration);
    }

    [Fact]
    public void Until_with_default_and_explicit_timeout()
    {
        var defaulted = Assert.IsType<UntilStep>(SessionStep.Parse("until:PF"));
        var explicitTimeout = Assert.IsType<UntilStep>(SessionStep.Parse("until:V:3"));

        Assert.Equal("PF"u8.ToArray(), defaulted.Prefix);
        Assert.Equal(TimeSpan.FromSeconds(10), defaulted.Timeout);
        Assert.Equal("V"u8.ToArray(), explicitTimeout.Prefix);
        Assert.Equal(TimeSpan.FromSeconds(3), explicitTimeout.Timeout);
    }

    [Fact]
    public void Frames_with_default_and_explicit_timeout()
    {
        Assert.Equal(new FramesStep("frames:5", 5, TimeSpan.FromSeconds(10)), SessionStep.Parse("frames:5"));
        Assert.Equal(new FramesStep("frames:1:30", 1, TimeSpan.FromSeconds(30)), SessionStep.Parse("frames:1:30"));
    }

    [Theory]
    [InlineData("bogus:1")]
    [InlineData("until:")]
    [InlineData("settings:fe")]
    [InlineData("settings:nope=1")]
    [InlineData("settings:level=300")]
    [InlineData("settings:dac=0x10000")]
    [InlineData("settings:level=-1")]
    [InlineData("in:XYZ")]
    [InlineData("wait:soon")]
    public void Malformed_steps_throw_format_exception(string text)
    {
        Assert.Throws<FormatException>(() => SessionStep.Parse(text));
    }
}
