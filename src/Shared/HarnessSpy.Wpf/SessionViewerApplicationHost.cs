using System.Windows;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Claude;
using HarnessSpy.Core.Sessions.Copilot;
using HarnessSpy.Core.Sessions.Cursor;
using HarnessSpy.Core.Sessions.Opening;
using HarnessSpy.Core.Sessions.Process;
using HarnessSpy.Wpf.ViewModels;
using HarnessSpy.Wpf.Views;

namespace HarnessSpy.Wpf;

public sealed class SessionViewerApplicationHost : IAsyncDisposable
{
    private SessionCatalogCoordinator? _coordinator;
    private SessionCatalogViewModel? _viewModel;
    private bool _disposed;

    public void Start(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_coordinator is not null)
        {
            throw new InvalidOperationException(
                "The Session Viewer application host has already started.");
        }

        SessionViewerSettingsService settingsService = new();
        SessionViewerSettings settings = settingsService.Load();

        // Session Viewer favors completeness over speed: the discovery guard
        // limits are relaxed so full folder scans are never truncated and no
        // "time/size limit reached" advisories surface. The per-value byte
        // guard is kept to avoid loading pathologically large database blobs.
        SessionDiscoveryContext discoveryContext = new(
            limits: new SessionDiscoveryLimits(
                MaximumDirectories: int.MaxValue,
                MaximumFiles: int.MaxValue,
                MaximumDuration: Timeout.InfiniteTimeSpan));
        IReadOnlyList<ISessionCatalogSource> sources =
        [
            new CursorSessionCatalogSource(discoveryContext),
            new ClaudeCodeSessionCatalogSource(discoveryContext),
            new CopilotCliSessionCatalogSource(discoveryContext)
        ];

        TimeSpan recoveryInterval = TimeSpan.FromSeconds(
            Math.Clamp(settings.RecoveryRescanSeconds, 5, 3600));
        RunningSessionProbeFactory probeFactory = new();

        _coordinator = new SessionCatalogCoordinator(
            sources,
            probeFactory.CreateDefault(),
            recoveryInterval: recoveryInterval);
        ProviderSessionOpener sessionOpener = new();
        _viewModel = new SessionCatalogViewModel(
            _coordinator,
            sessionOpener,
            settings.WatchEnabled,
            application.Dispatcher,
            initialFavorites: settings.Favorites,
            persistFavorites: favorites =>
            {
                settings.Favorites = [.. favorites];
                settingsService.Save(settings);
            },
            initialEnabledProviders: settings.EnabledHarnesses,
            persistEnabledProviders: providers =>
            {
                settings.EnabledHarnesses = [.. providers];
                settingsService.Save(settings);
            });

        SessionViewerWindow window = new()
        {
            DataContext = _viewModel
        };
        application.MainWindow = window;
        window.Show();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel?.Dispose();
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync().ConfigureAwait(false);
        }
    }
}
