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

    [Fact]
    public void SelectingHarnessScansOnlyNewProvider()
    {
        SessionCatalogEntry cursorSession = Session(
            "cursor:s1",
            "s1",
            HookProvider.Cursor,
            @"C:\repoCursor");
        SessionCatalogEntry claudeSession = Session(
            "claude:s2",
            "s2",
            HookProvider.ClaudeCode,
            @"C:\repoClaude");

        (int cursorScans, int claudeScans, List<HookProvider> providers) =
            RunWithDispatcher(async dispatcher =>
            {
                FakeSource cursor = new(
                    "cursor",
                    HookProvider.Cursor,
                    [cursorSession]);
                FakeSource claude = new(
                    "claude",
                    HookProvider.ClaudeCode,
                    [claudeSession]);
                SessionCatalogCoordinator coordinator = new(
                    [cursor, claude]);
                SessionCatalogViewModel viewModel = new(
                    coordinator,
                    new NoopSessionOpener(),
                    dispatcher,
                    initialEnabledProviders: [HookProvider.ClaudeCode]);

                await viewModel.InitializeAsync();
                Assert.Equal(0, cursor.ScanCount);
                Assert.Equal(1, claude.ScanCount);

                Assert.Single(
                    viewModel.ProviderFilters,
                    option => option.Provider == HookProvider.Cursor)
                    .IsSelected = true;
                await WaitUntilAsync(
                    () => cursor.ScanCount == 1 &&
                        SessionProviders(viewModel).Count == 2,
                    TimeSpan.FromSeconds(5));

                return (
                    cursor.ScanCount,
                    claude.ScanCount,
                    SessionProviders(viewModel));
            });

        Assert.Equal(1, cursorScans);
        Assert.Equal(1, claudeScans);
        Assert.Equal(
            [HookProvider.Cursor, HookProvider.ClaudeCode],
            providers.Order().ToArray());
    }

    [Fact]
    public void SessionAndTurnChildrenLoadOnlyWhenExpanded()
    {
        SessionEventRecord record = new()
        {
            Id = "event-1",
            NativeName = "assistant",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Role = ObservationRole.AgentResponse,
            EventKind = CanonicalEventKind.AssistantMessage,
            Text = "done",
            Provenance = new SessionSourceProvenance(
                SessionSourceKind.CursorTranscriptJsonl,
                @"C:\session.jsonl",
                "jsonl",
                "{}")
        };
        SessionCatalogEntry session = Session(
            "cursor:s1",
            "s1",
            HookProvider.Cursor,
            @"C:\repoCursor") with
        {
            Turns =
            [
                new SessionTurn(
                    "turn-1",
                    1,
                    "inspect",
                    null,
                    null,
                    InferenceEvidence.Derived,
                    [record])
            ]
        };

        (bool initialShell, bool turnShell, bool eventLoaded) =
            RunWithDispatcher(async dispatcher =>
            {
                SessionCatalogCoordinator coordinator = new(
                [
                    new FakeSource(
                        "cursor",
                        HookProvider.Cursor,
                        [session])
                ]);
                SessionCatalogViewModel viewModel = new(
                    coordinator,
                    new NoopSessionOpener(),
                    dispatcher,
                    initialEnabledProviders: [HookProvider.Cursor]);

                await viewModel.InitializeAsync();
                SessionTreeNodeViewModel sessionNode =
                    SessionTreeNodeViewModel
                        .EnumerateDepthFirst(viewModel.Roots)
                        .Single(static node => node.IsSession);
                bool wasShell =
                    !sessionNode.AreChildrenLoaded &&
                    Assert.Single(sessionNode.Children).IsPlaceholder;

                sessionNode.IsExpanded = true;
                await WaitUntilAsync(
                    () => sessionNode.AreChildrenLoaded,
                    TimeSpan.FromSeconds(5));
                SessionTreeNodeViewModel turnNode =
                    Assert.Single(
                        sessionNode.Children,
                        static node => node.IsTurn);
                bool wasTurnShell =
                    !turnNode.AreChildrenLoaded &&
                    Assert.Single(turnNode.Children).IsPlaceholder;

                turnNode.IsExpanded = true;
                await WaitUntilAsync(
                    () => turnNode.AreChildrenLoaded,
                    TimeSpan.FromSeconds(5));

                return (
                    wasShell,
                    wasTurnShell,
                    Assert.Single(turnNode.Children).IsEvent);
            });

        Assert.True(initialShell);
        Assert.True(turnShell);
        Assert.True(eventLoaded);
    }

    [Fact]
    public void CursorDetailsHydrateOnlyWhenSessionIsExpanded()
    {
        SessionCatalogEntry skeleton = Session(
            "cursor:s1",
            "s1",
            HookProvider.Cursor,
            @"C:\repoCursor") with
        {
            Metadata = new Dictionary<string, string?>
            {
                ["cursor.desktopDetailsLoaded"] = bool.FalseString
            }
        };
        SessionTurn turn = new(
            "turn-1",
            1,
            "inspect",
            null,
            null,
            InferenceEvidence.Observed,
            []);

        (int beforeExpand, int afterExpand, int afterReopen, int turnCount) =
            RunWithDispatcher(async dispatcher =>
            {
                FakeDetailHydrator hydrator = new(
                    skeleton with
                    {
                        Turns = [turn],
                        Metadata = new Dictionary<string, string?>
                        {
                            ["cursor.desktopDetailsLoaded"] = bool.TrueString
                        }
                    });
                SessionCatalogCoordinator coordinator = new(
                [
                    new FakeSource(
                        "cursor",
                        HookProvider.Cursor,
                        [skeleton])
                ]);
                SessionCatalogViewModel viewModel = new(
                    coordinator,
                    new NoopSessionOpener(),
                    dispatcher,
                    detailHydrators: [hydrator],
                    initialEnabledProviders: [HookProvider.Cursor]);

                await viewModel.InitializeAsync();
                SessionTreeNodeViewModel sessionNode =
                    SessionTreeNodeViewModel
                        .EnumerateDepthFirst(viewModel.Roots)
                        .Single(static node => node.IsSession);
                int initialCalls = hydrator.CallCount;

                sessionNode.IsExpanded = true;
                await WaitUntilAsync(
                    () => sessionNode.AreChildrenLoaded,
                    TimeSpan.FromSeconds(5));
                int hydratedCalls = hydrator.CallCount;
                int turns = sessionNode.Children.Count(
                    static node => node.IsTurn);

                sessionNode.IsExpanded = false;
                sessionNode.IsExpanded = true;
                await WaitUntilAsync(
                    () => sessionNode.AreChildrenLoaded,
                    TimeSpan.FromSeconds(5));
                return (
                    initialCalls,
                    hydratedCalls,
                    hydrator.CallCount,
                    turns);
            });

        Assert.Equal(0, beforeExpand);
        Assert.Equal(1, afterExpand);
        Assert.Equal(1, afterReopen);
        Assert.Equal(1, turnCount);
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
        private int _scanCount;

        public string Name => name;

        public HookProvider Provider => provider;

        public IReadOnlyList<string> WatchRoots => [];

        public int ScanCount => Volatile.Read(ref _scanCount);

        public Task<SessionCatalogScanResult> ScanAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _scanCount);
            return Task.FromResult(
                new SessionCatalogScanResult(sessions, true, []));
        }
    }

    private sealed class NoopSessionOpener : ISessionOpener
    {
        public bool CanOpen(SessionCatalogEntry session) => false;

        public Task<SessionOpenResult> OpenAsync(
            SessionCatalogEntry session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionOpenResult(false, "unsupported"));
    }

    private sealed class FakeDetailHydrator(
        SessionCatalogEntry hydratedSession) : ISessionDetailHydrator
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public bool CanHydrate(SessionCatalogEntry session) =>
            session.Metadata.TryGetValue(
                "cursor.desktopDetailsLoaded",
                out string? loaded) &&
            !string.Equals(
                loaded,
                bool.TrueString,
                StringComparison.OrdinalIgnoreCase);

        public Task<SessionDetailHydrationResult> HydrateAsync(
            SessionCatalogEntry session,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(
                new SessionDetailHydrationResult(hydratedSession, []));
        }
    }
}
