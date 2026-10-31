using Aeroscope.Core.Bluetooth;
using Aeroscope.Core.Diagnostics;

namespace Aeroscope.Probe;

/// <summary>Parsed "session" arguments. Steps are parsed up front so typos fail before connecting.</summary>
public sealed record SessionOptions(ulong? Address, string? CapturePath, bool PrintRawData, IReadOnlyList<SessionStep> Steps)
{
    public static readonly string[] DefaultSteps = ["QTI", "QP", "until:PF", "QVR", "wait:1"];

    public static SessionOptions Parse(string[] args)
    {
        ulong? address = null;
        string? capturePath = null;
        var printRawData = false;
        var stepTexts = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--address":
                    address = BluetoothAddress.Parse(ValueAfter(args, ref i));
                    break;
                case "--out":
                    capturePath = ValueAfter(args, ref i);
                    break;
                case "--raw":
                    printRawData = true;
                    break;
                default:
                    stepTexts.Add(args[i]);
                    break;
            }
        }

        if (stepTexts.Count == 0)
        {
            stepTexts.AddRange(DefaultSteps);
        }

        return new SessionOptions(address, capturePath, printRawData, stepTexts.Select(SessionStep.Parse).ToList());
    }

    private static string ValueAfter(string[] args, ref int i)
        => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
}

internal static class SessionCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = SessionOptions.Parse(args);

        ulong address;
        if (options.Address is { } given)
        {
            address = given;
        }
        else
        {
            Console.WriteLine("Looking for an Aeroscope...");
            var found = await ScanCommand.FindFirstScopeAsync(TimeSpan.FromSeconds(15));
            address = found.Address;
        }

        Console.WriteLine($"Connecting to {BluetoothAddress.Format(address)}...");
        using var connection = await ScopeConnection.ConnectAsync(address);
        using var recorder = new SessionRecorder(options.CapturePath, options.PrintRawData);

        recorder.Comment($"Aeroscope capture {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        recorder.Comment($"device {connection.Name} {BluetoothAddress.Format(address)}");
        return await RunStepsAsync(connection, recorder, options.Steps);
    }

    /// <summary>Runs the steps against an open link, recording all traffic. Returns the exit code.</summary>
    public static async Task<int> RunStepsAsync(IScopeLink connection, SessionRecorder recorder, IReadOnlyList<SessionStep> steps)
    {
        foreach (var characteristic in connection.Characteristics)
        {
            recorder.Comment($"characteristic {characteristic.Uuid} {characteristic.Role} {characteristic.Properties}");
        }

        connection.OutPacketReceived += (_, packet) => recorder.Record(CaptureChannel.Out, packet);
        connection.DataPacketReceived += (_, packet) => recorder.Record(CaptureChannel.Data, packet);
        connection.ConnectionChanged += (_, connected) => recorder.Comment(connected ? "reconnected" : "connection lost");
        connection.Diagnostic += (_, message) => recorder.Comment(message);

        var exitCode = 0;
        foreach (var step in steps)
        {
            recorder.Comment($"step {step.Text}");
            try
            {
                await step.ExecuteAsync(connection, recorder);
            }
            catch (TimeoutException ex)
            {
                // Keep going: a missing reply is a finding, and later steps may still tell us something.
                recorder.Comment($"TIMEOUT: {ex.Message}");
                exitCode = 3;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ScopeConnectionException)
            {
                // The link is gone (ZZ drops it before acknowledging the write), so later steps can't run.
                recorder.Comment($"WRITE FAILED: {ex.Message}");
                exitCode = 4;
                break;
            }
        }

        recorder.Comment($"done: {recorder.OutPacketCount} Scope Out packet(s), {recorder.FrameCount} frame(s)");
        return exitCode;
    }
}
