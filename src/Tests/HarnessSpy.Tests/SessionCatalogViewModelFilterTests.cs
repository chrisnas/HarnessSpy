using System.Windows.Threading;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Wpf.ViewModels;

namespace HarnessSpy.Tests;

public sealed class SessionCatalogViewModelFilterTests
{
    [Fact]
    public void StartupHarnessFilterHidesDisabledHarnesses()
    {
        SessionCatalogEntry cursor = Session(
            "cursor:s1", "s1", HookProvider.Cursor, @"C:\repoCursor");
        SessionCatalogEntry claude = Session(
            "claude:s2", "s2", HookProvider.ClaudeCode, @"C:\repoClaude");
        SessionCatalogEntry copilot = Session(
            "copilot:s3", "s3", HookProvider.GitHubCopilot, @"C:\repoCopilot");

        List<HookProvider> sessionProviders = RunWithDispatcher(async dispatcher =>
        {
            SessionCatalogCoordinator coordinator = new(
            [
                new FakeSource("cursor", HookProvider.Cursor, [cursor]),
                new FakeSource("claude", HookProvider.ClaudeCode, [claude]),
                new FakeSource("copilot", HookProvider.GitHubCopilot, [copilot])
            ]);

            SessionCatalogViewModel viewModel = new(
                coordinator,
                new NoopSessionOpener(),
                watchEnabled: false,
                dispatcher,
                initialEnabledProviders: [HookProvider.ClaudeCode]);

            await viewModel.InitializeAsync();

            return SessionTreeNodeViewModel
                .EnumerateDepthFirst(viewModel.Roots)
                .Where(static node => node.IsSession)
                .Select(static node => node.Provider)
                .Distinct()
                .ToList();
        });

        Assert.Equal([HookProvider.ClaudeCode], sessionProviders);
    }

    [Fact]
    public void TogglingHarnessFilterThenRefreshingKeepsOnlySelectedHarness()
    {
        SessionCatalogEntry cursor = Session(
            "cursor:s1", "s1", HookProvider.Cursor, @"C:\repoCursor");
        SessionCatalogEntry claude = Session(
            "claude:s2", "s2", HookProvider.ClaudeCode, @"C:\repoClaude");
        SessionCatalogEntry copilot = Session(
            "copilot:s3", "s3", HookProvider.GitHubCopilot, @"C:\repoCopilot");

        (List<HookProvider> afterToggle, List<HookProvider> afterRefresh) =
            RunWithDispatcher(async dispatcher =>
        {
            SessionCatalogCoordinator coordinator = new(
            [
                new FakeSource("cursor", HookProvider.Cursor, [cursor]),
                new FakeSource("claude", HookProvider.ClaudeCode, [claude]),
                new FakeSource("copilot", HookProvider.GitHubCopilot, [copilot])
            ]);

            SessionCatalogViewModel viewModel = new(
                coordinator,
                new NoopSessionOpener(),
                watchEnabled: false,
                dispatcher,
                initialEnabledProviders:
                [
                    HookProvider.Cursor,
                    HookProvider.ClaudeCode,
                    HookProvider.GitHubCopilot
                ]);

            await viewModel.InitializeAsync();

            // Deselect every harness except Claude, mimicking the combobox.
            foreach (ProviderFilterOption option in viewModel.ProviderFilters)
            {
                option.IsSelected = option.Provider == HookProvider.ClaudeCode;
            }

            await WaitUntilAsync(
                () => OnlyClaude(viewModel),
                TimeSpan.FromSeconds(5));
            List<HookProvider> toggled = SessionProviders(viewModel);

            // A later discovery pass must not resurrect the hidden harnesses.
            await viewModel.RefreshAsync();
            await WaitUntilAsync(
                () => OnlyClaude(viewModel),
                TimeSpan.FromSeconds(5));

            return (toggled, SessionProviders(viewModel));
        });

        Assert.Equal([HookProvider.ClaudeCode], afterToggle);
        Assert.Equal([HookProvider.ClaudeCode], afterRefresh);
    }

    private static bool OnlyClaude(SessionCatalogViewModel viewModel)
    {
        List<HookProvider> providers = SessionProviders(viewModel);
        return providers.Count == 1 && providers[0] == HookProvider.ClaudeCode;
    }

    private static List<HookProvider> SessionProviders(
        SessionCatalogViewModel viewModel) =>
        SessionTreeNodeViewModel
            .EnumerateDepthFirst(viewModel.Roots)
            .Where(static node => node.IsSession)
            .Select(static node => node.Provider)
            .Distinct()
            .ToList();

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }

    private static SessionCatalogEntry Session(
        string catalogId,
        string nativeId,
        HookProvider provider,
        string root) =>
        new()
        {
            CatalogSessionId = catalogId,
            NativeSessionId = nativeId,
            Provider = provider,
            Surface = provider switch
            {
                HookProvider.Cursor => HookSurface.CursorIde,
                HookProvider.ClaudeCode => HookSurface.ClaudeCode,
                HookProvider.GitHubCopilot => HookSurface.CopilotCli,
                _ => HookSurface.Unknown
            },
            Workspace = WorkspaceContext.FromRoot(root),
            Title = catalogId
        };

    // Runs an async body on a dedicated STA dispatcher thread with a live
    // message pump, so the view model's cross-thread InvokeAsync callbacks are
    // actually dispatched, then tears the dispatcher down and returns the result.
    private static T RunWithDispatcher<T>(Func<Dispatcher, Task<T>> body)
    {
        T result = default!;
        Exception? failure = null;
        TaskCompletionSource<Dispatcher> ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            ready.SetResult(dispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Dispatcher dispatcher = ready.Task.GetAwaiter().GetResult();
        dispatcher.InvokeAsync(async () =>
        {
            try
            {
                result = await body(dispatcher);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                dispatcher.InvokeShutdown();
            }
        });

        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("Dispatcher body did not complete in time.");
        }

        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }

    private sealed class FakeSource(
        string name,
        HookProvider provider,
        IReadOnlyList<SessionCatalogEntry> sessions) : ISessionCatalogSource
    {
        public string Name => name;

        public HookProvider Provider => provider;

        public IReadOnlyList<string> WatchRoots => [];

        public Task<SessionCatalogScanResult> ScanAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new SessionCatalogScanResult(sessions, true, []));
    }

    private sealed class NoopSessionOpener : ISessionOpener
    {
        public bool CanOpen(SessionCatalogEntry session) => false;

        public Task<SessionOpenResult> OpenAsync(
            SessionCatalogEntry session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionOpenResult(false, "unsupported"));
    }
}
