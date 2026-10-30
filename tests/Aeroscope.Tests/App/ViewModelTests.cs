using System.ComponentModel;
using Aeroscope.App.ViewModels;
using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.App;

public class LogEntryTests
{
    [Fact]
    public void Packet_shows_hex_and_printable_ascii()
    {
        var entry = LogEntry.Packet(LogKind.Out, [0x50, 0x46, 0x00, 0x7F, 0x20]);

        Assert.Equal("50 46 00 7F 20  |PF.. |", entry.Text);
        Assert.Equal("OUT", entry.KindText);
    }

    [Fact]
    public void Message_keeps_text_and_kind()
    {
        var entry = LogEntry.Message(LogKind.Error, "boom");

        Assert.Equal("boom", entry.Text);
        Assert.Equal("ERROR", entry.KindText);
    }

    [Fact]
    [UseInvariantCulture]
    public void Time_has_milliseconds()
    {
        var entry = new LogEntry(new DateTime(2026, 10, 27, 21, 5, 9, 42), LogKind.Info, "");

        Assert.Equal("21:05:09.042", entry.TimeText);
    }
}

public class DeviceItemTests
{
    [Fact]
    public void Address_is_formatted()
    {
        Assert.Equal("C6:A7:C8:D1:1A:B1", new DeviceItem(0xC6A7C8D11AB1).AddressText);
    }

    [Fact]
    public void Display_name_falls_back_when_unnamed()
    {
        var item = new DeviceItem(1);
        Assert.Equal("(unnamed)", item.DisplayName);

        item.Name = "Aeroscope";
        Assert.Equal("Aeroscope", item.DisplayName);
    }

    [Fact]
    public void Setting_name_notifies_display_name()
    {
        var item = new DeviceItem(1);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.Name = "Aeroscope";

        Assert.Equal([nameof(DeviceItem.Name), nameof(DeviceItem.DisplayName)], changed);
    }
}

[UseInvariantCulture]
public class ScopeStatusTests
{
    private readonly ScopeStatus _status = new();

    [Fact]
    public void Starts_unknown()
    {
        Assert.Equal("–", _status.Power);
        Assert.Equal("–", _status.Battery);
        Assert.Equal(0, _status.FramesReceived);
    }

    [Fact]
    public void Real_startup_replies_fill_the_status()
    {
        foreach (var packet in Captures.Packets(Captures.Startup, CaptureChannel.Out))
        {
            _status.Apply(ScopeOutMessage.Parse(packet));
        }

        Assert.Equal("Fully on", _status.Power);
        Assert.Equal("100 %, charging", _status.Battery);
        Assert.Equal("Raw level 245 (full)", _status.BatteryLevel);
        Assert.Equal("20.6 °C", _status.Temperature);
        Assert.Equal("MCU 5, FPGA 5, HW 1", _status.Firmware);
        Assert.Equal("1000057", _status.SerialNumber);
    }

    [Fact]
    public void Power_off_while_configuring()
    {
        _status.Apply(new PowerStateMessage(FullyOn: false));

        Assert.Equal("FPGA off / configuring", _status.Power);
    }

    [Theory]
    [InlineData(false, false, "")]
    [InlineData(true, false, ", charger connected")]
    [InlineData(true, true, ", charging")]
    public void Charger_state(bool connected, bool charging, string suffix)
    {
        _status.Apply(new TelemetryMessage(connected, charging, 230, 21));

        Assert.Equal($"55 %{suffix}", _status.Battery);
    }

    [Theory]
    // Spec: above 238 full, 226-238 partial, 220-226 low.
    [InlineData(255, "full", 100)]
    [InlineData(239, "full", 100)]
    [InlineData(238, "partial", 95)]
    [InlineData(226, "partial", 35)]
    [InlineData(225, "low", 30)]
    [InlineData(220, "low", 5)]
    [InlineData(219, "empty", 0)]
    [InlineData(0, "empty", 0)]
    public void Battery_thresholds(byte level, string state, int percent)
    {
        _status.Apply(new TelemetryMessage(false, false, level, 21));

        Assert.Equal($"{percent} %", _status.Battery);
        Assert.Equal($"Raw level {level} ({state})", _status.BatteryLevel);
    }

    [Fact]
    public void Messages_without_status_fields_change_nothing()
    {
        _status.Apply(new ButtonPressedMessage());
        _status.Apply(new ErrorLogMessage([]));

        Assert.Equal("–", _status.Power);
    }

    [Fact]
    public void Real_frames_are_counted_and_summarised()
    {
        var assembler = new FrameAssembler();
        assembler.FrameCompleted += (_, frame) => _status.Apply(frame);
        foreach (var packet in Captures.Packets(Captures.RawSingle, CaptureChannel.Data))
        {
            assembler.AddPacket(packet);
        }

        Assert.Equal(2, _status.FramesReceived);
        Assert.StartsWith("512 samples, subtrigger 58", _status.LastFrame);
    }

    [Fact]
    public void Reset_clears_everything()
    {
        _status.Apply(new PowerStateMessage(true));
        _status.Apply(new ScopeFrame([1], 0));

        _status.Reset();

        Assert.Equal("–", _status.Power);
        Assert.Equal("–", _status.LastFrame);
        Assert.Equal(0, _status.FramesReceived);
    }
}
