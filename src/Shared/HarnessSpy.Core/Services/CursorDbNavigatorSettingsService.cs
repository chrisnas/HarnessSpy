using System.Text.Json;

namespace HarnessSpy.Core.Services;

public sealed class CursorDbNavigatorSettingsService
{
    private readonly string _settingsPath;

    public CursorDbNavigatorSettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HarnessSpy",
            "CursorDBNavigator",
            "settings.json");
    }

    public CursorDbNavigatorSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new CursorDbNavigatorSettings();
            }

            return JsonSerializer.Deserialize<CursorDbNavigatorSettings>(
                File.ReadAllText(_settingsPath)) ??
                new CursorDbNavigatorSettings();
        }
        catch
        {
            return new CursorDbNavigatorSettings();
        }
    }

    public void Save(CursorDbNavigatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            string? directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                _settingsPath,
                JsonSerializer.Serialize(
                    settings,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
        }
        catch
        {
            // Navigator preferences are convenience state and never block startup.
        }
    }
}

public sealed class CursorDbNavigatorSettings
{
    public string? LastDatabasePath { get; set; }

    public int PageSize { get; set; } = 200;

    public double DatabasePaneWidth { get; set; } = 260;

    public double RowsPaneWidth { get; set; } = 520;
}
