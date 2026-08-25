using System.Windows;
using CursorSpy.App.Services;
using CursorSpy.App.ViewModels;

namespace CursorSpy.App;

public partial class App : Application
{
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listenerTask;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SettingsService settingsService = new();
        AppSettings settings = settingsService.Load();
        MainWindowViewModel viewModel = new();
        NamedPipeListener listener = new();
        _listenerTask = listener.RunAsync(
            (observation, _) =>
            {
                Dispatcher.Invoke(() => viewModel.AddObservation(observation));
                return Task.CompletedTask;
            },
            _shutdown.Token,
            exception => Dispatcher.Invoke(() => ShowPipeCreationError(exception)));

        MainWindow window = new(settingsService, settings.LastReplayFolder)
        {
            DataContext = viewModel
        };

        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();

        try
        {
            _listenerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }
        finally
        {
            _shutdown.Dispose();
        }

        base.OnExit(e);
    }

    private static void ShowPipeCreationError(Exception exception)
    {
        MessageBox.Show(
            $"CursorSpy could not create the named pipe \"{NamedPipeListener.DefaultPipeName}\".\n\n" +
            $"{exception.GetType().Name}: {exception.Message}\n\n" +
            "Hook activity will not be captured until this is resolved.",
            "CursorSpy",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}

