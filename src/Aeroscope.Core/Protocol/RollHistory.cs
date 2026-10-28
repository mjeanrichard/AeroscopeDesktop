namespace Aeroscope.Core.Protocol;

/// <summary>Roll-mode samples taken back to back with the same settings, kept as raw ADC codes.</summary>
public sealed class RollSegment
{
    private readonly List<byte> _codes = [];
    private readonly double[] _voltsByCode;

    internal RollSegment(DateTimeOffset start, ScopeSettings settings)
    {
        Start = start;
        Settings = settings;
        _voltsByCode = Enumerable.Range(0, 256).Select(code => settings.ToVolts(code)).ToArray();
    }

    /// <summary>Time of the first sample.</summary>
    public DateTimeOffset Start { get; private set; }

    public ScopeSettings Settings { get; }

    public double SampleInterval => Settings.TimeBase.SampleInterval;

    public int Count => _codes.Count;

    public DateTimeOffset End => Start + TimeSpan.FromSeconds(Count * SampleInterval);

    public double VoltsAt(int index) => _voltsByCode[_codes[index]];

    internal void Add(byte[] codes) => _codes.AddRange(codes);

    internal void RemoveFirst(int count)
    {
        _codes.RemoveRange(0, count);
        Start += TimeSpan.FromSeconds(count * SampleInterval);
    }
}

/// <summary>
/// Everything recorded in roll mode, as segments: a new one starts after Run or a reconnect
/// (<see cref="StartSegment"/>), and when the settings change. The scope streams 16-sample frames back to back, so sample times follow from
/// the segment start (the first frame's arrival minus its duration) and the sample count.
/// The oldest samples are dropped beyond the limit (24 hours at 100 samples/s, about 8.6 MB).
/// </summary>
public sealed class RollHistory(int maxSamples = RollHistory.DefaultMaxSamples)
{
    public const int FrameLength = 16;

    public const int DefaultMaxSamples = 24 * 3600 * 100;

    /// <summary>
    /// After a stop, R first flushes 2-3 frames from before it, all at once. Frames that close
    /// together at the start of a segment are dropped; the segment starts with the next one.
    /// </summary>
    public static readonly TimeSpan BurstGap = TimeSpan.FromMilliseconds(30);

    /// <summary>Samples drawn per screen width before long windows are reduced to min/max pairs.</summary>
    public const int ScreenColumns = 1000;

    private readonly List<RollSegment> _segments = [];
    private int _sampleCount;
    private bool _startNew = true;
    private bool _firstFrameOnly;
    private bool _inBurst;
    private DateTimeOffset _lastArrival;

    public IReadOnlyList<RollSegment> Segments => _segments;

    public int SampleCount => _sampleCount;

    public DateTimeOffset? Start => _segments.Count > 0 ? _segments[0].Start : null;

    public DateTimeOffset? End => _segments.Count > 0 ? _segments[^1].End : null;

    /// <summary>Samples in the segment being recorded; 0 until its first frame.</summary>
    public int CurrentSegmentCount => _startNew || _inBurst || _segments.Count == 0 ? 0 : _segments[^1].Count;

    /// <summary>The next frame starts a new segment.</summary>
    public void StartSegment() => _startNew = true;

    public void Clear()
    {
        _segments.Clear();
        _sampleCount = 0;
        _startNew = true;
        _inBurst = false;
    }

    public void Add(ScopeFrame frame, ScopeSettings settings, DateTimeOffset received)
    {
        var sinceLast = received - _lastArrival;
        _lastArrival = received;
        if (_inBurst)
        {
            if (sinceLast < BurstGap)
            {
                return;
            }

            _inBurst = false;
            _startNew = true;
        }
        else if (_firstFrameOnly && sinceLast < BurstGap)
        {
            RemoveLastSegment();
            _firstFrameOnly = false;
            _inBurst = true;
            return;
        }

        if (_startNew || _segments.Count == 0 || _segments[^1].Settings != settings)
        {
            var duration = TimeSpan.FromSeconds(frame.Samples.Length * settings.TimeBase.SampleInterval);
            _segments.Add(new RollSegment(received - duration, settings));
            _startNew = false;
            _firstFrameOnly = true;
        }
        else
        {
            _firstFrameOnly = false;
        }

        _segments[^1].Add(frame.Samples);
        _sampleCount += frame.Samples.Length;
        Trim();
    }

    /// <summary>
    /// The samples from <paramref name="start"/> to start + span, with times relative to the
    /// window centre. Where a column of the screen holds more than two samples, they are
    /// reduced to their minimum and maximum so drawing stays fast and spikes stay visible.
    /// Null if the window holds no samples.
    /// </summary>
    public ScreenTrace? Window(DateTimeOffset start, TimeSpan span)
    {
        var end = start + span;
        var centre = start + span / 2;
        var column = span.TotalSeconds / ScreenColumns;
        var traces = new List<Waveform>();
        var minimum = double.MaxValue;
        var maximum = double.MinValue;
        var sum = 0.0;
        var count = 0;

        foreach (var segment in _segments)
        {
            if (segment.End <= start || segment.Start >= end)
            {
                continue;
            }

            var dt = segment.SampleInterval;
            // Samples at or after the start and before the end; the tolerance absorbs rounding in the division.
            var first = Math.Max(0, (int)Math.Ceiling((start - segment.Start).TotalSeconds / dt - 1e-6));
            var last = Math.Min(segment.Count, (int)Math.Ceiling((end - segment.Start).TotalSeconds / dt - 1e-6));
            if (first >= last)
            {
                continue;
            }

            for (var i = first; i < last; i++)
            {
                var v = segment.VoltsAt(i);
                minimum = Math.Min(minimum, v);
                maximum = Math.Max(maximum, v);
                sum += v;
            }

            count += last - first;
            var offset = (segment.Start - centre).TotalSeconds;
            traces.Add(2 * dt < column
                ? MinMaxPairs(segment, first, last, offset, column, -span.TotalSeconds / 2)
                : new Waveform([.. Enumerable.Range(first, last - first).Select(segment.VoltsAt)], dt, offset + first * dt));
        }

        return count == 0 ? null : new ScreenTrace(traces, minimum, maximum, sum / count);
    }

    /// <summary>Minimum then maximum of each screen column, half a column apart.</summary>
    private static Waveform MinMaxPairs(RollSegment segment, int first, int last, double offset, double column, double left)
    {
        var dt = segment.SampleInterval;
        var columnOf = (int i) => (int)Math.Floor((offset + i * dt - left) / column);
        var firstColumn = columnOf(first);
        var pairs = new List<double>();
        var current = firstColumn;
        double low = double.MaxValue, high = double.MinValue;
        for (var i = first; i < last; i++)
        {
            var c = columnOf(i);
            if (c != current)
            {
                pairs.Add(low);
                pairs.Add(high);
                low = double.MaxValue;
                high = double.MinValue;
                current = c;
            }

            var v = segment.VoltsAt(i);
            low = Math.Min(low, v);
            high = Math.Max(high, v);
        }

        pairs.Add(low);
        pairs.Add(high);
        return new Waveform([.. pairs], column / 2, left + firstColumn * column);
    }

    private void RemoveLastSegment()
    {
        _sampleCount -= _segments[^1].Count;
        _segments.RemoveAt(_segments.Count - 1);
    }

    private void Trim()
    {
        if (_sampleCount <= maxSamples)
        {
            return;
        }

        // Drop a twenty-fourth (an hour at the default limit) at a time, so trimming is rare.
        var excess = _sampleCount - maxSamples + maxSamples / 24;
        while (excess > 0)
        {
            var oldest = _segments[0];
            if (oldest.Count <= excess && _segments.Count > 1)
            {
                _segments.RemoveAt(0);
                _sampleCount -= oldest.Count;
                excess -= oldest.Count;
            }
            else
            {
                var remove = Math.Min(excess, oldest.Count - 1); // the segment being recorded keeps a sample
                oldest.RemoveFirst(remove);
                _sampleCount -= remove;
                break;
            }
        }
    }
}
