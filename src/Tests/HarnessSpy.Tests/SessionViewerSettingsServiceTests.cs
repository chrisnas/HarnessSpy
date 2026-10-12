using System.IO;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;

namespace HarnessSpy.Tests;

public sealed class SessionViewerSettingsServiceTests
{
    [Fact]
    public void EnabledHarnessesRoundTripsThroughSaveAndLoad()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"harnessspy-settings-{Guid.NewGuid():N}.json");
        try
        {
            SessionViewerSettingsService service = new(path);
            SessionViewerSettings saved = new()
            {
                EnabledHarnesses = [HookProvider.ClaudeCode]
            };
            service.Save(saved);

            SessionViewerSettings loaded = service.Load();

            Assert.Equal(
                [HookProvider.ClaudeCode],
                loaded.EnabledHarnesses);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void MissingEnabledHarnessesDefaultsToAllHarnesses()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"harnessspy-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"watchEnabled\":true}");
            SessionViewerSettingsService service = new(path);

            SessionViewerSettings loaded = service.Load();

            Assert.Equal(
                [HookProvider.Cursor, HookProvider.ClaudeCode, HookProvider.GitHubCopilot],
                loaded.EnabledHarnesses);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void PerformancePreferencesRoundTripAndIgnoreLegacyRefreshSettings()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"harnessspy-settings-{Guid.NewGuid():N}.json");
        try
        {
            SessionViewerSettingsService service = new(path);
            SessionViewerSettings saved = new()
            {
                KeepDisabledProviderCache = true,
                CatalogProfile = SessionCatalogProfile.Complete,
                RawSourceMode = SessionRawSourceMode.SnapshotCache,
                RawPreviewBytes = 256 * 1024,
                IncludeRawSourceInSearch = true,
                ExpandWorkspacesOnStartup = true
            };
            service.Save(saved);

            string json = File.ReadAllText(path);
            json = json.TrimEnd('}', '\r', '\n') +
                ",\"watchEnabled\":true,\"recoveryRescanSeconds\":5}";
            File.WriteAllText(path, json);
            SessionViewerSettings loaded = service.Load();

            Assert.True(loaded.KeepDisabledProviderCache);
            Assert.Equal(
                SessionCatalogProfile.Complete,
                loaded.CatalogProfile);
            Assert.Equal(
                SessionRawSourceMode.SnapshotCache,
                loaded.RawSourceMode);
            Assert.Equal(256 * 1024, loaded.RawPreviewBytes);
            Assert.True(loaded.IncludeRawSourceInSearch);
            Assert.True(loaded.ExpandWorkspacesOnStartup);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
