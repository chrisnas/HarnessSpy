using System.Windows;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Claude;
using HarnessSpy.Core.Sessions.Copilot;
using HarnessSpy.Core.Sessions.Cursor;
using HarnessSpy.Core.Sessions.Opening;
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
        SessionDiscoveryContext discoveryContext = new(
            limits: DiscoveryLimits(settings.CatalogProfile));
        SessionViewerPreferencesViewModel preferences = new(
            settings,
            value =>
            {
                settings.KeepDisabledProviderCache =
                    value.KeepDisabledProviderCache;
                settings.CatalogProfile = value.CatalogProfile;
                settings.RawSourceMode = value.RawSourceMode;
                settings.RawPreviewBytes = value.RawPreviewBytes;
                settings.IncludeRawSourceInSearch =
                    value.IncludeRawSourceInSearch;
                settings.ExpandWorkspacesOnStartup =
                    value.ExpandWorkspacesOnStartup;
                discoveryContext.UpdateLimits(
                    DiscoveryLimits(value.CatalogProfile));
                settingsService.Save(settings);
            });
        SessionRawSourceResolver rawSourceResolver = new(
            new SessionRawSnapshotCache(),
            () => preferences.RawSourceMode ==
                SessionRawSourceMode.SnapshotCache);

        // Both profiles remain finite. Startup retains catalog headers only;
        // these limits bound the per-session detail hydration triggered by
        // expansion. Complete raises the limits without becoming unbounded.
        CursorSessionCatalogSource cursorSource = new(discoveryContext);
        IReadOnlyList<ISessionCatalogSource> sources =
        [
            cursorSource,
            new ClaudeCodeSessionCatalogSource(discoveryContext),
            new CopilotCliSessionCatalogSource(discoveryContext)
        ];

        _coordinator = new SessionCatalogCoordinator(
            sources,
            enabledProviders: settings.EnabledHarnesses,
            compactor: new SessionCatalogCompactor(
                () => preferences.CatalogProfile));
        ProviderSessionOpener sessionOpener = new();
        _viewModel = new SessionCatalogViewModel(
            _coordinator,
            sessionOpener,
            application.Dispatcher,
            preferences: preferences,
            rawSourceResolver: rawSourceResolver,
            detailHydrators: [cursorSource],
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

    private SessionDiscoveryLimits DiscoveryLimits(
        SessionCatalogProfile profile) =>
        profile == SessionCatalogProfile.Complete
            ? new SessionDiscoveryLimits(
                MaximumDirectories: 100_000,
                MaximumFiles: 250_000,
                MaximumRecordsPerSession: 250_000,
                MaximumLineBytes: 16 * 1024 * 1024,
                MaximumFileBytes: 2L * 1024 * 1024 * 1024,
                MaximumDuration: TimeSpan.FromMinutes(5),
                MaximumHydratedSessions: int.MaxValue,
                MaximumConcurrentHydrations: 4,
                MaximumHydrationBytes: 256L * 1024 * 1024)
            : new SessionDiscoveryLimits(
                MaximumDirectories: 10_000,
                MaximumFiles: 25_000,
                MaximumRecordsPerSession: 100_000,
                MaximumLineBytes: 4 * 1024 * 1024,
                MaximumFileBytes: 512L * 1024 * 1024,
                MaximumDuration: TimeSpan.FromSeconds(30),
                MaximumHydratedSessions: 500,
                MaximumConcurrentHydrations: 4,
                MaximumHydrationBytes: 128L * 1024 * 1024);
}
