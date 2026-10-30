using System.Globalization;
using Aeroscope.Core.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aeroscope.App.ViewModels;

/// <summary>
/// History: every frame received while running is kept. Triggered frames are stepped through
/// one at a time (they are separate snapshots: most of the time between them was never
/// captured); roll mode is one long recording that scrolls in time and can be zoomed out
/// beyond the time base. While live, the screen follows the newest data; moving back detaches
/// it (acquisition carries on) until Live is pressed or the end is reached again.
/// The history position is a frame index for triggered frames and seconds for roll mode.
///
/// Zoom is display only: the time base sets the sample rate, the view scale (Ctrl+wheel) how
/// much time a division shows, in 1-2-5 steps about the screen centre. It stays the same while
/// stepping through the history, so frames taken at another rate show at the same scale. It
/// starts at 1:1 (50 samples a division) and goes back to it when the time base or frame size
/// changes. Zooming in stops at 5 samples a division of the fastest rate, whatever rate is
/// selected (50 ns/div, or 50 ms/div in roll mode), so older frames can be zoomed into as far
/// as newer ones; zooming out stops at the whole record (or in roll mode at an hour a division). A frame wider than the screen can be panned.
/// </summary>
public partial class AcquisitionViewModel
{
    /// <summary>View scales, seconds per division: 1-2-5 from 50 ns, then longer steps for a long roll recording.</summary>
    public static IReadOnlyList<double> ViewScales { get; } =
    [
        .. Enumerable.Range(-8, 9)
            .SelectMany(exponent => new[] { 1, 2, 5 }.Select(m => double.Parse($"{m}e{exponent}", CultureInfo.InvariantCulture)))
            .Where(s => s >= 50e-9),
        30, 60, 120, 300, 600, 1200, 1800, 3600,
    ];

    /// <summary>Zooming in stops at this many samples a division.</summary>
    private const int MinimumSamplesPerDivision = 5;

    /// <summary>A record this much wider than the screen still counts as one screen (512 samples on a 500-sample screen).</summary>
    private const double ScreenSlack = 1.05;

    private readonly RollHistory _roll = new();
    private readonly FrameHistory _frames = new();
    private int _frameIndex = -1;
    private DateTimeOffset _rollViewStart;
    private double _viewSecondsPerDivision = TimeBase.Default.SecondsPerDivision;

    // The frame shown, and the time at the screen centre when panned within it.
    private Waveform? _shown;
    private double _framePan;

    /// <summary>The screen follows the newest data.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoLiveCommand))]
    public partial bool IsLive { get; private set; } = true;

    /// <summary>Horizontal scale of the screen, the same for every frame in the history.</summary>
    public double ViewSecondsPerDivision => _viewSecondsPerDivision;

    /// <summary>Scale, the sample rate of the data shown and the zoom, e.g. "200 ns/div · 100 MS/s · ×2.5".</summary>
    public string HorizontalText
    {
        get
        {
            var text = Math.Abs(ZoomFactor - 1) > 1e-9 ? $"{ViewScaleText} · {ViewRateText} · {ZoomText}" : $"{ViewScaleText} · {ViewRateText}";
            return IsRoll ? text + " · roll" : text;
        }
    }

    /// <summary>The view scale, e.g. "200 ns/div".</summary>
    public string ViewScaleText => $"{Units.Seconds(_viewSecondsPerDivision)}/div";

    /// <summary>The sample rate of the data shown, e.g. "100 MS/s" (the frame's own, in the history).</summary>
    public string ViewRateText => Units.SampleRate(1 / ShownTimeBase.SampleInterval);

    /// <summary>How much the view magnifies the data shown: "×1" at 50 samples a division.</summary>
    public string ZoomText => $"×{ZoomFactor.ToString("G3", CultureInfo.InvariantCulture)}";

    private TimeBase ShownTimeBase => IsRoll ? TimeBase : ViewTimeBase;

    private double ZoomFactor => ShownTimeBase.SecondsPerDivision / _viewSecondsPerDivision;

    /// <summary>Time at the screen centre: 0 (the trigger) unless panned within a frame.</summary>
    public double ViewCentreSeconds => CanPan ? _framePan : 0;

    /// <summary>The frame shown is wider than the screen.</summary>
    public bool CanPan => !IsRoll && _shown is not null && Duration(_shown) > ScreenSpan * ScreenSlack;

    /// <summary>Pan position: the time from the frame's left end to the screen's, in seconds.</summary>
    public double FramePanPosition
    {
        get => CanPan ? _framePan - ScreenSpan / 2 - _shown!.FirstSampleTime : 0;
        set
        {
            if (CanPan)
            {
                _framePan = _shown!.FirstSampleTime + ScreenSpan / 2 + Math.Clamp(value, 0, FramePanMaximum);
                RefreshScreen();
            }
        }
    }

    public double FramePanMaximum => CanPan ? Duration(_shown!) - ScreenSpan : 0;

    public double FramePanViewport => ScreenSpan;

    public double FramePanSmallChange => ViewSecondsPerDivision;

    /// <summary>The trigger level the shown frame was taken with; null in roll mode or without a frame.</summary>
    public double? FrameTriggerLevelVolts => !IsRoll && _frameIndex >= 0 ? _frames[_frameIndex].Settings.TriggerLevelVolts : null;

    public bool HasHistory => IsRoll ? _roll.SampleCount > 0 : _frames.Count > 0;

    public double HistoryMaximum => IsRoll
        ? Math.Max(0, RollDuration - RollSpan.TotalSeconds)
        : Math.Max(0, _frames.Count - 1);

    public double HistoryViewport => IsRoll ? RollSpan.TotalSeconds : 1;

    /// <summary>A division, or a frame.</summary>
    public double HistorySmallChange => IsRoll ? _viewSecondsPerDivision : 1;

    /// <summary>A screen, or 10 frames.</summary>
    public double HistoryLargeChange => IsRoll ? RollSpan.TotalSeconds : 10;

    public double HistoryPosition
    {
        get => IsRoll
            ? (_roll.Start is { } first ? (_rollViewStart - first).TotalSeconds : 0)
            : Math.Max(0, _frameIndex);
        set
        {
            if (!HasHistory || Math.Abs(value - HistoryPosition) < 1e-9)
            {
                return;
            }

            var position = Math.Clamp(value, 0, HistoryMaximum);
            if (IsRoll)
            {
                _rollViewStart = _roll.Start!.Value + TimeSpan.FromSeconds(position);
                IsLive = position >= HistoryMaximum - HistorySmallChange / 10;
            }
            else
            {
                _frameIndex = (int)Math.Round(position);
                IsLive = _frameIndex == _frames.Count - 1;
            }

            RefreshScreen();
        }
    }

    public string HistoryText
    {
        get
        {
            if (!HasHistory)
            {
                return "No history";
            }

            var where = IsRoll
                ? $"{TimeText(_rollViewStart)} to {TimeText(_rollViewStart + RollSpan)}"
                : $"Frame {_frameIndex + 1} of {_frames.Count}, {TimeText(_frames[_frameIndex].Received)}";
            return IsLive ? $"{where} (live)" : where;
        }
    }

    private TimeSpan RollSpan => TimeSpan.FromSeconds(ScreenSpan);

    private double ScreenSpan => TimeBase.Divisions * _viewSecondsPerDivision;

    private double RollDuration => _roll.Start is { } first ? (_roll.End!.Value - first).TotalSeconds : 0;

    /// <summary>The time base of the frame shown: it may predate a time base change.</summary>
    private TimeBase ViewTimeBase => _frameIndex >= 0 ? _frames[_frameIndex].Settings.TimeBase : TimeBase;

    /// <summary>Zooming in stops at 5 samples a division of the fastest rate in this mode, not the selected one.</summary>
    private double SmallestViewScale
        => TimeBase.All.Where(t => t.IsRoll == IsRoll).Min(t => t.SampleInterval) * MinimumSamplesPerDivision;

    /// <summary>Zooming out stops at the first scale that shows the whole record, or in roll mode an hour a division.</summary>
    private double LargestViewScale => IsRoll
        ? ViewScales[^1]
        : ViewScales.First(s => s >= Settings.FrameSize * TimeBase.SampleInterval / TimeBase.Divisions / ScreenSlack * (1 - 1e-9));

    private static double Duration(Waveform waveform) => waveform.Volts.Length * waveform.SampleInterval;

    /// <summary>Moves through the history by small or large steps (see above); negative is back in time.</summary>
    public void Scroll(int steps, bool large = false) => HistoryPosition += steps * (large ? HistoryLargeChange : HistorySmallChange);

    [RelayCommand(CanExecute = nameof(CanGoLive))]
    private void GoLive()
    {
        IsLive = true;
        RefreshScreen();
    }

    private bool CanGoLive() => !IsLive;

    [RelayCommand]
    private void ClearHistory()
    {
        _roll.Clear();
        _frames.Clear();
        _frameIndex = -1;
        IsLive = true;
        RefreshScreen();
    }

    /// <summary>The next smaller view scale, down to 5 samples a division.</summary>
    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private void ZoomIn() => Zoom(ViewScales.Last(s => s < _viewSecondsPerDivision * (1 - 1e-9)));

    private bool CanZoomIn() => ViewScales.Any(s => s < _viewSecondsPerDivision * (1 - 1e-9) && s >= SmallestViewScale * (1 - 1e-9));

    /// <summary>The next larger view scale, up to the whole record.</summary>
    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private void ZoomOut() => Zoom(ViewScales.First(s => s > _viewSecondsPerDivision * (1 + 1e-9)));

    private bool CanZoomOut() => _viewSecondsPerDivision < LargestViewScale * (1 - 1e-9);

    /// <summary>Back to 1:1, 50 samples a division.</summary>
    [RelayCommand(CanExecute = nameof(CanResetZoom))]
    private void ResetZoom() => Zoom(TimeBase.SecondsPerDivision);

    private bool CanResetZoom() => Math.Abs(_viewSecondsPerDivision / TimeBase.SecondsPerDivision - 1) > 1e-9;

    /// <summary>Changes the scale about the screen centre.</summary>
    private void Zoom(double secondsPerDivision)
    {
        if (IsRoll && _roll.Start is not null)
        {
            var centre = _rollViewStart + RollSpan / 2;
            _viewSecondsPerDivision = secondsPerDivision;
            _rollViewStart = centre - RollSpan / 2; // when not live
        }
        else
        {
            _viewSecondsPerDivision = secondsPerDivision; // a pan within a frame keeps its centre
        }

        RefreshScreen();
    }

    /// <summary>Run or Single: show live data; roll mode starts a new segment.</summary>
    private void StartRecording()
    {
        _roll.StartSegment();
        IsLive = true;
        RefreshScreen();
    }

    /// <param name="settings">The settings the frame was taken with.</param>
    private void Record(ScopeFrame frame, ScopeSettings settings, DateTimeOffset received)
    {
        if (IsRoll)
        {
            _roll.Add(frame, settings, received);
        }
        else
        {
            var dropped = _frames.Add(new RecordedFrame(frame, settings, received));
            _frameIndex = Math.Max(0, _frameIndex - dropped);
        }

        RefreshScreen();
    }

    /// <param name="viewChanged">The time base or frame size changed: back to the whole of the newest data.</param>
    private void OnSettingsChanged(bool viewChanged)
    {
        if (viewChanged)
        {
            _viewSecondsPerDivision = TimeBase.SecondsPerDivision;
            _framePan = 0;
            IsLive = true;
        }

        RefreshScreen();
    }

    private void RefreshScreen()
    {
        if (IsRoll)
        {
            _shown = null;
            Screen = RollScreen();
        }
        else
        {
            if (IsLive)
            {
                _frameIndex = _frames.Count - 1;
            }

            Screen = FrameScreen();
        }

        OnPropertyChanged(nameof(FrameTriggerLevelVolts));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HistoryMaximum));
        OnPropertyChanged(nameof(HistoryViewport));
        OnPropertyChanged(nameof(HistorySmallChange));
        OnPropertyChanged(nameof(HistoryLargeChange));
        OnPropertyChanged(nameof(HistoryPosition));
        OnPropertyChanged(nameof(HistoryText));
        OnPropertyChanged(nameof(ViewSecondsPerDivision));
        OnPropertyChanged(nameof(HorizontalText));
        OnPropertyChanged(nameof(ViewScaleText));
        OnPropertyChanged(nameof(ViewRateText));
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(ViewCentreSeconds));
        OnPropertyChanged(nameof(CanPan));
        OnPropertyChanged(nameof(FramePanMaximum));
        OnPropertyChanged(nameof(FramePanViewport));
        OnPropertyChanged(nameof(FramePanSmallChange));
        OnPropertyChanged(nameof(FramePanPosition));
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
        ResetZoomCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The frame shown, whole: the screen clips it, and the readouts cover all of it.</summary>
    private ScreenTrace? FrameScreen()
    {
        _shown = _frameIndex >= 0 ? _frames[_frameIndex].ToWaveform() : null;
        if (_shown is null)
        {
            return null;
        }

        if (CanPan)
        {
            var half = ScreenSpan / 2;
            _framePan = Math.Clamp(_framePan, _shown.FirstSampleTime + half, _shown.FirstSampleTime + Duration(_shown) - half);
        }
        else
        {
            _framePan = 0;
        }

        return ScreenTrace.From(_shown);
    }

    private ScreenTrace? RollScreen()
    {
        if (_roll.Start is not { } first)
        {
            return null;
        }

        // Until the recording is a screen long it fills from the left edge, then it scrolls.
        var latestStart = _roll.End!.Value - RollSpan;
        var lastStart = latestStart > first ? latestStart : first;
        _rollViewStart = IsLive ? lastStart
            : _rollViewStart < first ? first
            : _rollViewStart > lastStart ? lastStart
            : _rollViewStart;
        return _roll.Window(_rollViewStart, RollSpan);
    }

    private string TimeText(DateTimeOffset time)
        => TimeZoneInfo.ConvertTime(time, _time.LocalTimeZone).ToString("HH:mm:ss.f", CultureInfo.InvariantCulture);
}
