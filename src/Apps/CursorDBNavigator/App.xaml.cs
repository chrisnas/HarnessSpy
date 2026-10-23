using System.Windows;
using HarnessSpy.Wpf;

namespace CursorDBNavigator;

public partial class App : Application
{
    private CursorDbNavigatorApplicationHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = new CursorDbNavigatorApplicationHost();
        _host.Start(this);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
