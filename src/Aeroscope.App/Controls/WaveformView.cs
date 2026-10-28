using System.Windows;
using System.Windows.Media;
using Aeroscope.Core.Protocol;

namespace Aeroscope.App.Controls;

/// <summary>
/// Scope screen: 10 x 8 division grid, the trace, a ground marker (0 V) on the left,
/// the trigger level on the right and the trigger time at the top (unless <see cref="ShowTrigger"/> is off).
/// A second, blue level marker shows the trigger level the shown frame was taken with.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty TracesProperty = Register<IReadOnlyList<Waveform>?>(nameof(Traces), null);
    public static readonly DependencyProperty VoltsPerDivisionProperty = Register(nameof(VoltsPerDivision), 1.0);
    public static readonly DependencyProperty SecondsPerDivisionProperty = Register(nameof(SecondsPerDivision), 1e-6);
    public static readonly DependencyProperty CentreVoltsProperty = Register(nameof(CentreVolts), 0.0);
    public static readonly DependencyProperty TriggerLevelVoltsProperty = Register(nameof(TriggerLevelVolts), 0.0);
    public static readonly DependencyProperty ShowTriggerProperty = Register(nameof(ShowTrigger), true);
    public static readonly DependencyProperty FrameTriggerLevelVoltsProperty = Register<double?>(nameof(FrameTriggerLevelVolts), null);
    public static readonly DependencyProperty CentreSecondsProperty = Register(nameof(CentreSeconds), 0.0);

    private const double MarkerSize = 7;

    private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x18)));
    private static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x2B, 0x31, 0x38))), 1));
    private static readonly Pen AxisPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x48, 0x52, 0x5C))), 1));
    private static readonly Pen TracePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x4C))), 1.5) { LineJoin = PenLineJoin.Round });
    private static readonly Brush GroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4C, 0xC9, 0x7A)));
    private static readonly Brush TriggerBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0x8C, 0x38)));
    private static readonly Pen TriggerLevelPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0xF2, 0x8C, 0x38))), 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush FrameTriggerBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x5C, 0xA8, 0xF2)));
    private static readonly Pen FrameTriggerLevelPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x5C, 0xA8, 0xF2))), 1) { DashStyle = DashStyles.Dash });

    /// <summary>The traces to draw, with times relative to the screen centre.</summary>
    public IReadOnlyList<Waveform>? Traces
    {
        get => (IReadOnlyList<Waveform>?)GetValue(TracesProperty);
        set => SetValue(TracesProperty, value);
    }

    public double VoltsPerDivision
    {
        get => (double)GetValue(VoltsPerDivisionProperty);
        set => SetValue(VoltsPerDivisionProperty, value);
    }

    public double SecondsPerDivision
    {
        get => (double)GetValue(SecondsPerDivisionProperty);
        set => SetValue(SecondsPerDivisionProperty, value);
    }

    /// <summary>Time at the horizontal centre of the screen: 0 (the trigger) unless panned.</summary>
    public double CentreSeconds
    {
        get => (double)GetValue(CentreSecondsProperty);
        set => SetValue(CentreSecondsProperty, value);
    }

    /// <summary>Voltage at the vertical centre of the screen (the offset).</summary>
    public double CentreVolts
    {
        get => (double)GetValue(CentreVoltsProperty);
        set => SetValue(CentreVoltsProperty, value);
    }

    public double TriggerLevelVolts
    {
        get => (double)GetValue(TriggerLevelVoltsProperty);
        set => SetValue(TriggerLevelVoltsProperty, value);
    }

    /// <summary>False hides the trigger markers (roll mode has no trigger).</summary>
    public bool ShowTrigger
    {
        get => (bool)GetValue(ShowTriggerProperty);
        set => SetValue(ShowTriggerProperty, value);
    }

    /// <summary>The trigger level the shown frame was taken with; null hides its marker.</summary>
    public double? FrameTriggerLevelVolts
    {
        get => (double?)GetValue(FrameTriggerLevelVoltsProperty);
        set => SetValue(FrameTriggerLevelVoltsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(Background, null, bounds);
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            return;
        }

        var map = new ScreenMapping(bounds.Width, bounds.Height, VoltsPerDivision, SecondsPerDivision, CentreVolts, CentreSeconds);
        dc.PushClip(new RectangleGeometry(bounds));

        DrawGrid(dc, map);
        if (ShowTrigger)
        {
            if (FrameTriggerLevelVolts is { } frameLevel)
            {
                DrawTriggerLevel(dc, map, frameLevel, FrameTriggerLevelPen, FrameTriggerBrush);
            }

            DrawTriggerLevel(dc, map, TriggerLevelVolts, TriggerLevelPen, TriggerBrush); // the current level on top
        }

        foreach (var waveform in Traces ?? [])
        {
            DrawTrace(dc, map, waveform);
        }

        DrawGroundMarker(dc, map);
        if (ShowTrigger)
        {
            DrawTriggerTimeMarker(dc, map);
        }

        dc.Pop();
    }

    private static void DrawGrid(DrawingContext dc, ScreenMapping map)
    {
        for (var i = 0; i <= TimeBase.Divisions; i++)
        {
            var x = Snap(i * map.DivisionWidth);
            dc.DrawLine(i == TimeBase.Divisions / 2 ? AxisPen : GridPen, new Point(x, 0), new Point(x, map.Height));
        }

        for (var i = 0; i <= VoltageRange.Divisions; i++)
        {
            var y = Snap(i * map.DivisionHeight);
            dc.DrawLine(i == VoltageRange.Divisions / 2 ? AxisPen : GridPen, new Point(0, y), new Point(map.Width, y));
        }

        // Minor ticks on the centre axes, 5 per division.
        var centreX = Snap(map.Width / 2);
        var centreY = Snap(map.Height / 2);
        for (var i = 0; i <= TimeBase.Divisions * 5; i++)
        {
            var x = Snap(i * map.DivisionWidth / 5);
            dc.DrawLine(AxisPen, new Point(x, centreY - 3), new Point(x, centreY + 3));
        }

        for (var i = 0; i <= VoltageRange.Divisions * 5; i++)
        {
            var y = Snap(i * map.DivisionHeight / 5);
            dc.DrawLine(AxisPen, new Point(centreX - 3, y), new Point(centreX + 3, y));
        }
    }

    private static void DrawTrace(DrawingContext dc, ScreenMapping map, Waveform waveform)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < waveform.Volts.Length; i++)
            {
                var point = new Point(map.X(waveform.TimeOf(i)), map.Y(waveform.Volts[i]));
                if (i == 0)
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: true);
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, TracePen, geometry);
    }

    private static void DrawTriggerLevel(DrawingContext dc, ScreenMapping map, double volts, Pen line, Brush marker)
    {
        var y = Math.Clamp(map.Y(volts), 0, map.Height);
        dc.DrawLine(line, new Point(0, y), new Point(map.Width, y));
        dc.DrawGeometry(marker, null, Triangle(new Point(map.Width, y), new Vector(-MarkerSize, 0)));
    }

    private static void DrawGroundMarker(DrawingContext dc, ScreenMapping map)
    {
        var y = Math.Clamp(map.Y(0), 0, map.Height);
        dc.DrawGeometry(GroundBrush, null, Triangle(new Point(0, y), new Vector(MarkerSize, 0)));
    }

    private static void DrawTriggerTimeMarker(DrawingContext dc, ScreenMapping map)
    {
        dc.DrawGeometry(TriggerBrush, null, Triangle(new Point(map.X(0), 0), new Vector(0, MarkerSize)));
    }

    /// <summary>A filled marker triangle whose base is centred on <paramref name="baseCentre"/> and whose tip is at baseCentre + depth.</summary>
    private static Geometry Triangle(Point baseCentre, Vector depth)
    {
        var side = new Vector(-depth.Y, depth.X);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(baseCentre + side, isFilled: true, isClosed: true);
            context.LineTo(baseCentre + depth, isStroked: false, isSmoothJoin: false);
            context.LineTo(baseCentre - side, isStroked: false, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>Half-pixel offset so 1 px lines stay crisp.</summary>
    private static double Snap(double value) => Math.Floor(value) + 0.5;

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static DependencyProperty Register<T>(string name, T defaultValue)
        => DependencyProperty.Register(name, typeof(T), typeof(WaveformView),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
}
