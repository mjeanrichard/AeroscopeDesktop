using System.IO;
using System.Globalization;
using Aeroscope.Core.Diagnostics;

namespace Aeroscope.Tests.Core;

public class CaptureFileTests
{
    [Fact]
    public void Line_round_trips()
    {
        var packet = new CapturedPacket(TimeSpan.FromSeconds(12.345), CaptureChannel.State, [0x03, 0x80, 0x00, 0xA2]);

        var line = CaptureFile.FormatLine(packet);
        var parsed = CaptureFile.ParseLine(line);

        Assert.Equal("12.345 STATE 03 80 00 A2", line);
        Assert.Equal(packet.Time, parsed.Time);
        Assert.Equal(packet.Channel, parsed.Channel);
        Assert.Equal(packet.Bytes, parsed.Bytes);
    }

    [Fact]
    public void Formatting_ignores_the_current_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-CH");
        try
        {
            var line = CaptureFile.FormatLine(new CapturedPacket(TimeSpan.FromSeconds(1.5), CaptureChannel.In, [0x52]));
            Assert.Equal("1.500 IN    52", line);
            Assert.Equal(TimeSpan.FromSeconds(1.5), CaptureFile.ParseLine(line).Time);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Reader_skips_comments_and_blank_lines()
    {
        var text = """
            # Aeroscope capture

            0.020 OUT   50 46
            # step QTI
            0.043 in    51 54 49
            """;

        var packets = CaptureFile.Read(new StringReader(text));

        Assert.Equal([CaptureChannel.Out, CaptureChannel.In], packets.Select(p => p.Channel));
        Assert.Equal([0x51, 0x54, 0x49], packets[1].Bytes);
    }

    [Fact]
    public void Real_captures_load()
    {
        var packets = Captures.Load(Captures.FramesDefault);

        Assert.Equal(136, packets.Count(p => p.Channel == CaptureChannel.Data));
        Assert.All(packets, p => Assert.Equal(20, p.Bytes.Length));
        Assert.Equal(packets.OrderBy(p => p.Time), packets); // in time order
    }

    [Theory]
    [InlineData("0.1")]
    [InlineData("x OUT 50")]
    [InlineData("0.1 NOPE 50")]
    [InlineData("0.1 OUT ZZ")]
    public void Malformed_lines_throw(string line)
    {
        Assert.ThrowsAny<Exception>(() => CaptureFile.ParseLine(line));
    }
}
