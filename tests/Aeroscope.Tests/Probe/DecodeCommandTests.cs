using System.IO;
using Aeroscope.Probe;

namespace Aeroscope.Tests.Probe;

[Collection(ConsoleCollection.Name)]
[UseInvariantCulture]
public sealed class DecodeCommandTests : IDisposable
{
    private readonly TextWriter _originalOut = Console.Out;
    private readonly StringWriter _output = new();

    public DecodeCommandTests() => Console.SetOut(_output);

    public void Dispose() => Console.SetOut(_originalOut);

    private static string CapturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Captures", name);

    [Fact]
    public void Decodes_replies_and_frames_from_a_real_capture()
    {
        var exitCode = DecodeCommand.Run([CapturePath(Captures.FramesDefault)]);

        var output = _output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("Power: fully on (FPGA ready)", output);
        Assert.Contains("frame #5: 512 samples, subtrigger 0", output);
        Assert.EndsWith($"5 frame(s){Environment.NewLine}", output);
    }

    [Fact]
    public void Samples_and_plot_options_print_the_frame()
    {
        DecodeCommand.Run([CapturePath(Captures.RawSingle), "--samples", "--plot"]);

        var output = _output.ToString();
        Assert.Contains("   480: ", output); // last row of 32 samples
        Assert.Contains("|####", output);
    }

    [Fact]
    public void Requires_a_file()
    {
        Assert.Throws<ArgumentException>(() => DecodeCommand.Run([]));
    }

    [Fact]
    public void Plot_of_a_flat_line_fills_one_row()
    {
        var plot = DecodeCommand.Plot(Enumerable.Repeat((byte)139, 512).ToArray(), width: 10, height: 4).Split(Environment.NewLine);

        // Row labels are the top of each band; 139 falls in 128-191.
        Assert.Equal(["  255 |          ", "  191 |##########", "  127 |          ", "   63 |          "], plot);
    }

    [Fact]
    public void Plot_column_spans_from_bucket_min_to_max()
    {
        var plot = DecodeCommand.Plot([0, 255], width: 1, height: 4).Split(Environment.NewLine);

        Assert.Equal(["  255 |#", "  191 |#", "  127 |#", "   63 |#"], plot);
    }

    [Fact]
    public void Plot_uses_one_column_per_sample_for_short_frames()
    {
        var plot = DecodeCommand.Plot([0, 255], width: 10, height: 4).Split(Environment.NewLine);

        Assert.Equal(["  255 | #", "  191 |  ", "  127 |  ", "   63 |# "], plot);
    }
}
