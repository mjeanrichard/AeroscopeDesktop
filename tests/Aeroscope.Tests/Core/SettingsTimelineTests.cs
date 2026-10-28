using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.Core;

public class SettingsTimelineTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly ScopeSettings At1V = new();
    private static readonly ScopeSettings At2V = new() { Range = VoltageRange.Range2V };

    private static DateTimeOffset Ms(double milliseconds) => Epoch + TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>Replays a capture's writes and frames; returns each frame with the settings it was given.</summary>
    private static List<(ScopeFrame Frame, ScopeSettings Settings)> Replay(string capture)
    {
        var timeline = new SettingsTimeline();
        var assembler = new FrameAssembler();
        var result = new List<(ScopeFrame, ScopeSettings)>();
        DateTimeOffset now = default, started = default;
        assembler.FrameCompleted += (_, frame) => result.Add((frame, timeline.FrameArrived(started, now)));

        foreach (var packet in Captures.Load(capture))
        {
            now = Epoch + packet.Time;
            switch (packet.Channel)
            {
                case CaptureChannel.State:
                    timeline.Written(Captures.SettingsFrom(packet.Bytes), now);
                    break;
                case CaptureChannel.In when packet.Bytes[0] == 'R':
                    timeline.Started(now);
                    break;
                case CaptureChannel.Data:
                    if (packet.Bytes[0] != 0)
                    {
                        started = now;
                    }

                    assembler.AddPacket(packet.Bytes);
                    break;
            }
        }

        return result;
    }

    [Theory]
    [InlineData(Captures.SettingsWhileRunning, 65)] // 500 ns/div
    [InlineData(Captures.SettingsWhileRunning20ms, 18)]
    [InlineData(Captures.SettingsWhileRunning200ms, 3)]
    public void Each_frame_gets_the_range_it_was_taken_with(string capture, int count)
    {
        // Captures 20-22: the sine is 64 codes peak to peak at 1 V/div and 33 at 2 V/div.
        var frames = Replay(capture);

        Assert.Equal(count, frames.Count);
        Assert.All(frames, f =>
        {
            var taken = f.Frame.Samples.Max() - f.Frame.Samples.Min() > 48 ? VoltageRange.Range1V : VoltageRange.Range2V;
            Assert.Equal(taken, f.Settings.Range);
        });
    }

    [Fact]
    public void Each_frame_gets_the_offset_it_was_taken_with()
    {
        // Capture 16: the floating input reads the same voltage whatever the offset.
        var frames = Replay(Captures.OffsetDac).Where(f => !f.Frame.Samples.Contains((byte)255)).ToList();

        Assert.Equal(15, frames.Count);
        Assert.All(frames, f => Assert.InRange(Waveform.From(f.Frame, f.Settings).Mean, 0.25, 0.38));
    }

    [Fact]
    public void A_write_acknowledged_with_the_end_of_the_previous_frame_applies_to_the_next()
    {
        var timeline = new SettingsTimeline();
        timeline.Written(At1V, Ms(0));
        timeline.Started(Ms(0));
        Assert.Same(At1V, timeline.FrameArrived(Ms(100), Ms(200)));

        timeline.Written(At2V, Ms(200));

        Assert.Same(At2V, timeline.FrameArrived(Ms(201), Ms(300)));
    }

    [Fact]
    public void A_frame_waiting_to_be_sent_keeps_the_old_settings()
    {
        var timeline = new SettingsTimeline();
        timeline.Written(At1V, Ms(0));
        timeline.Started(Ms(0));
        timeline.FrameArrived(Ms(100), Ms(200));

        timeline.Written(At2V, Ms(240)); // after the previous frame...

        Assert.Same(At1V, timeline.FrameArrived(Ms(241), Ms(400))); // ...but just before this one
        Assert.Same(At2V, timeline.FrameArrived(Ms(401), Ms(500)));
    }

    [Fact]
    public void A_frame_already_arriving_keeps_the_old_settings()
    {
        var timeline = new SettingsTimeline();
        timeline.Written(At1V, Ms(0));
        timeline.Started(Ms(0));

        timeline.Written(At2V, Ms(150));

        Assert.Same(At1V, timeline.FrameArrived(Ms(100), Ms(200)));
    }

    [Fact]
    public void A_write_one_connection_event_before_the_frame_applies()
    {
        var timeline = new SettingsTimeline();
        timeline.Written(At1V, Ms(0));
        timeline.Started(Ms(0));
        timeline.FrameArrived(Ms(100), Ms(200));

        timeline.Written(At2V, Ms(237));

        Assert.Same(At2V, timeline.FrameArrived(Ms(274), Ms(400)));
    }

    [Fact]
    public void Run_starts_with_the_latest_settings()
    {
        var timeline = new SettingsTimeline();
        timeline.Written(At1V, Ms(0));
        timeline.Written(At2V, Ms(50));

        timeline.Started(Ms(60));

        Assert.Same(At2V, timeline.Latest);
        Assert.Same(At2V, timeline.FrameArrived(Ms(61), Ms(100)));
    }
}
