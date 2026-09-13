using System.Windows;
using HarnessSpy.Wpf;

namespace SessionViewer;

public partial class App : Application
{
    private SessionViewerApplicationHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = new SessionViewerApplicationHost();
        _host.Start(this);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
