namespace Aeroscope.Core.Protocol;

/// <summary>
/// What the screen shows: one or more traces (separate where acquisition was interrupted),
/// with times relative to the screen centre, and statistics over the samples shown.
/// </summary>
public sealed record ScreenTrace(IReadOnlyList<Waveform> Traces, double Minimum, double Maximum, double Mean)
{
    public double PeakToPeak => Maximum - Minimum;

    public static ScreenTrace From(Waveform waveform) => new([waveform], waveform.Minimum, waveform.Maximum, waveform.Mean);
}
