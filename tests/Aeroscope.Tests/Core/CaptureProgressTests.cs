using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.Core;

public class CaptureProgressTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly ScopeSettings At200ms = new() { TimeBase = TimeBase.All.Single(t => t.Name == "200 ms") };

    private static DateTimeOffset Seconds(double seconds) => Epoch + TimeSpan.FromSeconds(seconds);

    private static readonly byte[] FirstPacket = Captures.Packet("06 00 80");
    private static readonly byte[] NextPacket = Captures.Packet("00 80");

    /// <summary>Replays a capture; returns the status just before each frame's first packet while running.</summary>
    private static List<(TimeSpan Time, CaptureStatus Status)> BeforeEachFrame(string capture)
    {
        var progress = new CaptureProgress();
        var settings = new ScopeSettings();
        var result = new List<(TimeSpan, CaptureStatus)>();
        var running = false;
        foreach (var packet in Captures.Load(capture))
        {
            var time = Epoch + packet.Time;
            switch (packet.Channel)
            {
                case CaptureChannel.State:
                    settings = Captures.SettingsFrom(packet.Bytes);
                    progress.Written(settings, time);
                    break;
                case CaptureChannel.In when packet.Bytes[0] == 'R':
                    progress.Started(settings, time);
                    running = true;
                    break;
                case CaptureChannel.In when packet.Bytes[0] == 'S':
                    progress.Stopped();
                    running = false;
                    break;
                case CaptureChannel.Data:
                    if (packet.Bytes[0] != 0 && running)
                    {
                        result.Add((packet.Time, progress.At(time)));
                    }

                    progress.PacketArrived(packet.Bytes, time);
                    break;
            }
        }

        return result;
    }

    [Theory]
    [InlineData(16, 1)]
    [InlineData(512, 27)]
    [InlineData(1024, 54)]
    [InlineData(2048, 108)]
    [InlineData(4096, 216)]
    public void Packets_per_frame_match_the_captures(int length, int packets)
    {
        Assert.Equal(packets, CaptureProgress.PacketCount(length));
    }

    [Theory]
    [InlineData(Captures.WriteDepth512)] // 512 record at 0.5 µs, 20 and 200 ms/div
    [InlineData(Captures.SettingsWhileRunning20ms)] // 4096 record, writes restarting the capture
    [InlineData(Captures.SettingsWhileRunning200ms)]
    [InlineData(Captures.NormalTrigger)] // with and without auto trigger
    public void Frames_arrive_within_a_few_tenths_of_a_second_of_the_capture_time(string capture)
    {
        var statuses = BeforeEachFrame(capture);

        Assert.NotEmpty(statuses);
        Assert.All(statuses, s =>
        {
            Assert.Equal(CapturePhase.Capturing, s.Status.Phase);
            Assert.InRange((s.Status.Elapsed - s.Status.Expected).TotalSeconds, -0.1, CaptureProgress.Latency.TotalSeconds);
        });
    }

    [Fact]
    public void Auto_trigger_frames_arrive_while_capturing()
    {
        // Capture 25: the level above the signal; the auto trigger fires soon after the capture.
        var statuses = BeforeEachFrame(Captures.TriggerWait);

        Assert.Equal(9, statuses.Count);
        Assert.All(statuses, s => Assert.Equal(CapturePhase.Capturing, s.Status.Phase));
        Assert.All(statuses.Take(7), s => Assert.InRange((s.Status.Elapsed - s.Status.Expected).TotalSeconds, 0, CaptureProgress.Latency.TotalSeconds));
    }

    [Fact]
    public void Without_auto_trigger_and_an_edge_the_scope_waits()
    {
        // Capture 25, 10.52 s: R without auto trigger, the level above the signal; nothing until 15.85 s.
        var progress = new CaptureProgress();
        progress.Started(new ScopeSettings { AutoTrigger = false, TimeBase = TimeBase.All.Single(t => t.Name == "20 ms") }, Seconds(0));

        Assert.Equal(CapturePhase.Capturing, progress.At(Seconds(0.5)).Phase);
        var waiting = progress.At(Seconds(5));
        Assert.Equal(CapturePhase.WaitingForTrigger, waiting.Phase);
        Assert.Equal(5 - 0.2048, waiting.Elapsed.TotalSeconds, precision: 9);
    }

    [Fact]
    public void Capturing_runs_from_R_for_the_frame_size_times_the_sample_interval()
    {
        var progress = new CaptureProgress();
        progress.Started(At200ms with { FrameSize = 4096 }, Seconds(0));

        var status = progress.At(Seconds(4.096));

        Assert.Equal(CapturePhase.Capturing, status.Phase);
        Assert.Equal(16.384, status.Expected.TotalSeconds, precision: 9);
        Assert.Equal(4.096, status.Position.TotalSeconds, precision: 9);
        Assert.Equal(4.096 / (16.384 + 216 * 0.007), status.Fraction, precision: 9);
    }

    [Fact]
    public void One_bar_runs_through_the_capture_then_the_packets()
    {
        var progress = new CaptureProgress();
        progress.Started(At200ms, Seconds(0));

        var transfer = 27 * 0.007;
        var late = progress.At(Seconds(2.09)); // past the capture time: the bar waits at its end
        Assert.Equal(CapturePhase.Capturing, late.Phase);
        Assert.Equal(2.048 / (2.048 + transfer), late.Fraction, precision: 9);

        progress.PacketArrived(FirstPacket, Seconds(2.1));
        progress.PacketArrived(NextPacket, Seconds(2.11));
        var receiving = progress.At(Seconds(2.12));
        Assert.Equal(CapturePhase.Receiving, receiving.Phase);
        Assert.Equal((2, 27), (receiving.Packets, receiving.ExpectedPackets));
        Assert.Equal((2.048 + transfer * 2 / 27) / (2.048 + transfer), receiving.Fraction, precision: 9);

        for (var i = 2; i < 27; i++)
        {
            progress.PacketArrived(NextPacket, Seconds(2.3));
        }

        var next = progress.At(Seconds(2.4));
        Assert.Equal(CapturePhase.Capturing, next.Phase);
        Assert.Equal(0.1, next.Elapsed.TotalSeconds, precision: 9);
    }

    [Fact]
    public void A_settings_write_restarts_the_capture_but_not_a_frame_arriving()
    {
        var progress = new CaptureProgress();
        progress.Started(At200ms, Seconds(0));

        progress.Written(At200ms, Seconds(1));
        Assert.Equal(0.5, progress.At(Seconds(1.5)).Elapsed.TotalSeconds, precision: 9);

        progress.PacketArrived(FirstPacket, Seconds(3));
        progress.Written(At200ms, Seconds(3.1));
        Assert.Equal(CapturePhase.Receiving, progress.At(Seconds(3.2)).Phase);
    }

    [Fact]
    public void Nothing_to_show_when_stopped_or_rolling()
    {
        var progress = new CaptureProgress();
        Assert.Equal(CaptureStatus.Idle, progress.At(Seconds(1)));

        progress.Started(new ScopeSettings { TimeBase = TimeBase.All.First(t => t.IsRoll) }, Seconds(0));
        Assert.Equal(CaptureStatus.Idle, progress.At(Seconds(1)));
        Assert.Equal(TimeSpan.Zero, progress.ExpectedCycle);

        progress.Started(At200ms, Seconds(0));
        progress.Stopped();
        Assert.Equal(CaptureStatus.Idle, progress.At(Seconds(1)));
    }

    [Theory]
    [InlineData("500 ns", 512, 0.1, 0.3)] // several frames a second
    [InlineData("500 ns", 4096, 1.4, 1.6)] // the transfer alone takes 1.8 s
    [InlineData("200 ms", 512, 2.1, 2.3)]
    public void Expected_cycle_is_capture_plus_transfer(string timeBase, int frameSize, double from, double to)
    {
        var progress = new CaptureProgress();
        progress.Started(new ScopeSettings { TimeBase = TimeBase.All.Single(t => t.Name == timeBase), FrameSize = frameSize }, Seconds(0));

        Assert.InRange(progress.ExpectedCycle.TotalSeconds, from, to);
    }
}
