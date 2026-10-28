namespace Aeroscope.Core.Protocol;

/// <summary>
/// What the screen shows: one or more traces (separate where acquisition was interrupted),
/// with times relative to the screen centre, and statistics over the samples shown.
/// </summary>
public sealed record ScreenTrace(IReadOnlyList<Waveform> Traces, double Minimum, double Maximum, double Mean)
{
    public double PeakToPeak => Maximum - Minimum;

    public static ScreenTrace From(Waveform waveform) => new([waveform], waveform.Minimum, waveform.Maximum, waveform.Mean);

    /// <summary>The samples of a waveform from <paramref name="from"/> to <paramref name="to"/> seconds.</summary>
    public static ScreenTrace Window(Waveform waveform, double from, double to)
    {
        var length = waveform.Volts.Length;
        var first = Math.Clamp((int)Math.Ceiling((from - waveform.FirstSampleTime) / waveform.SampleInterval - 1e-9), 0, length - 1);
        var end = Math.Clamp((int)Math.Floor((to - waveform.FirstSampleTime) / waveform.SampleInterval + 1e-9) + 1, first + 1, length);
        return From(new Waveform(waveform.Volts[first..end], waveform.SampleInterval, waveform.TimeOf(first)));
    }
}
