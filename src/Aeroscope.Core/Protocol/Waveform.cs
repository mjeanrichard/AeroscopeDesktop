namespace Aeroscope.Core.Protocol;

/// <summary>A frame converted to volts, with time 0 at the trigger (the screen centre).</summary>
/// <param name="FirstSampleTime">Time of sample 0 relative to the trigger, in seconds.</param>
public sealed record Waveform(double[] Volts, double SampleInterval, double FirstSampleTime)
{
    public static Waveform From(ScopeFrame frame, ScopeSettings settings)
    {
        var dt = settings.TimeBase.SampleInterval;

        // The subtrigger shifts the trace right by subtrigger/64 of a sample to remove trigger jitter.
        var firstSampleTime = (frame.SubTrigger / 64.0 - ScopeSettings.TriggerIndex(frame.Samples.Length)) * dt;
        var volts = frame.Samples.Select(code => settings.ToVolts(code)).ToArray();
        return new Waveform(volts, dt, firstSampleTime);
    }

    public double TimeOf(int index) => FirstSampleTime + index * SampleInterval;

    /// <summary>
    /// The measured sample nearest a time, in whichever of the traces covers it; null in a gap
    /// between traces or more than half a sample interval beyond their ends.
    /// </summary>
    public static (double Time, double Volts)? SampleNear(IEnumerable<Waveform> traces, double seconds)
    {
        foreach (var trace in traces)
        {
            var index = (int)Math.Round((seconds - trace.FirstSampleTime) / trace.SampleInterval, MidpointRounding.AwayFromZero);
            if (index >= 0 && index < trace.Volts.Length)
            {
                return (trace.TimeOf(index), trace.Volts[index]);
            }
        }

        return null;
    }

    public double Minimum => Volts.Min();

    public double Maximum => Volts.Max();

    public double PeakToPeak => Maximum - Minimum;

    public double Mean => Volts.Average();
}
