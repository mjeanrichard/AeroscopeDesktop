using System.IO;
using Aeroscope.Core.Bluetooth;
using Aeroscope.Core.Diagnostics;
using Aeroscope.Probe;

namespace Aeroscope.Tests.Probe;

public class SessionOptionsTests
{
    [Fact]
    public void Defaults_to_the_startup_steps()
    {
        var options = SessionOptions.Parse([]);

        Assert.Null(options.Address);
        Assert.Null(options.CapturePath);
        Assert.False(options.PrintRawData);
        Assert.Equal(SessionOptions.DefaultSteps, options.Steps.Select(s => s.Text));
    }

    [Fact]
    public void Options_and_steps_can_be_mixed()
    {
        var options = SessionOptions.Parse(["--address", "C6:A7:C8:D1:1A:B1", "QP", "--raw", "--out", "x.txt", "until:PF"]);

        Assert.Equal(0xC6A7C8D11AB1UL, options.Address);
        Assert.Equal("x.txt", options.CapturePath);
        Assert.True(options.PrintRawData);
        Assert.Equal(["QP", "until:PF"], options.Steps.Select(s => s.Text));
    }

    [Theory]
    [InlineData("--address")]
    [InlineData("--out")]
    public void Option_without_value_throws(string option)
    {
        var ex = Assert.Throws<ArgumentException>(() => SessionOptions.Parse([option]));

        Assert.Equal($"{option} needs a value.", ex.Message);
    }

    [Fact]
    public void Bad_address_or_step_fails_before_connecting()
    {
        Assert.Throws<FormatException>(() => SessionOptions.Parse(["--address", "nope"]));
        Assert.Throws<FormatException>(() => SessionOptions.Parse(["until:"]));
    }
}

[Collection(ConsoleCollection.Name)]
public sealed class SessionCommandTests : IDisposable
{
    private readonly string _capturePath = Path.GetTempFileName();
    private readonly TextWriter _originalOut = Console.Out;
    private readonly FakeScopeLink _link = new();

    public SessionCommandTests()
    {
        Console.SetOut(TextWriter.Null);

        // Answer like the real scope did in the recorded captures.
        var frames = Captures.Packets(Captures.RawSingle, CaptureChannel.Data).ToList();
        _link.OnWrite = (link, characteristic, packet) =>
        {
            switch ((char)packet[0], (char)packet[1])
            {
                case ('Q', 'P'):
                    link.RaiseOut(Captures.Packet("50 46"));
                    break;
                case ('Q', 'V'):
                    link.RaiseOut(Captures.Packet("56 01 05 05 00 0F 42 79"));
                    break;
                case ('R', _):
                    frames.ForEach(link.RaiseData);
                    break;
            }
        };
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        File.Delete(_capturePath);
    }

    private async Task<int> RunAsync(params string[] steps)
    {
        using var recorder = new SessionRecorder(_capturePath, printRawData: false);
        return await SessionCommand.RunStepsAsync(_link, recorder, steps.Select(SessionStep.Parse).ToList());
    }

    [Fact]
    public async Task Script_runs_in_order_and_is_captured()
    {
        var exitCode = await RunAsync("QP", "until:PF:1", "settings", "QVR", "until:V:1", "R", "frames:2:1", "S", "wait:0.01");

        Assert.Equal(0, exitCode);
        Assert.Equal([AeroscopeGatt.ScopeIn, AeroscopeGatt.ScopeState, AeroscopeGatt.ScopeIn, AeroscopeGatt.ScopeIn, AeroscopeGatt.ScopeIn],
            _link.Writes.Select(w => w.Characteristic));

        var capture = CaptureFile.Read(_capturePath);
        Assert.Equal(2, capture.Count(p => p.Channel == CaptureChannel.Out));
        Assert.Equal(54, capture.Count(p => p.Channel == CaptureChannel.Data));

        var text = File.ReadAllText(_capturePath);
        Assert.Contains("# characteristic 00001235-0000-1000-8000-00805f9b34fb Scope Data Read, Notify", text);
        Assert.Contains("# step until:PF:1", text);
        Assert.Contains("# done: 2 Scope Out packet(s), 2 frame(s)", text);
    }

    [Fact]
    public async Task Timeouts_are_recorded_and_later_steps_still_run()
    {
        // Seen on the real scope: F gets no frame, but R afterwards works.
        var exitCode = await RunAsync("F", "frames:1:0.1", "R", "frames:2:1");

        Assert.Equal(3, exitCode);
        var text = File.ReadAllText(_capturePath);
        Assert.Contains("# TIMEOUT: Fewer than 1 frames arrived within 0.1 s.", text);
        Assert.Contains("# done: 0 Scope Out packet(s), 2 frame(s)", text);
    }

    [Fact]
    public async Task A_failed_write_is_recorded_and_ends_the_session()
    {
        // Seen on the real scope: ZZ drops the link and the write is cancelled instead of acknowledged.
        _link.WriteFailure = new OperationCanceledException("The operation was canceled.");

        var exitCode = await RunAsync("ZZ", "QP");

        Assert.Equal(4, exitCode);
        var text = File.ReadAllText(_capturePath);
        Assert.Contains("# step ZZ", text);
        Assert.Contains("# WRITE FAILED: The operation was canceled.", text);
        Assert.DoesNotContain("# step QP", text);
        Assert.Contains("# done: 0 Scope Out packet(s), 0 frame(s)", text);
    }

    [Fact]
    public async Task Link_events_become_comments()
    {
        _link.OnWrite = (link, _, _) =>
        {
            link.RaiseConnectionChanged(false);
            link.RaiseConnectionChanged(true);
            link.RaiseDiagnostic("Re-subscribing after reconnect failed: x");
        };

        await RunAsync("QP");

        var text = File.ReadAllText(_capturePath);
        Assert.Contains("# connection lost", text);
        Assert.Contains("# reconnected", text);
        Assert.Contains("# Re-subscribing after reconnect failed: x", text);
    }

    [Fact]
    public async Task Write_step_pads_to_20_bytes()
    {
        await RunAsync("in:5154");

        Assert.Equal(Captures.Packet("51 54"), Assert.Single(_link.Writes).Packet);
    }
}
