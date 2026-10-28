using System.Globalization;
using Aeroscope.Core.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aeroscope.App.ViewModels;

/// <summary>
/// History: every frame received while running is kept. Triggered frames are stepped through
/// one at a time; roll mode is one long recording that scrolls in time and can be zoomed out
/// beyond the time base. While live, the screen follows the newest data; moving back detaches
/// it (acquisition carries on) until Live is pressed or the end is reached again.
/// The history position is a frame index for triggered frames and seconds for roll mode.
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

    /// <summary>The screen follows the newest data.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoLiveCommand))]
    public partial bool IsLive { get; private set; } = true;

    /// <summary>Horizontal scale of the screen: the time base, or in roll mode the zoomed-out view.</summary>
    public double ViewSecondsPerDivision => IsRoll ? _rollSecondsPerDivision : TimeBase.SecondsPerDivision;

    public string HorizontalText => IsRoll && _rollSecondsPerDivision != TimeBase.SecondsPerDivision
        ? $"{TimeBase}, view {Units.Seconds(_rollSecondsPerDivision)}/div"
        : TimeBase.ToString();

    public bool HasHistory => IsRoll ? _roll.SampleCount > 0 : _frames.Count > 0;

    public double HistoryMaximum => IsRoll
        ? Math.Max(0, RollDuration - RollSpan.TotalSeconds)
        : Math.Max(0, _frames.Count - 1);

    public double HistoryViewport => IsRoll ? RollSpan.TotalSeconds : 1;

    public double HistorySmallChange => IsRoll ? _rollSecondsPerDivision : 1;

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

    /// <summary>Moves through the history by small steps (a division, or a frame); negative is back in time.</summary>
    public void Scroll(int steps) => HistoryPosition += steps * HistorySmallChange;

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

    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private void ZoomIn() => Zoom(RollViewScales.LastOrDefault(s => s < _rollSecondsPerDivision, TimeBase.SecondsPerDivision));

    private bool CanZoomIn() => IsRoll && _rollSecondsPerDivision > TimeBase.SecondsPerDivision;

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private void ZoomOut() => Zoom(RollViewScales.First(s => s > _rollSecondsPerDivision));

    private bool CanZoomOut() => IsRoll && _rollSecondsPerDivision < RollViewScales[^1];

    private void Zoom(double secondsPerDivision)
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

    private void Record(ScopeFrame frame, DateTimeOffset received)
    {
        if (IsRoll)
        {
            _roll.Add(frame, Settings, received);
        }
        else
        {
            var dropped = _frames.Add(new RecordedFrame(frame, Settings, received));
            _frameIndex = Math.Max(0, _frameIndex - dropped);
        }

        RefreshScreen();
    }

    private void OnSettingsChanged(bool timeBaseChanged)
    {
        if (timeBaseChanged)
        {
            _rollSecondsPerDivision = TimeBase.SecondsPerDivision;
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

            Screen = _frameIndex >= 0 ? ScreenTrace.From(_frames[_frameIndex].ToWaveform()) : null;
        }

        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HistoryMaximum));
        OnPropertyChanged(nameof(HistoryViewport));
        OnPropertyChanged(nameof(HistorySmallChange));
        OnPropertyChanged(nameof(HistoryLargeChange));
        OnPropertyChanged(nameof(HistoryPosition));
        OnPropertyChanged(nameof(HistoryText));
        OnPropertyChanged(nameof(ViewSecondsPerDivision));
        OnPropertyChanged(nameof(HorizontalText));
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
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
