using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.Core;

public class RollHistoryTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly ScopeSettings Roll500ms = new() { TimeBase = TimeBase.All.Single(t => t.Name == "500 ms") };
    private static readonly TimeSpan FramePeriod = TimeSpan.FromMilliseconds(160); // 16 samples of 10 ms

    /// <summary>
    /// Replays a capture's roll frames with their arrival times: a new segment at each R, the
    /// time base from each State write. Frames that don't match the time base are skipped, as the app does.
    /// </summary>
    private static (RollHistory History, List<DateTimeOffset> Arrivals, List<TimeSpan> Writes) Replay(string capture)
    {
        var history = new RollHistory();
        var settings = new ScopeSettings();
        var arrivals = new List<DateTimeOffset>();
        var writes = new List<TimeSpan>();
        var received = Epoch;
        var assembler = new FrameAssembler();
        assembler.FrameCompleted += (_, frame) =>
        {
            if (settings.TimeBase.IsRoll && frame.Samples.Length == RollHistory.FrameLength)
            {
                history.Add(frame, settings, received);
                arrivals.Add(received);
            }
        };

        foreach (var packet in Captures.Load(capture))
        {
            switch (packet.Channel)
            {
                case CaptureChannel.State:
                    settings = new ScopeSettings { TimeBase = TimeBase.All.Single(t => t.SamplerCode == packet.Bytes[4]) };
                    writes.Add(packet.Time);
                    break;
                case CaptureChannel.In when packet.Bytes[0] == 'R':
                    history.StartSegment();
                    break;
                case CaptureChannel.Data:
                    received = Epoch + packet.Time;
                    assembler.AddPacket(packet.Bytes);
                    break;
            }
        }

        return (history, arrivals, writes);
    }

    private static ScopeFrame Frame(byte code = 139) => new(Enumerable.Repeat(code, RollHistory.FrameLength).ToArray(), 2);

    /// <summary>Adds frames 160 ms apart, as the scope sends them at 500 ms/div.</summary>
    private static DateTimeOffset AddFrames(RollHistory history, int count, DateTimeOffset received, byte code = 139)
    {
        for (var i = 0; i < count; i++)
        {
            received += FramePeriod;
            history.Add(Frame(code), Roll500ms, received);
        }

        return received;
    }

    [Fact]
    public void Roll_frames_are_16_samples_in_a_single_packet()
    {
        var assembler = new FrameAssembler();
        var lengths = new List<int>();
        assembler.FrameCompleted += (_, frame) => lengths.Add(frame.Samples.Length);
        foreach (var packet in Captures.Packets(Captures.Roll, CaptureChannel.Data))
        {
            assembler.AddPacket(packet);
        }

        Assert.Equal([16, 16, 16, 16, 16, 16, 512, 512, 16, 16, 16], lengths);
    }

    [Fact]
    public void Sample_count_keeps_pace_with_the_clock()
    {
        // Capture 17: 5 frames at 500 ms/div, then (after 512-sample frames) 3 at 5 s/div.
        var (history, arrivals, writes) = Replay(Captures.Roll);

        Assert.Equal([80, 48], history.Segments.Select(s => s.Count));
        Assert.Equal(80 + 48, history.SampleCount);

        // Timing the samples from the first arrival puts the last one where the last frame arrived...
        Assert.InRange((history.Segments[0].End - arrivals[4]).TotalSeconds, -0.03, 0.03);
        Assert.InRange((history.Segments[1].End - arrivals[^1]).TotalSeconds, -0.03, 0.03);

        // ...and the 5 s/div recording starts right after its settings were written.
        Assert.InRange((history.Segments[1].Start - (Epoch + writes[^1])).TotalSeconds, 0, 0.1);
    }

    [Fact]
    public void Leftover_burst_after_run_is_dropped()
    {
        // Capture 18: the 2nd and 3rd R each first deliver 2 frames at once.
        var (history, _, _) = Replay(Captures.RollRestart);

        Assert.Equal([48, 80, 64], history.Segments.Select(s => s.Count));
        Assert.Equal(64, history.CurrentSegmentCount);
    }

    [Fact]
    public void Burst_frames_never_start_a_segment()
    {
        // Synthetic timing: 3 frames 1 ms apart, then the normal cadence.
        var history = new RollHistory();
        var t = Epoch;
        foreach (var gap in new[] { 0, 1, 1, 110, 160 })
        {
            t += TimeSpan.FromMilliseconds(gap);
            history.Add(Frame(), Roll500ms, t);
            if (gap == 1)
            {
                Assert.Equal(0, history.CurrentSegmentCount);
            }
        }

        var segment = Assert.Single(history.Segments);
        Assert.Equal(32, segment.Count);
        Assert.Equal(t - TimeSpan.FromSeconds(0.32), segment.Start);
    }

    [Fact]
    public void Settings_change_starts_a_segment_in_its_own_volts()
    {
        var history = new RollHistory();
        var t = AddFrames(history, 2, Epoch);

        history.Add(Frame(), Roll500ms with { Range = VoltageRange.Range100mV }, t + FramePeriod);

        Assert.Equal(2, history.Segments.Count);
        Assert.InRange(history.Segments[0].VoltsAt(0), 0.34, 0.38); // code 139 at 1 V/div
        Assert.InRange(history.Segments[1].VoltsAt(0), 0.03, 0.04); // the same code at 100 mV/div
    }

    [Fact]
    public void Oldest_samples_are_dropped_beyond_the_limit()
    {
        var history = new RollHistory(maxSamples: 240); // trims 10 extra at a time
        var t = AddFrames(history, 3, Epoch);
        history.StartSegment();
        AddFrames(history, 20, t + TimeSpan.FromSeconds(5));

        var segment = Assert.Single(history.Segments); // the first segment went first
        Assert.Equal(230, history.SampleCount);
        Assert.Equal(230, segment.Count);
        Assert.Equal(segment.End - TimeSpan.FromSeconds(2.3), segment.Start);
    }

    [Fact]
    public void Window_selects_samples_with_times_from_the_screen_centre()
    {
        var history = new RollHistory();
        AddFrames(history, 10, Epoch); // 160 samples from Epoch to Epoch + 1.6 s
        var start = history.Start!.Value;

        var screen = history.Window(start + TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1))!;

        var trace = Assert.Single(screen.Traces);
        Assert.Equal(100, trace.Volts.Length);
        Assert.Equal(-0.5, trace.FirstSampleTime, precision: 9);
        Assert.Equal(0.01, trace.SampleInterval, precision: 12);
        Assert.Equal(Roll500ms.ToVolts(139), screen.Mean, precision: 12);
    }

    [Fact]
    public void Gaps_between_runs_split_the_trace()
    {
        var history = new RollHistory();
        var t = AddFrames(history, 5, Epoch, code: 100);
        history.StartSegment();
        AddFrames(history, 5, t + TimeSpan.FromSeconds(1), code: 200);

        var screen = history.Window(history.Start!.Value, TimeSpan.FromSeconds(5))!;

        Assert.Equal(2, screen.Traces.Count);
        Assert.Equal(Roll500ms.ToVolts(100), screen.Minimum, precision: 12);
        Assert.Equal(Roll500ms.ToVolts(200), screen.Maximum, precision: 12);
        Assert.Equal(1.8, screen.Traces[1].FirstSampleTime - screen.Traces[0].FirstSampleTime, precision: 2); // 0.8 s of samples + 1 s gap
    }

    [Fact]
    public void Long_windows_keep_minimum_and_maximum_per_column()
    {
        // An hour at 500 ms/div with one 10 ms spike: 360,000 samples reduced to 1000 columns.
        var history = new RollHistory();
        var t = Epoch;
        for (var i = 0; i < 22_500; i++)
        {
            t += FramePeriod;
            history.Add(Frame(i == 10_000 ? (byte)255 : (byte)139), Roll500ms, t);
        }

        var screen = history.Window(history.Start!.Value, TimeSpan.FromHours(1))!;

        var trace = Assert.Single(screen.Traces);
        Assert.InRange(trace.Volts.Length, 2 * RollHistory.ScreenColumns - 2, 2 * RollHistory.ScreenColumns + 2);
        Assert.Equal(Roll500ms.ToVolts(255), trace.Volts.Max(), precision: 12); // the spike survives
        Assert.Equal(Roll500ms.ToVolts(255), screen.Maximum, precision: 12);
        Assert.Equal(-1800, trace.FirstSampleTime, precision: 6); // left edge
    }

    [Fact]
    public void Empty_window_is_null()
    {
        var history = new RollHistory();
        AddFrames(history, 2, Epoch);

        Assert.Null(history.Window(Epoch + TimeSpan.FromHours(1), TimeSpan.FromSeconds(5)));
        Assert.Null(new RollHistory().Window(Epoch, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var history = new RollHistory();
        AddFrames(history, 2, Epoch);

        history.Clear();

        Assert.Equal(0, history.SampleCount);
        Assert.Null(history.Start);
        Assert.Null(history.End);
    }
}

public class ScreenTraceTests
{
    [Fact]
    public void Window_keeps_the_samples_between_two_times_with_their_statistics()
    {
        var waveform = new Waveform([0, 1, 2, 3, 4, 5, 6, 7], SampleInterval: 1, FirstSampleTime: -4);

        var screen = ScreenTrace.Window(waveform, -1, 1);

        var trace = Assert.Single(screen.Traces);
        Assert.Equal([3.0, 4, 5], trace.Volts);
        Assert.Equal(-1, trace.FirstSampleTime);
        Assert.Equal(3, screen.Minimum);
        Assert.Equal(5, screen.Maximum);
    }
}

public class FrameHistoryTests
{
    private static RecordedFrame Recorded(int index) => new(
        new ScopeFrame(new byte[512], 0),
        new ScopeSettings(),
        new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero) + TimeSpan.FromSeconds(index));

    [Fact]
    public void Keeps_frames_in_order()
    {
        var history = new FrameHistory();

        history.Add(Recorded(0));
        history.Add(Recorded(1));

        Assert.Equal(2, history.Count);
        Assert.Equal(1024, history.Bytes);
        Assert.True(history[0].Received < history[1].Received);
    }

    [Fact]
    public void Drops_the_oldest_tenth_beyond_the_limit()
    {
        var history = new FrameHistory(maxBytes: 100 * 512);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(0, history.Add(Recorded(i)));
        }

        var dropped = history.Add(Recorded(100));

        Assert.Equal(11, dropped); // down to 90 frames
        Assert.Equal(90, history.Count);
        Assert.Equal(Recorded(11).Received, history[0].Received);
    }

    [Fact]
    public void A_frame_larger_than_the_limit_is_still_kept()
    {
        var history = new FrameHistory(maxBytes: 100);

        history.Add(Recorded(0));

        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void Converts_with_the_recorded_settings()
    {
        var frame = new RecordedFrame(new ScopeFrame(Enumerable.Repeat((byte)255, 512).ToArray(), 0), new ScopeSettings { Range = VoltageRange.Range2V }, default);

        var screen = ScreenTrace.From(frame.ToWaveform());

        Assert.Equal(8, screen.Maximum, precision: 12); // top of the screen at 2 V/div
        Assert.Equal(0, screen.PeakToPeak);
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var history = new FrameHistory();
        history.Add(Recorded(0));

        history.Clear();

        Assert.Equal(0, history.Count);
        Assert.Equal(0, history.Bytes);
    }
}
