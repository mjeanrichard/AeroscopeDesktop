using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.Core;

/// <summary>Captures 27, 28 and 33: the probe tip shorted, so every frame should read 0 V.</summary>
public class CalibrationTests
{
    private sealed record Frame(VoltageRange Range, ushort Dac, double MeanCode, bool Calibrated);

    /// <summary>The calibration the scope reported before deep sleep erased it (the DAC counts added in capture 33).</summary>
    private static readonly short[] Remembered = [156, 166, 192, 245, 326, 512, 819];

    /// <summary>Frames of a capture with the range and DAC they were taken at and whether the scope was calibrated then.</summary>
    private static List<Frame> Frames(string capture)
    {
        var assembler = new FrameAssembler();
        var frames = new List<Frame>();
        var range = VoltageRange.Range1V;
        ushort dac = 0x8000;
        var calibrated = false;
        assembler.FrameCompleted += (_, frame) => frames.Add(new Frame(range, dac, frame.Samples.Average(s => (double)s), calibrated));

        foreach (var packet in Captures.Load(capture))
        {
            switch (packet.Channel)
            {
                case CaptureChannel.State:
                    range = Captures.SettingsFrom(packet.Bytes).Range;
                    dac = (ushort)(packet.Bytes[11] << 8 | packet.Bytes[12]);
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

    [Theory]
    [InlineData("1 V", 0x80F5)]
    [InlineData("100 mV", 0x809C)]
    public void The_app_adds_remembered_offsets_to_the_dac_but_not_to_the_scaling(string rangeName, int expectedDac)
    {
        var range = VoltageRange.All.Single(r => r.Name == rangeName);
        var plain = new ScopeSettings { Range = range };
        var corrected = plain with { CalibrationOffsets = Remembered };

        Assert.Equal(0x8000, plain.ToRegisters().OffsetDac);
        Assert.Equal(expectedDac, corrected.ToRegisters().OffsetDac);
        Assert.Equal(plain.ZeroCode, corrected.ZeroCode);
        Assert.Equal(plain.TriggerLevelCode, corrected.TriggerLevelCode);
    }

    [Fact]
    public void Remembered_offsets_added_by_the_app_read_zero_like_the_scopes_own_calibration()
    {
        // Capture 33: uncalibrated after deep sleep, frames at the plain DAC, at the DAC the app sends with
        // the remembered offsets, then at the plain DAC once the scope calibrated itself again.
        var frames = Frames(Captures.AppCalibration);
        Assert.Equal(12, frames.Count);

        foreach (var range in new[] { VoltageRange.Range1V, VoltageRange.Range100mV })
        {
            var settings = new ScopeSettings { Range = range, CalibrationOffsets = Remembered };
            var ofRange = frames.Where(f => f.Range == range).ToList();
            var plain = ofRange.Where(f => !f.Calibrated && f.Dac == 0x8000);
            var corrected = ofRange.Where(f => !f.Calibrated && f.Dac == settings.ToRegisters().OffsetDac);
            var calibrated = ofRange.Where(f => f.Calibrated);

            Assert.All(plain, f => Assert.True(f.MeanCode - settings.ZeroCode > 10, $"{range}: uncalibrated reads high"));
            Assert.Equal(2, corrected.Count());
            Assert.All(corrected, f => Assert.Equal(0, settings.ToVolts(f.MeanCode), 1.5 * range.VoltsPerCode));
            Assert.All(calibrated, f => Assert.Equal(0, settings.ToVolts(f.MeanCode), 1.5 * range.VoltsPerCode));
        }
    }

    [Fact]
    public void Range_index_follows_the_order_of_the_calibration_offsets()
    {
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], VoltageRange.All.Select(r => r.Index));
        Assert.Equal(3, VoltageRange.Range1V.Index);
    }
}
