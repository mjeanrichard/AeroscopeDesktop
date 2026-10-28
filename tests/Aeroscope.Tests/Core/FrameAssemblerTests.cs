using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.Core;

public class FrameAssemblerTests
{
    private readonly FrameAssembler _assembler = new();
    private readonly List<ScopeFrame> _frames = [];
    private readonly List<string> _drops = [];

    public FrameAssemblerTests()
    {
        _assembler.FrameCompleted += (_, frame) => _frames.Add(frame);
        _assembler.FrameDropped += (_, reason) => _drops.Add(reason);
    }

    private void Replay(string capture)
    {
        foreach (var packet in Captures.Packets(capture, CaptureChannel.Data))
        {
            _assembler.AddPacket(packet);
        }
    }

    [Fact]
    public void Run_mode_capture_yields_five_512_sample_frames()
    {
        Replay(Captures.FramesDefault);

        Assert.Equal(5, _frames.Count);
        Assert.All(_frames, frame =>
        {
            Assert.Equal(512, frame.Samples.Length);
            Assert.Equal(0, frame.SubTrigger);
            Assert.All(frame.Samples, s => Assert.InRange(s, (byte)138, (byte)139)); // unconnected input
        });

        // The sixth frame was cut off by Cancel after one packet; nothing is reported until a new frame starts.
        Assert.Empty(_drops);
    }

    [Fact]
    public void A_512_sample_frame_is_exactly_27_packets()
    {
        var packets = Captures.Packets(Captures.RawSingle, CaptureChannel.Data).ToList();
        Replay(Captures.RawSingle);

        Assert.Equal(54, packets.Count);
        Assert.Equal(2, _frames.Count);
    }

    [Fact]
    public void Subtrigger_comes_from_the_second_byte_of_the_start_packet()
    {
        Replay(Captures.RawSingle);

        Assert.All(_frames, frame => Assert.Equal(58, frame.SubTrigger));
    }

    [Fact]
    public void Frame_cancelled_mid_transfer_is_reported_when_the_next_frame_starts()
    {
        Replay(Captures.SingleAndFull); // 2 frames, then 9 packets of a third before Cancel
        Assert.Equal(2, _frames.Count);
        Assert.Empty(_drops);

        _assembler.AddPacket(Captures.Packet("06 00 8B"));

        Assert.Equal(["New frame started after 170 of 512 samples"], _drops);
    }

    [Fact]
    public void Mains_hum_swing_halves_when_volts_per_division_double()
    {
        Replay(Captures.TouchHum);

        Assert.Equal(7, _frames.Count);
        var swingAt1V = _frames.Take(4).Average(f => f.Samples.Max() - f.Samples.Min());
        var swingAt2V = _frames.Skip(4).Average(f => f.Samples.Max() - f.Samples.Min());
        Assert.InRange(swingAt1V, 70, 80);
        Assert.InRange(swingAt1V / swingAt2V, 1.9, 2.1);
    }

    [Fact]
    public void Full_frame_download_is_the_same_acquisition_as_the_single_frame()
    {
        Replay(Captures.SingleThenFull);

        Assert.Equal([512, 512, 512, 4096], _frames.Select(f => f.Samples.Length));
        var single = _frames[2];
        var full = _frames[3];
        Assert.Equal(single.SubTrigger, full.SubTrigger);

        // The 512-sample read window starts at the read-start register (0x700) of the 4096-sample memory.
        Assert.Equal(single.Samples, full.Samples[0x700..(0x700 + 512)]);
    }

    [Fact]
    public void Run_mode_with_read_depth_9_streams_4096_sample_frames()
    {
        Replay(Captures.RunFullDepth);

        Assert.Equal([4096, 4096], _frames.Select(f => f.Samples.Length));
        Assert.Equal(451, Captures.Packets(Captures.RunFullDepth, CaptureChannel.Data).Count()); // 2 x 216 + 19 of a cancelled third
    }

    [Fact]
    public void Samples_are_taken_in_order_from_both_packet_types()
    {
        var start = new byte[20];
        start[0] = 0x06;
        start[1] = 7;
        for (var i = 2; i < 20; i++)
        {
            start[i] = (byte)(i - 2);
        }

        _assembler.AddPacket(start);
        for (var p = 0; p < 26; p++)
        {
            var continuation = new byte[20];
            for (var i = 1; i < 20; i++)
            {
                continuation[i] = (byte)(18 + p * 19 + i - 1);
            }

            _assembler.AddPacket(continuation);
        }

        var frame = Assert.Single(_frames);
        Assert.Equal(7, frame.SubTrigger);
        Assert.Equal(Enumerable.Range(0, 512).Select(i => (byte)i), frame.Samples);
    }

    [Fact]
    public void Sixteen_sample_frame_fits_in_one_packet_and_ignores_padding()
    {
        var packet = new byte[20];
        packet[0] = 0x01;
        for (var i = 2; i < 20; i++)
        {
            packet[i] = 100;
        }

        _assembler.AddPacket(packet);

        var frame = Assert.Single(_frames);
        Assert.Equal(16, frame.Samples.Length);
    }

    [Fact]
    public void Continuation_without_start_is_dropped()
    {
        _assembler.AddPacket(Captures.Packet("00 8B 8B"));

        Assert.Empty(_frames);
        Assert.Equal(["Continuation packet without a start-of-frame packet"], _drops);
    }

    [Fact]
    public void Unknown_size_code_is_dropped()
    {
        _assembler.AddPacket(Captures.Packet("0A 00 8B"));
        _assembler.AddPacket(Captures.Packet("00 8B"));

        Assert.Empty(_frames);
        Assert.Equal(["Unknown frame size code 0x0A", "Continuation packet without a start-of-frame packet"], _drops);
    }

    [Fact]
    public void Packets_shorter_than_two_bytes_are_ignored()
    {
        _assembler.AddPacket([0x06]);
        _assembler.AddPacket([]);

        Assert.Empty(_frames);
        Assert.Empty(_drops);
    }

    [Fact]
    public void Reset_discards_a_partial_frame()
    {
        _assembler.AddPacket(Captures.Packet("06 00 8B"));
        _assembler.Reset();
        _assembler.AddPacket(Captures.Packet("06 00 8B"));

        Assert.Empty(_drops);
    }

    [Theory]
    [InlineData(0x01, 16)]
    [InlineData(0x05, 256)]
    [InlineData(0x06, 512)]
    [InlineData(0x09, 4096)]
    [InlineData(0x00, 0)]
    [InlineData(0x0A, 0)]
    public void Frame_length_from_size_code(byte code, int length)
    {
        Assert.Equal(length, FrameAssembler.FrameLength(code));
    }

    [Fact]
    public void Frame_description()
    {
        var frame = new ScopeFrame([10, 20, 30], SubTrigger: 5);

        Assert.Equal("3 samples, subtrigger 5, min 10, max 30, mean 20.0", frame.ToString());
    }
}
