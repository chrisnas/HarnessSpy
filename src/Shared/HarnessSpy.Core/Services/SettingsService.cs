using System.IO;
using System.Text.Json;
using HarnessSpy.Core.Hooks;

namespace HarnessSpy.Core.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HarnessSpy",
            "Cursor",
            "settings.json");
    }

    public SettingsService(ProviderProfile profile)
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HarnessSpy",
            profile.SettingsFolderName,
            "settings.json"))
    {
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // Settings are convenience state; app startup and capture must not depend on them.
        }
    }
}

public enum TranscriptReplayMode
{
    // Reproduce the live tree from the finalized bindings journal.
    Exact,

    // Re-run the current parsers/reconciler over captured source rows.
    Reinterpret
}

public sealed class AppSettings
{
    public string? LastReplayFolder { get; set; }

    // Discover, backfill, tail, and durably capture provider transcripts as a
    // secondary source. On by default; turning it off stops tailers but leaves
    // already-captured sidecar evidence visible.
    public bool EnableTranscriptIngestion { get; set; } = true;

    public TranscriptReplayMode TranscriptReplayMode { get; set; } = TranscriptReplayMode.Exact;
}
