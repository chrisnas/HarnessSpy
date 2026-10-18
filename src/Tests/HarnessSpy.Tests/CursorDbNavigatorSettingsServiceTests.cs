using HarnessSpy.Core.Services;

namespace HarnessSpy.Tests;

public sealed class CursorDbNavigatorSettingsServiceTests
{
    [Fact]
    public void SavesAndLoadsNavigatorPreferences()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"HarnessSpy-CursorDbNavigator-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "settings.json");
        CursorDbNavigatorSettingsService service = new(path);
        try
        {
            service.Save(new CursorDbNavigatorSettings
            {
                LastDatabasePath = @"C:\temp\state.vscdb",
                PageSize = 500,
                DatabasePaneWidth = 300,
                RowsPaneWidth = 640
            });

            CursorDbNavigatorSettings loaded = service.Load();

            Assert.Equal(@"C:\temp\state.vscdb", loaded.LastDatabasePath);
            Assert.Equal(500, loaded.PageSize);
            Assert.Equal(300, loaded.DatabasePaneWidth);
            Assert.Equal(640, loaded.RowsPaneWidth);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
