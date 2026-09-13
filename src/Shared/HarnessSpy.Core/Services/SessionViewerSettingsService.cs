using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Services;

public sealed class SessionViewerSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

    private readonly string _settingsPath;

    public SessionViewerSettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HarnessSpy",
            "SessionViewer",
            "settings.json");
    }

    public SessionViewerSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new SessionViewerSettings();
            }

            return JsonSerializer.Deserialize<SessionViewerSettings>(
                File.ReadAllText(_settingsPath),
                JsonOptions) ?? new SessionViewerSettings();
        }
        catch
        {
            return new SessionViewerSettings();
        }
    }

    public void Save(SessionViewerSettings settings)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                _settingsPath,
                JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // SessionViewer settings are convenience state and never block startup.
        }
    }
}

public sealed class SessionViewerSettings
{
    public bool WatchEnabled { get; set; } = true;

    public int RecoveryRescanSeconds { get; set; } = 60;

    public List<string> Favorites { get; set; } = [];

    // Harnesses whose sessions are shown in the tree. All known harnesses are
    // enabled by default so a fresh install shows everything.
    public List<HookProvider> EnabledHarnesses { get; set; } =
    [
        HookProvider.Cursor,
        HookProvider.ClaudeCode,
        HookProvider.GitHubCopilot
    ];
}
