namespace Aeroscope.Core.Protocol;

public enum CapturePhase { Idle, Capturing, WaitingForTrigger, Receiving }

/// <summary>Where the scope is in its frame cycle: a capture, then the transfer of the frame.</summary>
/// <param name="Elapsed">Capturing: time since the capture started; waiting: time past the expected capture.</param>
/// <param name="Expected">How long the capture takes.</param>
/// <param name="Transfer">Roughly how long sending the frame takes.</param>
public sealed record CaptureStatus(CapturePhase Phase, TimeSpan Elapsed, TimeSpan Expected, TimeSpan Transfer, int Packets, int ExpectedPackets)
{
    public static readonly CaptureStatus Idle = new(CapturePhase.Idle, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, 0);

    public TimeSpan Cycle => Expected + Transfer;

    /// <summary>How far through the cycle: the capture by time, then the transfer by packets.</summary>
    public TimeSpan Position => Phase switch
    {
        CapturePhase.Capturing => Elapsed < Expected ? Elapsed : Expected,
        CapturePhase.Receiving => Expected + Transfer * Packets / ExpectedPackets,
        _ => TimeSpan.Zero,
    };

    /// <summary>0-1 through the cycle while capturing or receiving.</summary>
    public double Fraction => Cycle > TimeSpan.Zero ? Position / Cycle : 0;
}

/// <summary>
/// Follows a triggered acquisition's frame cycle. The scope captures a record (frame size x
/// sample interval) from R, from the end of the previous frame, or from a settings write
/// (which restarts it), then sends it in packets of 19 samples. The first packet arrives
/// between 0.07 s before and 0.35 s after the capture time, with the trigger firing at once
/// or by auto trigger (captures 21-23, 25, 26). Without auto trigger and an edge the scope
/// waits for one indefinitely.
/// </summary>
public sealed class CaptureProgress
{
    /// <summary>A frame later than this after its capture time is waiting for a trigger.</summary>
    public static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(400);

    /// <summary>Rough time per packet, to tell when a cycle is long enough to show (27 packets: 0.1-0.3 s).</summary>
    private static readonly TimeSpan PacketTime = TimeSpan.FromMilliseconds(7);

    private ScopeSettings _settings = new();
    private bool _running;
    private DateTimeOffset _captureStart;
    private int _packets;
    private int _expectedPackets;

    /// <summary>Packets for a frame: the first carries 18 samples, the others 19.</summary>
    public static int PacketCount(int frameLength) => frameLength <= 18 ? 1 : 1 + (frameLength - 18 + 19 - 1) / 19;

    /// <summary>Capture plus transfer, roughly, for the latest settings; zero in roll mode.</summary>
    public TimeSpan ExpectedCycle => _settings.TimeBase.IsRoll
        ? TimeSpan.Zero
        : CaptureTime + TransferTime(_settings.FrameLength);

    private static TimeSpan TransferTime(int frameLength) => PacketTime * PacketCount(frameLength);

    private TimeSpan CaptureTime => TimeSpan.FromSeconds(_settings.FrameSize * _settings.TimeBase.SampleInterval);

    /// <summary>R: a capture starts with these settings.</summary>
    public void Started(ScopeSettings settings, DateTimeOffset time)
    {
        _settings = settings;
        _running = true;
        _captureStart = time;
        _packets = 0;
    }

    public void Stopped() => _running = false;

    /// <summary>A settings write was acknowledged: the capture in progress starts again (a frame already arriving finishes first).</summary>
    public void Written(ScopeSettings settings, DateTimeOffset acknowledged)
    {
        _settings = settings;
        if (_packets == 0)
        {
            _captureStart = acknowledged;
        }
    }

    public void PacketArrived(ReadOnlySpan<byte> packet, DateTimeOffset time)
    {
        if (packet.Length == 0)
        {
            return;
        }

        if (packet[0] != 0)
        {
            _expectedPackets = PacketCount(FrameAssembler.FrameLength(packet[0]));
            _packets = 0;
        }
        else if (_packets == 0)
        {
            return; // the rest of a frame whose start we missed
        }

        if (++_packets >= _expectedPackets)
        {
            _packets = 0;
            _captureStart = time; // the next capture starts once a frame is sent
        }
    }

    public CaptureStatus At(DateTimeOffset now)
    {
        if (!_running || _settings.TimeBase.IsRoll)
        {
            return CaptureStatus.Idle;
        }

        if (_packets > 0)
        {
            return new CaptureStatus(CapturePhase.Receiving, TimeSpan.Zero, CaptureTime, PacketTime * _expectedPackets, _packets, _expectedPackets);
        }

        var elapsed = now - _captureStart;
        var transfer = TransferTime(_settings.FrameLength);
        return elapsed <= CaptureTime + Latency || _settings.AutoTrigger
            ? new CaptureStatus(CapturePhase.Capturing, elapsed, CaptureTime, transfer, 0, 0)
            : new CaptureStatus(CapturePhase.WaitingForTrigger, elapsed - CaptureTime, CaptureTime, transfer, 0, 0);
    }
}
