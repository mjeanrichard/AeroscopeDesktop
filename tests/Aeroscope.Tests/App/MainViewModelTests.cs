using Aeroscope.App;
using Aeroscope.App.ViewModels;
using Aeroscope.Core.Bluetooth;
using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;
using Windows.Devices.Bluetooth;

namespace Aeroscope.Tests.App;

[UseInvariantCulture]
public class MainViewModelTests
{
    private const ulong ScopeAddress = 0xC6A7C8D11AB1;

    private readonly FakeScopeScanner _scanner = new();
    private readonly FakeScopeLink _link = new();
    private readonly FakePreferencesStore _preferences = new();
    private readonly List<ulong> _connectedAddresses = [];
    private Exception? _connectFailure;
    private readonly MainViewModel _viewModel;

    public MainViewModelTests()
    {
        // Run posted actions inline: the tests play the role of the UI thread.
        _viewModel = new MainViewModel(_scanner, ConnectAsync, action => action(), _preferences);
    }

    private Task<IScopeLink> ConnectAsync(ulong address)
    {
        _connectedAddresses.Add(address);
        return _connectFailure is null ? Task.FromResult<IScopeLink>(_link) : Task.FromException<IScopeLink>(_connectFailure);
    }

    private IEnumerable<DeviceItem> VisibleDevices => _viewModel.DevicesView.Cast<DeviceItem>();

    private IEnumerable<string> LogTexts => _viewModel.Log.Select(e => e.Text);

    private async Task ConnectToScopeAsync()
    {
        _scanner.RaiseAdvertisement(new ScopeAdvertisement(ScopeAddress, "Aeroscope", -68, AdvertisesScopeService: true));
        _viewModel.SelectedDevice = _viewModel.Devices.Single(d => d.Address == ScopeAddress);
        await _viewModel.ConnectCommand.ExecuteAsync(null);
        _link.Writes.Clear();
    }

    // Scanning

    [Fact]
    public void Scan_button_toggles_the_scanner()
    {
        _viewModel.ToggleScanCommand.Execute(null);
        Assert.True(_viewModel.IsScanning);
        Assert.Equal("Stop scan", _viewModel.ScanButtonText);
        Assert.Equal(1, _scanner.StartCount);

        _viewModel.ToggleScanCommand.Execute(null);
        Assert.False(_viewModel.IsScanning);
        Assert.Equal("Scan", _viewModel.ScanButtonText);
        Assert.Equal(1, _scanner.StopCount);
    }

    [Fact]
    public void Starting_a_scan_clears_old_devices()
    {
        _scanner.RaiseAdvertisement(new ScopeAdvertisement(1, "Old", -50, true));

        _viewModel.ToggleScanCommand.Execute(null);

        Assert.Empty(_viewModel.Devices);
    }

    [Fact]
    public void Only_aeroscopes_are_listed_unless_all_devices_are_requested()
    {
        _scanner.RaiseAdvertisement(new ScopeAdvertisement(1, "Headphones", -60, false));
        _scanner.RaiseAdvertisement(new ScopeAdvertisement(ScopeAddress, "Aeroscope", -68, true));

        Assert.Equal([ScopeAddress], VisibleDevices.Select(d => d.Address));

        _viewModel.ShowAllDevices = true;

        Assert.Equal(2, VisibleDevices.Count());
    }

    [Fact]
    public void Advertisement_and_scan_response_are_merged()
    {
        // Seen on the real scope: one packet may carry the name, another the service UUID.
        _scanner.RaiseAdvertisement(new ScopeAdvertisement(ScopeAddress, "Aeroscope", -70, false));
        Assert.Empty(VisibleDevices);

        _scanner.RaiseAdvertisement(new ScopeAdvertisement(ScopeAddress, "", -66, true));

        var device = Assert.Single(VisibleDevices);
        Assert.Equal("Aeroscope", device.Name);
        Assert.Equal(-66, device.Rssi);
        Assert.True(device.IsAeroscope);
        Assert.Contains("Found Aeroscope Aeroscope (C6:A7:C8:D1:1A:B1)", LogTexts);
    }

    [Fact]
    public void Scanner_stopping_with_an_error_is_reported()
    {
        _viewModel.ToggleScanCommand.Execute(null);

        _scanner.RaiseStopped(BluetoothError.RadioNotAvailable);

        Assert.False(_viewModel.IsScanning);
        Assert.Contains("Scanning stopped: RadioNotAvailable. Is Bluetooth switched on?", LogTexts);
    }

    [Fact]
    public void Scanner_stopping_normally_is_not_an_error()
    {
        _viewModel.ToggleScanCommand.Execute(null);

        _scanner.RaiseStopped(BluetoothError.Success);

        Assert.False(_viewModel.IsScanning);
        Assert.DoesNotContain(_viewModel.Log, e => e.Kind == LogKind.Error);
    }

    // Connecting

    [Fact]
    public void Connect_needs_a_selected_device()
    {
        Assert.False(_viewModel.ConnectCommand.CanExecute(null));

        _scanner.RaiseAdvertisement(new ScopeAdvertisement(ScopeAddress, "Aeroscope", -68, true));
        _viewModel.SelectedDevice = _viewModel.Devices[0];

        Assert.True(_viewModel.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task Connect_opens_the_link_and_sends_the_android_startup_queries()
    {
        _viewModel.ToggleScanCommand.Execute(null);
        _scanner.RaiseAdvertisement(new ScopeAdvertisement(ScopeAddress, "Aeroscope", -68, true));
        _viewModel.SelectedDevice = _viewModel.Devices[0];

        await _viewModel.ConnectCommand.ExecuteAsync(null);

        Assert.Equal([ScopeAddress], _connectedAddresses);
        Assert.Equal(1, _scanner.StopCount); // scanning stops before connecting
        Assert.False(_viewModel.IsScanning);
        Assert.Equal(ConnectionState.Connected, _viewModel.State);
        Assert.Equal("Connected to Aeroscope", _viewModel.Status);
        Assert.Equal(FakeScopeLink.RealCharacteristics, _viewModel.Characteristics);
        Assert.Equal(
            [(AeroscopeGatt.ScopeIn, Captures.Packet("51 54 49")), (AeroscopeGatt.ScopeIn, Captures.Packet("51 50")), (AeroscopeGatt.ScopeIn, Captures.Packet("51 43")),
             (AeroscopeGatt.ScopeIn, Captures.Packet("51 56 52"))],
            _link.Writes.Select(w => (w.Characteristic, w.Packet)));
        Assert.False(_viewModel.ConnectCommand.CanExecute(null));
        Assert.True(_viewModel.DisconnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task Failed_connect_is_reported_and_can_be_retried()
    {
        _connectFailure = new ScopeConnectionException("Service discovery failed: Unreachable.");

        _scanner.RaiseAdvertisement(new ScopeAdvertisement(ScopeAddress, "Aeroscope", -68, true));
        _viewModel.SelectedDevice = _viewModel.Devices[0];
        await _viewModel.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionState.Disconnected, _viewModel.State);
        Assert.Equal("Connection failed", _viewModel.Status);
        Assert.Contains("Service discovery failed: Unreachable.", LogTexts);
        Assert.True(_viewModel.ConnectCommand.CanExecute(null));
    }

    // Auto-connect

    private readonly ScopeAdvertisement _scopeAdvertisement = new(ScopeAddress, "Aeroscope", -68, AdvertisesScopeService: true);

    private MainViewModel RememberingTheScope(bool autoConnect = true)
    {
        var preferences = new FakePreferencesStore(new Preferences { LastScope = ScopeAddress, AutoConnect = autoConnect });
        return new MainViewModel(_scanner, ConnectAsync, action => action(), preferences);
    }

    [Fact]
    public async Task Connecting_remembers_the_scope()
    {
        await ConnectToScopeAsync();

        Assert.Equal(ScopeAddress, _preferences.Stored.LastScope);
        Assert.True(_preferences.Stored.AutoConnect); // on by default
    }

    [Fact]
    public void Without_a_remembered_scope_start_up_does_nothing()
    {
        _viewModel.Start();

        Assert.Equal(0, _scanner.StartCount);
        Assert.Equal("Not connected", _viewModel.Status);
    }

    [Fact]
    public void Start_up_looks_for_the_last_scope()
    {
        var viewModel = RememberingTheScope();

        viewModel.Start();

        Assert.True(viewModel.IsScanning);
        Assert.Equal("Looking for C6:A7:C8:D1:1A:B1...", viewModel.Status);
    }

    [Fact]
    public void The_last_scope_is_connected_to_once_it_advertises_the_scope_service()
    {
        var viewModel = RememberingTheScope();
        viewModel.Start();

        _scanner.RaiseAdvertisement(new ScopeAdvertisement(1, "Headphones", -60, false));
        _scanner.RaiseAdvertisement(new ScopeAdvertisement(ScopeAddress, "Aeroscope", -68, AdvertisesScopeService: false)); // name first
        Assert.Empty(_connectedAddresses);
        Assert.Equal(ScopeAddress, viewModel.SelectedDevice?.Address); // already picked in the list

        _scanner.RaiseAdvertisement(_scopeAdvertisement);
        _scanner.RaiseAdvertisement(_scopeAdvertisement);

        Assert.Equal([ScopeAddress], _connectedAddresses);
        Assert.Equal(ConnectionState.Connected, viewModel.State);
        Assert.False(viewModel.IsScanning);
    }

    [Fact]
    public void Another_aeroscope_is_not_connected_to()
    {
        var viewModel = RememberingTheScope();
        viewModel.Start();

        _scanner.RaiseAdvertisement(new ScopeAdvertisement(ScopeAddress + 1, "Aeroscope", -68, AdvertisesScopeService: true));

        Assert.Empty(_connectedAddresses);
    }

    [Fact]
    public void Auto_connect_off_does_not_look_for_the_scope()
    {
        var viewModel = RememberingTheScope(autoConnect: false);

        viewModel.Start();
        viewModel.ToggleScanCommand.Execute(null);
        _scanner.RaiseAdvertisement(_scopeAdvertisement);

        Assert.False(viewModel.AutoConnect);
        Assert.Empty(_connectedAddresses);
    }

    [Fact]
    public void A_failed_auto_connect_is_not_retried()
    {
        _connectFailure = new ScopeConnectionException("Service discovery failed: Unreachable.");
        var viewModel = RememberingTheScope();
        viewModel.Start();

        _scanner.RaiseAdvertisement(_scopeAdvertisement);
        viewModel.ToggleScanCommand.Execute(null);
        _scanner.RaiseAdvertisement(_scopeAdvertisement);

        Assert.Equal([ScopeAddress], _connectedAddresses);
        Assert.Equal("Connection failed", viewModel.Status);
    }

    [Fact]
    public void No_reconnect_after_the_user_disconnects()
    {
        var viewModel = RememberingTheScope();
        viewModel.Start();
        _scanner.RaiseAdvertisement(_scopeAdvertisement);

        viewModel.DisconnectCommand.Execute(null);
        viewModel.ToggleScanCommand.Execute(null);
        _scanner.RaiseAdvertisement(_scopeAdvertisement);

        Assert.Equal([ScopeAddress], _connectedAddresses);
        Assert.Equal(ConnectionState.Disconnected, viewModel.State);
    }

    [Fact]
    public void Turning_auto_connect_on_is_saved_and_looks_for_the_scope()
    {
        var preferences = new FakePreferencesStore(new Preferences { LastScope = ScopeAddress, AutoConnect = false });
        var viewModel = new MainViewModel(_scanner, ConnectAsync, action => action(), preferences);
        viewModel.Start();
        viewModel.ToggleScanCommand.Execute(null);
        _scanner.RaiseAdvertisement(_scopeAdvertisement); // already in the list

        viewModel.AutoConnect = true;

        Assert.True(preferences.Stored.AutoConnect);
        Assert.Equal([ScopeAddress], _connectedAddresses);
    }

    [Fact]
    public void Turning_auto_connect_off_is_saved()
    {
        var preferences = new FakePreferencesStore(new Preferences { LastScope = ScopeAddress });
        var viewModel = new MainViewModel(_scanner, ConnectAsync, action => action(), preferences);
        viewModel.Start();

        viewModel.AutoConnect = false;
        _scanner.RaiseAdvertisement(_scopeAdvertisement);

        Assert.Empty(_connectedAddresses);
        Assert.Equal(new Preferences { LastScope = ScopeAddress, AutoConnect = false }, preferences.Stored);
    }

    [Fact]
    public void Loading_the_preferences_saves_nothing()
    {
        var preferences = new FakePreferencesStore(new Preferences { LastScope = ScopeAddress, AutoConnect = false });

        new MainViewModel(_scanner, ConnectAsync, action => action(), preferences).Start();

        Assert.Equal(0, preferences.SaveCount);
    }

    // Connection dialog

    [Fact]
    public void A_scope_must_be_picked_without_a_remembered_one_or_with_auto_connect_off()
    {
        Assert.True(_viewModel.ShouldPickScope);
        Assert.False(RememberingTheScope().ShouldPickScope);
        Assert.True(RememberingTheScope(autoConnect: false).ShouldPickScope);
    }

    [Fact]
    public void Opening_the_dialog_scans_and_closing_it_stops()
    {
        _viewModel.ConnectionDialogOpened();
        Assert.True(_viewModel.IsScanning);

        _viewModel.ConnectionDialogClosed();
        Assert.False(_viewModel.IsScanning);
        Assert.Equal(1, _scanner.StopCount);
    }

    [Fact]
    public async Task Opening_the_dialog_while_connected_does_not_scan()
    {
        await ConnectToScopeAsync();

        _viewModel.ConnectionDialogOpened();

        Assert.False(_viewModel.IsScanning);
    }

    [Fact]
    public void Closing_the_dialog_keeps_looking_for_the_last_scope()
    {
        var viewModel = RememberingTheScope();
        viewModel.Start();

        viewModel.ConnectionDialogOpened();
        viewModel.ConnectionDialogClosed();
        _scanner.RaiseAdvertisement(_scopeAdvertisement);

        Assert.Equal(1, _scanner.StartCount);
        Assert.Equal([ScopeAddress], _connectedAddresses);
    }

    [Fact]
    public async Task Disconnect_closes_the_link()
    {
        await ConnectToScopeAsync();

        _viewModel.DisconnectCommand.Execute(null);

        Assert.True(_link.IsDisposed);
        Assert.Empty(_viewModel.Characteristics);
        Assert.Equal(ConnectionState.Disconnected, _viewModel.State);
        Assert.Equal("Not connected", _viewModel.Status);
        Assert.False(_viewModel.SendCommandCommand.CanExecute("R"));
    }

    [Fact]
    public async Task Link_drop_and_recovery_update_the_state()
    {
        await ConnectToScopeAsync();

        _link.RaiseConnectionChanged(false);
        Assert.Equal(ConnectionState.Reconnecting, _viewModel.State);
        Assert.False(_viewModel.SendCommandCommand.CanExecute("R"));
        Assert.True(_viewModel.DisconnectCommand.CanExecute(null));

        _link.RaiseConnectionChanged(true);
        Assert.Equal(ConnectionState.Connected, _viewModel.State);
        Assert.Equal(["Connection lost", "Reconnected"], LogTexts.Where(t => t.Contains("onnect")).TakeLast(2));
    }

    [Fact]
    public async Task Events_after_disconnect_are_ignored()
    {
        await ConnectToScopeAsync();
        _viewModel.DisconnectCommand.Execute(null);

        _link.RaiseConnectionChanged(false);

        Assert.Equal(ConnectionState.Disconnected, _viewModel.State);
    }

    [Fact]
    public async Task Link_diagnostics_are_logged_as_errors()
    {
        await ConnectToScopeAsync();

        _link.RaiseDiagnostic("Re-subscribing after reconnect failed");

        Assert.Equal(LogKind.Error, _viewModel.Log.Last().Kind);
    }

    // Traffic

    [Fact]
    public async Task Real_scope_replies_update_the_status_and_are_logged_decoded()
    {
        await ConnectToScopeAsync();

        foreach (var packet in Captures.Packets(Captures.Startup, CaptureChannel.Out))
        {
            _link.RaiseOut(packet);
        }

        Assert.Equal("Fully on", _viewModel.Scope.Power);
        Assert.Equal("1000057", _viewModel.Scope.SerialNumber);
        Assert.Contains("Version: FPGA rev 5, firmware rev 5, hardware id 1, serial 1000057", LogTexts);
        Assert.Equal(6, _viewModel.Log.Count(e => e.Kind == LogKind.Out));
    }

    [Fact]
    public async Task Real_frames_are_assembled_without_logging_every_packet()
    {
        await ConnectToScopeAsync();

        foreach (var packet in Captures.Packets(Captures.FramesDefault, CaptureChannel.Data))
        {
            _link.RaiseData(packet);
        }

        Assert.Equal(5, _viewModel.Scope.FramesReceived);
        Assert.Equal(5, LogTexts.Count(t => t.StartsWith("Frame: 512 samples")));
        Assert.DoesNotContain(_viewModel.Log, e => e.Kind == LogKind.Data);
    }

    [Fact]
    public async Task Raw_data_packets_are_logged_on_request()
    {
        await ConnectToScopeAsync();
        _viewModel.LogDataPackets = true;

        foreach (var packet in Captures.Packets(Captures.RawSingle, CaptureChannel.Data))
        {
            _link.RaiseData(packet);
        }

        Assert.Equal(54, _viewModel.Log.Count(e => e.Kind == LogKind.Data));
    }

    [Fact]
    public async Task Dropped_frames_are_logged()
    {
        await ConnectToScopeAsync();

        _link.RaiseData(Captures.Packet("00 8B"));

        Assert.Contains("Frame dropped: Continuation packet without a start-of-frame packet", LogTexts);
    }

    // Acquisition

    [Fact]
    public async Task Power_on_message_readies_acquisition_and_writes_its_settings()
    {
        await ConnectToScopeAsync();

        _link.RaiseOut(Captures.Packet("50 46"));

        Assert.True(_viewModel.Acquisition.IsReady);
        var write = Assert.Single(_link.Writes);
        Assert.Equal(AeroscopeGatt.ScopeState, write.Characteristic);
        Assert.Equal(_viewModel.Acquisition.Settings.ToRegisters().ToPacket(), write.Packet);
        Assert.Equal(LogKind.State, _viewModel.Log.Last().Kind);
    }

    [Fact]
    public async Task Frames_reach_the_waveform()
    {
        await ConnectToScopeAsync();
        _link.RaiseOut(Captures.Packet("50 46"));
        await _viewModel.Acquisition.RunCommand.ExecuteAsync(null);

        foreach (var packet in Captures.Packets(Captures.TouchHum, CaptureChannel.Data))
        {
            _link.RaiseData(packet);
        }

        Assert.Equal(512, Assert.Single(_viewModel.Acquisition.Screen!.Traces).Volts.Length);
        Assert.Equal("1.19 V", _viewModel.Acquisition.PeakToPeakText); // last frame: 38 codes, converted with the default 1 V/div
    }

    [Fact]
    public async Task Link_drop_pauses_acquisition_and_power_on_resumes_it()
    {
        await ConnectToScopeAsync();
        _link.RaiseOut(Captures.Packet("50 46"));
        await _viewModel.Acquisition.RunCommand.ExecuteAsync(null);

        _link.RaiseConnectionChanged(false);
        Assert.False(_viewModel.Acquisition.IsReady);
        Assert.Equal(RunMode.Running, _viewModel.Acquisition.RunMode);

        _link.Writes.Clear();
        _link.RaiseConnectionChanged(true);
        _link.RaiseOut(Captures.Packet("50 46"));

        Assert.Equal([AeroscopeGatt.ScopeState, AeroscopeGatt.ScopeIn], _link.Writes.Select(w => w.Characteristic));
        Assert.Equal((byte)'R', _link.Writes[1].Packet[0]);
    }

    [Theory]
    [InlineData(Captures.ButtonPresses, 5)] // two of them 0.8 s apart, one held about 3 s
    [InlineData(Captures.Button, 26)] // quick taps 0.26-0.7 s apart
    public async Task Every_device_button_press_toggles_run(string capture, int presses)
    {
        await ConnectToScopeAsync();
        _link.Writes.Clear();

        foreach (var packet in Captures.Packets(capture, CaptureChannel.Out))
        {
            _link.RaiseOut(packet);
        }

        var expected = Enumerable.Range(0, presses).SelectMany(i => i % 2 == 0 ? "R" : "SX");
        Assert.Equal(expected, _link.Writes.Where(w => w.Characteristic == AeroscopeGatt.ScopeIn).Select(w => (char)w.Packet[0]));
        Assert.Equal(presses % 2 == 1 ? RunMode.Running : RunMode.Stopped, _viewModel.Acquisition.RunMode);
    }

    [Fact]
    public async Task Disconnect_stops_acquisition()
    {
        await ConnectToScopeAsync();
        _link.RaiseOut(Captures.Packet("50 46"));
        await _viewModel.Acquisition.RunCommand.ExecuteAsync(null);

        _viewModel.DisconnectCommand.Execute(null);

        Assert.False(_viewModel.Acquisition.IsReady);
        Assert.Equal(RunMode.Stopped, _viewModel.Acquisition.RunMode);
    }

    [Fact]
    public async Task Send_settings_uses_the_acquisition_settings()
    {
        _viewModel.Acquisition.Range = VoltageRange.Range5V;
        await ConnectToScopeAsync();

        await _viewModel.SendSettingsCommand.ExecuteAsync(null);

        Assert.Equal(0x84, Assert.Single(_link.Writes).Packet[3]); // DC, 5 V/div
    }

    // Commands

    [Fact]
    public void Commands_are_disabled_until_connected()
    {
        Assert.False(_viewModel.SendCommandCommand.CanExecute("R"));
        Assert.False(_viewModel.SendSettingsCommand.CanExecute(null));
        Assert.False(_viewModel.SendCustomCommandCommand.CanExecute(null));
        Assert.False(_viewModel.CalibrateCommand.CanExecute(null));
        Assert.False(_viewModel.ClearCalibrationCommand.CanExecute(null));
    }

    // Calibration

    [Fact]
    public async Task Calibrate_sends_cf_and_shows_the_reply()
    {
        await ConnectToScopeAsync();

        await _viewModel.CalibrateCommand.ExecuteAsync(null);

        Assert.Equal(Captures.Packet("43 46"), Assert.Single(_link.Writes).Packet);
        Assert.Equal("Calibrating...", _viewModel.Scope.Calibration);

        _link.RaiseOut(Captures.Packet("43 00 00 00 00 00 9C 00 A6 00 C1 00 E0 01 46 02 00 03 33")); // capture 27
        Assert.Equal("DAC offsets 100 mV 156, 200 mV 166, 500 mV 193, 1 V 224, 2 V 326, 5 V 512, 10 V 819", _viewModel.Scope.Calibration);
    }

    [Fact]
    public async Task Calibrate_stops_a_run_first()
    {
        await ConnectToScopeAsync();
        _link.RaiseOut(Captures.Packet("50 46"));
        await _viewModel.Acquisition.RunStopCommand.ExecuteAsync(null);
        _link.Writes.Clear();

        await _viewModel.CalibrateCommand.ExecuteAsync(null);

        Assert.Equal("SXC", new string(_link.Writes.Select(w => (char)w.Packet[0]).ToArray()));
        Assert.Equal(RunMode.Stopped, _viewModel.Acquisition.RunMode);
    }

    [Fact]
    public async Task Clearing_the_calibration_asks_for_it_again()
    {
        await ConnectToScopeAsync();

        await _viewModel.ClearCalibrationCommand.ExecuteAsync(null);
        _link.RaiseOut(Captures.Packet("43"));

        Assert.Equal([Captures.Packet("43 58"), Captures.Packet("51 43")], _link.Writes.Select(w => w.Packet));
        Assert.Equal("Not calibrated", _viewModel.Scope.Calibration);
    }

    [Fact]
    public async Task Command_buttons_write_ascii_to_scope_in_and_log_it()
    {
        await ConnectToScopeAsync();

        await _viewModel.SendCommandCommand.ExecuteAsync("R");

        var write = Assert.Single(_link.Writes);
        Assert.Equal(AeroscopeGatt.ScopeIn, write.Characteristic);
        Assert.Equal(Captures.Packet("52"), write.Packet);
        var logged = _viewModel.Log.Last();
        Assert.Equal(LogKind.In, logged.Kind);
        Assert.StartsWith("52 00", logged.Text);
    }

    [Fact]
    public async Task Send_settings_writes_the_default_settings_to_scope_state()
    {
        await ConnectToScopeAsync();

        await _viewModel.SendSettingsCommand.ExecuteAsync(null);

        var write = Assert.Single(_link.Writes);
        Assert.Equal(AeroscopeGatt.ScopeState, write.Characteristic);
        Assert.Equal(new ScopeSettings().ToRegisters().ToPacket(), write.Packet);
        Assert.Equal(LogKind.State, _viewModel.Log.Last().Kind);
    }

    [Fact]
    public async Task Custom_command_is_sent_as_typed()
    {
        await ConnectToScopeAsync();
        _viewModel.CustomCommand = "QE";

        await _viewModel.SendCustomCommandCommand.ExecuteAsync(null);

        Assert.Equal(Captures.Packet("51 45"), Assert.Single(_link.Writes).Packet);
    }

    [Fact]
    public async Task Too_long_command_is_rejected_without_writing()
    {
        await ConnectToScopeAsync();
        _viewModel.CustomCommand = "N" + new string('x', 20);

        await _viewModel.SendCustomCommandCommand.ExecuteAsync(null);

        Assert.Empty(_link.Writes);
        Assert.Contains("Command is 21 bytes; the maximum is 20", LogTexts);
    }

    [Fact]
    public async Task Write_failures_are_logged()
    {
        await ConnectToScopeAsync();
        _link.WriteFailure = new ScopeConnectionException("Write to Scope In failed: Unreachable.");

        await _viewModel.SendCommandCommand.ExecuteAsync("R");

        Assert.Equal(LogKind.Error, _viewModel.Log.Last().Kind);
        Assert.Equal("Write to Scope In failed: Unreachable.", _viewModel.Log.Last().Text);
    }

    // Log and lifetime

    [Fact]
    public async Task Log_keeps_the_newest_2000_entries()
    {
        await ConnectToScopeAsync();
        _viewModel.ClearLogCommand.Execute(null);

        for (var i = 0; i < 2005; i++)
        {
            _link.RaiseDiagnostic($"line {i}");
        }

        Assert.Equal(2000, _viewModel.Log.Count);
        Assert.Equal("line 5", _viewModel.Log[0].Text);
        Assert.Equal("line 2004", _viewModel.Log[^1].Text);
    }

    [Fact]
    public void Clear_log_empties_it()
    {
        _viewModel.ToggleScanCommand.Execute(null);

        _viewModel.ClearLogCommand.Execute(null);

        Assert.Empty(_viewModel.Log);
    }

    [Fact]
    public async Task Dispose_releases_scanner_and_link()
    {
        await ConnectToScopeAsync();

        _viewModel.Dispose();

        Assert.True(_scanner.IsDisposed);
        Assert.True(_link.IsDisposed);
    }
}
