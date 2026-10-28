namespace Aeroscope.Core.Protocol;

/// <summary>
/// A horizontal setting: sampler code and the resulting sample interval (100 MS/s divided
/// down). Codes from the spec's sampler register table. The screen shows 10 divisions of
/// 50 samples each. In roll mode (500 ms/div and slower) the scope streams 16-sample frames
/// back to back instead of triggered frames. Faster zoomed settings are not included.
/// </summary>
public sealed record TimeBase(string Name, byte SamplerCode, double SampleInterval, bool IsRoll = false)
{
    public const int Divisions = 10;

    public const int SamplesPerDivision = 50;

    public const int SamplesOnScreen = Divisions * SamplesPerDivision;

    public static IReadOnlyList<TimeBase> All { get; } =
    [
        new("500 ns", 0x00, 10e-9),
        new("1 µs", 0x10, 20e-9),
        new("2 µs", 0x20, 40e-9),
        new("5 µs", 0x09, 100e-9),
        new("10 µs", 0x11, 200e-9),
        new("20 µs", 0x21, 400e-9),
        new("50 µs", 0x0A, 1e-6),
        new("100 µs", 0x12, 2e-6),
        new("200 µs", 0x22, 4e-6),
        new("500 µs", 0x0B, 10e-6),
        new("1 ms", 0x13, 20e-6),
        new("2 ms", 0x23, 40e-6),
        new("5 ms", 0x0C, 100e-6),
        new("10 ms", 0x14, 200e-6),
        new("20 ms", 0x24, 400e-6),
        new("50 ms", 0x0D, 1e-3),
        new("100 ms", 0x15, 2e-3),
        new("200 ms", 0x25, 4e-3),
        new("500 ms", 0xE7, 10e-3, IsRoll: true),
        new("1 s", 0xEF, 20e-3, IsRoll: true),
        new("2 s", 0xF7, 40e-3, IsRoll: true),
        new("5 s", 0xFF, 100e-3, IsRoll: true),
    ];

    public static TimeBase Default => All[0];

    public double SecondsPerDivision => SampleInterval * SamplesPerDivision;

    public override string ToString() => IsRoll ? $"{Name}/div roll" : $"{Name}/div";
}
