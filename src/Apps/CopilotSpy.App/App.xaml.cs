using System.Windows;
using HarnessSpy.Agent.Abstractions;
using HarnessSpy.Core.Hooks;
using HarnessSpy.Wpf;

namespace CopilotSpy.App;

public partial class App : Application
{
    private SpyApplicationHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = new SpyApplicationHost(new SpyAppProfile(
            ProviderProfile.Copilot,
            new UnavailableAgentProvider(ProviderProfile.Copilot.Provider)));
        _host.Start(this);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
