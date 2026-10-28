namespace Aeroscope.Core.Protocol;

/// <summary>
/// Which settings a frame was taken with, while settings are written during acquisition.
/// The scope captures a frame, then sends it on the next Bluetooth connection events (about
/// 37 ms apart). A write acknowledged after the previous frame had arrived, and less than an
/// event before this frame's first packet, came too late: the frame was already waiting to be
/// sent. So did a write acknowledged after the frame started arriving. Every other frame has
/// the newest settings (captures 16 and 20: all 24 frames that follow a write fit, with margins).
/// </summary>
public sealed class SettingsTimeline
{
    /// <summary>A frame arriving this soon after a write was already waiting (seen: old up to +16 ms, new from +37 ms).</summary>
    public static readonly TimeSpan QueuedFrame = TimeSpan.FromMilliseconds(25);

    /// <summary>A write acknowledged this long after the previous frame (seen: new up to 0 ms, old from 35 ms).</summary>
    public static readonly TimeSpan AfterPreviousFrame = TimeSpan.FromMilliseconds(10);

    private readonly List<(ScopeSettings Settings, DateTimeOffset Acknowledged)> _writes = [(new ScopeSettings(), DateTimeOffset.MinValue)];
    private DateTimeOffset _previousFrameEnd = DateTimeOffset.MinValue;

    /// <summary>The settings written last.</summary>
    public ScopeSettings Latest => _writes[^1].Settings;

    /// <summary>The scope acknowledged a settings write.</summary>
    public void Written(ScopeSettings settings, DateTimeOffset acknowledged) => _writes.Add((settings, acknowledged));

    /// <summary>R: a fresh acquisition, all taken with the settings written so far.</summary>
    public void Started(DateTimeOffset time)
    {
        _writes.RemoveRange(0, _writes.Count - 1);
        _writes[0] = (_writes[0].Settings, time);
        _previousFrameEnd = time;
    }

    /// <summary>The settings a frame was taken with.</summary>
    /// <param name="firstPacket">When the frame's first packet arrived.</param>
    /// <param name="lastPacket">When its last packet arrived.</param>
    public ScopeSettings FrameArrived(DateTimeOffset firstPacket, DateTimeOffset lastPacket)
    {
        var i = _writes.Count - 1;
        while (i > 0 && firstPacket - _writes[i].Acknowledged < QueuedFrame && _writes[i].Acknowledged - _previousFrameEnd > AfterPreviousFrame)
        {
            i--;
        }

        _writes.RemoveRange(0, i);
        _previousFrameEnd = lastPacket;
        return _writes[0].Settings;
    }
}
