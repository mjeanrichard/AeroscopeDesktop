using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.Core;

/// <summary>Capture 23: a 512-sample record gives the same frames as the full memory, sooner.</summary>
public class WriteDepthTests
{
    private sealed record Frame(ScopeFrame Data, byte[] Registers, TimeSpan Start, TimeSpan End);

    /// <summary>The complete frames of capture 23, each with the registers it ran with.</summary>
    private static List<Frame> Frames()
    {
        var assembler = new FrameAssembler();
        var frames = new List<Frame>();
        byte[] registers = [];
        TimeSpan start = default, now = default;
        assembler.FrameCompleted += (_, frame) => frames.Add(new Frame(frame, registers, start, now));

        foreach (var packet in Captures.Load(Captures.WriteDepth512))
        {
            now = packet.Time;
            switch (packet.Channel)
            {
                case CaptureChannel.State:
                    registers = packet.Bytes;
                    break;
                case CaptureChannel.In when packet.Bytes[0] == 'S':
                    assembler.Reset(); // the frame cut short by the stop
                    break;
                case CaptureChannel.Data:
                    if (packet.Bytes[0] != 0)
                    {
                        start = now;
                    }

                    assembler.AddPacket(packet.Bytes);
                    break;
            }
        }

        return frames;
    }

    [Fact]
    public void Trigger_is_at_sample_256_with_either_memory_depth()
    {
        var frames = Frames();

        Assert.Equal(17, frames.Count);
        Assert.Equal(4, frames.Count(f => f.Registers[9] == 9));
        Assert.All(frames, f =>
        {
            var samples = f.Data.Samples;
            var trigger = ScopeSettings.TriggerIndex(samples.Length);
            Assert.True(samples[trigger - 1] < 128 && samples[trigger] >= 128, $"rising through 128 at {trigger}");
        });
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void Larger_frames_have_the_trigger_in_the_middle(int size)
    {
        // Capture 24: the sine rises through code 128 at time 0, half way through every frame.
        var assembler = new FrameAssembler();
        var frames = new List<ScopeFrame>();
        assembler.FrameCompleted += (_, frame) => frames.Add(frame);
        foreach (var packet in Captures.Load(Captures.FrameSizes))
        {
            if (packet.Channel == CaptureChannel.In && packet.Bytes[0] == 'S')
            {
                assembler.Reset();
            }
            else if (packet.Channel == CaptureChannel.Data)
            {
                assembler.AddPacket(packet.Bytes);
            }
        }

        var sized = frames.Where(f => f.Samples.Length == size).ToList();
        Assert.Equal(3, sized.Count);
        Assert.All(sized, frame =>
        {
            var waveform = Waveform.From(frame, new ScopeSettings { FrameSize = size });
            var trigger = ScopeSettings.TriggerIndex(size);
            Assert.True(frame.Samples[trigger - 1] < 128 && frame.Samples[trigger] >= 128);
            Assert.Equal(0, waveform.TimeOf(trigger), tolerance: waveform.SampleInterval);
        });
    }

    [Theory]
    [InlineData(0x24)] // 20 ms/div: 205 ms
    [InlineData(0x25)] // 200 ms/div: 2.05 s
    public void A_frame_takes_512_samples_to_capture_after_the_previous_one_is_sent(byte sampler)
    {
        var frames = Frames().Where(f => f.Registers[4] == sampler).ToList();
        var capture = TimeSpan.FromSeconds(512 * TimeBase.All.Single(t => t.SamplerCode == sampler).SampleInterval);

        Assert.True(frames.Count >= 3);
        foreach (var (previous, next) in frames.Zip(frames.Skip(1)))
        {
            Assert.InRange(next.Start - previous.End, capture, capture + TimeSpan.FromMilliseconds(150));
        }
    }
}
