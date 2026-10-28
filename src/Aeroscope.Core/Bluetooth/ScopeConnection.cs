using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;

namespace Aeroscope.Core.Bluetooth;

public sealed record CharacteristicInfo(Guid Uuid, GattCharacteristicProperties Properties)
{
    public ushort ShortId => AeroscopeGatt.ShortId(Uuid);
    public string Role => AeroscopeGatt.RoleOf(ShortId);
}

public sealed class ScopeConnectionException(string message) : Exception(message);

/// <summary>
/// An open GATT connection to an Aeroscope. The scope does not require bonding
/// (the Android app never paired), so this connects directly without Windows pairing.
/// </summary>
public sealed class ScopeConnection : IScopeLink
{
    private const int ServiceDiscoveryAttempts = 3;

    private readonly BluetoothLEDevice _device;
    private readonly GattSession _session;
    private readonly GattDeviceService _service;
    private readonly Dictionary<ushort, GattCharacteristic> _characteristics;
    private bool _wasDisconnected;

    /// <summary>Raw 20-byte packets from the Scope Data characteristic.</summary>
    public event EventHandler<byte[]>? DataPacketReceived;

    /// <summary>Raw 20-byte packets from the Scope Out characteristic.</summary>
    public event EventHandler<byte[]>? OutPacketReceived;

    /// <summary>True when the link comes up again after a drop, false when it drops.</summary>
    public event EventHandler<bool>? ConnectionChanged;

    /// <summary>Non-fatal problems worth showing in a log.</summary>
    public event EventHandler<string>? Diagnostic;

    public string Name => _device.Name;

    public ulong Address => _device.BluetoothAddress;

    public IReadOnlyList<CharacteristicInfo> Characteristics { get; }

    private ScopeConnection(
        BluetoothLEDevice device,
        GattSession session,
        GattDeviceService service,
        IReadOnlyList<GattCharacteristic> characteristics)
    {
        _device = device;
        _session = session;
        _service = service;
        _characteristics = characteristics.ToDictionary(c => AeroscopeGatt.ShortId(c.Uuid));
        Characteristics = characteristics
            .Select(c => new CharacteristicInfo(c.Uuid, c.CharacteristicProperties))
            .ToList();
    }

    public static async Task<ScopeConnection> ConnectAsync(ulong address, CancellationToken cancellationToken = default)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(cancellationToken).ConfigureAwait(false)
            ?? throw new ScopeConnectionException("Device not found. Is the scope switched on and in range?");

        GattSession? session = null;
        GattDeviceService? service = null;
        try
        {
            // Keeps the link up while we hold the session, instead of letting
            // Windows drop it between GATT operations.
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(cancellationToken).ConfigureAwait(false);
            session.MaintainConnection = true;

            service = await GetScopeServiceAsync(device, cancellationToken).ConfigureAwait(false);

            var result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
            if (result.Status != GattCommunicationStatus.Success)
            {
                throw new ScopeConnectionException($"Reading characteristics failed: {result.Status}.");
            }

            var connection = new ScopeConnection(device, session, service, result.Characteristics);
            connection.EnsureRequiredCharacteristics();
            await connection.SubscribeAsync(cancellationToken).ConfigureAwait(false);
            device.ConnectionStatusChanged += connection.OnConnectionStatusChanged;
            return connection;
        }
        catch
        {
            service?.Dispose();
            session?.Dispose();
            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes one packet to Scope In or Scope State, zero padding it to 20 bytes.
    /// </summary>
    public async Task WriteAsync(ushort shortId, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > AeroscopeGatt.PacketLength)
        {
            throw new ArgumentException($"Payload is {payload.Length} bytes; the maximum is {AeroscopeGatt.PacketLength}.", nameof(payload));
        }

        var packet = new byte[AeroscopeGatt.PacketLength];
        payload.CopyTo(packet);

        var characteristic = _characteristics[shortId];
        var option = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;

        var status = await characteristic.WriteValueAsync(CryptographicBuffer.CreateFromByteArray(packet), option).AsTask().ConfigureAwait(false);
        if (status != GattCommunicationStatus.Success)
        {
            throw new ScopeConnectionException($"Write to {AeroscopeGatt.RoleOf(shortId)} failed: {status}.");
        }
    }

    private static async Task<GattDeviceService> GetScopeServiceAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        // Windows often reports Unreachable on the first attempt while the link is still coming up.
        var status = GattCommunicationStatus.Unreachable;
        for (var attempt = 0; attempt < ServiceDiscoveryAttempts; attempt++)
        {
            var result = await device.GetGattServicesForUuidAsync(AeroscopeGatt.ServiceUuid, BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken).ConfigureAwait(false);
            status = result.Status;
            if (status == GattCommunicationStatus.Success)
            {
                return result.Services.Count > 0
                    ? result.Services[0]
                    : throw new ScopeConnectionException("Connected, but the device does not expose the Aeroscope service.");
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new ScopeConnectionException($"Service discovery failed: {status}.");
    }

    private void EnsureRequiredCharacteristics()
    {
        ushort[] required = [AeroscopeGatt.ScopeData, AeroscopeGatt.ScopeIn, AeroscopeGatt.ScopeState, AeroscopeGatt.ScopeOut];
        var missing = required.Where(id => !_characteristics.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var found = string.Join(", ", Characteristics.Select(c => c.Uuid));
            throw new ScopeConnectionException(
                $"Missing characteristics {string.Join(", ", missing.Select(id => $"0x{id:X4}"))}. Found: {found}.");
        }
    }

    private async Task SubscribeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var id in new[] { AeroscopeGatt.ScopeOut, AeroscopeGatt.ScopeData })
        {
            var characteristic = _characteristics[id];
            characteristic.ValueChanged -= OnValueChanged;
            characteristic.ValueChanged += OnValueChanged;

            var status = await characteristic
                .WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (status != GattCommunicationStatus.Success)
            {
                throw new ScopeConnectionException($"Enabling notifications on {AeroscopeGatt.RoleOf(id)} failed: {status}.");
            }
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out var bytes);
        switch (AeroscopeGatt.ShortId(sender.Uuid))
        {
            case AeroscopeGatt.ScopeData:
                DataPacketReceived?.Invoke(this, bytes);
                break;
            case AeroscopeGatt.ScopeOut:
                OutPacketReceived?.Invoke(this, bytes);
                break;
        }
    }

    private async void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _wasDisconnected = true;
            ConnectionChanged?.Invoke(this, false);
            return;
        }

        if (!_wasDisconnected)
        {
            return;
        }

        // MaintainConnection makes Windows reconnect on its own. The scope is not
        // bonded, so it forgets notification subscriptions; enable them again.
        _wasDisconnected = false;
        try
        {
            await SubscribeAsync().ConfigureAwait(false);
            ConnectionChanged?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke(this, $"Re-subscribing after reconnect failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        foreach (var characteristic in _characteristics.Values)
        {
            characteristic.ValueChanged -= OnValueChanged;
        }

        _service.Dispose();
        _session.Dispose();
        _device.Dispose();
    }
}
