using Aeroscope.Core.Bluetooth;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aeroscope.App.ViewModels;

/// <summary>A BLE device seen while scanning.</summary>
public partial class DeviceItem(ulong address) : ObservableObject
{
    public ulong Address { get; } = address;

    public string AddressText { get; } = BluetoothAddress.Format(address);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial short Rssi { get; set; }

    [ObservableProperty]
    public partial bool IsAeroscope { get; set; }

    public string DisplayName => string.IsNullOrEmpty(Name) ? "(unnamed)" : Name;
}
