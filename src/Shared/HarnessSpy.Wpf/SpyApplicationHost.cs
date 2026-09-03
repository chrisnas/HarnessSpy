using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using HarnessSpy.Agent.Abstractions;
using HarnessSpy.Core.Hooks;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;
using HarnessSpy.Wpf.ViewModels;
using HarnessSpy.Wpf.Views;

namespace HarnessSpy.Wpf;

// WindowIconUri lets each app override the main window's title-bar icon (the
// exe/binary icon is set separately via <ApplicationIcon>); null keeps the
// WPF default. EnableTranscripts turns on the provider-owned transcript
// secondary source (discover from hooks, backfill, tail, durably capture).
public sealed record SpyAppProfile(
    ProviderProfile Provider,
    IAgentProvider AgentProvider,
    Uri? WindowIconUri = null,
    bool EnableTranscripts = true);

public sealed class SpyApplicationHost(SpyAppProfile profile) : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ObservationBuffer<HookObservation> _buffer = new();
    private Task? _listenerTask;
    private Task? _dispatcherTask;
    private ObservationIngestionCoordinator? _coordinator;

    public void Start(Application application)
    {
        SettingsService settingsService = new(profile.Provider);
        AppSettings settings = settingsService.Load();
        MainWindowViewModel viewModel = new();

        bool enableTranscripts = profile.EnableTranscripts && settings.EnableTranscriptIngestion;
        string payloadsDirectory = Path.Combine(
            FileHookDiagnostics.GetDefaultDirectory(profile.Provider),
            "Payloads");
        TranscriptCaptureStore captureStore = new(payloadsDirectory);
        TranscriptBindingJournal bindingJournal = new(captureStore);
        TranscriptSessionRegistry registry = new();

        _coordinator = new ObservationIngestionCoordinator(
            registry,
            captureStore,
            bindingJournal,
            (change, cancellationToken) =>
            {
                application.Dispatcher.InvokeAsync(() => viewModel.ApplyObservationChange(change));
                return Task.CompletedTask;
            },
            enableTranscripts);
        _coordinator.Start(_shutdown.Token);

        _dispatcherTask = DispatchAsync(_shutdown.Token);
        NamedPipeListener listener = new(
            profile.Provider.PipeName,
            profile.Provider.Provider);
        _listenerTask = listener.RunAsync(
            async (observation, cancellationToken) =>
                await _buffer.WriteAsync(observation, cancellationToken).ConfigureAwait(false),
            _shutdown.Token,
            exception => application.Dispatcher.Invoke(
                () => ShowPipeCreationError(profile.Provider, exception)));

        SpyWindow window = new(
            settingsService,
            settings.LastReplayFolder,
            profile.Provider.DisplayName)
        {
            DataContext = viewModel
        };

        if (profile.WindowIconUri is Uri windowIconUri)
        {
            window.Icon = BitmapFrame.Create(windowIconUri);
        }

        application.MainWindow = window;
        window.Show();
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _buffer.Complete();

        await AwaitQuietly(_listenerTask).ConfigureAwait(false);
        await AwaitQuietly(_dispatcherTask).ConfigureAwait(false);
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync().ConfigureAwait(false);
        }

        await profile.AgentProvider.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    // Feeds pipe-delivered hooks into the coordinator, which serializes hook
    // and transcript ingestion and emits reconciled changes back on the UI
    // thread. The bounded buffer preserves backpressure from the pipe.
    private async Task DispatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (HookObservation observation in
                _buffer.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_coordinator is not null)
                {
                    await _coordinator.IngestHookAsync(observation, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task AwaitQuietly(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static void ShowPipeCreationError(
        ProviderProfile provider,
        Exception exception)
    {
        MessageBox.Show(
            $"{provider.DisplayName} could not create the named pipe \"{provider.PipeName}\".\n\n" +
            $"{exception.GetType().Name}: {exception.Message}\n\n" +
            "Hook activity will not be captured until this is resolved.",
            provider.DisplayName,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
