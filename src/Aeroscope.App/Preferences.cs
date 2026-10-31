using System.IO;
using System.Text.Json;

namespace Aeroscope.App;

/// <summary>What the app remembers between runs.</summary>
public sealed record Preferences
{
    /// <summary>Address of the scope last connected to.</summary>
    public ulong? LastScope { get; init; }

    /// <summary>Connect to the last scope as soon as it is in range.</summary>
    public bool AutoConnect { get; init; } = true;

    /// <summary>Put the scope into deep sleep ("ZZ") when disconnecting, also when the app closes.</summary>
    public bool SleepOnDisconnect { get; init; }

    /// <summary>Turn the scope's FPGA off ("PO") after a while stopped.</summary>
    public bool IdlePowerOff { get; init; } = true;

    /// <summary>
    /// The last calibration each scope (by address) reported, DAC counts from 100 mV to 10 V. Deep sleep
    /// erases a scope's calibration and it can't be written back, so the app applies these itself.
    /// </summary>
    public Dictionary<ulong, short[]>? Calibrations { get; init; }
}

public interface IPreferencesStore
{
    Preferences Load();

    void Save(Preferences preferences);
}

/// <summary>Keeps the preferences in a JSON file; a missing or unreadable file gives the defaults.</summary>
public sealed class FilePreferencesStore(string path) : IPreferencesStore
{
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aeroscope", "preferences.json");

    public Preferences Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(Preferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
    }
}
