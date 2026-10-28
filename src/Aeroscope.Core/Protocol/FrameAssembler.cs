namespace Aeroscope.Core.Protocol;

/// <summary>One captured frame of 8-bit ADC samples.</summary>
/// <param name="SubTrigger">Fraction of a sample interval (value / 64) to shift the trace by.</param>
public sealed record ScopeFrame(byte[] Samples, byte SubTrigger)
{
    public override string ToString()
        => $"{Samples.Length} samples, subtrigger {SubTrigger}, min {Samples.Min()}, max {Samples.Max()}, mean {Samples.Average(s => (double)s):0.0}";
}

/// <summary>
/// Rebuilds frames from Scope Data packets. A start-of-frame packet carries a size
/// code, the subtrigger and 18 samples; continuation packets start with 0x00 and
/// carry 19 samples. The last packet may be zero padded.
/// </summary>
public sealed class FrameAssembler
{
    private byte[]? _samples;
    private int _count;
    private byte _subTrigger;

    public event EventHandler<ScopeFrame>? FrameCompleted;

    /// <summary>Raised with a description when packets are lost or arrive out of order.</summary>
    public event EventHandler<string>? FrameDropped;

    /// <summary>Size codes: 0x01 = 16, 0x06 = 512, 0x09 = 4096 samples (2^(code + 3)).</summary>
    public static int FrameLength(byte sizeCode) => sizeCode is >= 1 and <= 9 ? 1 << (sizeCode + 3) : 0;

    public void Reset()
    {
        _samples = null;
        _count = 0;
    }

    public void AddPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 2)
        {
            return;
        }

        if (packet[0] != 0)
        {
            if (_samples is not null)
            {
                FrameDropped?.Invoke(this, $"New frame started after {_count} of {_samples.Length} samples");
            }

            var length = FrameLength(packet[0]);
            if (length == 0)
            {
                FrameDropped?.Invoke(this, $"Unknown frame size code 0x{packet[0]:X2}");
                Reset();
                return;
            }

            _samples = new byte[length];
            _count = 0;
            _subTrigger = packet[1];
            Append(packet[2..]);
        }
        else if (_samples is not null)
        {
            Append(packet[1..]);
        }
        else
        {
            FrameDropped?.Invoke(this, "Continuation packet without a start-of-frame packet");
        }
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        var take = Math.Min(data.Length, _samples!.Length - _count);
        data[..take].CopyTo(_samples.AsSpan(_count));
        _count += take;

        if (_count == _samples.Length)
        {
            var frame = new ScopeFrame(_samples, _subTrigger);
            Reset();
            FrameCompleted?.Invoke(this, frame);
        }
    }
}
