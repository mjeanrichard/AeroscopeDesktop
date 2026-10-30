using Aeroscope.App;
using Aeroscope.Core.Bluetooth;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Aeroscope.Tests;

/// <summary>Stands in for a connected scope: records writes and lets tests raise notifications.</summary>
internal sealed class FakeScopeLink : IScopeLink
{
    /// <summary>What the real scope (firmware rev 5) reports.</summary>
    public static readonly IReadOnlyList<CharacteristicInfo> RealCharacteristics =
    [
        new(Guid.Parse("00001235-0000-1000-8000-00805f9b34fb"), GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify),
        new(Guid.Parse("00001236-0000-1000-8000-00805f9b34fb"), GattCharacteristicProperties.Read | GattCharacteristicProperties.Write),
        new(Guid.Parse("00001237-0000-1000-8000-00805f9b34fb"), GattCharacteristicProperties.Read | GattCharacteristicProperties.Write),
        new(Guid.Parse("00001239-0000-1000-8000-00805f9b34fb"), GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify),
    ];

    public event EventHandler<byte[]>? DataPacketReceived;
    public event EventHandler<byte[]>? OutPacketReceived;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<string>? Diagnostic;

    public string Name { get; init; } = "Aeroscope";

    public IReadOnlyList<CharacteristicInfo> Characteristics { get; init; } = RealCharacteristics;

    public List<(ushort Characteristic, byte[] Packet)> Writes { get; } = [];

    /// <summary>Called after each write, e.g. to answer a query the way the scope would.</summary>
    public Action<FakeScopeLink, ushort, byte[]>? OnWrite { get; set; }

    public Exception? WriteFailure { get; set; }

    public bool IsDisposed { get; private set; }

    public Task WriteAsync(ushort shortId, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > AeroscopeGatt.PacketLength)
        {
            throw new ArgumentException("Payload too long.", nameof(payload));
        }

        if (WriteFailure is not null)
        {
            return Task.FromException(WriteFailure);
        }

        var packet = new byte[AeroscopeGatt.PacketLength];
        payload.CopyTo(packet);
        Writes.Add((shortId, packet));
        OnWrite?.Invoke(this, shortId, packet);
        return Task.CompletedTask;
    }

    public void RaiseOut(byte[] packet) => OutPacketReceived?.Invoke(this, packet);

    public void RaiseData(byte[] packet) => DataPacketReceived?.Invoke(this, packet);

    public void RaiseConnectionChanged(bool connected) => ConnectionChanged?.Invoke(this, connected);

    public void RaiseDiagnostic(string message) => Diagnostic?.Invoke(this, message);

    public void Dispose() => IsDisposed = true;
}

/// <summary>Preferences in memory: what the app would load, and what it saved.</summary>
internal sealed class FakePreferencesStore(Preferences? stored = null) : IPreferencesStore
{
    public Preferences Stored { get; private set; } = stored ?? new();

    public int SaveCount { get; private set; }

    public Preferences Load() => Stored;

    public void Save(Preferences preferences)
    {
        Stored = preferences;
        SaveCount++;
    }
}

internal sealed class FakeScopeScanner : IScopeScanner
{
    public event EventHandler<ScopeAdvertisement>? AdvertisementReceived;
    public event EventHandler<BluetoothError>? Stopped;

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public bool IsDisposed { get; private set; }

    public void Start() => StartCount++;

    public void Stop() => StopCount++;

    public void RaiseAdvertisement(ScopeAdvertisement advertisement) => AdvertisementReceived?.Invoke(this, advertisement);

    public void RaiseStopped(BluetoothError error) => Stopped?.Invoke(this, error);

    public void Dispose() => IsDisposed = true;
}
