namespace Aeroscope.Core.Protocol;

/// <summary>
/// Roll mode display: the scope streams 16-sample frames back to back, and the screen shows
/// the latest 500 samples, filling from the left edge and then scrolling. Samples are kept
/// in volts, so changing the range or offset leaves the trace already on screen correct.
/// </summary>
public sealed class RollBuffer
{
    public const int FrameLength = 16;

    private readonly List<double> _volts = [];
    private double _sampleInterval;

    public int Count => _volts.Count;

    public bool IsFull => _volts.Count == TimeBase.SamplesOnScreen;

    public void Clear() => _volts.Clear();

    public void Add(ScopeFrame frame, ScopeSettings settings)
    {
        _sampleInterval = settings.TimeBase.SampleInterval;
        _volts.AddRange(frame.Samples.Select(code => settings.ToVolts(code)));
        var excess = _volts.Count - TimeBase.SamplesOnScreen;
        if (excess > 0)
        {
            _volts.RemoveRange(0, excess);
        }
    }

    /// <summary>The samples so far, the oldest at the left edge of the screen.</summary>
    public Waveform ToWaveform()
        => new([.. _volts], _sampleInterval, -TimeBase.Divisions / 2.0 * TimeBase.SamplesPerDivision * _sampleInterval);
}
