using System.IO;
using Aeroscope.Core.Bluetooth;
using Aeroscope.Core.Protocol;
using Aeroscope.Probe;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Aeroscope.Tests.Hardware;

/// <summary>
/// A test that needs a real, switched-on Aeroscope in range. Skipped unless the
/// AEROSCOPE_ADDRESS environment variable holds its address, e.g. C6:A7:C8:D1:1A:B1.
/// </summary>
public sealed class HardwareFactAttribute : FactAttribute
{
    public const string AddressVariable = "AEROSCOPE_ADDRESS";

    public HardwareFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AddressVariable)))
        {
            Skip = $"Set {AddressVariable} to run against a real scope.";
        }
    }
}

/// <summary>
/// Exercises the WinRT adapters (ScopeScanner, ScopeConnection) and the probe's
/// BLE commands against the real device. Runs serially: the scope accepts one connection.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ScopeHardwareTests : IDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);
    private readonly TextWriter _originalOut = Console.Out;

    public ScopeHardwareTests() => Console.SetOut(TextWriter.Null);

    public void Dispose() => Console.SetOut(_originalOut);

    private static ulong Address => BluetoothAddress.Parse(Environment.GetEnvironmentVariable(HardwareFactAttribute.AddressVariable)!);

    [HardwareFact]
    public async Task Scanner_sees_the_scope_advertising_its_service()
    {
        var seen = new TaskCompletionSource<ScopeAdvertisement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scanner = new ScopeScanner();
        scanner.AdvertisementReceived += (_, ad) =>
        {
            if (ad.Address == Address && ad.AdvertisesScopeService)
            {
                seen.TrySetResult(ad);
            }
        };

        scanner.Start();
        var advertisement = await seen.Task.WaitAsync(ReplyTimeout);
        Assert.True(scanner.IsScanning); // the watcher reports Started asynchronously, so check after the first advert
        scanner.Stop();

        Assert.InRange(advertisement.Rssi, (short)-127, (short)0);
    }

    [HardwareFact]
    public async Task Probe_finds_the_scope()
    {
        var found = await ScanCommand.FindFirstScopeAsync(ReplyTimeout);

        Assert.Equal(Address, found.Address);
        Assert.Equal(0, await ScanCommand.RunAsync(["1"]));
    }

    [HardwareFact]
    public async Task Connection_exposes_the_four_characteristics()
    {
        using var connection = await ScopeConnection.ConnectAsync(Address);

        Assert.Equal(Address, connection.Address);
        Assert.False(string.IsNullOrEmpty(connection.Name));
        var byRole = connection.Characteristics.ToDictionary(c => c.Role);
        Assert.True(byRole["Scope Data"].Properties.HasFlag(GattCharacteristicProperties.Notify));
        Assert.True(byRole["Scope Out"].Properties.HasFlag(GattCharacteristicProperties.Notify));
        Assert.True(byRole["Scope In"].Properties.HasFlag(GattCharacteristicProperties.Write));
        Assert.True(byRole["Scope State"].Properties.HasFlag(GattCharacteristicProperties.Write));
    }

    [HardwareFact]
    public async Task Scope_answers_version_and_telemetry_queries()
    {
        using var connection = await ScopeConnection.ConnectAsync(Address);
        var version = WaitForOut<VersionMessage>(connection);
        var telemetry = WaitForOut<TelemetryMessage>(connection);

        await connection.WriteAsync(AeroscopeGatt.ScopeIn, "QVR"u8.ToArray());
        await connection.WriteAsync(AeroscopeGatt.ScopeIn, "QTI"u8.ToArray());

        Assert.NotEqual(0u, (await version.WaitAsync(ReplyTimeout)).SerialNumber);
        Assert.InRange((await telemetry.WaitAsync(ReplyTimeout)).TemperatureCelsius, -20, 60);
    }

    [HardwareFact]
    public async Task Run_mode_streams_512_sample_frames()
    {
        using var connection = await ScopeConnection.ConnectAsync(Address);
        var assembler = new FrameAssembler();
        var frames = new List<ScopeFrame>();
        var threeFrames = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        assembler.FrameCompleted += (_, frame) =>
        {
            // After roll mode (e.g. an earlier test), R first flushes up to 3 leftover 16-sample frames.
            if (frames.Count == 0 && frame.Samples.Length == RollBuffer.FrameLength)
            {
                return;
            }

            frames.Add(frame);
            if (frames.Count == 3)
            {
                threeFrames.TrySetResult();
            }
        };
        connection.DataPacketReceived += (_, packet) =>
        {
            lock (assembler)
            {
                assembler.AddPacket(packet);
            }
        };

        await WaitForPowerOnAsync(connection);
        await connection.WriteAsync(AeroscopeGatt.ScopeState, new ScopeRegisters().ToPacket());
        await connection.WriteAsync(AeroscopeGatt.ScopeIn, "R"u8.ToArray());
        try
        {
            await threeFrames.Task.WaitAsync(ReplyTimeout);
        }
        finally
        {
            await connection.WriteAsync(AeroscopeGatt.ScopeIn, "S"u8.ToArray());
            await connection.WriteAsync(AeroscopeGatt.ScopeIn, "X"u8.ToArray());
        }

        lock (assembler)
        {
            Assert.All(frames, frame => Assert.Equal(512, frame.Samples.Length));
        }
    }

    [HardwareFact]
    public async Task Acquisition_view_model_runs_and_takes_a_single_shot()
    {
        using var connection = await ScopeConnection.ConnectAsync(Address);
        var acquisition = new Aeroscope.App.ViewModels.AcquisitionViewModel((id, payload) => connection.WriteAsync(id, payload));
        var assembler = new FrameAssembler();
        var frames = 0;
        var frameArrived = new SemaphoreSlim(0);
        assembler.FrameCompleted += (_, frame) =>
        {
            acquisition.OnFrameAsync(frame).Wait();
            frames++;
            frameArrived.Release();
        };
        connection.DataPacketReceived += (_, packet) =>
        {
            lock (assembler)
            {
                assembler.AddPacket(packet);
            }
        };

        await WaitForPowerOnAsync(connection);
        await acquisition.OnPowerChangedAsync(true);

        await acquisition.RunCommand.ExecuteAsync(null);
        Assert.True(await frameArrived.WaitAsync(ReplyTimeout));
        await acquisition.StopCommand.ExecuteAsync(null);
        Assert.NotNull(acquisition.Waveform);

        // Single: exactly one more frame, then the scope stays quiet.
        await Task.Delay(500);
        while (frameArrived.CurrentCount > 0)
        {
            await frameArrived.WaitAsync();
        }

        var before = frames;
        await acquisition.SingleCommand.ExecuteAsync(null);
        Assert.True(await frameArrived.WaitAsync(ReplyTimeout));
        await Task.Delay(1500);

        Assert.Equal(Aeroscope.App.ViewModels.RunMode.Stopped, acquisition.RunMode);
        Assert.Equal(before + 1, frames);
    }

    [HardwareFact]
    public async Task Roll_mode_single_fills_the_screen_then_stops()
    {
        using var connection = await ScopeConnection.ConnectAsync(Address);
        var acquisition = new Aeroscope.App.ViewModels.AcquisitionViewModel((id, payload) => connection.WriteAsync(id, payload));
        var assembler = new FrameAssembler();
        var lengths = new List<int>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        assembler.FrameCompleted += (_, frame) =>
        {
            lengths.Add(frame.Samples.Length);
            acquisition.OnFrameAsync(frame).Wait();
            if (acquisition.RunMode == Aeroscope.App.ViewModels.RunMode.Stopped)
            {
                stopped.TrySetResult();
            }
        };
        connection.DataPacketReceived += (_, packet) =>
        {
            lock (assembler)
            {
                assembler.AddPacket(packet);
            }
        };

        acquisition.TimeBase = TimeBase.All.Single(t => t.Name == "500 ms");
        await WaitForPowerOnAsync(connection);
        await acquisition.OnPowerChangedAsync(true);

        await acquisition.SingleCommand.ExecuteAsync(null); // 32 frames of 160 ms
        await stopped.Task.WaitAsync(ReplyTimeout);
        var waveform = acquisition.Waveform;
        await Task.Delay(1000);

        lock (assembler)
        {
            Assert.All(lengths, length => Assert.Equal(RollBuffer.FrameLength, length));
            Assert.InRange(lengths.Count, 32, 34); // up to 2 more: a leftover at the start, one racing the stop
        }

        Assert.Same(waveform, acquisition.Waveform); // frozen after the stop
        Assert.Equal(TimeBase.SamplesOnScreen, waveform!.Volts.Length);
    }

    [HardwareFact]
    public async Task Probe_session_runs_the_default_steps()
    {
        var capturePath = Path.GetTempFileName();
        try
        {
            var exitCode = await SessionCommand.RunAsync(["--address", BluetoothAddress.Format(Address), "--out", capturePath]);

            Assert.Equal(0, exitCode);
            Assert.Contains("# step until:PF", File.ReadAllText(capturePath));
        }
        finally
        {
            File.Delete(capturePath);
        }
    }

    private static Task<T> WaitForOut<T>(IScopeLink link) where T : ScopeOutMessage
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.OutPacketReceived += (_, packet) =>
        {
            if (ScopeOutMessage.Parse(packet) is T message)
            {
                result.TrySetResult(message);
            }
        };
        return result.Task;
    }

    private static async Task WaitForPowerOnAsync(IScopeLink link)
    {
        var power = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        link.OutPacketReceived += (_, packet) =>
        {
            if (ScopeOutMessage.Parse(packet) is PowerStateMessage { FullyOn: true })
            {
                power.TrySetResult();
            }
        };

        await link.WriteAsync(AeroscopeGatt.ScopeIn, "QP"u8.ToArray());
        await power.Task.WaitAsync(ReplyTimeout);
    }
}
