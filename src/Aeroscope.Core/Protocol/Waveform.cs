namespace Aeroscope.Core.Protocol;

/// <summary>A frame converted to volts, with time 0 at the trigger (the screen centre).</summary>
/// <param name="FirstSampleTime">Time of sample 0 relative to the trigger, in seconds.</param>
public sealed record Waveform(double[] Volts, double SampleInterval, double FirstSampleTime)
{
    public static Waveform From(ScopeFrame frame, ScopeSettings settings)
    {
        var dt = settings.TimeBase.SampleInterval;

        // The subtrigger shifts the trace right by subtrigger/64 of a sample to remove trigger jitter.
        var firstSampleTime = (frame.SubTrigger / 64.0 - ScopeSettings.TriggerSampleIndex) * dt;
        var volts = frame.Samples.Select(code => settings.ToVolts(code)).ToArray();
        return new Waveform(volts, dt, firstSampleTime);
    }

    public double TimeOf(int index) => FirstSampleTime + index * SampleInterval;

    public double Minimum => Volts.Min();

    public double Maximum => Volts.Max();

    public double PeakToPeak => Maximum - Minimum;

    public double Mean => Volts.Average();
}
