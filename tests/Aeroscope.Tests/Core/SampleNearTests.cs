using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.Core;

/// <summary>What the screen cursor reads: the measured sample nearest a time.</summary>
public class SampleNearTests
{
    private static readonly Waveform Ramp = new([0, 1, 2, 3], SampleInterval: 1, FirstSampleTime: -2);

    [Theory]
    [InlineData(-2, -2, 0)]
    [InlineData(0.4, 0, 2)]
    [InlineData(0.6, 1, 3)] // the nearer sample, not an interpolation
    [InlineData(-2.4, -2, 0)] // within half an interval of an end
    [InlineData(1.4, 1, 3)]
    public void Snaps_to_the_nearest_sample(double seconds, double time, double volts)
    {
        Assert.Equal((time, volts), Waveform.SampleNear([Ramp], seconds));
    }

    [Theory]
    [InlineData(-2.6)]
    [InlineData(1.6)]
    public void Nothing_beyond_the_ends(double seconds)
    {
        Assert.Null(Waveform.SampleNear([Ramp], seconds));
    }

    [Fact]
    public void Separate_traces_with_a_gap_between()
    {
        // Roll mode: acquisition stopped and restarted, so two traces.
        Waveform[] traces = [new([5, 6], 1, 0), new([7, 8], 1, 10)];

        Assert.Equal((1.0, 6.0), Waveform.SampleNear(traces, 1.2));
        Assert.Null(Waveform.SampleNear(traces, 5));
        Assert.Equal((10.0, 7.0), Waveform.SampleNear(traces, 9.8));
        Assert.Null(Waveform.SampleNear([], 0));
    }
}
