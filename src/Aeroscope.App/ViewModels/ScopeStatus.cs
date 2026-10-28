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
    public partial string Temperature { get; set; } = Unknown;

    [ObservableProperty]
    public partial string Firmware { get; set; } = Unknown;

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
                Battery = $"{telemetry.BatteryLevel} ({BatteryState(telemetry.BatteryLevel)}){charger}";
                Temperature = $"{telemetry.TemperatureCelsius:0.0} °C";
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

    public void Reset()
    {
        Power = Battery = Temperature = Firmware = SerialNumber = LastFrame = Unknown;
        FramesReceived = 0;
    }

    // Thresholds from the spec's telemetry section.
    private static string BatteryState(byte level) => level switch
    {
        > 238 => "full",
        >= 226 => "partial",
        >= 220 => "low",
        _ => "empty",
    };
}
