using System.Windows;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Wpf.ViewModels;
using HarnessSpy.Wpf.Views;

namespace HarnessSpy.Wpf;

public sealed class CursorDbNavigatorApplicationHost : IDisposable
{
    private CursorDbNavigatorViewModel? _viewModel;
    private bool _disposed;

    public void Start(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_viewModel is not null)
        {
            throw new InvalidOperationException(
                "The Cursor database navigator has already started.");
        }

        CursorDbNavigatorSettingsService settingsService = new();
        CursorDbNavigatorSettings settings = settingsService.Load();
        SessionDiscoveryContext discoveryContext = new(
            limits: new SessionDiscoveryLimits(
                MaximumDirectories: 25_000,
                MaximumFiles: 100_000,
                MaximumRecordsPerSession: 250_000,
                MaximumLineBytes: 16 * 1024 * 1024,
                MaximumFileBytes: 2L * 1024 * 1024 * 1024,
                MaximumDuration: TimeSpan.FromMinutes(2),
                MaximumHydratedSessions: 500,
                MaximumConcurrentHydrations: 4,
                MaximumHydrationBytes: 512L * 1024 * 1024));
        _viewModel = new CursorDbNavigatorViewModel(
            discoveryContext,
            settings,
            settingsService.Save,
            application.Dispatcher);
        CursorDbNavigatorWindow window = new()
        {
            DataContext = _viewModel
        };
        application.MainWindow = window;
        window.Show();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel?.Dispose();
    }
}
