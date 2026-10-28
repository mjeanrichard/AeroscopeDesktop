using System.IO;
using Aeroscope.Core.Bluetooth;
using Aeroscope.Core.Diagnostics;
using Aeroscope.Probe;

namespace Aeroscope.Tests.Probe;

[Collection(ConsoleCollection.Name)]
public sealed class SessionRecorderTests : IDisposable
{
    private readonly string _capturePath = Path.GetTempFileName();
    private readonly TextWriter _originalOut = Console.Out;

    public SessionRecorderTests() => Console.SetOut(TextWriter.Null);

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        File.Delete(_capturePath);
    }

    [Fact]
    public void Replaying_a_real_capture_counts_packets_and_frames_and_rewrites_it()
    {
        var original = Captures.Load(Captures.FramesDefault);

        using (var recorder = new SessionRecorder(_capturePath, printRawData: false))
        {
            foreach (var packet in original)
            {
                recorder.Record(packet.Channel, packet.Bytes);
            }

            Assert.Equal(2, recorder.OutPacketCount);
            Assert.Equal(5, recorder.FrameCount);
        }

        var rewritten = CaptureFile.Read(_capturePath);
        Assert.Equal(original.Select(p => (p.Channel, Convert.ToHexString(p.Bytes))),
            rewritten.Select(p => (p.Channel, Convert.ToHexString(p.Bytes))));
    }

    [Fact]
    public void Writes_are_recorded_on_the_right_channel_and_comments_are_kept()
    {
        using (var recorder = new SessionRecorder(_capturePath, printRawData: true))
        {
            recorder.Comment("step QTI");
            recorder.RecordWrite(AeroscopeGatt.ScopeIn, Captures.Packet("51 54 49"));
            recorder.RecordWrite(AeroscopeGatt.ScopeState, Captures.Packet("03 80"));
        }

        var lines = File.ReadAllLines(_capturePath);
        Assert.Equal("# step QTI", lines[0]);
        Assert.Equal([CaptureChannel.In, CaptureChannel.State], CaptureFile.Read(_capturePath).Select(p => p.Channel));
    }

    [Fact]
    public void Works_without_a_capture_file()
    {
        using var recorder = new SessionRecorder(capturePath: null, printRawData: false);

        recorder.Comment("hello");
        recorder.Record(CaptureChannel.Out, Captures.Packet("50 46"));

        Assert.Equal(1, recorder.OutPacketCount);
    }

    [Fact]
    public async Task Until_sees_a_reply_that_arrived_before_the_step_started()
    {
        // Regression: on the real scope "PF" came back 1 ms after QP, before the until step ran.
        using var recorder = new SessionRecorder(null, false);
        recorder.Record(CaptureChannel.Out, Captures.Packet("50 46")); // unsolicited PF on connect
        recorder.MarkWrite();
        recorder.RecordWrite(AeroscopeGatt.ScopeIn, Captures.Packet("51 50"));
        recorder.Record(CaptureChannel.Out, Captures.Packet("50 46"));

        var step = Assert.IsType<UntilStep>(SessionStep.Parse("until:PF:1"));
        await step.ExecuteAsync(null!, recorder);

        Assert.Equal(2, recorder.OutPacketsSince(0).Count);
        Assert.Single(recorder.OutPacketsSince(recorder.OutPacketCountAtLastWrite));
    }

    [Fact]
    public async Task Until_ignores_replies_from_before_the_last_write()
    {
        using var recorder = new SessionRecorder(null, false);
        recorder.Record(CaptureChannel.Out, Captures.Packet("50 46"));
        recorder.MarkWrite();

        var step = Assert.IsType<UntilStep>(SessionStep.Parse("until:PF:0.2"));

        await Assert.ThrowsAsync<TimeoutException>(() => step.ExecuteAsync(null!, recorder));
    }

    [Fact]
    public async Task Frames_counts_frames_since_the_last_write()
    {
        using var recorder = new SessionRecorder(null, false);
        recorder.MarkWrite();
        foreach (var packet in Captures.Packets(Captures.RawSingle, CaptureChannel.Data))
        {
            recorder.Record(CaptureChannel.Data, packet);
        }

        await SessionStep.Parse("frames:2:1").ExecuteAsync(null!, recorder);
        await Assert.ThrowsAsync<TimeoutException>(() => SessionStep.Parse("frames:3:0.2").ExecuteAsync(null!, recorder));
        Assert.Equal(2, recorder.FrameCount);
    }

    [Fact]
    public async Task Wait_for_times_out_with_the_failure_message()
    {
        using var recorder = new SessionRecorder(null, false);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => recorder.WaitForAsync(() => false, TimeSpan.FromMilliseconds(100), "Nothing"));

        Assert.StartsWith("Nothing within 0.1 s", ex.Message);
    }
}
