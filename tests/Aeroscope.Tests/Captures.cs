using System.IO;
using Aeroscope.Core.Diagnostics;
using Aeroscope.Core.Protocol;

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

    /// <summary>
    /// 1 MHz, 2 V peak-to-peak sine from a function generator at 500 ns/div: the range switched
    /// between 1 V/div (64 codes peak to peak) and 2 V/div (33 codes) every 0.2-0.3 s while
    /// running, 24 times with the trigger at 0 V, then 24 times with it above the signal (auto
    /// trigger). 65 frames; the 5 at 2.058, 2.321, 2.620, 2.882 and 14.470 s were still taken
    /// with the range before the write just before them.
    /// </summary>
    public const string SettingsWhileRunning = "20-settings-while-running.txt";

    /// <summary>
    /// The same 1 MHz sine (aliased) at 20 ms/div: each frame takes 1.65 s to capture (the whole
    /// 4096-sample memory). Range switched 12 times, 1.2-4 s apart; each write restarts the capture,
    /// so the next frame comes 1.6-1.8 s after it. Two writes landed while a frame was arriving.
    /// </summary>
    public const string SettingsWhileRunning20ms = "21-settings-while-running-20ms.txt";

    /// <summary>
    /// The same at 200 ms/div: a frame every 16.5 s. Writes at 16.9 s (just after a frame), and at
    /// 36.9 and 43.0 s (mid-capture, each restarting it): 3 frames.
    /// </summary>
    public const string SettingsWhileRunning200ms = "22-settings-while-running-200ms.txt";

    /// <summary>
    /// The 1 MHz sine with the trigger rising through 0 V (code 128): 4 frames at 500 ns/div with
    /// the Android defaults (4096-sample memory), then a 512-sample record (write depth 6, trigger
    /// position 0x100, read start 0) for 4 frames at 500 ns/div, 5 at 20 ms/div and 4 at
    /// 200 ms/div, each run ended by S X. Every frame crosses the level between samples 255 and 256.
    /// </summary>
    public const string WriteDepth512 = "23-write-depth-512.txt";

    /// <summary>
    /// The 1 MHz sine at 500 ns/div with write and read depth 7, 8 and 9 (trigger position half
    /// the record, read start 0): 3 frames each of 1024, 2048 and 4096 samples (54, 108 and 216
    /// packets), each run ended by S X. The sine rises through 0 V (code 128) mid-frame.
    /// </summary>
    public const string FrameSizes = "24-frame-sizes.txt";

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

    /// <summary>Range, time base and offset from a recorded State (register block) packet.</summary>
    public static ScopeSettings SettingsFrom(byte[] registers)
    {
        var range = VoltageRange.All.Single(r => r.FrontEndCode == (registers[3] & 0x7F));
        var dac = registers[11] << 8 | registers[12];
        return new ScopeSettings
        {
            Range = range,
            TimeBase = TimeBase.All.Single(t => t.SamplerCode == registers[4]),
            OffsetVolts = (dac - 0x8000) * range.VoltsPerDacCount,
        };
    }

    /// <summary>Parses "54 C0 F5" style hex into a 20-byte zero-padded packet.</summary>
    public static byte[] Packet(string hex)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", ""));
        var packet = new byte[20];
        bytes.CopyTo(packet, 0);
        return packet;
    }
}
