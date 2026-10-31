using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aeroscope.App;
using Aeroscope.App.Controls;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.App;

[UseInvariantCulture]
public class UnitsTests
{
    [Theory]
    [InlineData(2.3529, "2.35 V")]
    [InlineData(12.345, "12.3 V")]
    [InlineData(123.4, "123 V")]
    [InlineData(0.5, "500 mV")]
    [InlineData(0.0123, "12.3 mV")]
    [InlineData(-0.25, "-250 mV")]
    [InlineData(0, "0 V")]
    public void Volts(double value, string expected)
    {
        Assert.Equal(expected, Units.Volts(value));
    }

    [Theory]
    [InlineData(500e-9, "500 ns")]
    [InlineData(1e-6, "1.00 µs")]
    [InlineData(5e-3, "5.00 ms")]
    [InlineData(2, "2.00 s")]
    public void Seconds(double value, string expected)
    {
        Assert.Equal(expected, Units.Seconds(value));
    }
}

[UseInvariantCulture]
public class SampleRateTests
{
    [Theory]
    [InlineData(100e6, "100 MS/s")]
    [InlineData(25e6, "25 MS/s")]
    [InlineData(2.5e6, "2.5 MS/s")]
    [InlineData(500e3, "500 kS/s")]
    [InlineData(2500, "2.5 kS/s")]
    [InlineData(100, "100 S/s")]
    [InlineData(10, "10 S/s")]
    public void Rates_use_k_and_M(double samplesPerSecond, string expected)
    {
        Assert.Equal(expected, Units.SampleRate(samplesPerSecond));
    }

    [Fact]
    public void Time_bases_show_their_rate_and_1_to_1_scale()
    {
        Assert.Equal("100 MS/s (500 ns/div)", Units.TimeBaseText(TimeBase.Default));
        Assert.Equal("5 kS/s (10 ms/div)", Units.TimeBaseText(TimeBase.All.Single(t => t.Name == "10 ms")));
        Assert.Equal("100 S/s (500 ms/div, roll)", Units.TimeBaseText(TimeBase.All.Single(t => t.Name == "500 ms")));
        Assert.Equal("100 S/s (500 ms/div, roll)", new TimeBaseText().Convert(TimeBase.All.Single(t => t.Name == "500 ms"), typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("100 MS/s", new TimeBaseText { Part = TimeBaseText.Parts.Rate }.Convert(TimeBase.Default, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("500 ns/div", new TimeBaseText { Part = TimeBaseText.Parts.Scale }.Convert(TimeBase.Default, typeof(string), null, CultureInfo.InvariantCulture));
    }
}

public class SampleDotTests
{
    [Theory]
    [InlineData(500e-9, false)] // 50 samples a division: a line
    [InlineData(200e-9, false)] // 20
    [InlineData(100e-9, true)]  // 10: dots on the line
    [InlineData(50e-9, true)]
    public void Samples_are_dotted_below_20_a_division(double secondsPerDivision, bool dots)
    {
        var map = new ScreenMapping(500, 400, 1, secondsPerDivision, 0);
        var waveform = new Waveform([0, 1, 2], SampleInterval: 10e-9, FirstSampleTime: 0);

        Assert.Equal(dots, WaveformView.ShowsSampleDots(map, waveform));
    }
}

public class VoltsEntryTests
{
    [Theory]
    [InlineData("1.2", 1.2)]
    [InlineData(" -0,5 V ", -0.5)]
    [InlineData("500 mV", 0.5)]
    [InlineData("20mv", 0.02)]
    [InlineData("-120 mV", -0.12)]
    [InlineData("250 µV", 250e-6)]
    [InlineData("3v", 3)]
    [InlineData("1e-3", 0.001)]
    public void Typed_voltages_are_read(string text, double expected)
    {
        Assert.True(Units.TryParseVolts(text, out var volts));
        Assert.Equal(expected, volts, precision: 12);
    }

    [Theory]
    [InlineData("")]
    [InlineData("V")]
    [InlineData("abc")]
    [InlineData("1.2 kV")]
    [InlineData("2 MV")]
    [InlineData("NaN")]
    public void Anything_else_is_rejected(string text)
    {
        Assert.False(Units.TryParseVolts(text, out _));
    }

    [Theory]
    [InlineData(1.2)]
    [InlineData(-0.5)]
    [InlineData(0.0123)]
    public void Shown_values_read_back(double volts)
    {
        Assert.True(Units.TryParseVolts(Units.Volts(volts), out var read));
        Assert.Equal(volts, read, precision: 9);
    }
}

public class ScreenMappingTests
{
    private static readonly ScreenMapping Map = new(Width: 500, Height: 400, VoltsPerDivision: 1, SecondsPerDivision: 1e-3, CentreVolts: 0);

    [Fact]
    public void Trigger_time_and_centre_voltage_are_the_screen_centre()
    {
        Assert.Equal(250, Map.X(0));
        Assert.Equal(200, Map.Y(0));
        Assert.Equal(200, (Map with { CentreVolts = 2 }).Y(2));
    }

    [Fact]
    public void One_division_per_unit()
    {
        Assert.Equal(50, Map.DivisionWidth);
        Assert.Equal(50, Map.DivisionHeight);
        Assert.Equal(300, Map.X(1e-3));
        Assert.Equal(150, Map.Y(1));
        Assert.Equal(400, Map.Y(-4));
    }

    [Theory]
    [InlineData(6, 0, 0)]
    [InlineData(256, 0, 250)]
    [InlineData(506, 0, 500)]
    [InlineData(256, 32, 250.5)]
    public void Matches_the_android_layout_of_a_512_sample_frame(int sample, byte subTrigger, double expectedX)
    {
        // Android: x = (sample - 6 + subtrigger/64) / 500 * width.
        var settings = new ScopeSettings();
        var waveform = Waveform.From(new ScopeFrame(new byte[512], subTrigger), settings);
        var map = Map with { SecondsPerDivision = settings.TimeBase.SecondsPerDivision };

        Assert.Equal(expectedX, map.X(waveform.TimeOf(sample)), precision: 9);
    }
}

/// <summary>Renders the control to a bitmap and checks where the trace and markers land.</summary>
public class WaveformViewTests
{
    private const int Width = 500;
    private const int Height = 400;

    private static Color[,] Render(Action<WaveformView> configure)
    {
        Color[,]? pixels = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new WaveformView();
                configure(view);
                view.Measure(new Size(Width, Height));
                view.Arrange(new Rect(0, 0, Width, Height));

                var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var raw = new byte[Width * Height * 4];
                bitmap.CopyPixels(raw, Width * 4, 0);

                pixels = new Color[Width, Height];
                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        var i = (y * Width + x) * 4;
                        pixels[x, y] = Color.FromRgb(raw[i + 2], raw[i + 1], raw[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return error is null ? pixels! : throw new InvalidOperationException("Rendering failed", error);
    }

    private static bool IsTrace(Color c) => c.R > 180 && c.G > 140 && c.B < 120;

    private static bool IsGround(Color c) => c.G > 150 && c.R < 120;

    private static bool IsTrigger(Color c) => c.R > 200 && c.G is > 100 and < 180 && c.B < 100;

    private static bool AnyInColumn(Color[,] pixels, int x, int fromY, int toY, Func<Color, bool> test)
        => Enumerable.Range(fromY, toY - fromY + 1).Any(y => test(pixels[x, y]));

    [Fact]
    public void Background_is_dark_between_grid_lines()
    {
        var pixels = Render(_ => { });

        var c = pixels[25, 25];
        Assert.True(c.R < 40 && c.G < 40 && c.B < 40, $"Got {c}");
    }

    [Fact]
    public void Flat_trace_is_drawn_at_its_voltage()
    {
        // 2 V at 1 V/div with 0 V at the centre: two divisions (100 px) above the centre.
        var pixels = Render(view => view.Traces = [new Waveform(Enumerable.Repeat(2.0, 512).ToArray(), 10e-9, -2.56e-6)]);

        Assert.True(AnyInColumn(pixels, 125, 98, 102, IsTrace));
        Assert.False(AnyInColumn(pixels, 125, 150, 250, IsTrace));
    }

    [Fact]
    public void Separate_traces_are_not_joined()
    {
        // Two recordings with a gap between them, as roll history draws runs separated by a stop.
        var pixels = Render(view => view.Traces =
        [
            new Waveform(Enumerable.Repeat(2.0, 400).ToArray(), 10e-9, -5e-6), // x 0-200, 2 V
            new Waveform(Enumerable.Repeat(-2.0, 400).ToArray(), 10e-9, 1e-6), // x 300-500, -2 V
        ]);

        Assert.True(AnyInColumn(pixels, 100, 98, 102, IsTrace));
        Assert.True(AnyInColumn(pixels, 400, 298, 302, IsTrace));
        Assert.False(AnyInColumn(pixels, 250, 0, Height - 1, IsTrace));
    }

    [Fact]
    public void Panning_moves_the_trace_and_the_trigger_time()
    {
        // 2 V from 0 to 2 µs; at 1 µs/div with 2 µs at the centre it spans x = 150-250, not 250-350.
        var pixels = Render(view =>
        {
            view.Traces = [new Waveform(Enumerable.Repeat(2.0, 201).ToArray(), 10e-9, 0)];
            view.CentreSeconds = 2e-6;
        });

        Assert.True(AnyInColumn(pixels, 200, 98, 102, IsTrace));
        Assert.False(AnyInColumn(pixels, 300, 0, Height - 1, IsTrace));
        Assert.True(AnyInColumn(pixels, 150, 0, 4, IsTrigger)); // t = 0 is now 2 divisions left of the centre
        Assert.False(AnyInColumn(pixels, Width / 2, 0, 4, IsTrigger));
    }

    [Fact]
    public void Offset_and_scale_move_the_trace()
    {
        var pixels = Render(view =>
        {
            view.Traces = [new Waveform(Enumerable.Repeat(2.0, 512).ToArray(), 10e-9, -2.56e-6)];
            view.VoltsPerDivision = 2;
            view.CentreVolts = 2; // the trace now sits on the centre line
        });

        Assert.True(AnyInColumn(pixels, 125, 197, 203, IsTrace));
        Assert.False(AnyInColumn(pixels, 125, 90, 110, IsTrace));
    }

    [Fact]
    public void No_trace_without_a_waveform()
    {
        var pixels = Render(_ => { });

        Assert.DoesNotContain(Enumerable.Range(0, Height), y => IsTrace(pixels[125, y]));
    }

    [Fact]
    public void Ground_and_trigger_markers()
    {
        var pixels = Render(view =>
        {
            view.CentreVolts = 1; // 0 V one division below the centre: y = 250
            view.TriggerLevelVolts = 2; // one division above the centre: y = 150
        });

        Assert.True(AnyInColumn(pixels, 2, 246, 254, IsGround));
        Assert.True(AnyInColumn(pixels, Width - 2, 146, 154, IsTrigger));
        Assert.True(AnyInColumn(pixels, Width / 2, 0, 4, IsTrigger)); // trigger time at the top centre
    }

    [Fact]
    public void Frame_trigger_level_is_a_second_marker_in_blue()
    {
        static bool IsFrameTrigger(Color c) => c.B > 200 && c.G is > 140 and < 200 && c.R < 120;

        var pixels = Render(view =>
        {
            view.TriggerLevelVolts = 2; // y = 100
            view.FrameTriggerLevelVolts = -1; // y = 250
        });

        Assert.True(AnyInColumn(pixels, Width - 2, 246, 254, IsFrameTrigger));
        Assert.True(AnyInColumn(pixels, Width - 2, 96, 104, IsTrigger));
        Assert.False(AnyInColumn(pixels, Width - 2, 96, 104, IsFrameTrigger));
    }

    [Fact]
    public void No_frame_trigger_marker_without_a_frame_level()
    {
        static bool IsBlue(Color c) => c.B > 200 && c.R < 120;

        var pixels = Render(view => view.TriggerLevelVolts = 2);

        Assert.False(AnyInColumn(pixels, Width - 2, 0, Height - 1, IsBlue));
    }

    [Fact]
    public void Roll_mode_hides_the_trigger_markers()
    {
        var pixels = Render(view =>
        {
            view.TriggerLevelVolts = 2;
            view.FrameTriggerLevelVolts = -1;
            view.ShowTrigger = false;
        });

        Assert.False(AnyInColumn(pixels, Width - 2, 0, Height - 1, IsTrigger));
        Assert.False(AnyInColumn(pixels, Width - 2, 246, 254, c => c.B > 200 && c.R < 120));
        Assert.False(AnyInColumn(pixels, Width / 2, 0, 4, IsTrigger));
        Assert.True(AnyInColumn(pixels, 2, 196, 204, IsGround)); // the ground marker stays
    }

    /// <summary>Lays the view out at 500 x 400 (50 px a division) and runs <paramref name="test"/> on its thread.</summary>
    private static void WithView(Action<WaveformView> test)
    {
        Render(view =>
        {
            view.Measure(new Size(Width, Height));
            view.Arrange(new Rect(0, 0, Width, Height));
            test(view);
        });
    }

    [Fact]
    public void Ground_marker_and_trigger_line_can_be_grabbed()
    {
        WithView(view =>
        {
            view.TriggerLevelVolts = 2; // y = 100; ground at y = 200

            Assert.Equal(DragHandle.Offset, view.HandleAt(new Point(4, 205)));
            Assert.Equal(DragHandle.TriggerLevel, view.HandleAt(new Point(Width - 3, 100)));
            Assert.Equal(DragHandle.TriggerLevel, view.HandleAt(new Point(250, 106))); // anywhere along the line
            Assert.Equal(DragHandle.None, view.HandleAt(new Point(250, 150)));
            Assert.Equal(DragHandle.None, view.HandleAt(new Point(250, 200))); // the ground marker only at the edge

            view.ShowTrigger = false; // roll mode
            Assert.Equal(DragHandle.None, view.HandleAt(new Point(250, 100)));
            Assert.Equal(DragHandle.Offset, view.HandleAt(new Point(4, 200)));
        });
    }

    [Fact]
    public void Clicking_anywhere_else_places_the_cursor_at_that_time()
    {
        WithView(view =>
        {
            view.SecondsPerDivision = 1e-6;
            Assert.Equal(DragHandle.None, view.HandleAt(new Point(375, 300))); // not a marker: the cursor

            view.PlaceCursor(375);

            Assert.Equal(2.5e-6, view.CursorSeconds!.Value, precision: 15); // 2.5 divisions right of the trigger

            view.CentreSeconds = 1e-6; // panned: the screen shows other times
            view.PlaceCursor(-20); // kept on the screen
            Assert.Equal(-4e-6, view.CursorSeconds!.Value, precision: 15);
        });
    }

    [Fact]
    [UseInvariantCulture]
    public void Cursor_label_shows_the_nearest_sample()
    {
        Waveform[] traces = [new([0.25, 1.5, -0.04], SampleInterval: 1e-6, FirstSampleTime: -1e-6)];

        Assert.Equal("0 s\n1.50 V",WaveformView.CursorLabel(traces, 0.3e-6, withTime: true));
        Assert.Equal("1.00 µs\n-40.0 mV", WaveformView.CursorLabel(traces, 0.9e-6, withTime: true));
        Assert.Equal("-40.0 mV", WaveformView.CursorLabel(traces, 0.9e-6, withTime: false)); // roll mode: no trigger to time from
        Assert.Equal("No data", WaveformView.CursorLabel(traces, 5e-6, withTime: true));
        Assert.Equal("No data", WaveformView.CursorLabel(null, 0, withTime: true));
    }

    [Fact]
    public void Cursor_is_a_line_through_the_screen_on_the_nearest_sample()
    {
        static bool IsCursor(Color c) => c.R > 150 && c.G > 150 && c.B > 150;

        var pixels = Render(view =>
        {
            view.Traces = [new Waveform(Enumerable.Repeat(2.0, 512).ToArray(), 10e-9, -2.56e-6)];
            view.SecondsPerDivision = 0.5e-6;
            view.CursorSeconds = 1.004e-6; // nearest sample 1.00 µs: two divisions right, x = 350
        });

        Assert.True(AnyInColumn(pixels, 350, 300, 390, IsCursor));
        Assert.False(AnyInColumn(pixels, 360, 300, 390, IsCursor));
        Assert.True(AnyInColumn(pixels, 352, 96, 104, IsTrace)); // the ring on the trace at 2 V (y = 100)
    }

    [Fact]
    public void No_cursor_until_placed()
    {
        var pixels = Render(view => view.Traces = [new Waveform(Enumerable.Repeat(2.0, 512).ToArray(), 10e-9, -2.56e-6)]);

        Assert.DoesNotContain(Enumerable.Range(0, Width), x => AnyInColumn(pixels, x, 300, 390, c => c.R > 150 && c.G > 150 && c.B > 150));
    }

    [Fact]
    public void Only_the_samples_on_screen_are_drawn()
    {
        // A 4096-sample frame at 1:1: one pixel a sample, 500 of them on screen.
        var map = new ScreenMapping(500, 400, 1, 500e-9, 0);
        var waveform = new Waveform(Enumerable.Range(0, 4096).Select(i => Math.Sin(i / 30.0)).ToArray(), 10e-9, -20.48e-6);

        var points = WaveformView.TracePoints(map, waveform);

        Assert.Equal(501, points.Count); // both edges fall on a sample here
        Assert.True(points[0].X <= 0 && points[^1].X >= 500);
    }

    [Fact]
    public void Dense_samples_keep_each_columns_extremes()
    {
        // The same frame zoomed out to 5 µs/div: about 10 samples to a pixel column.
        var map = new ScreenMapping(500, 400, 1, 5e-6, 0);
        var volts = Enumerable.Range(0, 4096).Select(i => Math.Sin(i / 30.0)).ToArray();
        volts[2000] = 3.5; // a one-sample spike
        var waveform = new Waveform(volts, 10e-9, -20.48e-6);

        var points = WaveformView.TracePoints(map, waveform);

        Assert.InRange(points.Count, 400, 4 * 420); // at most 4 a column, not 4096
        Assert.Contains(points, p => Math.Abs(p.Y - map.Y(3.5)) < 1e-9);
        Assert.Equal(map.Y(volts.Min()), points.Max(p => p.Y), 9);
        Assert.True(points.Zip(points.Skip(1)).All(pair => pair.First.X <= pair.Second.X)); // in time order
    }

    [Fact]
    public void Moving_the_trigger_level_or_cursor_keeps_the_drawn_trace()
    {
        WithView(view =>
        {
            view.Traces = [new Waveform(Enumerable.Repeat(2.0, 4096).ToArray(), 10e-9, -20.48e-6)];
            view.UpdateLayout();
            var drawn = view.TraceDrawCount;

            view.TriggerLevelVolts = 1.5;
            view.CursorSeconds = 1e-6;
            view.UpdateLayout();
            Assert.Equal(drawn, view.TraceDrawCount);

            view.CentreVolts = 1; // the trace moves: drawn again
            view.UpdateLayout();
            Assert.Equal(drawn + 1, view.TraceDrawCount);
        });
    }

    [Fact]
    public void Dragging_the_trigger_line_sets_the_level_where_it_is_dropped()
    {
        WithView(view =>
        {
            view.CentreVolts = 1;

            view.DragTo(DragHandle.TriggerLevel, 150); // a division above the centre
            Assert.Equal(2, view.TriggerLevelVolts, precision: 9);

            view.DragTo(DragHandle.TriggerLevel, 150.4); // 1.992 V: snaps to 1/50 division
            Assert.Equal(2, view.TriggerLevelVolts, precision: 9);

            view.DragTo(DragHandle.TriggerLevel, -80); // kept on the screen
            Assert.Equal(5, view.TriggerLevelVolts, precision: 9);
        });
    }

    [Fact]
    public void Dragging_the_ground_marker_moves_the_offset()
    {
        WithView(view =>
        {
            view.VoltsPerDivision = 0.1;

            view.DragTo(DragHandle.Offset, 250); // 0 V a division below the centre: the centre is +0.1 V
            Assert.Equal(0.1, view.CentreVolts, precision: 9);
            Assert.Equal(250, new ScreenMapping(Width, Height, 0.1, 1e-6, view.CentreVolts).Y(0), precision: 9);

            view.DragTo(DragHandle.Offset, 0);
            Assert.Equal(-0.4, view.CentreVolts, precision: 9);
        });
    }

    private sealed class Levels
    {
        public double Offset { get; set; }

        public double Trigger { get; set; }
    }

    [Fact]
    public void Dragged_values_reach_the_bound_settings()
    {
        var levels = new Levels();
        WithView(view =>
        {
            view.DataContext = levels;
            view.SetBinding(WaveformView.CentreVoltsProperty, nameof(Levels.Offset));
            view.SetBinding(WaveformView.TriggerLevelVoltsProperty, nameof(Levels.Trigger));

            view.DragTo(DragHandle.TriggerLevel, 100);
            view.DragTo(DragHandle.Offset, 250);
        });

        Assert.Equal(1, levels.Offset, precision: 9);
        Assert.Equal(2, levels.Trigger, precision: 9);
    }

    [Fact]
    public void Off_screen_ground_marker_sticks_to_the_edge()
    {
        var pixels = Render(view => view.CentreVolts = 100);

        Assert.True(AnyInColumn(pixels, 2, Height - 6, Height - 1, IsGround));
    }
}
