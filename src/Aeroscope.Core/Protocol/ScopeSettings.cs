using System.Numerics;

namespace Aeroscope.Core.Protocol;

public enum TriggerEdge { Rising, Falling, Both }

/// <summary>
/// User-facing acquisition settings in volts and seconds, and their mapping to FPGA
/// registers and ADC codes. Offset is the voltage at the screen centre: raising it
/// moves the trace down (verified on the real scope).
/// </summary>
public sealed record ScopeSettings
{
    /// <summary>Triggered frame sizes: depth codes 6-9 (2^(code + 3) samples), all verified (capture 24).</summary>
    public static IReadOnlyList<int> FrameSizes { get; } = [512, 1024, 2048, 4096];

    private readonly int _frameSize = 512;

    private const ushort DacMidScale = 0x8000;

    /// <summary>The Android app keeps the DAC this far from either end.</summary>
    private const ushort DacLimit = 3277;

    private const double CentreCode = 127.5;

    public VoltageRange Range { get; init; } = VoltageRange.Range1V;

    public bool DcCoupled { get; init; } = true;

    public TimeBase TimeBase { get; init; } = TimeBase.Default;

    /// <summary>Samples per triggered frame, one of <see cref="FrameSizes"/>; 512 is one screen.</summary>
    public int FrameSize
    {
        get => _frameSize;
        init => _frameSize = FrameSizes.Contains(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a frame size");
    }

    /// <summary>The frame length the scope sends with these settings.</summary>
    public int FrameLength => TimeBase.IsRoll ? RollHistory.FrameLength : FrameSize;

    public TriggerEdge TriggerEdge { get; init; } = TriggerEdge.Rising;

    public bool AutoTrigger { get; init; } = true;

    /// <summary>Only honoured with a single edge, as in the Android app.</summary>
    public bool NoiseReject { get; init; }

    public double TriggerLevelVolts { get; init; }

    public double OffsetVolts { get; init; }

    /// <summary>
    /// Zero offsets in DAC counts per range (100 mV to 10 V, as <see cref="VoltageRange.All"/>) that the app
    /// adds to the offset DAC itself, or null. Only for a scope that lost its calibration (deep sleep erases
    /// it): a calibrated scope applies its offsets itself. Adding the remembered offsets reads 0 V where the
    /// scope's own calibration does (capture 33).
    /// </summary>
    public IReadOnlyList<short>? CalibrationOffsets { get; init; }

    /// <summary>The DAC value for the offset alone: what the screen scaling is based on.</summary>
    private double UncorrectedDac => Math.Clamp(Math.Round(DacMidScale + OffsetVolts / Range.VoltsPerDacCount), DacLimit, ushort.MaxValue - DacLimit);

    /// <summary>The DAC value sent: the offset plus the app's calibration correction, if any.</summary>
    public ushort OffsetDac
        => (ushort)Math.Clamp(UncorrectedDac + (CalibrationOffsets?[Range.Index] ?? 0), DacLimit, ushort.MaxValue - DacLimit);

    /// <summary>The ADC code at which 0 V appears; a calibration correction (the scope's or the app's) only cancels the zero error.</summary>
    public double ZeroCode => CentreCode - (UncorrectedDac - DacMidScale) / Range.DacCountsPerCode;

    public byte TriggerLevelCode => (byte)Math.Clamp(Math.Round(ZeroCode + TriggerLevelVolts / Range.VoltsPerCode), 0, 255);

    public byte TriggerControl
    {
        get
        {
            var control = TriggerEdge switch
            {
                TriggerEdge.Rising => 0x02,
                TriggerEdge.Falling => 0x04,
                _ => 0x06,
            };

            if (AutoTrigger)
            {
                control |= 0x01;
            }

            if (NoiseReject && TriggerEdge != TriggerEdge.Both)
            {
                control |= 0x20;
            }

            return (byte)control;
        }
    }

    public double ToVolts(double code) => (code - ZeroCode) * Range.VoltsPerCode;

    /// <summary>Where t = 0 (the trigger) falls in a triggered frame: the middle, whatever its size.</summary>
    public static int TriggerIndex(int frameLength) => frameLength / 2;

    /// <summary>
    /// Triggered frames capture just the samples sent (the frame size), trigger in the middle.
    /// The Android app's defaults fill the whole 4096-sample memory (trigger at 0x800, read from
    /// 0x700) for a 512-sample frame, 8 times slower: 16.5 s instead of 2.1 s per frame at
    /// 200 ms/div (capture 23). Roll mode keeps the Android defaults it was verified with.
    /// </summary>
    private byte DepthCode => (byte)(BitOperations.Log2((uint)FrameSize) - 3);

    public ScopeRegisters ToRegisters()
    {
        var registers = new ScopeRegisters
        {
            TriggerControl = TriggerControl,
            TriggerLevel = TriggerLevelCode,
            FrontEnd = (byte)((DcCoupled ? 0x80 : 0x00) | Range.FrontEndCode),
            SamplerControl = TimeBase.SamplerCode,
            OffsetDac = OffsetDac,
        };
        return TimeBase.IsRoll
            ? registers
            : registers with
            {
                WriteDepth = DepthCode,
                ReadDepth = DepthCode,
                TriggerPosition = (ushort)TriggerIndex(FrameSize),
                ReadStartAddress = 0,
            };
    }
}
