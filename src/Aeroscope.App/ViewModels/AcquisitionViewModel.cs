using System.Text;
using Aeroscope.Core.Bluetooth;
using Aeroscope.Core.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aeroscope.App.ViewModels;

public enum RunMode { Stopped, Running, Single }

/// <summary>
/// Acquisition settings, run control and the latest waveform. Settings changes are
/// written to the scope immediately (coalesced while a write is in flight); the
/// scope applies them from the next frame on.
/// </summary>
public partial class AcquisitionViewModel : ObservableObject
{
    /// <summary>The device button's "BD"s came in bursts 0.3-0.5 s apart (probably repeats while held); only the first of a burst toggles.</summary>
    public static readonly TimeSpan ButtonRepeatGap = TimeSpan.FromSeconds(1);

    private const string NoValue = "–";

    private readonly Func<ushort, byte[], Task> _write;
    private readonly TimeProvider _time;
    private readonly RollBuffer _roll = new();
    private bool _pushing;
    private bool _pushPending;
    private DateTimeOffset _lastButton = DateTimeOffset.MinValue;

    /// <param name="write">Writes a payload to a characteristic (Scope In or Scope State).</param>
    /// <param name="time">Clock for the button repeat filter; the system clock if null.</param>
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaximumText), nameof(MinimumText), nameof(PeakToPeakText), nameof(MeanText))]
    public partial Waveform? Waveform { get; private set; }

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

    /// <summary>False in roll mode, where the scope streams continuously and ignores the trigger.</summary>
    public bool HasTrigger => !TimeBase.IsRoll;

    public string OffsetText => Units.Volts(OffsetVolts);

    public string TriggerLevelText => Units.Volts(TriggerLevelVolts);

    public string MaximumText => Waveform is null ? NoValue : Units.Volts(Waveform.Maximum);

    public string MinimumText => Waveform is null ? NoValue : Units.Volts(Waveform.Minimum);

    public string PeakToPeakText => Waveform is null ? NoValue : Units.Volts(Waveform.PeakToPeak);

    public string MeanText => Waveform is null ? NoValue : Units.Volts(Waveform.Mean);

    [RelayCommand]
    private void ZeroOffset() => OffsetVolts = 0;

    [RelayCommand]
    private void ZeroTriggerLevel() => TriggerLevelVolts = 0;

    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task RunAsync()
    {
        _roll.Clear();
        RunMode = RunMode.Running;
        await SendAsync("R");
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
        _roll.Clear();
        RunMode = RunMode.Single;
        await SendAsync("R");
    }

    public async Task OnFrameAsync(ScopeFrame frame)
    {
        // The screen freezes on stop, even if a frame already on its way arrives after it.
        // After a time base change the scope sends one more frame of the old kind; drop that too.
        if (RunMode == RunMode.Stopped || TimeBase.IsRoll != (frame.Samples.Length == RollBuffer.FrameLength))
        {
            return;
        }

        if (TimeBase.IsRoll)
        {
            _roll.Add(frame, Settings);
            Waveform = _roll.ToWaveform();
        }
        else
        {
            Waveform = Waveform.From(frame, Settings);
        }

        if (RunMode == RunMode.Single && (!TimeBase.IsRoll || _roll.IsFull))
        {
            RunMode = RunMode.Stopped;
            await StopAcquisitionAsync();
        }
    }

    /// <summary>The device button toggles between running and stopped, as the spec asks.</summary>
    public async Task OnButtonPressedAsync()
    {
        var now = _time.GetUtcNow();
        var repeat = now - _lastButton < ButtonRepeatGap;
        _lastButton = now;
        if (repeat || !IsReady)
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
            await SendAsync("R");
        }
    }

    public void OnDisconnected()
    {
        IsReady = false;
        RunMode = RunMode.Stopped;
    }

    private void Update(ScopeSettings settings)
    {
        if (settings == Settings)
        {
            return;
        }

        if (settings.TimeBase != Settings.TimeBase)
        {
            _roll.Clear();
        }

        Settings = settings;

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
                await _write(AeroscopeGatt.ScopeState, Settings.ToRegisters().ToPacket());
            }
            while (_pushPending);
        }
        finally
        {
            _pushing = false;
        }
    }

    private async Task StopAcquisitionAsync()
    {
        // Stop, then cancel any frame already requested so no stale frame arrives later.
        await SendAsync("S");
        await SendAsync("X");
    }

    private Task SendAsync(string command) => _write(AeroscopeGatt.ScopeIn, Encoding.ASCII.GetBytes(command));
}
