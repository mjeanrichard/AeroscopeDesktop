namespace Aeroscope.Core.Protocol;

public enum TriggerEdge { Rising, Falling, Both }

/// <summary>
/// User-facing acquisition settings in volts and seconds, and their mapping to FPGA
/// registers and ADC codes. Offset is the voltage at the screen centre: raising it
/// moves the trace down (verified on the real scope).
/// </summary>
public sealed record ScopeSettings
{
    /// <summary>Default trigger position minus default read start: where t = 0 falls in a 512-sample frame.</summary>
    public const int TriggerSampleIndex = 0x800 - 0x700;

    private const ushort DacMidScale = 0x8000;

    /// <summary>The Android app keeps the DAC this far from either end.</summary>
    private const ushort DacLimit = 3277;

    private const double CentreCode = 127.5;

    public VoltageRange Range { get; init; } = VoltageRange.Range1V;

    public bool DcCoupled { get; init; } = true;

    public TimeBase TimeBase { get; init; } = TimeBase.Default;

    public TriggerEdge TriggerEdge { get; init; } = TriggerEdge.Rising;

    public bool AutoTrigger { get; init; } = true;

    /// <summary>Only honoured with a single edge, as in the Android app.</summary>
    public bool NoiseReject { get; init; }

    public double TriggerLevelVolts { get; init; }

    public double OffsetVolts { get; init; }

    public ushort OffsetDac
        => (ushort)Math.Clamp(Math.Round(DacMidScale + OffsetVolts / Range.VoltsPerDacCount), DacLimit, ushort.MaxValue - DacLimit);

    /// <summary>The ADC code at which 0 V appears, given the DAC value actually sent.</summary>
    public double ZeroCode => CentreCode - (OffsetDac - DacMidScale) / Range.DacCountsPerCode;

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

    public ScopeRegisters ToRegisters() => new()
    {
        TriggerControl = TriggerControl,
        TriggerLevel = TriggerLevelCode,
        FrontEnd = (byte)((DcCoupled ? 0x80 : 0x00) | Range.FrontEndCode),
        SamplerControl = TimeBase.SamplerCode,
        OffsetDac = OffsetDac,
    };
}
