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
/// A frame larger than 512 samples is shown whole; it can be zoomed in down to the time base
/// and panned.
/// </summary>
public partial class AcquisitionViewModel
{
    /// <summary>Roll view scales, seconds per division, for zooming out over a long recording.</summary>
    public static IReadOnlyList<double> RollViewScales { get; } = [0.5, 1, 2, 5, 10, 20, 30, 60, 120, 300, 600, 1200, 1800, 3600];

    private readonly RollHistory _roll = new();
    private readonly FrameHistory _frames = new();
    private int _frameIndex = -1;
    private DateTimeOffset _rollViewStart;
    private double _rollSecondsPerDivision = TimeBase.Default.SecondsPerDivision;

    // Zoom into a frame wider than the screen: time base multiples shown (null = the whole
    // frame), and the time at the screen centre.
    private int? _frameZoom;
    private double _framePan;

    /// <summary>The screen follows the newest data.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoLiveCommand))]
    public partial bool IsLive { get; private set; } = true;

    /// <summary>Horizontal scale of the screen: the shown frame's time base times the zoom, or in roll mode the zoomed-out view.</summary>
    public double ViewSecondsPerDivision => IsRoll ? _rollSecondsPerDivision : ViewTimeBase.SecondsPerDivision * FrameZoom;

    public string HorizontalText
    {
        get
        {
            var timeBase = IsRoll ? TimeBase : ViewTimeBase;
            return ViewSecondsPerDivision != timeBase.SecondsPerDivision
                ? $"{timeBase}, view {Units.Seconds(ViewSecondsPerDivision)}/div"
                : timeBase.ToString();
        }
    }

    /// <summary>Time at the screen centre: 0 (the trigger) unless panned within a frame.</summary>
    public double ViewCentreSeconds => CanPan ? _framePan : 0;

    /// <summary>Zoomed into a frame wider than the screen.</summary>
    public bool CanPan => !IsRoll && FrameZoom < FrameScreens;

    /// <summary>Pan position: the time from the frame's left end to the screen's, in seconds.</summary>
    public double FramePanPosition
    {
        get => _framePan + FramePanMaximum / 2;
        set
        {
            _framePan = Math.Clamp(value, 0, FramePanMaximum) - FramePanMaximum / 2;
            RefreshScreen();
        }
    }

    public double FramePanMaximum => (FrameScreens - FrameZoom) * ScreenSeconds(1);

    public double FramePanViewport => ScreenSeconds(FrameZoom);

    public double FramePanSmallChange => ViewSecondsPerDivision;

    /// <summary>The trigger level the shown frame was taken with; null in roll mode or without a frame.</summary>
    public double? FrameTriggerLevelVolts => !IsRoll && _frameIndex >= 0 ? _frames[_frameIndex].Settings.TriggerLevelVolts : null;

    public bool HasHistory => IsRoll ? _roll.SampleCount > 0 : _frames.Count > 0;

    public double HistoryMaximum => IsRoll
        ? Math.Max(0, RollDuration - RollSpan.TotalSeconds)
        : Math.Max(0, _frames.Count - 1);

    public double HistoryViewport => IsRoll ? RollSpan.TotalSeconds : 1;

    /// <summary>A division, or a frame.</summary>
    public double HistorySmallChange => IsRoll ? _rollSecondsPerDivision : 1;

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

    private TimeSpan RollSpan => TimeSpan.FromSeconds(TimeBase.Divisions * _rollSecondsPerDivision);

    private double RollDuration => _roll.Start is { } first ? (_roll.End!.Value - first).TotalSeconds : 0;

    /// <summary>The time base of the frame shown: it may predate a time base change.</summary>
    private TimeBase ViewTimeBase => _frameIndex >= 0 ? _frames[_frameIndex].Settings.TimeBase : TimeBase;

    /// <summary>How many screens wide the frame shown is: 1 for 512 samples, 8 for 4096.</summary>
    private int FrameScreens => _frameIndex >= 0 ? Math.Max(1, _frames[_frameIndex].Frame.Samples.Length / ScopeSettings.FrameSizes[0]) : 1;

    /// <summary>Screens of the frame shown at once: from 1 (the time base) to all of them.</summary>
    private int FrameZoom => Math.Min(_frameZoom ?? FrameScreens, FrameScreens);

    private double ScreenSeconds(int screens) => screens * TimeBase.Divisions * ViewTimeBase.SecondsPerDivision;

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

    /// <summary>Roll mode: towards the time base. A frame wider than the screen: halves the time shown, about the screen centre.</summary>
    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private void ZoomIn()
    {
        if (IsRoll)
        {
            ZoomRoll(RollViewScales.LastOrDefault(s => s < _rollSecondsPerDivision, TimeBase.SecondsPerDivision));
        }
        else
        {
            _frameZoom = FrameZoom / 2;
            RefreshScreen();
        }
    }

    private bool CanZoomIn() => IsRoll ? _rollSecondsPerDivision > TimeBase.SecondsPerDivision : FrameZoom > 1;

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private void ZoomOut()
    {
        if (IsRoll)
        {
            ZoomRoll(RollViewScales.First(s => s > _rollSecondsPerDivision));
        }
        else
        {
            _frameZoom = FrameZoom * 2 >= FrameScreens ? null : FrameZoom * 2;
            RefreshScreen();
        }
    }

    private bool CanZoomOut() => IsRoll ? _rollSecondsPerDivision < RollViewScales[^1] : FrameZoom < FrameScreens;

    private void ZoomRoll(double secondsPerDivision)
    {
        var centre = _rollViewStart + RollSpan / 2;
        _rollSecondsPerDivision = Math.Max(secondsPerDivision, TimeBase.SecondsPerDivision);
        _rollViewStart = centre - RollSpan / 2; // when not live, zoom about the screen centre
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
            _rollSecondsPerDivision = TimeBase.SecondsPerDivision;
            _frameZoom = null;
            _framePan = 0;
            IsLive = true;
        }

        RefreshScreen();
    }

    private void RefreshScreen()
    {
        if (IsRoll)
        {
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
        OnPropertyChanged(nameof(ViewCentreSeconds));
        OnPropertyChanged(nameof(CanPan));
        OnPropertyChanged(nameof(FramePanMaximum));
        OnPropertyChanged(nameof(FramePanViewport));
        OnPropertyChanged(nameof(FramePanSmallChange));
        OnPropertyChanged(nameof(FramePanPosition));
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The frame shown: whole, or the part on screen when zoomed in.</summary>
    private ScreenTrace? FrameScreen()
    {
        if (_frameIndex < 0)
        {
            return null;
        }

        var waveform = _frames[_frameIndex].ToWaveform();
        if (!CanPan)
        {
            _framePan = 0;
            return ScreenTrace.From(waveform);
        }

        var limit = FramePanMaximum / 2;
        _framePan = Math.Clamp(_framePan, -limit, limit);
        var half = ScreenSeconds(FrameZoom) / 2;
        return ScreenTrace.Window(waveform, _framePan - half, _framePan + half);
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
