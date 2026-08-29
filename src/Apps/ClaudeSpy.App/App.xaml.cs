using System.Windows;
using HarnessSpy.Agent.Abstractions;
using HarnessSpy.Core.Hooks;
using HarnessSpy.Wpf;

namespace ClaudeSpy.App;

public partial class App : Application
{
    private SpyApplicationHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = new SpyApplicationHost(new SpyAppProfile(
            ProviderProfile.Claude,
            new UnavailableAgentProvider(ProviderProfile.Claude.Provider),
            new Uri("pack://application:,,,/ClaudeSpy.App;component/ClaudeSpy.png")));
        _host.Start(this);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
