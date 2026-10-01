using System.IO;
using System.Text.Json;

namespace Limiter;

internal sealed record UserSettings
{
    public string Language { get; init; } = "system";
}

internal sealed class SettingsStore
{
    private readonly string _file;
    internal SettingsStore(string? file = null) => _file = file ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Limiter", "settings.json");
    internal UserSettings Load()
    {
        if (!File.Exists(_file)) return new();
        var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_file)) ?? new();
        return settings with { Language = settings.Language is "tr" or "en" ? settings.Language : "system" };
    }
    internal void Save(UserSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string temporary = _file + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
            File.Move(temporary, _file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
