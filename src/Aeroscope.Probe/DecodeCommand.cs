using System.Text;
using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Probe;

internal static class DecodeCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("decode needs a capture file.");
        }

        var printSamples = args.Contains("--samples");
        var plot = args.Contains("--plot");

        var assembler = new FrameAssembler();
        var frameCount = 0;
        assembler.FrameCompleted += (_, frame) =>
        {
            frameCount++;
            Console.WriteLine($"        frame #{frameCount}: {frame}");
            if (printSamples)
            {
                Console.WriteLine(FormatSamples(frame.Samples));
            }

            if (plot)
            {
                Console.WriteLine(Plot(frame.Samples));
            }
        };
        assembler.FrameDropped += (_, reason) => Console.WriteLine($"        frame dropped: {reason}");

        foreach (var packet in CaptureFile.Read(args[0]))
        {
            var line = CaptureFile.FormatLine(packet);
            switch (packet.Channel)
            {
                case CaptureChannel.Out:
                    Console.WriteLine($"{line}  {ScopeOutMessage.Parse(packet.Bytes)}");
                    break;
                case CaptureChannel.Data:
                    assembler.AddPacket(packet.Bytes);
                    break;
                default:
                    Console.WriteLine(line);
                    break;
            }
        }

        Console.WriteLine($"{frameCount} frame(s)");
        return 0;
    }

    private static string FormatSamples(byte[] samples)
        => string.Join(Environment.NewLine, samples.Chunk(32).Select((row, i) => $"  {i * 32,4}: {string.Join(" ", row.Select(s => s.ToString().PadLeft(3)))}"));

    /// <summary>Min/max envelope of the frame, 0 at the bottom and 255 at the top.</summary>
    public static string Plot(byte[] samples, int width = 100, int height = 16)
    {
        var columns = Math.Min(width, samples.Length);
        var rows = new StringBuilder[height];
        for (var r = 0; r < height; r++)
        {
            rows[r] = new StringBuilder(new string(' ', columns));
        }

        for (var c = 0; c < columns; c++)
        {
            var bucket = samples[(c * samples.Length / columns)..((c + 1) * samples.Length / columns)];
            var top = height - 1 - bucket.Max() * height / 256;
            var bottom = height - 1 - bucket.Min() * height / 256;
            for (var r = top; r <= bottom; r++)
            {
                rows[r][c] = '#';
            }
        }

        return string.Join(Environment.NewLine, rows.Select((row, r) => $"  {255 - r * 256 / height,3} |{row}"));
    }
}
