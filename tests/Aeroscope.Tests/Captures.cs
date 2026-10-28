using System.IO;
using Aeroscope.Core.Diagnostics;

namespace Aeroscope.Tests;

/// <summary>
/// Recordings from a real Aeroscope (serial 1000057, FPGA rev 5, firmware rev 5),
/// made with "probe session --out". Unless noted, the input was unconnected, so
/// frames are a flat line around ADC code 130-139.
/// </summary>
internal static class Captures
{
    /// <summary>
    /// Probe tip held by hand (50 Hz mains hum) at 5 ms/div: 4 frames at 1 V/div (codes 100-175),
    /// then 3 at 2 V/div (codes 115-153). Then F and L, which got no reply.
    /// </summary>
    public const string TouchHum = "06-touch-hum.txt";

    /// <summary>
    /// Probe held, 5 ms/div, rising edge without auto trigger: 2 run frames, S X, then the one
    /// F that ever returned a frame, and L returning the same acquisition as 4096 samples.
    /// </summary>
    public const string SingleThenFull = "07-touch-normal-trigger.txt";

    /// <summary>Device button tapped quickly: 26 "BD" packets, 0.26-0.7 s apart, nothing on Scope Data.</summary>
    public const string Button = "11-button.txt";

    /// <summary>
    /// Device button: 5 presses ("BD" at 4.9, 19.3, 28.3, 58.5 and 59.4 s, the last two deliberate
    /// presses 0.8 s apart), one of them held about 3 s without repeating. Telemetry "T" twice.
    /// </summary>
    public const string ButtonPresses = "19-button-presses.txt";

    /// <summary>Read depth 9 with read start 0: run mode streams full 4096-sample frames.</summary>
    public const string RunFullDepth = "15-run-read-depth-4096.txt";

    /// <summary>
    /// Settings rewritten while running: offset DAC 0x8000, 0x8400, 0x7C00 at 1 V/div, then
    /// 0x8000, 0x8064, 0x7F9C at 100 mV/div (the last saturates at 255). The first frame
    /// after each write still uses the old settings.
    /// </summary>
    public const string OffsetDac = "16-offset-dac.txt";

    /// <summary>
    /// Roll mode: 500 ms/div (sampler 0xE7) without auto trigger streams 5 one-packet 16-sample
    /// frames 160 ms apart; 5 ms/div gives one stale 16-sample frame, then 512-sample frames;
    /// 5 s/div (0xFF) streams 16-sample frames 1.6 s apart. Ends with S X.
    /// </summary>
    public const string Roll = "17-roll-mode.txt";

    /// <summary>
    /// Roll mode at 500 ms/div started three times: R (3 frames) S X; R, which first delivers 2
    /// leftover frames at once, then 5 frames 150 ms apart, S (one more frame arrives); R, again 2
    /// at once, then 4 frames, S X.
    /// </summary>
    public const string RollRestart = "18-roll-restart.txt";

    /// <summary>QTI, QP, QVR, QC, QE and their replies.</summary>
    public const string Startup = "01-startup.txt";

    /// <summary>Default settings, F and L (no reply), then R for 5 frames, S, X.</summary>
    public const string FramesDefault = "02-frames-default.txt";

    /// <summary>S F (no frame), R for 2 frames, then S and X cancel a third frame mid-transfer.</summary>
    public const string SingleAndFull = "03-single-and-full.txt";

    /// <summary>R for 2 frames with subtrigger 58; F sent while running stops further frames.</summary>
    public const string RawSingle = "04-raw-single.txt";

    public static IReadOnlyList<CapturedPacket> Load(string name)
        => CaptureFile.Read(Path.Combine(AppContext.BaseDirectory, "Captures", name));

    public static IEnumerable<byte[]> Packets(string name, CaptureChannel channel)
        => Load(name).Where(p => p.Channel == channel).Select(p => p.Bytes);

    /// <summary>Parses "54 C0 F5" style hex into a 20-byte zero-padded packet.</summary>
    public static byte[] Packet(string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", ""));
        var packet = new byte[20];
        bytes.CopyTo(packet, 0);
        return packet;
    }
}
