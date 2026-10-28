using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace Aeroscope.Core.Bluetooth;

public sealed record ScopeAdvertisement(ulong Address, string Name, short Rssi, bool AdvertisesScopeService);

/// <summary>
/// Listens for BLE advertisements. Reports every device; callers decide whether
/// to show only those that advertise the Aeroscope service.
/// </summary>
public sealed class ScopeScanner : IScopeScanner
{
    // Active scanning requests scan responses, which is where the name and the
    // 128-bit service UUID often live.
    private readonly BluetoothLEAdvertisementWatcher _watcher = new()
    {
        ScanningMode = BluetoothLEScanningMode.Active,
    };

    public event EventHandler<ScopeAdvertisement>? AdvertisementReceived;

    /// <summary>Raised when scanning stops; carries the reason (e.g. RadioNotAvailable).</summary>
    public event EventHandler<BluetoothError>? Stopped;

    public ScopeScanner()
    {
        _watcher.Received += OnReceived;
        _watcher.Stopped += OnStopped;
    }

    public bool IsScanning => _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    public void Start() => _watcher.Start();

    public void Stop() => _watcher.Stop();

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var advertisement = args.Advertisement;
        AdvertisementReceived?.Invoke(this, new ScopeAdvertisement(
            args.BluetoothAddress,
            advertisement.LocalName,
            args.RawSignalStrengthInDBm,
            advertisement.ServiceUuids.Contains(AeroscopeGatt.ServiceUuid)));
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        => Stopped?.Invoke(this, args.Error);

    public void Dispose()
    {
        _watcher.Received -= OnReceived;
        _watcher.Stopped -= OnStopped;
        if (IsScanning)
        {
            _watcher.Stop();
        }
    }
}
