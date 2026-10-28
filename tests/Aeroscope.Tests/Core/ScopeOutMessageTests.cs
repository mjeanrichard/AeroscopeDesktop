using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.Core;

public class ScopeOutMessageTests
{
    [Fact]
    public void Startup_capture_decodes_to_the_expected_replies()
    {
        var messages = Captures.Packets(Captures.Startup, CaptureChannel.Out).Select(ScopeOutMessage.Parse).ToList();

        Assert.Collection(messages,
            m => Assert.IsType<TelemetryMessage>(m),
            m => Assert.Equal(new PowerStateMessage(FullyOn: true), m),
            m => Assert.Equal(new PowerStateMessage(FullyOn: true), m),
            m => Assert.IsType<VersionMessage>(m),
            m => Assert.IsType<UnknownMessage>(m),
            m => Assert.IsType<ErrorLogMessage>(m));
    }

    [Fact]
    public void Telemetry_from_real_scope()
    {
        var message = Assert.IsType<TelemetryMessage>(ScopeOutMessage.Parse(Captures.Packet("54 C0 F5 00 CE")));

        Assert.True(message.ChargerConnected);
        Assert.True(message.Charging);
        Assert.Equal(245, message.BatteryLevel);
        Assert.Equal(20.6, message.TemperatureCelsius, precision: 3);
    }

    [Fact]
    public void Telemetry_charger_connected_but_full()
    {
        var message = Assert.IsType<TelemetryMessage>(ScopeOutMessage.Parse(Captures.Packet("54 80 F0 00 FB")));

        Assert.True(message.ChargerConnected);
        Assert.False(message.Charging);
        Assert.Equal(25.1, message.TemperatureCelsius, precision: 3); // spec example: 0x00FB = 25.1 °C
    }

    [Fact]
    public void Telemetry_negative_temperature()
    {
        var message = Assert.IsType<TelemetryMessage>(ScopeOutMessage.Parse(Captures.Packet("54 00 E0 FF 9C")));

        Assert.Equal(-10.0, message.TemperatureCelsius, precision: 3);
    }

    [Fact]
    public void Version_from_real_scope()
    {
        var message = ScopeOutMessage.Parse(Captures.Packet("56 01 05 05 00 0F 42 79"));

        Assert.Equal(new VersionMessage(HardwareId: 1, FpgaRevision: 5, FirmwareRevision: 5, SerialNumber: 1000057), message);
    }

    [Theory]
    [InlineData("50 46", true)]
    [InlineData("50 4F", false)]
    public void Power_state(string hex, bool fullyOn)
    {
        Assert.Equal(new PowerStateMessage(fullyOn), ScopeOutMessage.Parse(Captures.Packet(hex)));
    }

    [Fact]
    public void Empty_error_log_from_real_scope()
    {
        var message = Assert.IsType<ErrorLogMessage>(ScopeOutMessage.Parse(Captures.Packet("45")));

        Assert.Empty(message.Codes);
        Assert.Equal("Error log: empty", message.ToString());
    }

    [Fact]
    public void Error_log_skips_empty_slots()
    {
        var message = Assert.IsType<ErrorLogMessage>(ScopeOutMessage.Parse(Captures.Packet("45 C0 00 C6")));

        Assert.Equal([0xC0, 0xC6], message.Codes);
    }

    [Fact]
    public void Critical_error()
    {
        Assert.Equal(new CriticalErrorMessage(0xC1), ScopeOutMessage.Parse(Captures.Packet("45 43 C1")));
    }

    [Fact]
    public void Button_presses_from_real_scope_are_BD()
    {
        // The spec text says "BP", its table "BD"; the scope sends "BD".
        var buttonPackets = Captures.Packets(Captures.Button, CaptureChannel.Out).Where(p => p[0] == 'B').ToList();

        Assert.Equal(26, buttonPackets.Count);
        Assert.All(buttonPackets, p => Assert.Equal(Captures.Packet("42 44"), p));
        Assert.All(buttonPackets, p => Assert.IsType<ButtonPressedMessage>(ScopeOutMessage.Parse(p)));
        Assert.Empty(Captures.Packets(Captures.Button, CaptureChannel.Data));
    }

    [Fact]
    public void Button_press_per_spec_text_is_also_accepted()
    {
        Assert.IsType<ButtonPressedMessage>(ScopeOutMessage.Parse(Captures.Packet("42 50")));
    }

    [Fact]
    public void Calibration_result_per_spec_layout()
    {
        var message = Assert.IsType<CalibrationMessage>(
            ScopeOutMessage.Parse(Captures.Packet("43 42 00 0A FF F6 00 00 00 01 00 02 00 03 01 00")));

        Assert.Equal([10, -10, 0, 1, 2, 3, 256], message.Offsets);
        Assert.StartsWith("Calibration: 10V 10, 5V -10", message.ToString());
    }

    [Fact]
    public void Calibration_query_reply_from_real_scope_is_not_the_spec_format()
    {
        // QC answers "C" plus zeros on firmware rev 5, not "CB" + offsets as the spec describes.
        var message = Assert.IsType<UnknownMessage>(ScopeOutMessage.Parse(Captures.Packet("43")));

        Assert.Equal("Unrecognised 'C' message", message.ToString());
    }

    [Fact]
    public void Empty_packet_is_unknown()
    {
        var message = Assert.IsType<UnknownMessage>(ScopeOutMessage.Parse([]));

        Assert.Equal("Unrecognised message", message.ToString());
    }

    [Fact]
    public void Truncated_packets_are_unknown_instead_of_throwing()
    {
        Assert.IsType<UnknownMessage>(ScopeOutMessage.Parse([(byte)'T', 0xC0]));
        Assert.IsType<UnknownMessage>(ScopeOutMessage.Parse([(byte)'V', 1, 5]));
        Assert.IsType<UnknownMessage>(ScopeOutMessage.Parse([(byte)'P']));
    }

    [Fact]
    public void Descriptions_are_readable()
    {
        Assert.Equal("Telemetry: battery 245, charger connected, charging, 20.6 °C",
            ScopeOutMessage.Parse(Captures.Packet("54 C0 F5 00 CE")).ToString());
        Assert.Equal("Version: FPGA rev 5, firmware rev 5, hardware id 1, serial 1000057",
            ScopeOutMessage.Parse(Captures.Packet("56 01 05 05 00 0F 42 79")).ToString());
        Assert.Equal("Power: fully on (FPGA ready)", new PowerStateMessage(true).ToString());
        Assert.Equal("Power: FPGA off / still configuring", new PowerStateMessage(false).ToString());
        Assert.Equal("Button pressed", new ButtonPressedMessage().ToString());
        Assert.Equal("Critical error 0xC1", new CriticalErrorMessage(0xC1).ToString());
        Assert.Equal("Error log: 0xC0 0xC6", new ErrorLogMessage([0xC0, 0xC6]).ToString());
    }
}
