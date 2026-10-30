using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.Core;

/// <summary>Captures 27 and 28: the probe tip shorted, so every frame should read 0 V.</summary>
public class CalibrationTests
{
    private sealed record Frame(VoltageRange Range, double MeanCode, bool Calibrated);

    /// <summary>Frames of a capture with the range they were taken at and whether the scope was calibrated then.</summary>
    private static List<Frame> Frames(string capture)
    {
        var assembler = new FrameAssembler();
        var frames = new List<Frame>();
        var range = VoltageRange.Range1V;
        var calibrated = false;
        assembler.FrameCompleted += (_, frame) => frames.Add(new Frame(range, frame.Samples.Average(s => (double)s), calibrated));

        foreach (var packet in Captures.Load(capture))
        {
            switch (packet.Channel)
            {
                case CaptureChannel.State:
                    range = Captures.SettingsFrom(packet.Bytes).Range;
                    break;
                case CaptureChannel.Out when ScopeOutMessage.Parse(packet.Bytes) is CalibrationMessage message:
                    calibrated = message.IsCalibrated;
                    break;
                case CaptureChannel.In when packet.Bytes[0] == 'S':
                    assembler.Reset();
                    break;
                case CaptureChannel.Data:
                    assembler.AddPacket(packet.Bytes);
                    break;
            }
        }

        return frames;
    }

    [Fact]
    public void Uncalibrated_zero_error_is_a_dac_offset_per_range()
    {
        // Each range's offset, converted to ADC codes, is where 0 V read before calibrating.
        var frames = Frames(Captures.Calibration);
        var reply = Captures.Packets(Captures.Calibration, CaptureChannel.Out)
            .Select(ScopeOutMessage.Parse).OfType<CalibrationMessage>().First(m => m.IsCalibrated);

        foreach (var (range, offset) in VoltageRange.All.Zip(reply.Offsets))
        {
            var before = frames.Where(f => f.Range == range && !f.Calibrated).Average(f => f.MeanCode);
            Assert.Equal(offset / range.DacCountsPerCode, before - 127.5, 2.0);
        }
    }

    [Fact]
    public void Calibrated_scope_corrects_itself()
    {
        // Same registers before and after CF: the scope applies the offsets, the app must not.
        var frames = Frames(Captures.Calibration);

        Assert.Equal(14, frames.Count(f => !f.Calibrated));
        Assert.Equal(14, frames.Count(f => f.Calibrated));
        Assert.All(frames.Where(f => f.Calibrated), f => Assert.InRange(f.MeanCode, 126, 129));
        Assert.All(frames.Where(f => !f.Calibrated), f => Assert.InRange(f.MeanCode, 130.9, 206));
    }

    [Fact]
    public void Clearing_brings_the_zero_error_back_and_calibrating_again_removes_it()
    {
        var frames = Frames(Captures.CalibrationClear);

        Assert.Equal(4, frames.Count);
        Assert.All(frames.Take(2), f => Assert.False(f.Calibrated));
        Assert.All(frames.Take(2), f => Assert.Equal(138.5, f.MeanCode, 0.5));
        Assert.All(frames.Skip(2), f => Assert.True(f.Calibrated));
        Assert.All(frames.Skip(2), f => Assert.Equal(127.5, f.MeanCode, 1.5));
    }
}
