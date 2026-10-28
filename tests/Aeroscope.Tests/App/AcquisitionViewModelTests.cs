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
    private static readonly DateTimeOffset Epoch = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly List<(ushort Characteristic, byte[] Payload)> _writes = [];
    private readonly ManualClock _clock = new(Epoch);
    private TaskCompletionSource? _blockWrites;
    private readonly AcquisitionViewModel _viewModel;

    public AcquisitionViewModelTests()
    {
        _viewModel = new AcquisitionViewModel((characteristic, payload) =>
        {
            _writes.Add((characteristic, payload));
            return _blockWrites?.Task ?? Task.CompletedTask;
        }, _clock);
    }

    private IEnumerable<string> Commands => _writes.Where(w => w.Characteristic == AeroscopeGatt.ScopeIn).Select(w => Encoding.ASCII.GetString(w.Payload));

    private IEnumerable<byte[]> SettingsWrites => _writes.Where(w => w.Characteristic == AeroscopeGatt.ScopeState).Select(w => w.Payload);

    private static List<ScopeFrame> HumFrames()
    {
        var assembler = new FrameAssembler();
        var frames = new List<ScopeFrame>();
        assembler.FrameCompleted += (_, frame) => frames.Add(frame);
        foreach (var packet in Captures.Packets(Captures.TouchHum, CaptureChannel.Data))
        {
            assembler.AddPacket(packet);
        }

        return frames;
    }

    private static ScopeFrame HumFrame() => HumFrames()[0];

    private Waveform Trace => Assert.Single(_viewModel.Screen!.Traces);

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
        Assert.NotNull(_viewModel.Screen);
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

    private static ScopeFrame RollFrame(byte code = 139) => new(Enumerable.Repeat(code, RollHistory.FrameLength).ToArray(), 2);

    private int VisibleSamples => _viewModel.Screen?.Traces.Sum(t => t.Volts.Length) ?? 0;

    /// <summary>Roll frames 160 ms apart, as the scope sends them at 500 ms/div.</summary>
    private async Task RollFramesAsync(int count, byte code = 139)
    {
        for (var i = 0; i < count; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(160));
            await _viewModel.OnFrameAsync(RollFrame(code));
        }
    }

    private async Task RollingAsync()
    {
        _viewModel.TimeBase = Roll500ms;
        await RunningAsync();
    }

    [Fact]
    public void Roll_mode_has_no_trigger()
    {
        Assert.True(_viewModel.HasTrigger);

        _viewModel.TimeBase = Roll500ms;

        Assert.False(_viewModel.HasTrigger);
        Assert.True(_viewModel.IsRoll);
    }

    [Fact]
    public async Task Capture_replayed_in_roll_mode_and_back()
    {
        // Capture 17: roll at 500 ms/div, then 5 ms/div (one stale roll frame first), then 5 s/div roll.
        await RunningAsync();
        var assembler = new FrameAssembler();
        var visible = new List<int>();
        assembler.FrameCompleted += (_, frame) =>
        {
            _viewModel.OnFrameAsync(frame).Wait();
            visible.Add(VisibleSamples);
        };

        foreach (var packet in Captures.Load(Captures.Roll))
        {
            switch (packet.Channel)
            {
                case CaptureChannel.State:
                    _viewModel.TimeBase = TimeBase.All.Single(t => t.SamplerCode == packet.Bytes[4]);
                    break;
                case CaptureChannel.Data:
                    _clock.Set(Epoch + packet.Time);
                    assembler.AddPacket(packet.Bytes);
                    break;
            }
        }

        // At 5 ms/div the screen shows the (still empty) frame history; the stale roll frame is
        // dropped. Back in roll mode at 5 s/div the screen spans 50 s, so the earlier 500 ms/div
        // recording shows on its left, its 80 samples drawn as 16 columns of min/max pairs.
        Assert.Equal([16, 32, 48, 64, 80, 0, 512, 512, 32 + 16, 32 + 32, 32 + 48], visible);
        Assert.Equal(2, _viewModel.Screen!.Traces.Count);
        Assert.Equal(-25, _viewModel.Screen.Traces[0].FirstSampleTime, precision: 9); // left edge at 5 s/div
    }

    [Fact]
    public async Task Normal_frame_is_ignored_in_roll_mode()
    {
        await RollingAsync();

        await _viewModel.OnFrameAsync(HumFrame());

        Assert.Null(_viewModel.Screen);
        Assert.False(_viewModel.HasHistory);
    }

    [Fact]
    public async Task Roll_fills_from_the_left_then_scrolls()
    {
        await RollingAsync();

        await RollFramesAsync(10);
        Assert.Equal(160, Trace.Volts.Length);
        Assert.Equal(-2.5, Trace.FirstSampleTime, precision: 9);
        Assert.Equal("12:00:00.0 to 12:00:05.0 (live)", _viewModel.HistoryText);

        await RollFramesAsync(30); // 6.4 s recorded, 5 s on screen

        Assert.Equal(TimeBase.SamplesOnScreen, Trace.Volts.Length);
        Assert.Equal(-2.5, Trace.FirstSampleTime, precision: 9);
        Assert.Equal(1.4, _viewModel.HistoryMaximum, precision: 9);
        Assert.Equal(1.4, _viewModel.HistoryPosition, precision: 9);
        Assert.Equal(5, _viewModel.HistoryViewport, precision: 9);
        Assert.True(_viewModel.IsLive);
    }

    [Fact]
    public async Task Scrolling_back_detaches_from_the_live_data()
    {
        await RollingAsync();
        await RollFramesAsync(20, code: 100);
        await RollFramesAsync(20, code: 200);
        Assert.False(_viewModel.GoLiveCommand.CanExecute(null));

        _viewModel.HistoryPosition = 0;

        Assert.False(_viewModel.IsLive);
        Assert.True(_viewModel.GoLiveCommand.CanExecute(null));
        Assert.Equal(_viewModel.Settings.ToVolts(100), Trace.Volts[0], precision: 12);
        Assert.Equal("12:00:00.0 to 12:00:05.0", _viewModel.HistoryText);

        await RollFramesAsync(10); // acquisition carries on, the screen stays put
        Assert.Equal(0, _viewModel.HistoryPosition);
        Assert.Equal(3, _viewModel.HistoryMaximum, precision: 9);

        _viewModel.Scroll(2); // two divisions
        Assert.Equal(1, _viewModel.HistoryPosition, precision: 9);

        _viewModel.GoLiveCommand.Execute(null);
        Assert.True(_viewModel.IsLive);
        Assert.Equal(3, _viewModel.HistoryPosition, precision: 9);
    }

    [Fact]
    public async Task Scrolling_to_the_end_goes_live_again()
    {
        await RollingAsync();
        await RollFramesAsync(40);
        _viewModel.HistoryPosition = 0;

        _viewModel.Scroll(100);

        Assert.True(_viewModel.IsLive);
    }

    [Fact]
    public async Task Run_keeps_the_history_as_a_new_segment()
    {
        await RollingAsync();
        await RollFramesAsync(2);
        await _viewModel.StopCommand.ExecuteAsync(null);
        _clock.Advance(TimeSpan.FromSeconds(1));

        await _viewModel.RunCommand.ExecuteAsync(null);
        await RollFramesAsync(1);

        Assert.Equal(2, _viewModel.Screen!.Traces.Count); // a gap where it was stopped
        Assert.Equal(48, VisibleSamples);
    }

    [Fact]
    public async Task Range_change_keeps_the_roll()
    {
        await RollingAsync();
        await RollFramesAsync(1);

        _viewModel.Range = VoltageRange.Range2V;
        await RollFramesAsync(1);

        Assert.Equal(32, VisibleSamples);
        Assert.InRange(_viewModel.Screen!.Traces[1].Volts[0] / _viewModel.Screen.Traces[0].Volts[0], 1.99, 2.01); // each in its own volts
    }

    [Fact]
    public async Task Zoom_out_over_the_recording()
    {
        await RollingAsync();
        await RollFramesAsync(40);
        Assert.False(_viewModel.ZoomInCommand.CanExecute(null)); // not below the time base
        Assert.Equal("500 ms/div roll", _viewModel.HorizontalText);

        _viewModel.ZoomOutCommand.Execute(null);

        Assert.Equal(1, _viewModel.ViewSecondsPerDivision);
        Assert.Equal($"500 ms/div roll, view {Aeroscope.App.Units.Seconds(1)}/div", _viewModel.HorizontalText);
        Assert.Equal(640, Trace.Volts.Length); // all 6.4 s fit on a 10 s screen
        Assert.True(_viewModel.ZoomInCommand.CanExecute(null));

        _viewModel.ZoomInCommand.Execute(null);

        Assert.Equal(0.5, _viewModel.ViewSecondsPerDivision);
    }

    [Fact]
    public async Task Time_base_change_resets_the_zoom_and_goes_live()
    {
        await RollingAsync();
        await RollFramesAsync(40);
        _viewModel.ZoomOutCommand.Execute(null);
        _viewModel.HistoryPosition = 0;

        _viewModel.TimeBase = TimeBase.All.Single(t => t.Name == "1 s");

        Assert.Equal(1, _viewModel.ViewSecondsPerDivision);
        Assert.True(_viewModel.IsLive);
    }

    [Fact]
    public void Zoom_is_for_roll_mode_only()
    {
        Assert.False(_viewModel.ZoomOutCommand.CanExecute(null));
        Assert.False(_viewModel.ZoomInCommand.CanExecute(null));
        Assert.Equal(TimeBase.Default.SecondsPerDivision, _viewModel.ViewSecondsPerDivision);
    }

    [Fact]
    public async Task Single_in_roll_mode_stops_when_the_screen_is_full()
    {
        _viewModel.TimeBase = Roll500ms;
        await MakeReadyAsync();
        await _viewModel.SingleCommand.ExecuteAsync(null);

        await RollFramesAsync(31);
        Assert.Equal(RunMode.Single, _viewModel.RunMode); // 496 of 500 samples

        await RollFramesAsync(1);

        Assert.Equal(RunMode.Stopped, _viewModel.RunMode);
        Assert.Equal(TimeBase.SamplesOnScreen, Trace.Volts.Length);
        Assert.Equal(["S", "X", "R", "S", "X"], Commands);
    }

    // Triggered frame history

    private async Task HumFramesAsync(int count)
    {
        foreach (var frame in HumFrames().Take(count))
        {
            _clock.Advance(TimeSpan.FromMilliseconds(250));
            await _viewModel.OnFrameAsync(frame);
        }
    }

    [Fact]
    public async Task Frames_are_kept_and_can_be_stepped_through()
    {
        var frames = HumFrames();
        await RunningAsync();
        await HumFramesAsync(3);
        Assert.Equal("Frame 3 of 3, 12:00:00.7 (live)", _viewModel.HistoryText);
        Assert.Equal(2, _viewModel.HistoryMaximum);

        _viewModel.HistoryPosition = 0;

        Assert.False(_viewModel.IsLive);
        Assert.Equal("Frame 1 of 3, 12:00:00.2", _viewModel.HistoryText);
        Assert.Equal(Waveform.From(frames[0], _viewModel.Settings).Volts, Trace.Volts);

        await HumFramesAsync(1); // recorded, but the screen stays on frame 1
        Assert.Equal("Frame 1 of 4, 12:00:00.2", _viewModel.HistoryText);

        _viewModel.Scroll(1);
        Assert.Equal(Waveform.From(frames[1], _viewModel.Settings).Volts, Trace.Volts);

        _viewModel.GoLiveCommand.Execute(null);
        Assert.Equal("Frame 4 of 4, 12:00:01.0 (live)", _viewModel.HistoryText);
    }

    [Fact]
    public async Task Stepping_to_the_newest_frame_goes_live()
    {
        await RunningAsync();
        await HumFramesAsync(3);
        _viewModel.HistoryPosition = 0;

        _viewModel.HistoryPosition = 2;

        Assert.True(_viewModel.IsLive);
    }

    [Fact]
    public async Task History_can_be_browsed_after_stopping()
    {
        await RunningAsync();
        await HumFramesAsync(3);
        await _viewModel.StopCommand.ExecuteAsync(null);

        _viewModel.Scroll(-1);

        Assert.Equal(1, _viewModel.HistoryPosition);
    }

    [Fact]
    public async Task Run_shows_live_data_again()
    {
        await RunningAsync();
        await HumFramesAsync(3);
        _viewModel.HistoryPosition = 0;

        await _viewModel.RunCommand.ExecuteAsync(null);

        Assert.True(_viewModel.IsLive);
        Assert.Equal(2, _viewModel.HistoryPosition);
    }

    [Fact]
    public async Task Clear_forgets_the_history()
    {
        await RunningAsync();
        await HumFramesAsync(2);

        _viewModel.ClearHistoryCommand.Execute(null);

        Assert.Null(_viewModel.Screen);
        Assert.False(_viewModel.HasHistory);
        Assert.Equal("No history", _viewModel.HistoryText);
        Assert.Equal("–", _viewModel.PeakToPeakText);
    }

    [Fact]
    public void Moving_without_history_does_nothing()
    {
        _viewModel.Scroll(-1);

        Assert.True(_viewModel.IsLive);
        Assert.Equal(0, _viewModel.HistoryPosition);
    }

    // Device button

    [Fact]
    public async Task Button_toggles_run_and_stop()
    {
        await MakeReadyAsync();

        await _viewModel.OnButtonPressedAsync();
        Assert.Equal(RunMode.Running, _viewModel.RunMode);

        await _viewModel.OnButtonPressedAsync();
        Assert.Equal(RunMode.Stopped, _viewModel.RunMode);

        Assert.Equal(["R", "S", "X"], Commands);
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

    [Fact]
    public async Task Frames_after_stop_leave_the_screen_frozen()
    {
        await RunningAsync();
        await _viewModel.OnFrameAsync(HumFrame());
        var frozen = _viewModel.Screen;

        await _viewModel.StopCommand.ExecuteAsync(null);
        await _viewModel.OnFrameAsync(HumFrame());

        Assert.Same(frozen, _viewModel.Screen);
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
