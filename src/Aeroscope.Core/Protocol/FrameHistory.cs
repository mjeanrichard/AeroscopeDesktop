namespace Aeroscope.Core.Protocol;

/// <summary>A triggered frame as received, with the settings it was taken with.</summary>
public sealed record RecordedFrame(ScopeFrame Frame, ScopeSettings Settings, DateTimeOffset Received)
{
    public Waveform ToWaveform() => Waveform.From(Frame, Settings);
}

/// <summary>
/// Every triggered frame received while running, oldest first. When the samples exceed the
/// limit the oldest tenth is dropped (64 MB is about 9 hours of 512-sample frames).
/// </summary>
public sealed class FrameHistory(long maxBytes = FrameHistory.DefaultMaxBytes)
{
    public const long DefaultMaxBytes = 64L << 20;

    private readonly List<RecordedFrame> _frames = [];

    public int Count => _frames.Count;

    public long Bytes { get; private set; }

    public RecordedFrame this[int index] => _frames[index];

    /// <returns>How many of the oldest frames were dropped to stay under the limit.</returns>
    public int Add(RecordedFrame frame)
    {
        _frames.Add(frame);
        Bytes += frame.Frame.Samples.Length;
        if (Bytes <= maxBytes)
        {
            return 0;
        }

        var dropped = 0;
        while (Bytes > maxBytes * 9 / 10 && dropped < _frames.Count - 1)
        {
            Bytes -= _frames[dropped++].Frame.Samples.Length;
        }

        _frames.RemoveRange(0, dropped);
        return dropped;
    }

    public void Clear()
    {
        _frames.Clear();
        Bytes = 0;
    }
}
