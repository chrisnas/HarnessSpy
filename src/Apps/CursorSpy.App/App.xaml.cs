using System.Windows;
using HarnessSpy.Agent.Abstractions;
using HarnessSpy.Core.Hooks;
using HarnessSpy.Wpf;

namespace CursorSpy.App;

public partial class App : Application
{
    private SpyApplicationHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = new SpyApplicationHost(new SpyAppProfile(
            ProviderProfile.Cursor,
            new UnavailableAgentProvider(ProviderProfile.Cursor.Provider)));
        _host.Start(this);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
