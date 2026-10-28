using System.ComponentModel;
using System.Text;
using Aeroscope.App.ViewModels;
using Aeroscope.Core.Bluetooth;
using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.App;

[UseInvariantCulture]
public class AcquisitionViewModelTests
{
    private readonly List<(ushort Characteristic, byte[] Payload)> _writes = [];
    private TaskCompletionSource? _blockWrites;
    private readonly AcquisitionViewModel _viewModel;

    public AcquisitionViewModelTests()
    {
        _viewModel = new AcquisitionViewModel((characteristic, payload) =>
        {
            _writes.Add((characteristic, payload));
            return _blockWrites?.Task ?? Task.CompletedTask;
        });
    }

    private IEnumerable<string> Commands => _writes.Where(w => w.Characteristic == AeroscopeGatt.ScopeIn).Select(w => Encoding.ASCII.GetString(w.Payload));

    private IEnumerable<byte[]> SettingsWrites => _writes.Where(w => w.Characteristic == AeroscopeGatt.ScopeState).Select(w => w.Payload);

    private static ScopeFrame HumFrame()
    {
        var assembler = new FrameAssembler();
        ScopeFrame? first = null;
        assembler.FrameCompleted += (_, frame) => first ??= frame;
        foreach (var packet in Captures.Packets(Captures.TouchHum, CaptureChannel.Data))
        {
            assembler.AddPacket(packet);
        }

        return first!;
    }

    private async Task MakeReadyAsync()
    {
        await _viewModel.OnPowerChangedAsync(true);
        _writes.Clear();
    }

    private async Task RunningAsync()
    {
        await MakeReadyAsync();
        await _viewModel.RunCommand.ExecuteAsync(null);
        _writes.Clear();
    }

    [Fact]
    public void Starts_with_android_defaults_and_not_ready()
    {
        Assert.Equal(VoltageRange.Range1V, _viewModel.Range);
        Assert.Equal(TimeBase.Default, _viewModel.TimeBase);
        Assert.True(_viewModel.DcCoupled);
        Assert.True(_viewModel.AutoTrigger);
        Assert.Equal(TriggerEdge.Rising, _viewModel.TriggerEdge);
        Assert.False(_viewModel.IsReady);
        Assert.False(_viewModel.RunCommand.CanExecute(null));
        Assert.Equal(RunMode.Stopped, _viewModel.RunMode);
        Assert.Equal([TriggerEdge.Rising, TriggerEdge.Falling, TriggerEdge.Both], _viewModel.TriggerEdges);
        Assert.Same(VoltageRange.All, _viewModel.Ranges);
        Assert.Same(TimeBase.All, _viewModel.TimeBases);
    }

    [Fact]
    public void Settings_are_not_written_before_the_scope_is_ready()
    {
        _viewModel.Range = VoltageRange.Range2V;

        Assert.Empty(_writes);
        Assert.Equal(VoltageRange.Range2V, _viewModel.Settings.Range);
    }

    [Fact]
    public async Task Power_on_writes_the_current_settings()
    {
        _viewModel.Range = VoltageRange.Range2V;

        await _viewModel.OnPowerChangedAsync(true);

        Assert.True(_viewModel.IsReady);
        Assert.True(_viewModel.RunCommand.CanExecute(null));
        Assert.Equal([_viewModel.Settings.ToRegisters().ToPacket()], SettingsWrites);
        Assert.Empty(Commands); // was stopped, stays stopped
    }

    [Fact]
    public async Task Power_on_resumes_running_after_a_reconnect()
    {
        await MakeReadyAsync();
        await _viewModel.RunCommand.ExecuteAsync(null);

        await _viewModel.OnPowerChangedAsync(false);
        Assert.False(_viewModel.IsReady);
        Assert.Equal(RunMode.Running, _viewModel.RunMode);

        _writes.Clear();
        await _viewModel.OnPowerChangedAsync(true);

        Assert.Single(SettingsWrites);
        Assert.Equal(["R"], Commands);
    }

    [Fact]
    public async Task Each_setting_change_is_written_when_ready()
    {
        await MakeReadyAsync();

        _viewModel.Range = VoltageRange.Range100mV;
        _viewModel.DcCoupled = false;
        _viewModel.TimeBase = TimeBase.All.Single(t => t.Name == "5 ms");
        _viewModel.TriggerEdge = TriggerEdge.Falling;
        _viewModel.AutoTrigger = false;
        _viewModel.NoiseReject = true;
        _viewModel.TriggerLevelVolts = 0.1;
        _viewModel.OffsetVolts = 0.2;

        Assert.Equal(8, SettingsWrites.Count());
        var last = SettingsWrites.Last();
        Assert.Equal(_viewModel.Settings.ToRegisters().ToPacket(), last);
        Assert.Equal(0x24, last[0]); // falling + noise reject, no auto
        Assert.Equal(0x60, last[3]); // AC, 100 mV
        Assert.Equal(0x0C, last[4]); // 5 ms/div
    }

    [Fact]
    public async Task Setting_the_same_value_writes_nothing()
    {
        await MakeReadyAsync();

        _viewModel.Range = VoltageRange.Range1V;

        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Writes_are_coalesced_while_one_is_in_flight()
    {
        await MakeReadyAsync();
        _blockWrites = new TaskCompletionSource();

        _viewModel.OffsetVolts = 0.1;
        _viewModel.OffsetVolts = 0.2;
        _viewModel.OffsetVolts = 0.3;
        Assert.Single(_writes);

        var release = _blockWrites;
        _blockWrites = null;
        release.SetResult();

        Assert.Equal(2, _writes.Count);
        Assert.Equal(_viewModel.Settings.ToRegisters().ToPacket(), _writes[1].Payload); // the latest value
    }

    [Fact]
    public void Changing_a_setting_refreshes_every_binding()
    {
        var changed = new List<string?>();
        ((INotifyPropertyChanged)_viewModel).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _viewModel.OffsetVolts = 1;

        Assert.Contains(string.Empty, changed);
    }

    [Fact]
    public async Task Run_and_stop()
    {
        await MakeReadyAsync();

        await _viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(RunMode.Running, _viewModel.RunMode);

        await _viewModel.StopCommand.ExecuteAsync(null);
        Assert.Equal(RunMode.Stopped, _viewModel.RunMode);

        Assert.Equal(["R", "S", "X"], Commands);
    }

    [Fact]
    public async Task Single_runs_until_the_first_frame_then_stops()
    {
        await MakeReadyAsync();

        await _viewModel.SingleCommand.ExecuteAsync(null);
        Assert.Equal(RunMode.Single, _viewModel.RunMode);
        Assert.Equal(["S", "X", "R"], Commands);

        await _viewModel.OnFrameAsync(HumFrame());

        Assert.Equal(RunMode.Stopped, _viewModel.RunMode);
        Assert.NotNull(_viewModel.Waveform);
        Assert.Equal(["S", "X", "R", "S", "X"], Commands);
    }

    [Fact]
    public async Task Frames_while_running_only_update_the_waveform()
    {
        await MakeReadyAsync();
        await _viewModel.RunCommand.ExecuteAsync(null);
        _writes.Clear();

        await _viewModel.OnFrameAsync(HumFrame());

        Assert.Empty(_writes);
        Assert.Equal(RunMode.Running, _viewModel.RunMode);
    }

    [Fact]
    public async Task Readouts_from_a_real_hum_frame()
    {
        Assert.Equal("–", _viewModel.PeakToPeakText);
        await RunningAsync();

        var frame = HumFrame(); // codes 100-175 at 1 V/div, 0 V at code 127.5
        await _viewModel.OnFrameAsync(frame);

        Assert.Equal("2.35 V", _viewModel.PeakToPeakText);
        Assert.Equal("1.49 V", _viewModel.MaximumText);
        Assert.Equal("-863 mV", _viewModel.MinimumText);
        Assert.Equal(Aeroscope.App.Units.Volts((frame.Samples.Average(s => (double)s) - 127.5) * 8 / 255), _viewModel.MeanText);
    }

    [Fact]
    public void Offset_divisions_follow_the_range()
    {
        _viewModel.OffsetDivisions = 2;
        Assert.Equal(2, _viewModel.OffsetVolts);
        Assert.Equal("2.00 V", _viewModel.OffsetText);

        _viewModel.Range = VoltageRange.Range2V; // keeps volts

        Assert.Equal(1, _viewModel.OffsetDivisions);
    }

    [Fact]
    public void Trigger_divisions_are_relative_to_the_screen_centre()
    {
        _viewModel.OffsetVolts = 1;

        _viewModel.TriggerLevelDivisions = 0.5;

        Assert.Equal(1.5, _viewModel.TriggerLevelVolts);
        Assert.Equal(0.5, _viewModel.TriggerLevelDivisions);
        Assert.Equal("1.50 V", _viewModel.TriggerLevelText);
    }

    [Fact]
    public void Zero_commands()
    {
        _viewModel.OffsetVolts = 1;
        _viewModel.TriggerLevelVolts = 2;

        _viewModel.ZeroOffsetCommand.Execute(null);
        _viewModel.ZeroTriggerLevelCommand.Execute(null);

        Assert.Equal(0, _viewModel.OffsetVolts);
        Assert.Equal(0, _viewModel.TriggerLevelVolts);
    }

    // Roll mode

    private static readonly TimeBase Roll500ms = TimeBase.All.Single(t => t.Name == "500 ms");

    private static ScopeFrame RollFrame(byte code = 139) => new(Enumerable.Repeat(code, RollBuffer.FrameLength).ToArray(), 2);

    [Fact]
    public void Roll_mode_has_no_trigger()
    {
        Assert.True(_viewModel.HasTrigger);

        _viewModel.TimeBase = Roll500ms;

        Assert.False(_viewModel.HasTrigger);
    }

    [Fact]
    public async Task Capture_replayed_in_roll_mode_and_back()
    {
        // Capture 17: roll at 500 ms/div, then 5 ms/div (one stale roll frame first), then 5 s/div roll.
        await RunningAsync();
        var assembler = new FrameAssembler();
        var lengths = new List<int>();
        assembler.FrameCompleted += (_, frame) =>
        {
            _viewModel.OnFrameAsync(frame).Wait();
            lengths.Add(_viewModel.Waveform!.Volts.Length);
        };

        foreach (var packet in Captures.Load(Captures.Roll))
        {
            switch (packet.Channel)
            {
                case CaptureChannel.State:
                    _viewModel.TimeBase = TimeBase.All.Single(t => t.SamplerCode == packet.Bytes[4]);
                    break;
                case CaptureChannel.Data:
                    assembler.AddPacket(packet.Bytes);
                    break;
            }
        }

        // The stale roll frame after the switch to 5 ms/div leaves the roll trace as it was.
        Assert.Equal([16, 32, 48, 64, 80, 80, 512, 512, 16, 32, 48], lengths);
        Assert.Equal(-25, _viewModel.Waveform!.FirstSampleTime, precision: 9); // left edge at 5 s/div
    }

    [Fact]
    public async Task Normal_frame_is_ignored_in_roll_mode()
    {
        _viewModel.TimeBase = Roll500ms;
        await RunningAsync();

        await _viewModel.OnFrameAsync(HumFrame());

        Assert.Null(_viewModel.Waveform);
    }

    [Fact]
    public async Task Run_and_time_base_changes_start_a_new_roll()
    {
        await MakeReadyAsync();
        _viewModel.TimeBase = Roll500ms;
        await _viewModel.RunCommand.ExecuteAsync(null);
        await _viewModel.OnFrameAsync(RollFrame());
        await _viewModel.OnFrameAsync(RollFrame());
        Assert.Equal(32, _viewModel.Waveform!.Volts.Length);

        await _viewModel.RunCommand.ExecuteAsync(null);
        await _viewModel.OnFrameAsync(RollFrame());
        Assert.Equal(16, _viewModel.Waveform!.Volts.Length);

        _viewModel.TimeBase = TimeBase.All.Single(t => t.Name == "1 s");
        await _viewModel.OnFrameAsync(RollFrame());
        Assert.Equal(16, _viewModel.Waveform!.Volts.Length);
        Assert.Equal(20e-3, _viewModel.Waveform.SampleInterval, precision: 15);
    }

    [Fact]
    public async Task Range_change_keeps_the_roll()
    {
        _viewModel.TimeBase = Roll500ms;
        await RunningAsync();
        await _viewModel.OnFrameAsync(RollFrame());

        _viewModel.Range = VoltageRange.Range2V;
        await _viewModel.OnFrameAsync(RollFrame());

        Assert.Equal(32, _viewModel.Waveform!.Volts.Length);
    }

    [Fact]
    public async Task Single_in_roll_mode_stops_when_the_screen_is_full()
    {
        await MakeReadyAsync();
        _viewModel.TimeBase = Roll500ms;
        await _viewModel.SingleCommand.ExecuteAsync(null);

        for (var i = 0; i < 31; i++)
        {
            await _viewModel.OnFrameAsync(RollFrame());
        }

        Assert.Equal(RunMode.Single, _viewModel.RunMode); // 496 of 500 samples

        await _viewModel.OnFrameAsync(RollFrame());

        Assert.Equal(RunMode.Stopped, _viewModel.RunMode);
        Assert.Equal(TimeBase.SamplesOnScreen, _viewModel.Waveform!.Volts.Length);
        Assert.Equal(["S", "X", "R", "S", "X"], Commands);
    }

    // Device button

    [Fact]
    public async Task Button_toggles_run_and_stop()
    {
        var clock = new ManualClock();
        var viewModel = new AcquisitionViewModel((c, p) =>
        {
            _writes.Add((c, p));
            return Task.CompletedTask;
        }, clock);
        await viewModel.OnPowerChangedAsync(true);

        await viewModel.OnButtonPressedAsync();
        Assert.Equal(RunMode.Running, viewModel.RunMode);

        clock.Advance(TimeSpan.FromSeconds(2));
        await viewModel.OnButtonPressedAsync();
        Assert.Equal(RunMode.Stopped, viewModel.RunMode);

        Assert.Equal(["R", "S", "X"], Commands);
    }

    [Fact]
    public async Task Button_repeats_while_held_are_ignored()
    {
        var clock = new ManualClock();
        var viewModel = new AcquisitionViewModel((c, p) =>
        {
            _writes.Add((c, p));
            return Task.CompletedTask;
        }, clock);
        await viewModel.OnPowerChangedAsync(true);

        // Held for 3 s: "BD" every 0.4 s.
        for (var i = 0; i < 8; i++)
        {
            await viewModel.OnButtonPressedAsync();
            clock.Advance(TimeSpan.FromSeconds(0.4));
        }

        Assert.Equal(RunMode.Running, viewModel.RunMode);

        clock.Advance(AcquisitionViewModel.ButtonRepeatGap);
        await viewModel.OnButtonPressedAsync();

        Assert.Equal(RunMode.Stopped, viewModel.RunMode);
    }

    [Fact]
    public async Task Button_stops_a_single_shot()
    {
        await MakeReadyAsync();
        await _viewModel.SingleCommand.ExecuteAsync(null);

        await _viewModel.OnButtonPressedAsync();

        Assert.Equal(RunMode.Stopped, _viewModel.RunMode);
    }

    [Fact]
    public async Task Button_does_nothing_before_the_scope_is_ready()
    {
        await _viewModel.OnButtonPressedAsync();

        Assert.Equal(RunMode.Stopped, _viewModel.RunMode);
        Assert.Empty(_writes);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public async Task Frames_after_stop_leave_the_screen_frozen()
    {
        await RunningAsync();
        await _viewModel.OnFrameAsync(HumFrame());
        var frozen = _viewModel.Waveform;

        await _viewModel.StopCommand.ExecuteAsync(null);
        await _viewModel.OnFrameAsync(HumFrame());

        Assert.Same(frozen, _viewModel.Waveform);
    }

    [Fact]
    public async Task Disconnect_stops_and_disables()
    {
        await MakeReadyAsync();
        await _viewModel.RunCommand.ExecuteAsync(null);

        _viewModel.OnDisconnected();

        Assert.False(_viewModel.IsReady);
        Assert.Equal(RunMode.Stopped, _viewModel.RunMode);
        Assert.False(_viewModel.SingleCommand.CanExecute(null));
    }
}
