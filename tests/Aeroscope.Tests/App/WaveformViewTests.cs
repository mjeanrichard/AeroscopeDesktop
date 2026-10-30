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
