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
}
