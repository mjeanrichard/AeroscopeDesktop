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

    /// <summary>With <see cref="IdlePowerOff"/>, the FPGA is turned off after this long stopped.</summary>
    public static readonly TimeSpan IdlePowerOffDelay = TimeSpan.FromMinutes(5);

    /// <summary>The scope answers "PF" about 1.4 s after being told to power up (captures 29, 30).</summary>
    private static readonly TimeSpan PowerUpTimeout = TimeSpan.FromSeconds(5);

    private bool _idlePowerOff;
    private DateTimeOffset _idleSince;
    private TaskCompletionSource<bool>? _poweredUp;

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
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand), nameof(RunStopCommand), nameof(SingleCommand))]
    public partial bool IsReady { get; private set; }

    /// <summary>The FPGA was turned off while idle ("PO"); Run, Single and calibrating turn it back on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunModeText))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(RunStopCommand), nameof(SingleCommand))]
    public partial bool IsFpgaOff { get; private set; }

    /// <summary>"PF" sent, waiting for the scope to report the FPGA ready.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunModeText))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(RunStopCommand), nameof(SingleCommand))]
    public partial bool IsPoweringUp { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunStopText), nameof(RunModeText))]
    [NotifyCanExecuteChangedFor(nameof(RunStopCommand))]
    public partial RunMode RunMode { get; private set; }

    /// <summary>What <see cref="RunStopCommand"/> does next.</summary>
    public string RunStopText => RunMode == RunMode.Stopped ? "Run" : "Stop";

    /// <summary>The run state under the Run button.</summary>
    public string RunModeText => IsPoweringUp ? "Turning the FPGA on..." : IsFpgaOff ? "Stopped, FPGA off to save power" : RunMode.ToString();

    /// <summary>Turn the FPGA off after <see cref="IdlePowerOffDelay"/> stopped (see <see cref="Tick"/>).</summary>
    public bool IdlePowerOff
    {
        get => _idlePowerOff;
        set
        {
            _idlePowerOff = value;
            _idleSince = _time.GetUtcNow(); // count from now, not from when it was last stopped
        }
    }

    partial void OnRunModeChanged(RunMode value) => _idleSince = _time.GetUtcNow();

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

    /// <summary>How far the offset reaches from 0 V either way, in divisions (as far as the old slider went).</summary>
    public const double OffsetLimitDivisions = 10;

    /// <summary>The − and + buttons (and Up/Down) move the offset and trigger level by this fraction of a division.</summary>
    public const double EntryStep = 0.1;

    /// <summary>Roll mode: the scope streams continuously and ignores the trigger.</summary>
    public bool IsRoll => TimeBase.IsRoll;

    public bool HasTrigger => !IsRoll;

    /// <summary>The offset as shown and typed, e.g. "1.20 V" or "-500 mV"; limited to ±10 divisions. Unreadable text is ignored.</summary>
    public string OffsetText
    {
        get => Units.Volts(OffsetVolts);
        set
        {
            if (Units.TryParseVolts(value, out var volts))
            {
                OffsetVolts = ClampOffset(volts);
            }

            OnPropertyChanged(); // show the value as applied
        }
    }

    /// <summary>The trigger level as shown and typed; limited to the screen, the codes the scope compares. Unreadable text is ignored.</summary>
    public string TriggerLevelText
    {
        get => Units.Volts(TriggerLevelVolts);
        set
        {
            if (Units.TryParseVolts(value, out var volts))
            {
                TriggerLevelVolts = ClampToScreen(volts);
            }

            OnPropertyChanged();
        }
    }

    private double ClampOffset(double volts)
        => Math.Clamp(volts, -OffsetLimitDivisions * Range.VoltsPerDivision, OffsetLimitDivisions * Range.VoltsPerDivision);

    private double ClampToScreen(double volts)
    {
        var half = VoltageRange.Divisions / 2.0 * Range.VoltsPerDivision;
        return Math.Clamp(volts, OffsetVolts - half, OffsetVolts + half);
    }

    /// <summary>Moves to the next step of <see cref="EntryStep"/> divisions up or down.</summary>
    private double Stepped(double volts, int steps)
    {
        var step = EntryStep * Range.VoltsPerDivision;
        return (Math.Round(volts / step) + steps) * step;
    }

    public string MaximumText => Screen is null ? NoValue : Units.Volts(Screen.Maximum);

    public string MinimumText => Screen is null ? NoValue : Units.Volts(Screen.Minimum);

    public string PeakToPeakText => Screen is null ? NoValue : Units.Volts(Screen.PeakToPeak);

    public string MeanText => Screen is null ? NoValue : Units.Volts(Screen.Mean);

    [RelayCommand]
    private void ZeroOffset() => OffsetVolts = 0;

    [RelayCommand]
    private void ZeroTriggerLevel() => TriggerLevelVolts = 0;

    [RelayCommand]
    private void OffsetUp() => OffsetVolts = ClampOffset(Stepped(OffsetVolts, 1));

    [RelayCommand]
    private void OffsetDown() => OffsetVolts = ClampOffset(Stepped(OffsetVolts, -1));

    [RelayCommand]
    private void TriggerLevelUp() => TriggerLevelVolts = ClampToScreen(Stepped(TriggerLevelVolts, 1));

    [RelayCommand]
    private void TriggerLevelDown() => TriggerLevelVolts = ClampToScreen(Stepped(TriggerLevelVolts, -1));

    /// <summary>Ready, or the FPGA is off and can be turned on first.</summary>
    private bool CanRun() => (IsReady || IsFpgaOff) && !IsPoweringUp;

    private bool CanRunStop() => RunMode == RunMode.Stopped ? CanRun() : IsReady;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (!await PowerUpAsync())
        {
            return;
        }

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

    /// <summary>One button for both: runs when stopped, stops a run or a single shot. The device button does the same.</summary>
    [RelayCommand(CanExecute = nameof(CanRunStop))]
    private Task RunStopAsync() => RunMode == RunMode.Stopped ? RunAsync() : StopAsync();

    /// <summary>
    /// The scope's own single-frame command ("F") rarely answers, so single shot is
    /// run mode that stops after the first frame. Turn auto trigger off to wait for a real edge.
    /// In roll mode it stops when the screen is full.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SingleAsync()
    {
        if (!await PowerUpAsync())
        {
            return;
        }

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

    /// <summary>Call a few times a second: moves the progress indicator along and turns the FPGA off when idle.</summary>
    public void Tick()
    {
        UpdateProgress();
        if (IdlePowerOff && IsReady && RunMode == RunMode.Stopped && !IsFpgaOff && _time.GetUtcNow() - _idleSince >= IdlePowerOffDelay)
        {
            // The scope answers "PO" (capture 29), which makes it not ready; settings are then held back until "PF".
            IsFpgaOff = true;
            _ = SendAsync("PO");
        }
    }

    /// <summary>
    /// Turns the FPGA back on if it was turned off while idle, and waits until the scope reports it ready
    /// and has the settings again (they are lost while it is off, capture 29). False if it didn't answer.
    /// </summary>
    public async Task<bool> PowerUpAsync()
    {
        if (!IsFpgaOff)
        {
            return true;
        }

        if (_poweredUp is null)
        {
            _poweredUp = new TaskCompletionSource<bool>();
            IsPoweringUp = true;
            await SendAsync("PF");
        }

        var poweredUp = _poweredUp.Task;
        if (await Task.WhenAny(poweredUp, Task.Delay(PowerUpTimeout, _time)) == poweredUp)
        {
            return await poweredUp;
        }

        _poweredUp = null; // no answer: the next Run tries again
        IsPoweringUp = false;
        return false;
    }

    /// <summary>
    /// Zero offsets the app adds to the offset DAC because the scope lost its calibration, or null when the
    /// scope applies its own (see <see cref="ScopeSettings.CalibrationOffsets"/>).
    /// </summary>
    public void UseCalibration(IReadOnlyList<short>? offsets) => Update(Settings with { CalibrationOffsets = offsets });

    /// <summary>Refreshes the progress indicator.</summary>
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
        if (CanRunStop())
        {
            await RunStopAsync();
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

        IsFpgaOff = false;
        _idleSince = _time.GetUtcNow();
        await PushSettingsAsync();
        if (RunMode == RunMode.Running)
        {
            _roll.StartSegment(); // the link was down: leave a gap
            await StartAcquisitionAsync();
        }

        // Last, so a Run waiting for this starts only now (and only once).
        FinishPowerUp(true);
    }

    private void FinishPowerUp(bool poweredUp)
    {
        var waiting = _poweredUp;
        _poweredUp = null;
        IsPoweringUp = false;
        waiting?.TrySetResult(poweredUp);
    }

    public void OnDisconnected()
    {
        IsReady = false;
        IsFpgaOff = false;
        FinishPowerUp(false);
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
        _idleSince = _time.GetUtcNow();
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
