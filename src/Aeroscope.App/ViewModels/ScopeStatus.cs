using Aeroscope.Core.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aeroscope.App.ViewModels;

/// <summary>What the scope has reported about itself on the Scope Out characteristic.</summary>
public partial class ScopeStatus : ObservableObject
{
    private const string Unknown = "–";

    [ObservableProperty]
    public partial string Power { get; set; } = Unknown;

    [ObservableProperty]
    public partial string Battery { get; set; } = Unknown;

    [ObservableProperty]
    public partial string BatteryLevel { get; set; } = Unknown;

    [ObservableProperty]
    public partial string Temperature { get; set; } = Unknown;

    [ObservableProperty]
    public partial string Firmware { get; set; } = Unknown;

    [ObservableProperty]
    public partial string Calibration { get; set; } = Unknown;

    [ObservableProperty]
    public partial string SerialNumber { get; set; } = Unknown;

    [ObservableProperty]
    public partial int FramesReceived { get; set; }

    [ObservableProperty]
    public partial string LastFrame { get; set; } = Unknown;

    public void Apply(ScopeOutMessage message)
    {
        switch (message)
        {
            case PowerStateMessage power:
                Power = power.FullyOn ? "Fully on" : "FPGA off / configuring";
                break;
            case TelemetryMessage telemetry:
                var charger = telemetry.Charging ? ", charging" : telemetry.ChargerConnected ? ", charger connected" : "";
                Battery = $"{BatteryPercent(telemetry.BatteryLevel)} %{charger}";
                BatteryLevel = $"Raw level {telemetry.BatteryLevel} ({BatteryState(telemetry.BatteryLevel)})";
                Temperature = $"{telemetry.TemperatureCelsius:0.0} °C";
                break;
            case CalibrationMessage calibration:
                Calibration = calibration.IsCalibrated ? Offsets(calibration.Offsets) : "Not calibrated";
                break;
            case VersionMessage version:
                Firmware = $"MCU {version.FirmwareRevision}, FPGA {version.FpgaRevision}, HW {version.HardwareId}";
                SerialNumber = version.SerialNumber.ToString();
                break;
        }
    }

    public void Apply(ScopeFrame frame)
    {
        FramesReceived++;
        LastFrame = frame.ToString();
    }

    public void CalibrationStarted() => Calibration = "Calibrating...";

    /// <summary>The scope reported no calibration and the app applies the one it remembers.</summary>
    public void CalibrationRestored(IReadOnlyList<short> offsets) => Calibration = $"Lost by the scope, applied by the app: {Offsets(offsets)}";

    private static string Offsets(IReadOnlyList<short> offsets)
        => "DAC offsets " + string.Join(", ", VoltageRange.All.Zip(offsets, (range, offset) => $"{range.Name} {offset}"));

    public void Reset()
    {
        Power = Battery = BatteryLevel = Temperature = Firmware = Calibration = SerialNumber = LastFrame = Unknown;
        FramesReceived = 0;
    }

    // The spec gives no scale, only bands (220 low ... above 238 full), so the percentage
    // runs linearly across them: 219 and below is 0 %, 239 and above 100 %, 5 % per step.
    public static int BatteryPercent(byte level)
        => Math.Clamp((level - EmptyLevel) * 100 / (FullLevel - EmptyLevel), 0, 100);

    private const int EmptyLevel = 219;
    private const int FullLevel = 239;

    // Thresholds from the spec's telemetry section.
    private static string BatteryState(byte level) => level switch
    {
        > 238 => "full",
        >= 226 => "partial",
        >= 220 => "low",
        _ => "empty",
    };
}
