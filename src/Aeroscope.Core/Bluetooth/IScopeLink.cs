namespace Aeroscope.Core.Bluetooth;

/// <summary>An open connection to a scope. Implemented by <see cref="ScopeConnection"/>; faked in tests.</summary>
public interface IScopeLink : IDisposable
{
    /// <summary>Raw 20-byte packets from the Scope Data characteristic.</summary>
    event EventHandler<byte[]>? DataPacketReceived;

    /// <summary>Raw 20-byte packets from the Scope Out characteristic.</summary>
    event EventHandler<byte[]>? OutPacketReceived;

    /// <summary>True when the link comes up again after a drop, false when it drops.</summary>
    event EventHandler<bool>? ConnectionChanged;

    /// <summary>Non-fatal problems worth showing in a log.</summary>
    event EventHandler<string>? Diagnostic;

    string Name { get; }

    IReadOnlyList<CharacteristicInfo> Characteristics { get; }

    /// <summary>Writes one packet to Scope In or Scope State, zero padding it to 20 bytes.</summary>
    Task WriteAsync(ushort shortId, ReadOnlyMemory<byte> payload);
}

/// <summary>A BLE advertisement listener. Implemented by <see cref="ScopeScanner"/>; faked in tests.</summary>
public interface IScopeScanner : IDisposable
{
    event EventHandler<ScopeAdvertisement>? AdvertisementReceived;

    /// <summary>Raised when scanning stops; carries the reason (e.g. RadioNotAvailable).</summary>
    event EventHandler<Windows.Devices.Bluetooth.BluetoothError>? Stopped;

    void Start();

    void Stop();
}
