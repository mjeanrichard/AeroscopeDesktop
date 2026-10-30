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
    public void Offset_is_typed_in_volts_and_kept_when_the_range_changes()
    {
        _viewModel.OffsetText = "2";
        Assert.Equal(2, _viewModel.OffsetVolts);
        Assert.Equal("2.00 V", _viewModel.OffsetText);

        _viewModel.Range = VoltageRange.Range2V;

        Assert.Equal(2, _viewModel.OffsetVolts);
    }

    [Fact]
    public void Typed_offset_is_limited_to_ten_divisions()
    {
        _viewModel.OffsetText = "-25 V"; // 1 V/div

        Assert.Equal(-10, _viewModel.OffsetVolts);
    }

    [Fact]
    public void Unreadable_entries_change_nothing()
    {
        _viewModel.OffsetVolts = 1;
        _viewModel.TriggerLevelVolts = 0.5;
        var changed = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _viewModel.OffsetText = "abc";
        _viewModel.TriggerLevelText = "";

        Assert.Equal((1, 0.5), (_viewModel.OffsetVolts, _viewModel.TriggerLevelVolts));
        Assert.Equal([nameof(AcquisitionViewModel.OffsetText), nameof(AcquisitionViewModel.TriggerLevelText)], changed); // the boxes show the value again
        Assert.Empty(_writes);
    }

    [Fact]
    public void Trigger_level_stays_in_volts_when_the_offset_moves()
    {
        _viewModel.TriggerLevelText = "500 mV";

        _viewModel.OffsetText = "2";

        Assert.Equal(0.5, _viewModel.TriggerLevelVolts);
        Assert.Equal("500 mV", _viewModel.TriggerLevelText);
    }

    [Fact]
    public void Typed_trigger_level_is_limited_to_the_screen()
    {
        _viewModel.OffsetVolts = 1; // the screen spans -3 V to 5 V

        _viewModel.TriggerLevelText = "7";
        Assert.Equal(5, _viewModel.TriggerLevelVolts);

        _viewModel.TriggerLevelText = "-7";
        Assert.Equal(-3, _viewModel.TriggerLevelVolts);
    }

    [Fact]
    public void Steps_are_a_tenth_of_a_division_on_a_grid()
    {
        _viewModel.Range = VoltageRange.All.Single(r => r.VoltsPerDivision == 0.1);
        _viewModel.OffsetVolts = 0.123;

        _viewModel.OffsetUpCommand.Execute(null);
        Assert.Equal(0.13, _viewModel.OffsetVolts, precision: 9); // 0.12 rounded to the 10 mV grid, plus a step

        _viewModel.TriggerLevelDownCommand.Execute(null);
        _viewModel.TriggerLevelDownCommand.Execute(null);
        Assert.Equal(-0.02, _viewModel.TriggerLevelVolts, precision: 9);
    }

    [Fact]
    public void Steps_stop_at_the_limits()
    {
        _viewModel.OffsetVolts = 10;
        _viewModel.OffsetUpCommand.Execute(null);
        Assert.Equal(10, _viewModel.OffsetVolts, precision: 9);

        _viewModel.TriggerLevelVolts = 14; // the screen reaches 14 V
        _viewModel.TriggerLevelUpCommand.Execute(null);
        Assert.Equal(14, _viewModel.TriggerLevelVolts, precision: 9);
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
                    _clock.Set(Epoch + packet.Time);
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
        Assert.Equal("500 ms/div · 100 S/s · roll", _viewModel.HorizontalText);

        _viewModel.ZoomOutCommand.Execute(null);

        Assert.Equal(1, _viewModel.ViewSecondsPerDivision);
        Assert.Equal("1.00 s/div · 100 S/s · ×0.5 · roll", _viewModel.HorizontalText);
        Assert.Equal(640, Trace.Volts.Length); // all 6.4 s fit on a 10 s screen

        _viewModel.ZoomInCommand.Execute(null);

        Assert.Equal(0.5, _viewModel.ViewSecondsPerDivision);
    }

    [Fact]
    public async Task Roll_zooms_in_to_5_samples_a_division()
    {
        await RollingAsync();
        await RollFramesAsync(40);

        _viewModel.ZoomInCommand.Execute(null);
        _viewModel.ZoomInCommand.Execute(null);
        _viewModel.ZoomInCommand.Execute(null);

        Assert.Equal(0.05, _viewModel.ViewSecondsPerDivision, precision: 12); // 10 ms samples
        Assert.False(_viewModel.ZoomInCommand.CanExecute(null));
        Assert.Equal(TimeBase.SamplesOnScreen / 10, VisibleSamples);
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

    // Capture progress

    private async Task RunningAt200msAsync(bool autoTrigger = true)
    {
        _viewModel.TimeBase = TimeBase.All.Single(t => t.Name == "200 ms");
        _viewModel.AutoTrigger = autoTrigger;
        await RunningAsync();
    }

    [Fact]
    public async Task No_progress_when_frames_come_several_times_a_second()
    {
        await RunningAsync(); // 500 ns/div, 512 samples
        _clock.Advance(TimeSpan.FromSeconds(0.1));

        _viewModel.UpdateProgress();

        Assert.False(_viewModel.ShowProgress);
    }

    [Fact]
    public async Task Progress_follows_a_long_capture()
    {
        await RunningAt200msAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));

        _viewModel.UpdateProgress();

        Assert.True(_viewModel.ShowProgress);
        Assert.False(_viewModel.ProgressIndeterminate);
        // One bar over the cycle: 2.048 s capture, then 27 packets at about 7 ms.
        Assert.Equal("Capturing 1.0 of 2.2 s", _viewModel.ProgressText);
        Assert.Equal(1 / (2.048 + 27 * 0.007), _viewModel.ProgressFraction, precision: 9);
    }

    [Fact]
    public async Task Progress_continues_through_the_packets_of_a_frame()
    {
        await RunningAt200msAsync();
        _clock.Advance(TimeSpan.FromSeconds(2.1));

        _viewModel.OnDataPacket(Captures.Packet("06 00 80"), _clock.GetUtcNow());

        Assert.Equal("Receiving 2.1 of 2.2 s", _viewModel.ProgressText);
        Assert.Equal((2.048 + 0.007) / (2.048 + 27 * 0.007), _viewModel.ProgressFraction, precision: 9);
    }

    [Fact]
    public async Task Without_auto_trigger_progress_waits_for_the_trigger()
    {
        await RunningAt200msAsync(autoTrigger: false);
        _clock.Advance(TimeSpan.FromSeconds(3.048));

        _viewModel.UpdateProgress();

        Assert.True(_viewModel.ProgressIndeterminate);
        Assert.Equal("Waiting for trigger, 1.0 s", _viewModel.ProgressText);
    }

    [Fact]
    public async Task A_settings_change_restarts_the_capture_progress()
    {
        await RunningAt200msAsync();
        _clock.Advance(TimeSpan.FromSeconds(1.5));

        _viewModel.Range = VoltageRange.Range2V;
        _viewModel.UpdateProgress();

        Assert.Equal("Capturing 0.0 of 2.2 s", _viewModel.ProgressText);
    }

    [Fact]
    public async Task Stop_hides_the_progress()
    {
        await RunningAt200msAsync();

        await _viewModel.StopCommand.ExecuteAsync(null);

        Assert.False(_viewModel.ShowProgress);
    }

    // Frame size

    /// <summary>The frames of a size from capture 24 (500 ns/div, the 1 MHz sine).</summary>
    private static List<ScopeFrame> SizedFrames(int size)
    {
        var assembler = new FrameAssembler();
        var frames = new List<ScopeFrame>();
        assembler.FrameCompleted += (_, frame) => frames.Add(frame);
        foreach (var packet in Captures.Packets(Captures.FrameSizes, CaptureChannel.Data))
        {
            assembler.AddPacket(packet);
        }

        return frames.Where(f => f.Samples.Length == size).ToList();
    }

    private async Task Showing4096Async()
    {
        _viewModel.FrameSize = 4096;
        await RunningAsync();
        await _viewModel.OnFrameAsync(SizedFrames(4096)[0]);
    }

    private double Spd => _viewModel.TimeBase.SecondsPerDivision;

    [Fact]
    public async Task Frame_size_is_written_to_the_scope()
    {
        await MakeReadyAsync();

        _viewModel.FrameSize = 2048;

        Assert.Equal(new ScopeSettings { FrameSize = 2048 }.ToRegisters().ToPacket(), Assert.Single(SettingsWrites));
    }

    [Fact]
    public async Task A_large_frame_starts_at_1_to_1_in_the_middle()
    {
        await Showing4096Async();

        Assert.Equal(4096, Trace.Volts.Length); // drawn whole, the screen clips it
        Assert.Equal(Spd, _viewModel.ViewSecondsPerDivision, precision: 15);
        Assert.Equal("500 ns/div · 100 MS/s", _viewModel.HorizontalText);
        Assert.True(_viewModel.CanPan);
        Assert.Equal(0, _viewModel.ViewCentreSeconds);
        Assert.Equal(4096 * _viewModel.TimeBase.SampleInterval - 10 * Spd, _viewModel.FramePanMaximum, precision: 12);
        Assert.Equal(_viewModel.FramePanMaximum / 2, _viewModel.FramePanPosition, _viewModel.TimeBase.SampleInterval);
        Assert.False(_viewModel.ResetZoomCommand.CanExecute(null));
    }

    [Fact]
    public async Task Zooming_out_a_large_frame_stops_at_the_whole_record()
    {
        await Showing4096Async();

        _viewModel.ZoomOutCommand.Execute(null); // 1 µs
        _viewModel.ZoomOutCommand.Execute(null); // 2 µs: 20 of the 41 µs
        Assert.True(_viewModel.CanPan);
        _viewModel.ZoomOutCommand.Execute(null); // 5 µs: all 41 µs on a 50 µs screen

        Assert.Equal(5e-6, _viewModel.ViewSecondsPerDivision, precision: 15);
        Assert.Equal("5.00 µs/div · 100 MS/s · ×0.1", _viewModel.HorizontalText);
        Assert.False(_viewModel.ZoomOutCommand.CanExecute(null));
        Assert.False(_viewModel.CanPan);
        Assert.Equal(0, _viewModel.ViewCentreSeconds);
    }

    [Fact]
    public async Task Zooming_keeps_a_panned_view_inside_the_frame()
    {
        await Showing4096Async();
        _viewModel.FramePanPosition = 0; // the frame's left end
        var start = Trace.FirstSampleTime;
        Assert.Equal(start + 5 * Spd, _viewModel.ViewCentreSeconds, precision: 12);

        _viewModel.ZoomOutCommand.Execute(null); // twice the width, about the centre: clamped to the left end

        Assert.Equal(start + 10 * Spd, _viewModel.ViewCentreSeconds, precision: 12);
    }

    [Fact]
    public async Task Readouts_cover_the_whole_frame_when_zoomed()
    {
        await Showing4096Async();
        var whole = Trace.Volts;

        _viewModel.ZoomInCommand.Execute(null);
        _viewModel.FramePanPosition = 0;

        Assert.Equal(whole.Max(), _viewModel.Screen!.Maximum);
        Assert.Equal(whole.Min(), _viewModel.Screen.Minimum);
    }

    [Fact]
    public async Task Frame_size_change_resets_to_1_to_1()
    {
        await Showing4096Async();
        _viewModel.ZoomOutCommand.Execute(null);

        _viewModel.FrameSize = 2048;
        await _viewModel.OnFrameAsync(SizedFrames(2048)[0]);

        Assert.Equal(Spd, _viewModel.ViewSecondsPerDivision, precision: 15);
        Assert.True(_viewModel.CanPan);
    }

    [Fact]
    public async Task A_512_sample_frame_zooms_in_to_5_samples_a_division_but_not_out()
    {
        await RunningAsync();
        await HumFramesAsync(1);
        Assert.False(_viewModel.ZoomOutCommand.CanExecute(null)); // 512 samples: one screen
        Assert.False(_viewModel.CanPan);

        while (_viewModel.ZoomInCommand.CanExecute(null))
        {
            _viewModel.ZoomInCommand.Execute(null);
        }

        Assert.Equal(5 * _viewModel.TimeBase.SampleInterval, _viewModel.ViewSecondsPerDivision, precision: 15);
        Assert.Equal("50.0 ns/div · 100 MS/s · ×10", _viewModel.HorizontalText);
        Assert.True(_viewModel.CanPan);
        Assert.Equal(512, Trace.Volts.Length);
    }

    [Fact]
    public async Task Horizontal_readouts_are_separate()
    {
        await RunningAsync();
        await HumFramesAsync(1);
        Assert.Equal(("500 ns/div", "100 MS/s", "×1"), (_viewModel.ViewScaleText, _viewModel.ViewRateText, _viewModel.ZoomText));

        _viewModel.ZoomInCommand.Execute(null);

        Assert.Equal(("200 ns/div", "100 MS/s", "×2.5"), (_viewModel.ViewScaleText, _viewModel.ViewRateText, _viewModel.ZoomText));
    }

    [Fact]
    public async Task Zooming_in_goes_as_far_whatever_rate_is_selected()
    {
        // A 100 MS/s frame in the history, then a slower rate: the old frame can still be zoomed into fully.
        await RunningAsync();
        await HumFramesAsync(1);
        _viewModel.TimeBase = TimeBase.All.Single(t => t.Name == "1 ms");
        _viewModel.HistoryPosition = 0;

        while (_viewModel.ZoomInCommand.CanExecute(null))
        {
            _viewModel.ZoomInCommand.Execute(null);
        }

        Assert.Equal(50e-9, _viewModel.ViewSecondsPerDivision, precision: 15);
        Assert.Equal("50.0 ns/div · 100 MS/s · ×10", _viewModel.HorizontalText);
    }

    [Fact]
    public async Task Roll_zooms_in_as_far_at_any_roll_rate()
    {
        _viewModel.TimeBase = TimeBase.All.Single(t => t.Name == "1 s");
        await MakeReadyAsync();

        while (_viewModel.ZoomInCommand.CanExecute(null))
        {
            _viewModel.ZoomInCommand.Execute(null);
        }

        Assert.Equal(0.05, _viewModel.ViewSecondsPerDivision, precision: 12); // 5 samples of the fastest roll rate (10 ms)
    }

    [Fact]
    public async Task One_to_one_resets_the_zoom()
    {
        await RunningAsync();
        await HumFramesAsync(1);
        _viewModel.ZoomInCommand.Execute(null);
        Assert.True(_viewModel.ResetZoomCommand.CanExecute(null));

        _viewModel.ResetZoomCommand.Execute(null);

        Assert.Equal(Spd, _viewModel.ViewSecondsPerDivision, precision: 15);
        Assert.False(_viewModel.ResetZoomCommand.CanExecute(null));
        Assert.False(_viewModel.CanPan);
    }

    [Fact]
    public async Task The_scale_stays_while_stepping_through_the_history()
    {
        await RunningAsync();
        await HumFramesAsync(1);
        _viewModel.FrameSize = 4096;
        await _viewModel.OnFrameAsync(SizedFrames(4096)[0]);
        _viewModel.ZoomOutCommand.Execute(null);

        _viewModel.HistoryPosition = 0;

        Assert.Equal(2 * Spd, _viewModel.ViewSecondsPerDivision, precision: 15); // the 512-sample frame: half the screen
        Assert.False(_viewModel.CanPan);
    }

    [Fact]
    public void Zoom_in_is_possible_from_the_start_but_not_out()
    {
        Assert.True(_viewModel.ZoomInCommand.CanExecute(null));
        Assert.False(_viewModel.ZoomOutCommand.CanExecute(null));
        Assert.Equal(TimeBase.Default.SecondsPerDivision, _viewModel.ViewSecondsPerDivision);
    }

    [Fact]
    public void View_scales_step_1_2_5_through_every_time_base()
    {
        var scales = AcquisitionViewModel.ViewScales;

        Assert.Equal(50e-9, scales[0]);
        Assert.Equal([50e-9, 100e-9, 200e-9, 500e-9, 1e-6], scales.Take(5));
        Assert.True(scales.Zip(scales.Skip(1)).All(pair => pair.First < pair.Second));
        Assert.All(TimeBase.All, timeBase => Assert.Contains(scales, s => Math.Abs(s / timeBase.SecondsPerDivision - 1) < 1e-9));
        Assert.Equal(3600, scales[^1]);
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
    public async Task A_frame_keeps_the_trigger_level_it_was_taken_with()
    {
        Assert.Null(_viewModel.FrameTriggerLevelVolts); // no frame yet
        await RunningAsync();
        await HumFramesAsync(1);
        Assert.Equal(0, _viewModel.FrameTriggerLevelVolts);

        _viewModel.TriggerLevelVolts = 1;
        Assert.Equal(0, _viewModel.FrameTriggerLevelVolts); // the frame shown predates the change

        await HumFramesAsync(1);
        Assert.Equal(1, _viewModel.FrameTriggerLevelVolts);

        _viewModel.HistoryPosition = 0;
        Assert.Equal(0, _viewModel.FrameTriggerLevelVolts);
    }

    /// <summary>
    /// Replays a capture with its timing: its settings writes through the view model (acknowledged
    /// at their recorded times), its R as Run, its frames with their first and last packet times.
    /// Returns the frames as received, in history order.
    /// </summary>
    private async Task<List<ScopeFrame>> ReplayWithWritesAsync(string capture)
    {
        await MakeReadyAsync();
        var assembler = new FrameAssembler();
        var frames = new List<ScopeFrame>();
        var started = Epoch;
        assembler.FrameCompleted += (_, frame) =>
        {
            frames.Add(frame);
            _viewModel.OnFrameAsync(frame, _clock.GetUtcNow(), started).Wait();
        };

        foreach (var packet in Captures.Load(capture))
        {
            _clock.Set(Epoch + packet.Time);
            switch (packet.Channel)
            {
                case CaptureChannel.State:
                    var settings = Captures.SettingsFrom(packet.Bytes);
                    _viewModel.Range = settings.Range;
                    _viewModel.OffsetVolts = settings.OffsetVolts;
                    break;
                case CaptureChannel.In when packet.Bytes[0] == 'R':
                    await _viewModel.RunCommand.ExecuteAsync(null);
                    break;
                case CaptureChannel.Data:
                    if (packet.Bytes[0] != 0)
                    {
                        started = _clock.GetUtcNow();
                    }

                    assembler.AddPacket(packet.Bytes);
                    break;
            }
        }

        Assert.Equal(frames.Count, (int)_viewModel.HistoryMaximum + 1);
        return frames;
    }

    [Fact]
    public async Task Frames_are_kept_with_the_range_they_were_taken_with()
    {
        // Capture 20: a 2 V peak-to-peak sine while the range switches between 1 and 2 V/div.
        // Converted with the range it was taken with, every frame shows 2 V.
        var frames = await ReplayWithWritesAsync(Captures.SettingsWhileRunning);

        Assert.Equal(65, frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            _viewModel.HistoryPosition = i;
            Assert.InRange(_viewModel.Screen!.PeakToPeak, 1.9, 2.2);
        }
    }

    [Fact]
    public async Task Frames_are_kept_with_the_offset_they_were_taken_with()
    {
        // Capture 16: offset and range rewritten while running. The floating input reads the same
        // voltage whatever the offset, so only frames converted with their own settings agree.
        var frames = await ReplayWithWritesAsync(Captures.OffsetDac);

        var means = new List<double>();
        for (var i = 0; i < frames.Count; i++)
        {
            if (!frames[i].Samples.Contains((byte)255)) // the last offset saturates the ADC
            {
                _viewModel.HistoryPosition = i;
                means.Add(Trace.Mean);
            }
        }

        Assert.Equal(15, means.Count); // 18 frames, the last 3 saturated
        Assert.All(means, m => Assert.InRange(m, 0.25, 0.38)); // +0.36 V at 1 V/div, +0.26 V at 100 mV/div
    }

    [Fact]
    public async Task A_frame_already_waiting_when_a_write_arrives_keeps_the_old_settings()
    {
        await RunningAsync();
        await HumFramesAsync(1);

        _clock.Advance(TimeSpan.FromMilliseconds(40)); // after the previous frame
        _viewModel.TriggerLevelVolts = 1;
        _clock.Advance(TimeSpan.FromMilliseconds(1)); // the next frame comes in the same connection event
        await _viewModel.OnFrameAsync(HumFrame());
        Assert.Equal(0, _viewModel.FrameTriggerLevelVolts);

        await HumFramesAsync(1);
        Assert.Equal(1, _viewModel.FrameTriggerLevelVolts);
    }

    [Fact]
    public async Task Run_after_a_change_while_stopped_has_no_stale_frame()
    {
        await RunningAsync();
        await HumFramesAsync(1);
        await _viewModel.StopCommand.ExecuteAsync(null);

        _viewModel.TriggerLevelVolts = 1;
        await _viewModel.RunCommand.ExecuteAsync(null);
        await HumFramesAsync(1);

        Assert.Equal(1, _viewModel.FrameTriggerLevelVolts);
    }

    [Fact]
    public async Task Of_several_writes_a_frame_gets_the_newest_in_time()
    {
        await RunningAsync();
        await HumFramesAsync(1);
        _clock.Advance(TimeSpan.FromMilliseconds(40));
        _viewModel.TriggerLevelVolts = 1;
        _clock.Advance(TimeSpan.FromMilliseconds(40));
        _viewModel.TriggerLevelVolts = 2;

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await _viewModel.OnFrameAsync(HumFrame());

        Assert.Equal(1, _viewModel.FrameTriggerLevelVolts); // too late for the second write only
    }

    [Fact]
    public async Task Roll_mode_has_no_frame_trigger_level()
    {
        await RunningAsync();
        await HumFramesAsync(1);

        _viewModel.TimeBase = TimeBase.All.First(t => t.IsRoll);

        Assert.Null(_viewModel.FrameTriggerLevelVolts);
    }

    [Fact]
    public async Task A_frame_taken_at_another_rate_shows_at_the_current_scale()
    {
        await RunningAsync();
        await HumFramesAsync(2);

        // The time base sets the scale at once; the old frame stays until a new one arrives, in its own rate.
        _viewModel.TimeBase = TimeBase.All.Single(t => t.Name == "1 ms");

        Assert.Equal(1e-3, _viewModel.ViewSecondsPerDivision, precision: 15);
        Assert.Equal("1.00 ms/div · 100 MS/s · ×0.0005", _viewModel.HorizontalText);
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

    // Run/Stop button and the device button

    [Fact]
    public async Task Run_stop_button_runs_when_stopped_and_stops_otherwise()
    {
        Assert.False(_viewModel.RunStopCommand.CanExecute(null));
        await MakeReadyAsync();
        Assert.Equal("Run", _viewModel.RunStopText);

        await _viewModel.RunStopCommand.ExecuteAsync(null);
        Assert.Equal(RunMode.Running, _viewModel.RunMode);
        Assert.Equal("Stop", _viewModel.RunStopText);

        await _viewModel.RunStopCommand.ExecuteAsync(null);
        Assert.Equal(RunMode.Stopped, _viewModel.RunMode);
        Assert.Equal("Run", _viewModel.RunStopText);

        await _viewModel.SingleCommand.ExecuteAsync(null);
        Assert.Equal("Stop", _viewModel.RunStopText);
        await _viewModel.RunStopCommand.ExecuteAsync(null);
        Assert.Equal(RunMode.Stopped, _viewModel.RunMode);

        Assert.Equal(["R", "S", "X", "S", "X", "R", "S", "X"], Commands);
    }

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
