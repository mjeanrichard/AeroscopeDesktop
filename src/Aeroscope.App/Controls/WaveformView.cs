using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Aeroscope.Core.Protocol;

namespace Aeroscope.App.Controls;

/// <summary>
/// Scope screen: 10 x 8 division grid, the trace, a ground marker (0 V) on the left,
/// the trigger level on the right and the trigger time at the top (unless <see cref="ShowTrigger"/> is off).
/// A second, blue level marker shows the trigger level the shown frame was taken with.
/// The ground marker (the offset) and the trigger level can be dragged with the mouse. Clicking
/// anywhere else places a cursor: a vertical line on the nearest measured sample, labelled with
/// its time and voltage; dragging moves it and a right-click removes it.
///
/// Drawn in layers so that dragging a marker or the cursor doesn't redraw the trace: the grid
/// (redrawn when the size changes), the trigger levels, the trace (redrawn when the data, scale,
/// centre or size change) and on top the cursor and markers. The trace holds only the samples
/// on screen, and at most a minimum and maximum per pixel column where they are denser.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty TracesProperty = Register<IReadOnlyList<Waveform>?>(nameof(Traces), null);
    public static readonly DependencyProperty VoltsPerDivisionProperty = Register(nameof(VoltsPerDivision), 1.0);
    public static readonly DependencyProperty SecondsPerDivisionProperty = Register(nameof(SecondsPerDivision), 1e-6);
    public static readonly DependencyProperty CentreVoltsProperty = Register(nameof(CentreVolts), 0.0, twoWay: true);
    public static readonly DependencyProperty TriggerLevelVoltsProperty = Register(nameof(TriggerLevelVolts), 0.0, twoWay: true);
    public static readonly DependencyProperty ShowTriggerProperty = Register(nameof(ShowTrigger), true);
    public static readonly DependencyProperty FrameTriggerLevelVoltsProperty = Register<double?>(nameof(FrameTriggerLevelVolts), null);
    public static readonly DependencyProperty CentreSecondsProperty = Register(nameof(CentreSeconds), 0.0);
    public static readonly DependencyProperty CursorSecondsProperty = Register<double?>(nameof(CursorSeconds), null);

    private const double MarkerSize = 7;

    /// <summary>Fewer samples than this to a division are drawn as dots too.</summary>
    private const double DotsBelowSamplesPerDivision = 20;

    private const double SampleDotRadius = 2;

    /// <summary>How close to a marker (or the trigger line) the mouse grabs it, in pixels.</summary>
    private const double GrabDistance = 8;

    /// <summary>Dragged values snap to this fraction of a division.</summary>
    private const double DragStep = 1.0 / 50;

    private DragHandle _dragging;

    private readonly DrawingVisual _gridLayer = new();
    private readonly DrawingVisual _levelLayer = new();
    private readonly DrawingVisual _traceLayer = new();
    private readonly DrawingVisual _overlayLayer = new();
    private Size _gridSize;
    private (IReadOnlyList<Waveform>? Traces, ScreenMapping Map) _traceDrawn;

    public WaveformView()
    {
        foreach (var layer in Layers)
        {
            AddVisualChild(layer);
        }
    }

    private DrawingVisual[] Layers => [_gridLayer, _levelLayer, _traceLayer, _overlayLayer];

    /// <summary>How often the trace layer has been drawn; moving a marker or the cursor must not add to it.</summary>
    public int TraceDrawCount { get; private set; }

    protected override int VisualChildrenCount => 4;

    protected override Visual GetVisualChild(int index) => Layers[index];

    private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x18)));
    private static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x2B, 0x31, 0x38))), 1));
    private static readonly Pen AxisPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x48, 0x52, 0x5C))), 1));
    private static readonly Pen TracePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x4C))), 1.5) { LineJoin = PenLineJoin.Round });
    private static readonly Brush GroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4C, 0xC9, 0x7A)));
    private static readonly Brush TriggerBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0x8C, 0x38)));
    private static readonly Pen TriggerLevelPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0xF2, 0x8C, 0x38))), 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush FrameTriggerBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x5C, 0xA8, 0xF2)));
    private static readonly Pen FrameTriggerLevelPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x5C, 0xA8, 0xF2))), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen CursorPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0xE6, 0xE9, 0xED))), 1));
    private static readonly Pen CursorMarkerPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xED))), 1.5));
    private static readonly Brush CursorLabelBackground = Frozen(new SolidColorBrush(Color.FromArgb(0xD0, 0x2B, 0x31, 0x38)));
    private static readonly Brush CursorTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xED)));
    private static readonly Typeface CursorTypeface = new("Consolas");

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

    /// <summary>Where the cursor is, in the traces' time; null for no cursor. Kept while new frames arrive.</summary>
    public double? CursorSeconds
    {
        get => (double?)GetValue(CursorSecondsProperty);
        set => SetValue(CursorSecondsProperty, value);
    }

    private ScreenMapping Map => new(RenderSize.Width, RenderSize.Height, VoltsPerDivision, SecondsPerDivision, CentreVolts, CentreSeconds);

    /// <summary>What the mouse would drag at this point: the ground marker, or the trigger level (its marker or anywhere along its line).</summary>
    public DragHandle HandleAt(Point point)
    {
        var map = Map;
        if (point.X <= MarkerSize + GrabDistance && Math.Abs(point.Y - Math.Clamp(map.Y(0), 0, map.Height)) <= GrabDistance)
        {
            return DragHandle.Offset;
        }

        return ShowTrigger && Math.Abs(point.Y - Math.Clamp(map.Y(TriggerLevelVolts), 0, map.Height)) <= GrabDistance
            ? DragHandle.TriggerLevel
            : DragHandle.None;
    }

    /// <summary>Moves a handle to a height on the screen (kept on the screen): sets the offset or the trigger level.</summary>
    public void DragTo(DragHandle handle, double y)
    {
        var map = Map;
        y = Math.Clamp(y, 0, map.Height);
        switch (handle)
        {
            case DragHandle.Offset:
                // 0 V goes to y: the centre is as far above 0 V as y is below the centre.
                CentreVolts = Snapped((y - map.Height / 2) / map.DivisionHeight * VoltsPerDivision);
                break;
            case DragHandle.TriggerLevel:
                TriggerLevelVolts = Snapped(map.VoltsAt(y));
                break;
        }
    }

    /// <summary>Puts the cursor at a point across the screen (kept on the screen).</summary>
    public void PlaceCursor(double x)
    {
        var map = Map;
        CursorSeconds = map.SecondsAt(Math.Clamp(x, 0, map.Width));
    }

    /// <summary>
    /// The cursor's label: the time (from the trigger, so not in roll mode) and voltage of the
    /// sample nearest the cursor, or "No data" where there is none.
    /// </summary>
    public static string CursorLabel(IEnumerable<Waveform>? traces, double seconds, bool withTime)
        => Waveform.SampleNear(traces ?? [], seconds) is { } sample
            ? withTime ? $"{Units.Seconds(sample.Time)}\n{Units.Volts(sample.Volts)}" : Units.Volts(sample.Volts)
            : "No data";

    private double Snapped(double volts) => Math.Round(volts / (VoltsPerDivision * DragStep)) * VoltsPerDivision * DragStep;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var position = e.GetPosition(this);
        _dragging = HandleAt(position);
        if (_dragging == DragHandle.None)
        {
            _dragging = DragHandle.Cursor;
            PlaceCursor(position.X);
        }

        if (CaptureMouse())
        {
            e.Handled = true;
        }

        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        CursorSeconds = null;
        e.Handled = true;
        base.OnMouseRightButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        if (_dragging == DragHandle.Cursor)
        {
            PlaceCursor(position.X);
        }
        else if (_dragging != DragHandle.None)
        {
            DragTo(_dragging, position.Y);
        }

        Cursor = (_dragging != DragHandle.None ? _dragging : HandleAt(position)) switch
        {
            DragHandle.Offset or DragHandle.TriggerLevel => Cursors.SizeNS,
            DragHandle.Cursor => Cursors.SizeWE,
            _ => Cursors.Cross,
        };
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _dragging = DragHandle.None;
        base.OnLostMouseCapture(e);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(Background, null, bounds); // also makes the whole screen take the mouse
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            foreach (var layer in Layers)
            {
                layer.RenderOpen().Close();
            }

            _gridSize = default;
            _traceDrawn = default;
            return;
        }

        var map = new ScreenMapping(bounds.Width, bounds.Height, VoltsPerDivision, SecondsPerDivision, CentreVolts, CentreSeconds);
        var clip = new RectangleGeometry(bounds);
        clip.Freeze();

        if (_gridSize != bounds.Size)
        {
            _gridSize = bounds.Size;
            using var grid = _gridLayer.RenderOpen();
            DrawGrid(grid, map);
        }

        using (var levels = _levelLayer.RenderOpen())
        {
            levels.PushClip(clip);
            if (ShowTrigger)
            {
                if (FrameTriggerLevelVolts is { } frameLevel)
                {
                    DrawTriggerLevel(levels, map, frameLevel, FrameTriggerLevelPen, FrameTriggerBrush);
                }

                DrawTriggerLevel(levels, map, TriggerLevelVolts, TriggerLevelPen, TriggerBrush); // the current level on top
            }

            levels.Pop();
        }

        if (!ReferenceEquals(_traceDrawn.Traces, Traces) || _traceDrawn.Map != map)
        {
            _traceDrawn = (Traces, map);
            TraceDrawCount++;
            using var trace = _traceLayer.RenderOpen();
            trace.PushClip(clip);
            foreach (var waveform in Traces ?? [])
            {
                DrawTrace(trace, map, waveform);
            }

            trace.Pop();
        }

        using var overlay = _overlayLayer.RenderOpen();
        overlay.PushClip(clip);
        if (CursorSeconds is { } cursor)
        {
            DrawCursor(overlay, map, cursor);
        }

        DrawGroundMarker(overlay, map);
        if (ShowTrigger)
        {
            DrawTriggerTimeMarker(overlay, map);
        }

        overlay.Pop();
    }

    /// <summary>The cursor line on the nearest sample, a ring on the trace there, and the label at the top beside the line.</summary>
    private void DrawCursor(DrawingContext dc, ScreenMapping map, double seconds)
    {
        var sample = Waveform.SampleNear(Traces ?? [], seconds);
        var x = map.X(sample?.Time ?? seconds);
        if (x < 0 || x > map.Width)
        {
            return; // panned away; it comes back with the view
        }

        dc.DrawLine(CursorPen, new Point(Snap(x), 0), new Point(Snap(x), map.Height));
        if (sample is { } s)
        {
            dc.DrawEllipse(TracePen.Brush, CursorMarkerPen, new Point(x, Math.Clamp(map.Y(s.Volts), 0, map.Height)), 4, 4);
        }

        var label = new FormattedText(CursorLabel(Traces, seconds, withTime: ShowTrigger), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, CursorTypeface, 12, CursorTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        const double gap = 6, padding = 4;
        var width = label.Width + 2 * padding;
        var left = x + gap + width <= map.Width ? x + gap : x - gap - width; // flips to the left near the right edge
        var box = new Rect(left, gap, width, label.Height + 2 * padding);
        dc.DrawRoundedRectangle(CursorLabelBackground, null, box, 3, 3);
        dc.DrawText(label, new Point(box.X + padding, box.Y + padding));
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
        var points = TracePoints(map, waveform);
        if (points.Count == 0)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: false);
            context.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, TracePen, geometry);

        if (ShowsSampleDots(map, waveform))
        {
            foreach (var point in points)
            {
                dc.DrawEllipse(TracePen.Brush, null, point, SampleDotRadius, SampleDotRadius);
            }
        }
    }

    /// <summary>
    /// The points to draw for a waveform: its samples on screen plus one beyond each edge, so the
    /// line runs to the edge. Where a pixel column holds more than two samples, only the first and
    /// last of them and their minimum and maximum are kept (in time order), so drawing stays fast and spikes stay visible.
    /// </summary>
    public static List<Point> TracePoints(ScreenMapping map, Waveform waveform)
    {
        var points = new List<Point>();
        var count = waveform.Volts.Length;
        var first = Math.Max(0, (int)Math.Floor((map.SecondsAt(0) - waveform.FirstSampleTime) / waveform.SampleInterval));
        var last = Math.Min(count - 1, (int)Math.Ceiling((map.SecondsAt(map.Width) - waveform.FirstSampleTime) / waveform.SampleInterval));
        if (first > last)
        {
            return points;
        }

        var pixelsPerSample = waveform.SampleInterval / map.SecondsPerDivision * map.DivisionWidth;
        if (pixelsPerSample >= 0.5)
        {
            for (var i = first; i <= last; i++)
            {
                points.Add(new Point(map.X(waveform.TimeOf(i)), map.Y(waveform.Volts[i])));
            }

            return points;
        }

        var i0 = first;
        while (i0 <= last)
        {
            // One pixel column: the samples whose x rounds down to the same pixel.
            var column = Math.Floor(map.X(waveform.TimeOf(i0)));
            var i1 = i0;
            int low = i0, high = i0;
            while (i1 + 1 <= last && Math.Floor(map.X(waveform.TimeOf(i1 + 1))) == column)
            {
                i1++;
                if (waveform.Volts[i1] < waveform.Volts[low]) low = i1;
                if (waveform.Volts[i1] > waveform.Volts[high]) high = i1;
            }

            foreach (var i in new SortedSet<int> { i0, low, high, i1 })
            {
                points.Add(new Point(map.X(waveform.TimeOf(i)), map.Y(waveform.Volts[i])));
            }

            i0 = i1 + 1;
        }

        return points;
    }

    /// <summary>Zoomed in so far that the samples are drawn as dots, so the lines between them don't pass for data.</summary>
    public static bool ShowsSampleDots(ScreenMapping map, Waveform waveform)
        => map.SecondsPerDivision / waveform.SampleInterval < DotsBelowSamplesPerDivision;

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

    private static DependencyProperty Register<T>(string name, T defaultValue, bool twoWay = false)
        => DependencyProperty.Register(name, typeof(T), typeof(WaveformView),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender
                | (twoWay ? FrameworkPropertyMetadataOptions.BindsTwoWayByDefault : 0)));
}

/// <summary>What a mouse drag moves; <see cref="Cursor"/> is anywhere else on the screen.</summary>
public enum DragHandle { None, Offset, TriggerLevel, Cursor }
