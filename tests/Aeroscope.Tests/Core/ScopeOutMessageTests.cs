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
            m => Assert.False(Assert.IsType<CalibrationMessage>(m).IsCalibrated),
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
    public void Calibration_reply_from_the_real_scope()
    {
        // The reply to CF, then QC: one offset per range, 100 mV first (not the spec's "CB" layout).
        var replies = Captures.Packets(Captures.Calibration, CaptureChannel.Out)
            .Select(ScopeOutMessage.Parse).OfType<CalibrationMessage>().ToList();

        Assert.Equal(3, replies.Count);
        Assert.False(replies[0].IsCalibrated);
        Assert.Equal([156, 166, 193, 224, 326, 512, 819], replies[1].Offsets);
        Assert.Equal(replies[1].Offsets, replies[2].Offsets);
        Assert.Equal("Calibration (DAC counts): 100 mV 156, 200 mV 166, 500 mV 193, 1 V 224, 2 V 326, 5 V 512, 10 V 819",
            replies[1].ToString());
    }

    [Fact]
    public void Uncalibrated_scope_answers_zeros()
    {
        var message = Assert.IsType<CalibrationMessage>(ScopeOutMessage.Parse(Captures.Packet("43")));

        Assert.False(message.IsCalibrated);
        Assert.Equal("Calibration: none", message.ToString());
    }

    [Fact]
    public void Offsets_are_signed()
    {
        // Synthetic: the scope has only reported positive offsets so far.
        var message = Assert.IsType<CalibrationMessage>(ScopeOutMessage.Parse(Captures.Packet("43 00 00 00 00 FF F6")));

        Assert.Equal(-10, message.Offsets[0]);
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
