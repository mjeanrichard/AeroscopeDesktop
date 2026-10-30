using System.IO;
using Aeroscope.App;

namespace Aeroscope.Tests.App;

public sealed class PreferencesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aeroscope-tests-" + Guid.NewGuid().ToString("N"));

    private string PreferencesFile => Path.Combine(_folder, "Aeroscope", "preferences.json");

    [Fact]
    public void Saved_preferences_load_back()
    {
        var saved = new Preferences { LastScope = 0xC6A7C8D11AB1, AutoConnect = false };

        new FilePreferencesStore(PreferencesFile).Save(saved);

        Assert.Equal(saved, new FilePreferencesStore(PreferencesFile).Load());
    }

    [Fact]
    public void Without_a_file_the_defaults_apply()
    {
        var loaded = new FilePreferencesStore(PreferencesFile).Load();

        Assert.Equal(new Preferences { LastScope = null, AutoConnect = true }, loaded);
    }

    [Fact]
    public void A_damaged_file_gives_the_defaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesFile)!);
        File.WriteAllText(PreferencesFile, "{ not json");

        Assert.Equal(new Preferences(), new FilePreferencesStore(PreferencesFile).Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
