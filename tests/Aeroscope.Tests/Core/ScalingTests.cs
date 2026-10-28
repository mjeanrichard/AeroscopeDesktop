using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.Core;

public class VoltageRangeTests
{
    [Fact]
    public void Seven_ranges_with_unique_front_end_codes()
    {
        Assert.Equal(7, VoltageRange.All.Count);
        Assert.Equal(VoltageRange.All.Count, VoltageRange.All.Select(r => r.FrontEndCode).Distinct().Count());
        Assert.Equal(VoltageRange.All.OrderBy(r => r.VoltsPerDivision), VoltageRange.All);
    }

    [Fact]
    public void Eight_divisions_span_the_adc()
    {
        Assert.Equal(8.0 / 255, VoltageRange.Range1V.VoltsPerCode, precision: 12);
        Assert.Equal(0.8 / 255, VoltageRange.Range100mV.VoltsPerCode, precision: 12);
    }

    [Fact]
    public void Dac_step_is_about_1_5_mV_on_every_range()
    {
        // Spec: the 16-bit DAC spans +/-50 V input-referred, i.e. 100 V / 65536 per count.
        Assert.All(VoltageRange.All, range => Assert.Equal(100.0 / 65536, range.VoltsPerDacCount, tolerance: 0.00002));
    }

    [Fact]
    public void Name_is_per_division()
    {
        Assert.Equal("100 mV/div", VoltageRange.Range100mV.ToString());
    }
}

public class TimeBaseTests
{
    [Fact]
    public void Settings_from_500_ns_to_5_s()
    {
        Assert.Equal(22, TimeBase.All.Count);
        Assert.Equal(TimeBase.All.Count, TimeBase.All.Select(t => t.SamplerCode).Distinct().Count());
        Assert.Equal(TimeBase.All.OrderBy(t => t.SampleInterval), TimeBase.All);
    }

    [Theory]
    [InlineData("500 ns", 0x00, 500e-9)] // undivided 100 MS/s
    [InlineData("5 ms", 0x0C, 5e-3)] // used for the mains-hum captures
    [InlineData("200 ms", 0x25, 200e-3)]
    public void Code_and_scale(string name, byte code, double secondsPerDivision)
    {
        var timeBase = TimeBase.All.Single(t => t.Name == name);

        Assert.Equal(code, timeBase.SamplerCode);
        Assert.Equal(secondsPerDivision, timeBase.SecondsPerDivision, precision: 12);
        Assert.Equal($"{name}/div", timeBase.ToString());
        Assert.False(timeBase.IsRoll);
    }

    [Theory]
    [InlineData("500 ms", 0xE7, 0.5)]
    [InlineData("1 s", 0xEF, 1)]
    [InlineData("2 s", 0xF7, 2)]
    [InlineData("5 s", 0xFF, 5)]
    public void Slowest_four_are_roll_mode(string name, byte code, double secondsPerDivision)
    {
        var timeBase = TimeBase.All.Single(t => t.Name == name);

        Assert.True(timeBase.IsRoll);
        Assert.Equal(code, timeBase.SamplerCode);
        Assert.Equal(secondsPerDivision, timeBase.SecondsPerDivision, precision: 12);
        Assert.Equal($"{name}/div roll", timeBase.ToString());
    }

    [Fact]
    public void Roll_frames_arrive_in_real_time()
    {
        // 16 samples per frame: one every 160 ms at 500 ms/div, every 1.6 s at 5 s/div, so
        // consecutive frames join up with no gap.
        var starts = Captures.Load(Captures.Roll)
            .Where(p => p.Channel == CaptureChannel.Data && p.Bytes[0] == 0x01)
            .Select(p => p.Time.TotalSeconds)
            .ToList();
        var fast = starts.Take(5).ToList();
        var slow = starts.TakeLast(3).ToList();

        Assert.InRange((fast[^1] - fast[0]) / (fast.Count - 1), 0.15, 0.17);
        Assert.InRange((slow[^1] - slow[0]) / (slow.Count - 1), 1.55, 1.65);
    }

    [Fact]
    public void Default_is_the_fastest()
    {
        Assert.Equal(TimeBase.All[0], TimeBase.Default);
    }
}

public class ScopeSettingsTests
{
    [Fact]
    public void Defaults_produce_the_android_registers_with_a_512_sample_record()
    {
        var record512 = new ScopeRegisters { WriteDepth = 0x06, TriggerPosition = 0x100, ReadStartAddress = 0 };

        Assert.Equal(record512.ToPacket(), new ScopeSettings().ToRegisters().ToPacket());
    }

    [Theory]
    [InlineData(TriggerEdge.Rising, false, false, 0x02)]
    [InlineData(TriggerEdge.Falling, false, false, 0x04)]
    [InlineData(TriggerEdge.Both, false, false, 0x06)]
    [InlineData(TriggerEdge.Rising, true, false, 0x03)]
    [InlineData(TriggerEdge.Falling, true, true, 0x25)]
    [InlineData(TriggerEdge.Both, true, true, 0x07)] // noise reject only with a single edge
    public void Trigger_control_bits(TriggerEdge edge, bool auto, bool noiseReject, byte expected)
    {
        var settings = new ScopeSettings { TriggerEdge = edge, AutoTrigger = auto, NoiseReject = noiseReject };

        Assert.Equal(expected, settings.TriggerControl);
    }

    [Theory]
    [InlineData(true, 0xE0)] // the 100 mV/div DC value used in capture 16
    [InlineData(false, 0x60)]
    public void Front_end_combines_coupling_and_range(bool dc, byte expected)
    {
        var settings = new ScopeSettings { Range = VoltageRange.Range100mV, DcCoupled = dc };

        Assert.Equal(expected, settings.ToRegisters().FrontEnd);
    }

    [Fact]
    public void Time_base_selects_the_sampler_code()
    {
        var settings = new ScopeSettings { TimeBase = TimeBase.All.Single(t => t.Name == "5 ms") };

        Assert.Equal(0x0C, settings.ToRegisters().SamplerControl);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(-1024)]
    public void Offset_converts_to_dac_counts(int counts)
    {
        var settings = new ScopeSettings { OffsetVolts = counts * VoltageRange.Range1V.VoltsPerDacCount };

        Assert.Equal(0x8000 + counts, settings.OffsetDac);
        Assert.Equal(127.5 - counts / 20.48, settings.ZeroCode, precision: 9); // raising the offset moves 0 V down
    }

    [Theory]
    [InlineData(1000, 65535 - 3277)]
    [InlineData(-1000, 3277)]
    public void Offset_dac_is_kept_away_from_the_ends(double volts, int dac)
    {
        Assert.Equal(dac, new ScopeSettings { OffsetVolts = volts }.OffsetDac);
    }

    [Fact]
    public void Codes_convert_to_volts_around_the_zero_code()
    {
        var settings = new ScopeSettings();

        Assert.Equal(0, settings.ToVolts(127.5), precision: 12);
        Assert.Equal(4, settings.ToVolts(255), precision: 12); // top of the screen at 1 V/div
        Assert.Equal(-4, settings.ToVolts(0), precision: 12);
    }

    [Theory]
    [InlineData(0, 0, 128)]
    [InlineData(1, 0, 159)]
    [InlineData(10, 0, 255)]
    [InlineData(-10, 0, 0)]
    [InlineData(2, 2, 127)] // follows the offset; the DAC's whole-count rounding puts 0 V at code 127.48
    public void Trigger_level_converts_to_an_adc_code(double triggerVolts, double offsetVolts, byte code)
    {
        var settings = new ScopeSettings { TriggerLevelVolts = triggerVolts, OffsetVolts = offsetVolts };

        Assert.Equal(code, settings.TriggerLevelCode);
        Assert.Equal(code, settings.ToRegisters().TriggerLevel);
    }
}

public class WaveformTests
{
    [Fact]
    public void Time_zero_is_at_the_trigger_sample_shifted_by_the_subtrigger()
    {
        var settings = new ScopeSettings(); // 10 ns per sample

        var plain = Waveform.From(new ScopeFrame(new byte[512], SubTrigger: 0), settings);
        var shifted = Waveform.From(new ScopeFrame(new byte[512], SubTrigger: 32), settings);

        Assert.Equal(0, plain.TimeOf(256), precision: 15);
        Assert.Equal(-2.56e-6, plain.TimeOf(0), precision: 15);
        Assert.Equal(5e-9, shifted.TimeOf(256), precision: 15); // half a sample later
        Assert.Equal(10e-9, plain.SampleInterval, precision: 15);
    }

    [Fact]
    public void Statistics()
    {
        var waveform = new Waveform([-1, 0, 3], 1, 0);

        Assert.Equal(-1, waveform.Minimum);
        Assert.Equal(3, waveform.Maximum);
        Assert.Equal(4, waveform.PeakToPeak);
        Assert.Equal(2.0 / 3, waveform.Mean, precision: 12);
    }

    [Fact]
    public void Mains_hum_has_the_same_amplitude_in_volts_at_both_ranges()
    {
        var waveforms = Replay(Captures.TouchHum);

        var at1V = waveforms.Take(4).Average(w => w.PeakToPeak);
        var at2V = waveforms.Skip(4).Average(w => w.PeakToPeak);
        Assert.InRange(at1V, 2.2, 2.5);
        Assert.InRange(at2V / at1V, 0.95, 1.05);
    }

    [Fact]
    public void Real_rising_edge_crosses_the_trigger_level_at_time_zero()
    {
        // Capture 06 at 1 V/div used a rising trigger at code 128 (0 V); the hum crosses it every 20 ms.
        var settings = new ScopeSettings { TimeBase = TimeBase.All.Single(t => t.Name == "5 ms") };
        var assembler = new FrameAssembler();
        var frames = new List<ScopeFrame>();
        assembler.FrameCompleted += (_, frame) => frames.Add(frame);
        foreach (var packet in Captures.Packets(Captures.TouchHum, CaptureChannel.Data))
        {
            assembler.AddPacket(packet);
        }

        Assert.All(frames.Take(4), frame =>
        {
            var waveform = Waveform.From(frame, settings);
            var trigger = ScopeSettings.TriggerIndex(frame.Samples.Length);
            Assert.Equal(0, waveform.TimeOf(trigger), tolerance: waveform.SampleInterval);
            Assert.True(waveform.Volts[(trigger - 8)..(trigger - 2)].Average() < 0, "below the level just before the trigger");
            Assert.True(waveform.Volts[(trigger + 2)..(trigger + 8)].Average() > 0, "above the level just after the trigger");
        });
    }

    [Fact]
    public void Floating_input_reads_the_same_voltage_whatever_the_offset()
    {
        // The unconnected input sits at the scope's uncalibrated zero error. Moving the
        // offset DAC moves the trace, but converted to volts the level must not change.
        var waveforms = Replay(Captures.OffsetDac, skipSaturated: true);

        var at1V = waveforms.Take(6).Select(w => w.Mean).ToList();
        var at100mV = waveforms.Skip(6).Select(w => w.Mean).ToList();
        Assert.Equal(4, at100mV.Count);
        Assert.All(at1V, v => Assert.InRange(v, 0.34, 0.38));
        Assert.All(at100mV, v => Assert.InRange(v, 0.25, 0.265));
    }

    /// <summary>
    /// Replays a capture, converting each frame with the settings from the last State write.
    /// The first frame after a write was acquired with the previous settings and is skipped.
    /// </summary>
    private static List<Waveform> Replay(string capture, bool skipSaturated = false)
    {
        var assembler = new FrameAssembler();
        var settings = new ScopeSettings();
        var skipNext = false;
        var waveforms = new List<Waveform>();
        assembler.FrameCompleted += (_, frame) =>
        {
            if (skipNext)
            {
                skipNext = false;
                return;
            }

            if (!(skipSaturated && frame.Samples.Contains((byte)255)))
            {
                waveforms.Add(Waveform.From(frame, settings));
            }
        };

        foreach (var packet in Captures.Load(capture))
        {
            switch (packet.Channel)
            {
                case CaptureChannel.State:
                    settings = Captures.SettingsFrom(packet.Bytes);
                    skipNext = waveforms.Count > 0;
                    break;
                case CaptureChannel.Data:
                    assembler.AddPacket(packet.Bytes);
                    break;
            }
        }

        return waveforms;
    }
}
