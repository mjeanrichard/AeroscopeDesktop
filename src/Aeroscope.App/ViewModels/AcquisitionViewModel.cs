using System.Globalization;
using System.Text;
using Aeroscope.Core.Bluetooth;
using Aeroscope.Core.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aeroscope.App.ViewModels;

public enum RunMode { Stopped, Running, Single }

/// <summary>
/// Acquisition settings, run control and what the screen shows. Settings changes are
/// written to the scope immediately (coalesced while a write is in flight); the
/// scope applies them from the next frame on. Everything received while running is kept
/// (see the History part) and can be scrolled back through.
/// </summary>
public partial class AcquisitionViewModel : ObservableObject
{
    private const string NoValue = "–";

    private readonly Func<ushort, byte[], Task> _write;
    private readonly TimeProvider _time;
    private bool _pushing;
    private bool _pushPending;
    private readonly SettingsTimeline _timeline = new();
    private readonly CaptureProgress _progress = new();

    /// <summary>Cycles shorter than this (several frames a second) get no progress indicator.</summary>
    private static readonly TimeSpan ProgressThreshold = TimeSpan.FromSeconds(0.5);

    /// <param name="write">Writes a payload to a characteristic (Scope In or Scope State).</param>
    /// <param name="time">Clock for frame times; the system clock if null.</param>
    public AcquisitionViewModel(Func<ushort, byte[], Task> write, TimeProvider? time = null)
    {
        _write = write;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<VoltageRange> Ranges => VoltageRange.All;

    public IReadOnlyList<TimeBase> TimeBases => TimeBase.All;

    public IReadOnlyList<TriggerEdge> TriggerEdges { get; } = Enum.GetValues<TriggerEdge>();

    [ObservableProperty]
    public partial ScopeSettings Settings { get; private set; } = new();

    /// <summary>True once the scope reports its FPGA is powered and configured.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand), nameof(SingleCommand))]
    public partial bool IsReady { get; private set; }

    [ObservableProperty]
    public partial RunMode RunMode { get; private set; }

    /// <summary>Running with a frame cycle long enough to be worth following.</summary>
    [ObservableProperty]
    public partial bool ShowProgress { get; private set; }

    /// <summary>0-1: through the capture, or through the frame's packets.</summary>
    [ObservableProperty]
    public partial double ProgressFraction { get; private set; }

    /// <summary>Waiting for a trigger: no telling how long.</summary>
    [ObservableProperty]
    public partial bool ProgressIndeterminate { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaximumText), nameof(MinimumText), nameof(PeakToPeakText), nameof(MeanText))]
    public partial ScreenTrace? Screen { get; private set; }

    public VoltageRange Range
    {
        get => Settings.Range;
        set => Update(Settings with { Range = value });
    }

    public bool DcCoupled
    {
        get => Settings.DcCoupled;
        set => Update(Settings with { DcCoupled = value });
    }

    public TimeBase TimeBase
    {
        get => Settings.TimeBase;
        set => Update(Settings with { TimeBase = value });
    }

    public IReadOnlyList<int> FrameSizes => ScopeSettings.FrameSizes;

    /// <summary>Samples per triggered frame: beyond 512 a frame is wider than the screen.</summary>
    public int FrameSize
    {
        get => Settings.FrameSize;
        set => Update(Settings with { FrameSize = value });
    }

    public TriggerEdge TriggerEdge
    {
        get => Settings.TriggerEdge;
        set => Update(Settings with { TriggerEdge = value });
    }

    public bool AutoTrigger
    {
        get => Settings.AutoTrigger;
        set => Update(Settings with { AutoTrigger = value });
    }

    public bool NoiseReject
    {
        get => Settings.NoiseReject;
        set => Update(Settings with { NoiseReject = value });
    }

    public double OffsetVolts
    {
        get => Settings.OffsetVolts;
        set => Update(Settings with { OffsetVolts = value });
    }

    public double TriggerLevelVolts
    {
        get => Settings.TriggerLevelVolts;
        set => Update(Settings with { TriggerLevelVolts = value });
    }

    /// <summary>Offset in divisions of the current range, for a slider.</summary>
    public double OffsetDivisions
    {
        get => OffsetVolts / Range.VoltsPerDivision;
        set => OffsetVolts = value * Range.VoltsPerDivision;
    }

    /// <summary>Trigger level in divisions from the screen centre, for a slider.</summary>
    public double TriggerLevelDivisions
    {
        get => (TriggerLevelVolts - OffsetVolts) / Range.VoltsPerDivision;
        set => TriggerLevelVolts = OffsetVolts + value * Range.VoltsPerDivision;
    }

    /// <summary>Roll mode: the scope streams continuously and ignores the trigger.</summary>
    public bool IsRoll => TimeBase.IsRoll;

    public bool HasTrigger => !IsRoll;

    public string OffsetText => Units.Volts(OffsetVolts);

    public string TriggerLevelText => Units.Volts(TriggerLevelVolts);

    public string MaximumText => Screen is null ? NoValue : Units.Volts(Screen.Maximum);

    public string MinimumText => Screen is null ? NoValue : Units.Volts(Screen.Minimum);

    public string PeakToPeakText => Screen is null ? NoValue : Units.Volts(Screen.PeakToPeak);

    public string MeanText => Screen is null ? NoValue : Units.Volts(Screen.Mean);

    [RelayCommand]
    private void ZeroOffset() => OffsetVolts = 0;

    [RelayCommand]
    private void ZeroTriggerLevel() => TriggerLevelVolts = 0;

    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task RunAsync()
    {
        StartRecording();
        RunMode = RunMode.Running;
        await StartAcquisitionAsync();
    }

    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task StopAsync()
    {
        RunMode = RunMode.Stopped;
        await StopAcquisitionAsync();
    }

    /// <summary>
    /// The scope's own single-frame command ("F") rarely answers, so single shot is
    /// run mode that stops after the first frame. Turn auto trigger off to wait for a real edge.
    /// In roll mode it stops when the screen is full.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task SingleAsync()
    {
        await StopAcquisitionAsync();
        StartRecording();
        RunMode = RunMode.Single;
        await StartAcquisitionAsync();
    }

    /// <param name="received">When the frame's last packet arrived; now if null.</param>
    /// <param name="started">When its first packet arrived; <paramref name="received"/> if null.</param>
    public async Task OnFrameAsync(ScopeFrame frame, DateTimeOffset? received = null, DateTimeOffset? started = null)
    {
        // The screen freezes on stop, even if a frame already on its way arrives after it.
        if (RunMode == RunMode.Stopped)
        {
            return;
        }

        // Settings written while running may come too late for a frame; one whose length doesn't
        // fit its settings (roll or triggered, frame size) was taken with the newest.
        var end = received ?? _time.GetUtcNow();
        var isRollFrame = frame.Samples.Length == RollHistory.FrameLength;
        var settings = _timeline.FrameArrived(started ?? end, end);
        if (settings.FrameLength != frame.Samples.Length)
        {
            settings = _timeline.Latest;
        }

        // After a time base change the scope sends one more frame of the old kind; drop it.
        if (IsRoll != isRollFrame)
        {
            return;
        }

        Record(frame, settings, end);
        if (RunMode == RunMode.Single && (!IsRoll || _roll.CurrentSegmentCount >= TimeBase.SamplesOnScreen))
        {
            RunMode = RunMode.Stopped;
            await StopAcquisitionAsync();
        }
    }

    /// <summary>A Scope Data packet, as it arrives: follows the transfer of a frame.</summary>
    public void OnDataPacket(byte[] packet, DateTimeOffset received)
    {
        _progress.PacketArrived(packet, received);
        UpdateProgress();
    }

    /// <summary>Refreshes the progress indicator; call it a few times a second while running.</summary>
    public void UpdateProgress()
    {
        var status = _progress.At(_time.GetUtcNow());
        ShowProgress = RunMode != RunMode.Stopped && status.Phase != CapturePhase.Idle && _progress.ExpectedCycle >= ProgressThreshold;
        ProgressFraction = status.Fraction;
        ProgressIndeterminate = status.Phase == CapturePhase.WaitingForTrigger;
        ProgressText = status.Phase switch
        {
            CapturePhase.Capturing => $"Capturing {Seconds(status.Position)} of {Seconds(status.Cycle)} s",
            CapturePhase.WaitingForTrigger => $"Waiting for trigger, {Seconds(status.Elapsed)} s",
            CapturePhase.Receiving => $"Receiving {Seconds(status.Position)} of {Seconds(status.Cycle)} s",
            _ => "",
        };

        static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>The device button toggles between running and stopped, as the spec asks. Each "BD" is one press (holding doesn't repeat).</summary>
    public async Task OnButtonPressedAsync()
    {
        if (!IsReady)
        {
            return;
        }

        if (RunMode == RunMode.Stopped)
        {
            await RunAsync();
        }
        else
        {
            await StopAsync();
        }
    }

    /// <summary>Called for each power-state message. On "fully on", sends the settings and resumes running, like the Android app.</summary>
    public async Task OnPowerChangedAsync(bool fullyOn)
    {
        IsReady = fullyOn;
        if (!fullyOn)
        {
            return;
        }

        await PushSettingsAsync();
        if (RunMode == RunMode.Running)
        {
            _roll.StartSegment(); // the link was down: leave a gap
            await StartAcquisitionAsync();
        }
    }

    public void OnDisconnected()
    {
        IsReady = false;
        RunMode = RunMode.Stopped;
        _progress.Stopped();
        UpdateProgress();
    }

    private void Update(ScopeSettings settings)
    {
        if (settings == Settings)
        {
            return;
        }

        var viewChanged = settings.TimeBase != Settings.TimeBase || settings.FrameSize != Settings.FrameSize;
        Settings = settings;
        OnSettingsChanged(viewChanged);

        // Most displayed values derive from the settings; refresh them all.
        OnPropertyChanged(string.Empty);
        _ = PushSettingsAsync();
    }

    private async Task PushSettingsAsync()
    {
        if (!IsReady)
        {
            return;
        }

        if (_pushing)
        {
            _pushPending = true;
            return;
        }

        _pushing = true;
        try
        {
            do
            {
                _pushPending = false;
                var settings = Settings;
                var write = _write(AeroscopeGatt.ScopeState, settings.ToRegisters().ToPacket());

                // Time the acknowledgement where it arrives, not when this resumes on the UI thread.
                var acknowledged = await write.ContinueWith(_ => _time.GetUtcNow(), TaskContinuationOptions.ExecuteSynchronously);
                await write;
                _timeline.Written(settings, acknowledged);
                _progress.Written(settings, acknowledged);
            }
            while (_pushPending);
        }
        finally
        {
            _pushing = false;
        }
    }

    /// <summary>R: a fresh acquisition, so its first frame already uses the written settings.</summary>
    private Task StartAcquisitionAsync()
    {
        _timeline.Started(_time.GetUtcNow());
        _progress.Started(_timeline.Latest, _time.GetUtcNow());
        UpdateProgress();
        return SendAsync("R");
    }

    private async Task StopAcquisitionAsync()
    {
        _progress.Stopped();
        UpdateProgress();

        // Stop, then cancel any frame already requested so no stale frame arrives later.
        await SendAsync("S");
        await SendAsync("X");
    }

    private Task SendAsync(string command) => _write(AeroscopeGatt.ScopeIn, Encoding.ASCII.GetBytes(command));
}
