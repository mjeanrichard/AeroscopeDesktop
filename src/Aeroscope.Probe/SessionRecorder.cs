using System.Diagnostics;
using Aeroscope.Core.Bluetooth;
using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Probe;

/// <summary>
/// Collects the traffic of a probe session: prints each packet decoded, writes the
/// capture file, assembles frames, and lets steps wait for what has arrived.
/// Notifications arrive on thread-pool threads, so everything goes through one lock.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TextWriter? _capture;
    private readonly bool _printRawData;
    private readonly FrameAssembler _frameAssembler = new();
    private readonly List<byte[]> _outPackets = [];
    private int _frameCount;
    private int _outPacketCountAtLastWrite;
    private int _frameCountAtLastWrite;

    public SessionRecorder(string? capturePath, bool printRawData)
    {
        _capture = capturePath is null ? null : new StreamWriter(capturePath);
        _printRawData = printRawData;
        _frameAssembler.FrameCompleted += (_, frame) =>
        {
            _frameCount++;
            Console.WriteLine($"        frame #{_frameCount}: {frame}");
        };
        _frameAssembler.FrameDropped += (_, reason) => Console.WriteLine($"        frame dropped: {reason}");
    }

    public int FrameCount
    {
        get { lock (_lock) { return _frameCount; } }
    }

    public int OutPacketCount
    {
        get { lock (_lock) { return _outPackets.Count; } }
    }

    /// <summary>
    /// Scope Out packet count when the last command was written. Replies can arrive
    /// before the next step starts, so waits count from here rather than from "now".
    /// </summary>
    public int OutPacketCountAtLastWrite
    {
        get { lock (_lock) { return _outPacketCountAtLastWrite; } }
    }

    public IReadOnlyList<byte[]> OutPacketsSince(int index)
    {
        lock (_lock)
        {
            return _outPackets.Skip(index).ToList();
        }
    }

    public void Comment(string text)
    {
        lock (_lock)
        {
            Console.WriteLine($"# {text}");
            _capture?.WriteLine($"# {text}");
        }
    }

    /// <summary>Frame count when the last command was written; see <see cref="OutPacketCountAtLastWrite"/>.</summary>
    public int FrameCountAtLastWrite
    {
        get { lock (_lock) { return _frameCountAtLastWrite; } }
    }

    /// <summary>Call just before writing, so replies racing the write are counted as after it.</summary>
    public void MarkWrite()
    {
        lock (_lock)
        {
            _outPacketCountAtLastWrite = _outPackets.Count;
            _frameCountAtLastWrite = _frameCount;
        }
    }

    public void RecordWrite(ushort characteristic, byte[] packet)
        => Record(characteristic == AeroscopeGatt.ScopeState ? CaptureChannel.State : CaptureChannel.In, packet);

    public void Record(CaptureChannel channel, byte[] packet)
    {
        lock (_lock)
        {
            var line = CaptureFile.FormatLine(new CapturedPacket(_clock.Elapsed, channel, packet));
            _capture?.WriteLine(line);

            switch (channel)
            {
                case CaptureChannel.Out:
                    _outPackets.Add(packet);
                    Console.WriteLine($"{line}  {ScopeOutMessage.Parse(packet)}");
                    break;
                case CaptureChannel.Data:
                    if (_printRawData)
                    {
                        Console.WriteLine(line);
                    }

                    _frameAssembler.AddPacket(packet);
                    break;
                default:
                    Console.WriteLine(line);
                    break;
            }
        }
    }

    public async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string failure)
    {
        var deadline = _clock.Elapsed + timeout;
        while (!condition())
        {
            if (_clock.Elapsed > deadline)
            {
                throw new TimeoutException($"{failure} within {timeout.TotalSeconds} s.");
            }

            await Task.Delay(50);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _capture?.Dispose();
        }
    }
}
