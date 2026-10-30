using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Aeroscope.Core.Bluetooth;
using Aeroscope.Core.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Devices.Bluetooth;

namespace Aeroscope.App.ViewModels;

public enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting }

public partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogEntries = 2000;

    private readonly IScopeScanner _scanner;
    private readonly Func<ulong, Task<IScopeLink>> _connect;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;
    private readonly FrameAssembler _frameAssembler = new();
    private readonly Dictionary<ulong, DeviceItem> _devicesByAddress = [];
    private readonly IPreferencesStore _preferencesStore;
    private Preferences _preferences;

    /// <summary>Connect when the last scope advertises: from start-up or turning auto-connect on, until a connection is tried.</summary>
    private bool _autoConnectArmed;

    private IScopeLink? _connection;
    private DateTimeOffset _lastDataReceived;
    private DateTimeOffset _frameStarted;

    public MainViewModel()
        : this(new ScopeScanner(), async address => await ScopeConnection.ConnectAsync(address), PostTo(Application.Current.Dispatcher),
            new FilePreferencesStore(FilePreferencesStore.DefaultPath))
    {
    }

    /// <param name="scanner">Source of advertisements.</param>
    /// <param name="connect">Opens a link to the device with the given address.</param>
    /// <param name="post">Runs an action on the UI thread; BLE events arrive on thread-pool threads.</param>
    /// <param name="preferences">Where the last scope and auto-connect are remembered.</param>
    /// <param name="time">Clock for packet arrival times; the system clock if null.</param>
    public MainViewModel(IScopeScanner scanner, Func<ulong, Task<IScopeLink>> connect, Action<Action> post, IPreferencesStore preferences, TimeProvider? time = null)
    {
        _scanner = scanner;
        _connect = connect;
        _post = post;
        _preferencesStore = preferences;
        _preferences = preferences.Load();
        AutoConnect = _preferences.AutoConnect;
        _time = time ?? TimeProvider.System;
        Acquisition = new AcquisitionViewModel(
            (characteristic, payload) => WriteAsync(characteristic, payload, characteristic == AeroscopeGatt.ScopeState ? LogKind.State : LogKind.In),
            _time);

        DevicesView = CollectionViewSource.GetDefaultView(Devices);
        DevicesView.Filter = item => ShowAllDevices || ((DeviceItem)item).IsAeroscope;

        _scanner.AdvertisementReceived += (_, ad) => _post(() => OnAdvertisement(ad));
        _scanner.Stopped += (_, error) => _post(() => OnScanStopped(error));

        _frameAssembler.FrameCompleted += (_, frame) => OnFrame(frame);
        _frameAssembler.FrameDropped += (_, reason) => AddLog(LogEntry.Message(LogKind.Error, $"Frame dropped: {reason}"));
    }

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    public ICollectionView DevicesView { get; }

    public ObservableCollection<CharacteristicInfo> Characteristics { get; } = [];

    public ObservableCollection<LogEntry> Log { get; } = [];

    public ScopeStatus Scope { get; } = new();

    public AcquisitionViewModel Acquisition { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanButtonText))]
    public partial bool IsScanning { get; set; }

    public string ScanButtonText => IsScanning ? "Stop scan" : "Scan";

    [ObservableProperty]
    public partial bool ShowAllDevices { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial DeviceItem? SelectedDevice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommandCommand), nameof(SendCustomCommandCommand), nameof(SendSettingsCommand))]
    public partial ConnectionState State { get; set; }

    public bool IsDisconnected => State == ConnectionState.Disconnected;

    [ObservableProperty]
    public partial string Status { get; set; } = "Not connected";

    /// <summary>Connect to the last scope as soon as it is in range (remembered between runs).</summary>
    [ObservableProperty]
    public partial bool AutoConnect { get; set; }

    /// <summary>Raw data packets are many per frame, so they are only logged on request.</summary>
    [ObservableProperty]
    public partial bool LogDataPackets { get; set; }

    [ObservableProperty]
    public partial string CustomCommand { get; set; } = "";

    partial void OnShowAllDevicesChanged(bool value) => DevicesView.Refresh();

    partial void OnAutoConnectChanged(bool value)
    {
        if (value == _preferences.AutoConnect)
        {
            return; // loaded from the preferences
        }

        SavePreferences(_preferences with { AutoConnect = value });
        _autoConnectArmed = value;
        if (value)
        {
            LookForLastScope();
        }
    }

    /// <summary>Call once the window is up: with auto-connect on, looks for the last scope.</summary>
    public void Start()
    {
        _autoConnectArmed = AutoConnect;
        if (AutoConnect)
        {
            LookForLastScope();
        }
    }

    /// <summary>Nothing will connect by itself: no remembered scope, or auto-connect off.</summary>
    public bool ShouldPickScope => _preferences.LastScope is null || !AutoConnect;

    /// <summary>The connection dialog opened: scans for scopes, unless connected or already scanning.</summary>
    public void ConnectionDialogOpened()
    {
        if (State == ConnectionState.Disconnected && !IsScanning)
        {
            StartScan();
        }
    }

    /// <summary>The connection dialog closed: stops scanning, unless auto-connect is still looking for the last scope.</summary>
    public void ConnectionDialogClosed()
    {
        if (IsScanning && !(_autoConnectArmed && _preferences.LastScope is not null))
        {
            StopScan();
        }
    }

    private void LookForLastScope()
    {
        if (_preferences.LastScope is not { } address || State != ConnectionState.Disconnected)
        {
            return;
        }

        if (!IsScanning)
        {
            StartScan();
        }
        else if (_devicesByAddress.TryGetValue(address, out var seen) && seen.IsAeroscope)
        {
            TryAutoConnect(seen);
            return;
        }

        Status = $"Looking for {BluetoothAddress.Format(address)}...";
    }

    private void TryAutoConnect(DeviceItem device)
    {
        if (_autoConnectArmed && device.IsAeroscope && device.Address == _preferences.LastScope && State == ConnectionState.Disconnected)
        {
            AddLog(LogEntry.Message(LogKind.Info, "The last scope is in range; connecting"));
            SelectedDevice = device;
            _ = ConnectCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private void ToggleScan()
    {
        if (IsScanning)
        {
            StopScan();
        }
        else
        {
            StartScan();
        }
    }

    private void StopScan()
    {
        _scanner.Stop();
        IsScanning = false;
        if (State == ConnectionState.Disconnected)
        {
            Status = "Not connected";
        }
    }

    private void StartScan()
    {
        Devices.Clear();
        _devicesByAddress.Clear();
        _scanner.Start();
        IsScanning = true;
        AddLog(LogEntry.Message(LogKind.Info, "Scanning for BLE devices..."));
    }

    private void SavePreferences(Preferences preferences)
    {
        _preferences = preferences;
        try
        {
            _preferencesStore.Save(preferences);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog(LogEntry.Message(LogKind.Error, $"Could not save the preferences: {ex.Message}"));
        }
    }

    private bool CanConnect() => SelectedDevice is not null && State == ConnectionState.Disconnected;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var device = SelectedDevice!;
        _autoConnectArmed = false; // one try; after that the user connects
        if (IsScanning)
        {
            // Scanning competes with the connection for radio time.
            _scanner.Stop();
            IsScanning = false;
        }

        State = ConnectionState.Connecting;
        Status = $"Connecting to {device.DisplayName}...";
        AddLog(LogEntry.Message(LogKind.Info, $"Connecting to {device.DisplayName} ({device.AddressText})"));

        try
        {
            var connection = await _connect(device.Address);
            connection.OutPacketReceived += (_, bytes) => _post(() => OnOutPacket(bytes));
            connection.DataPacketReceived += (_, bytes) =>
            {
                // Stamp on arrival: queued UI work must not bunch frames up (roll mode reads that as a burst).
                var received = _time.GetUtcNow();
                _post(() => OnDataPacket(bytes, received));
            };
            connection.ConnectionChanged += (_, connected) => _post(() => OnConnectionChanged(connected));
            connection.Diagnostic += (_, message) => _post(() => AddLog(LogEntry.Message(LogKind.Error, message)));
            _connection = connection;
            _frameAssembler.Reset();
            Scope.Reset();

            foreach (var characteristic in connection.Characteristics)
            {
                Characteristics.Add(characteristic);
                AddLog(LogEntry.Message(LogKind.Info, $"Characteristic {characteristic.Uuid} ({characteristic.Role}): {characteristic.Properties}"));
            }

            State = ConnectionState.Connected;
            Status = $"Connected to {connection.Name}";
            AddLog(LogEntry.Message(LogKind.Info, "Connected; notifications enabled on Scope Out and Scope Data"));
            if (_preferences.LastScope != device.Address)
            {
                SavePreferences(_preferences with { LastScope = device.Address });
            }

            // Same start-up queries as the Android app; the scope answers "PF" once its FPGA is ready.
            await SendCommandAsync("QTI");
            await SendCommandAsync("QP");
        }
        catch (Exception ex)
        {
            State = ConnectionState.Disconnected;
            Status = "Connection failed";
            AddLog(LogEntry.Message(LogKind.Error, ex.Message));
        }
    }

    private bool CanDisconnect() => State is ConnectionState.Connected or ConnectionState.Reconnecting;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void Disconnect()
    {
        _connection?.Dispose();
        _connection = null;
        Characteristics.Clear();
        Acquisition.OnDisconnected();
        State = ConnectionState.Disconnected;
        Status = "Not connected";
        AddLog(LogEntry.Message(LogKind.Info, "Disconnected"));
    }

    private bool IsConnected() => State == ConnectionState.Connected;

    /// <summary>Sends an ASCII command such as "QVR" or "R" to Scope In.</summary>
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task SendCommandAsync(string command) => WriteAsync(AeroscopeGatt.ScopeIn, Encoding.ASCII.GetBytes(command), LogKind.In);

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task SendCustomCommandAsync() => SendCommandAsync(CustomCommand);

    /// <summary>Writes the FPGA registers for the current acquisition settings.</summary>
    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task SendSettingsAsync() => WriteAsync(AeroscopeGatt.ScopeState, Acquisition.Settings.ToRegisters().ToPacket(), LogKind.State);

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    private async Task WriteAsync(ushort characteristic, byte[] payload, LogKind kind)
    {
        if (_connection is null)
        {
            return;
        }

        if (payload.Length > AeroscopeGatt.PacketLength)
        {
            AddLog(LogEntry.Message(LogKind.Error, $"Command is {payload.Length} bytes; the maximum is {AeroscopeGatt.PacketLength}"));
            return;
        }

        var packet = new byte[AeroscopeGatt.PacketLength];
        payload.CopyTo(packet, 0);
        try
        {
            await _connection.WriteAsync(characteristic, packet);
            AddLog(LogEntry.Packet(kind, packet));
        }
        catch (Exception ex)
        {
            AddLog(LogEntry.Message(LogKind.Error, ex.Message));
        }
    }

    private void OnOutPacket(byte[] packet)
    {
        AddLog(LogEntry.Packet(LogKind.Out, packet));
        var message = ScopeOutMessage.Parse(packet);
        Scope.Apply(message);
        AddLog(LogEntry.Message(LogKind.Info, message.ToString()));
        if (message is PowerStateMessage power)
        {
            _ = Acquisition.OnPowerChangedAsync(power.FullyOn);
        }
        else if (message is ButtonPressedMessage)
        {
            _ = Acquisition.OnButtonPressedAsync();
        }
    }

    private void OnDataPacket(byte[] packet, DateTimeOffset received)
    {
        _lastDataReceived = received;
        Acquisition.OnDataPacket(packet, received);
        if (packet[0] != 0)
        {
            _frameStarted = received; // a frame's first packet starts with its size code
        }

        if (LogDataPackets)
        {
            AddLog(LogEntry.Packet(LogKind.Data, packet));
        }

        _frameAssembler.AddPacket(packet);
    }

    private void OnFrame(ScopeFrame frame)
    {
        Scope.Apply(frame);
        AddLog(LogEntry.Message(LogKind.Info, $"Frame: {Scope.LastFrame}"));
        _ = Acquisition.OnFrameAsync(frame, _lastDataReceived, _frameStarted);
    }

    private void OnAdvertisement(ScopeAdvertisement ad)
    {
        if (!_devicesByAddress.TryGetValue(ad.Address, out var item))
        {
            item = new DeviceItem(ad.Address);
            _devicesByAddress.Add(ad.Address, item);
            Devices.Add(item);
            if (SelectedDevice is null && ad.Address == _preferences.LastScope)
            {
                SelectedDevice = item;
            }
        }

        // Name and service UUID can arrive in separate packets (advert vs scan response).
        if (!string.IsNullOrEmpty(ad.Name))
        {
            item.Name = ad.Name;
        }

        item.Rssi = ad.Rssi;
        if (ad.AdvertisesScopeService && !item.IsAeroscope)
        {
            item.IsAeroscope = true;
            DevicesView.Refresh();
            AddLog(LogEntry.Message(LogKind.Info, $"Found Aeroscope {item.DisplayName} ({item.AddressText})"));
            TryAutoConnect(item);
        }
    }

    private void OnScanStopped(BluetoothError error)
    {
        IsScanning = false;
        if (error != BluetoothError.Success)
        {
            AddLog(LogEntry.Message(LogKind.Error, $"Scanning stopped: {error}. Is Bluetooth switched on?"));
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        if (_connection is null)
        {
            return;
        }

        if (!connected)
        {
            // Keep the run mode: the scope re-sends "PF" after reconnecting, which pushes settings and resumes running.
            _ = Acquisition.OnPowerChangedAsync(false);
        }

        State = connected ? ConnectionState.Connected : ConnectionState.Reconnecting;
        Status = connected ? $"Connected to {_connection.Name}" : "Connection lost, waiting for Windows to reconnect...";
        AddLog(LogEntry.Message(connected ? LogKind.Info : LogKind.Error, connected ? "Reconnected" : "Connection lost"));
    }

    private void AddLog(LogEntry entry)
    {
        Log.Add(entry);
        if (Log.Count > MaxLogEntries)
        {
            Log.RemoveAt(0);
        }
    }

    public void Dispose()
    {
        _scanner.Dispose();
        _connection?.Dispose();
    }

    private static Action<Action> PostTo(Dispatcher dispatcher) => action => dispatcher.BeginInvoke(action);
}
