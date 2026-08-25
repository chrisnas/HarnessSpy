using System.Windows;
using HarnessSpy.Agent.Abstractions;
using HarnessSpy.Core.Hooks;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;
using HarnessSpy.Wpf.ViewModels;
using HarnessSpy.Wpf.Views;

namespace HarnessSpy.Wpf;

public sealed record SpyAppProfile(
    ProviderProfile Provider,
    IAgentProvider AgentProvider);

public sealed class SpyApplicationHost(SpyAppProfile profile) : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ObservationBuffer<HookObservation> _buffer = new();
    private Task? _listenerTask;
    private Task? _dispatcherTask;

    public void Start(Application application)
    {
        SettingsService settingsService = new(profile.Provider);
        AppSettings settings = settingsService.Load();
        MainWindowViewModel viewModel = new();

        _dispatcherTask = DispatchAsync(application, viewModel, _shutdown.Token);
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

        application.MainWindow = window;
        window.Show();
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _buffer.Complete();

        await AwaitQuietly(_listenerTask).ConfigureAwait(false);
        await AwaitQuietly(_dispatcherTask).ConfigureAwait(false);
        await profile.AgentProvider.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task DispatchAsync(
        Application application,
        MainWindowViewModel viewModel,
        CancellationToken cancellationToken)
    {
        const int maxBatch = 128;
        List<HookObservation> batch = new(maxBatch);

        await foreach (HookObservation observation in
            _buffer.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            batch.Add(observation);
            while (batch.Count < maxBatch &&
                   _buffer.TryRead(out HookObservation? queued) &&
                   queued is not null)
            {
                batch.Add(queued);
            }

            HookObservation[] pending = [.. batch];
            batch.Clear();
            await application.Dispatcher.InvokeAsync(() =>
            {
                foreach (HookObservation item in pending)
                {
                    viewModel.AddObservation(item);
                }
            });
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
