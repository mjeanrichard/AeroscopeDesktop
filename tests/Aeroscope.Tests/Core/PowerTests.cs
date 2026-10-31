using System.IO;
using System.Text;
using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.Core;

/// <summary>Captures 29-32: turning the FPGA off and on (PO, PF), and deep sleep (ZZ).</summary>
public class PowerTests
{
    /// <summary>The frames of a capture with the time of their last packet, in order.</summary>
    private static List<(double Time, ScopeFrame Frame)> Frames(string capture)
    {
        var assembler = new FrameAssembler();
        var frames = new List<(double, ScopeFrame)>();
        var time = 0.0;
        assembler.FrameCompleted += (_, frame) => frames.Add((time, frame));
        foreach (var packet in Captures.Load(capture).Where(p => p.Channel == CaptureChannel.Data))
        {
            time = packet.Time.TotalSeconds;
            assembler.AddPacket(packet.Bytes);
        }

        return frames;
    }

    private static List<(double Time, ScopeOutMessage Message)> OutMessages(string capture)
        => Captures.Load(capture).Where(p => p.Channel == CaptureChannel.Out)
            .Select(p => (p.Time.TotalSeconds, ScopeOutMessage.Parse(p.Bytes))).ToList();

    private static double TimeOf(string capture, string command)
        => Captures.Load(capture).First(p => p.Channel == CaptureChannel.In && p.Bytes.AsSpan().StartsWith(Encoding.ASCII.GetBytes(command))).Time.TotalSeconds;

    [Fact]
    public void Po_is_answered_at_once_and_pf_about_one_and_a_half_seconds_later()
    {
        var messages = OutMessages(Captures.FpgaOff);
        var off = TimeOf(Captures.FpgaOff, "PO");
        var on = TimeOf(Captures.FpgaOff, "PF");

        var firstOff = messages.First(m => m.Time >= off);
        Assert.Equal(new PowerStateMessage(FullyOn: false), firstOff.Message);
        Assert.InRange(firstOff.Time - off, 0, 0.1);

        var ready = messages.First(m => m.Time > on && m.Message is PowerStateMessage { FullyOn: true });
        Assert.InRange(ready.Time - on, 1.2, 1.6);
    }

    [Fact]
    public void Telemetry_keeps_coming_while_the_fpga_is_off()
    {
        var off = TimeOf(Captures.FpgaOff, "PO");
        var on = TimeOf(Captures.FpgaOff, "PF");

        Assert.Contains(OutMessages(Captures.FpgaOff), m => m.Time > off && m.Time < on && m.Message is TelemetryMessage);
    }

    [Fact]
    public void While_the_fpga_is_off_settings_and_r_give_no_frames_and_pf_loses_the_registers()
    {
        var off = TimeOf(Captures.FpgaOff, "PO");
        var on = TimeOf(Captures.FpgaOff, "PF");
        var frames = Frames(Captures.FpgaOff);

        Assert.Equal(2, frames.Count(f => f.Time < off));
        Assert.DoesNotContain(frames, f => f.Time > off && f.Time < on);

        // Same signal before (94-158); after PF and R without settings again, frames clip at both ends.
        Assert.All(frames.Where(f => f.Time < off), f => Assert.InRange(f.Frame.Samples.Min(), 90, 100));
        var after = frames.Where(f => f.Time > on).ToList();
        Assert.Equal(2, after.Count);
        Assert.All(after, f => Assert.Equal(0, f.Frame.Samples.Min()));
        Assert.All(after, f => Assert.Equal(255, f.Frame.Samples.Max()));
    }

    [Fact]
    public void Settings_written_again_after_pf_give_the_same_frames_as_before()
    {
        var on = TimeOf(Captures.FpgaOffSettings, "PF");
        var frames = Frames(Captures.FpgaOffSettings);

        Assert.Equal(4, frames.Count);
        Assert.All(frames, f => Assert.Equal(94, f.Frame.Samples.Min()));
        Assert.All(frames, f => Assert.Equal(158, f.Frame.Samples.Max()));
        Assert.Equal(2, frames.Count(f => f.Time > on));
    }

    [Fact]
    public void Deep_sleep_drops_the_link_without_acknowledging_zz()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Captures", Captures.DeepSleep));

        Assert.Contains("# step ZZ\n# connection lost", text.ReplaceLineEndings("\n"));
        Assert.DoesNotContain(Captures.Load(Captures.DeepSleep), p => p.Channel == CaptureChannel.In && p.Bytes[0] == 'Z');
    }

    [Fact]
    public void Waking_from_deep_sleep_sends_no_pf_and_the_calibration_is_gone()
    {
        var messages = OutMessages(Captures.Wake).Select(m => m.Message).ToList();

        Assert.DoesNotContain(messages, m => m is PowerStateMessage);
        var calibration = Assert.Single(messages.OfType<CalibrationMessage>());
        Assert.False(calibration.IsCalibrated);
        Assert.Equal(2, Frames(Captures.Wake).Count);
    }
}
