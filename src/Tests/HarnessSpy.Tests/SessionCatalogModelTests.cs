using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Opening;
using HarnessSpy.Core.Sessions.Process;
using HarnessSpy.Core.Services;

namespace HarnessSpy.Tests;

public sealed class SessionCatalogModelTests
{
    [Fact]
    public void MergerCombinesSourcesForSameSession()
    {
        WorkspaceContext workspace = WorkspaceContext.FromRoot(@"C:\repo");
        SessionCatalogEntry transcript = Session(
            "cursor:s1",
            "s1",
            HookProvider.Cursor,
            workspace,
            "s1",
            [File(@"C:\transcripts\s1.jsonl", SessionSourceKind.CursorTranscriptJsonl)]);
        SessionCatalogEntry database = Session(
            "cursor:s1",
            "s1",
            HookProvider.Cursor,
            workspace,
            "Readable title",
            [File(@"C:\Cursor\state.vscdb", SessionSourceKind.CursorDesktopSqlite)]);

        SessionCatalogEntry merged = Assert.Single(
            new SessionCatalogMerger().Merge([transcript, database]));

        Assert.Equal("Readable title", merged.Title);
        Assert.Equal(2, merged.Files.Count);
    }

    [Fact]
    public void ProcessCorrelatorRequiresPositiveSessionEvidence()
    {
        SessionCatalogEntry first = Session(
            "claude:first",
            "first",
            HookProvider.ClaudeCode,
            WorkspaceContext.FromRoot(@"C:\repo"),
            "first",
            []);
        SessionCatalogEntry second = Session(
            "claude:second",
            "second",
            HookProvider.ClaudeCode,
            WorkspaceContext.FromRoot(@"C:\repo"),
            "second",
            []);
        RunningSessionHint hint = new(
            HookProvider.ClaudeCode,
            42,
            "claude.exe",
            NativeSessionId: "second");

        IReadOnlyList<SessionCatalogEntry> correlated =
            new ProcessSessionCorrelator().Apply([first, second], [hint]);

        Assert.Equal(SessionLifecycleState.Closed, correlated[0].LifecycleState);
        Assert.Equal(SessionLifecycleState.Open, correlated[1].LifecycleState);
        Assert.Equal(InferenceEvidence.Observed, correlated[1].LifecycleEvidence);
    }

    [Fact]
    public void ProcessCorrelatorDoesNotUseAmbiguousWorkspace()
    {
        WorkspaceContext workspace = WorkspaceContext.FromRoot(@"C:\repo");
        SessionCatalogEntry first = Session(
            "copilot:first",
            "first",
            HookProvider.GitHubCopilot,
            workspace,
            "first",
            []);
        SessionCatalogEntry second = Session(
            "copilot:second",
            "second",
            HookProvider.GitHubCopilot,
            workspace,
            "second",
            []);
        RunningSessionHint hint = new(
            HookProvider.GitHubCopilot,
            42,
            "copilot.exe",
            WorkingDirectory: @"C:\repo",
            Evidence: InferenceEvidence.Heuristic);

        IReadOnlyList<SessionCatalogEntry> correlated =
            new ProcessSessionCorrelator().Apply([first, second], [hint]);

        Assert.All(
            correlated,
            session => Assert.Equal(SessionLifecycleState.Closed, session.LifecycleState));
    }

    [Fact]
    public void SelectedCursorComposerRequiresCursorDesktopProcess()
    {
        SessionCatalogEntry selected = Session(
            "cursor:selected",
            "selected",
            HookProvider.Cursor,
            WorkspaceContext.FromRoot(@"C:\repo"),
            "selected",
            []) with
        {
            IsSelectedInHarness = true
        };
        RunningSessionHint unrelatedAgent = new(
            HookProvider.Cursor,
            42,
            "agent.exe");
        ProcessSessionCorrelator correlator = new();

        SessionCatalogEntry closed = Assert.Single(
            correlator.Apply([selected], [unrelatedAgent]));
        SessionCatalogEntry open = Assert.Single(
            correlator.Apply(
                [selected],
                [new RunningSessionHint(HookProvider.Cursor, 43, "Cursor.exe")]));

        Assert.Equal(SessionLifecycleState.Closed, closed.LifecycleState);
        Assert.Equal(SessionLifecycleState.Open, open.LifecycleState);
    }

    [Fact]
    public async Task OpenerBuildsClaudeResumeWithoutLaunchingInTest()
    {
        RecordingLauncher launcher = new();
        ProviderSessionOpener opener = new(launcher);
        SessionCatalogEntry session = Session(
            "claude:abc-123",
            "abc-123",
            HookProvider.ClaudeCode,
            WorkspaceContext.FromRoot(@"C:\repo"),
            "test",
            []);

        SessionOpenResult result = await opener.OpenAsync(session);

        Assert.True(result.Started);
        Assert.Contains("claude --resume", launcher.Arguments, StringComparison.Ordinal);
        Assert.Contains("abc-123", launcher.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoordinatorMergesSourceResultsAndRaisesUpdate()
    {
        SessionCatalogEntry first = Session(
            "cursor:s1",
            "s1",
            HookProvider.Cursor,
            WorkspaceContext.FromRoot(@"C:\repo"),
            "s1",
            []);
        SessionCatalogEntry second = first with { Title = "Named session" };
        FakeCatalogSource sourceOne = new("one", [first]);
        FakeCatalogSource sourceTwo = new("two", [second]);
        await using SessionCatalogCoordinator coordinator = new(
            [sourceOne, sourceTwo],
            probes: []);
        SessionCatalogUpdatedEventArgs? notification = null;
        coordinator.Updated += (_, value) => notification = value;

        SessionCatalogUpdatedEventArgs result = await coordinator.RefreshAsync();

        Assert.Single(result.Sessions);
        Assert.Equal("Named session", result.Sessions[0].Title);
        Assert.Same(result, notification);
    }

    [Fact]
    public async Task CoordinatorMarksUnchangedCatalogRefresh()
    {
        SessionCatalogEntry session = Session(
            "cursor:s1",
            "s1",
            HookProvider.Cursor,
            WorkspaceContext.FromRoot(@"C:\repo"),
            "First title",
            []);
        MutableCatalogSource source = new([session]);
        await using SessionCatalogCoordinator coordinator = new(
            [source],
            probes: []);

        SessionCatalogUpdatedEventArgs first =
            await coordinator.RefreshAsync();
        SessionCatalogUpdatedEventArgs unchanged =
            await coordinator.RefreshAsync();
        source.Sessions = [session with { Title = "Changed title" }];
        SessionCatalogUpdatedEventArgs changed =
            await coordinator.RefreshAsync();

        Assert.True(first.HasCatalogChanges);
        Assert.False(unchanged.HasCatalogChanges);
        Assert.True(changed.HasCatalogChanges);
    }

    [Fact]
    public async Task FileNotificationsDoNotCancelRunningRefresh()
    {
        string firstRoot = Path.Combine(
            Path.GetTempPath(),
            "HarnessSpy.Tests",
            Guid.NewGuid().ToString("N"),
            "first");
        string secondRoot = Path.Combine(
            Path.GetDirectoryName(firstRoot)!,
            "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);

        BlockingCatalogSource first = new(firstRoot);
        CountingCatalogSource second = new(secondRoot);
        SessionCatalogCoordinator coordinator = new(
            [first, second],
            probes: [],
            recoveryInterval: TimeSpan.FromHours(1));
        try
        {
            await coordinator.RefreshAsync();
            coordinator.StartWatching();

            await System.IO.File.WriteAllTextAsync(
                Path.Combine(firstRoot, "first.jsonl"),
                "{}");
            await first.SecondScanStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

            await System.IO.File.WriteAllTextAsync(
                Path.Combine(firstRoot, "second.jsonl"),
                "{}");
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.False(first.SecondScanWasCancelled);
            Assert.Equal(1, second.ScanCount);

            first.ReleaseSecondScan();
            await WaitUntilAsync(
                () => first.ScanCount >= 3,
                TimeSpan.FromSeconds(10));

            Assert.False(first.SecondScanWasCancelled);
            Assert.Equal(1, second.ScanCount);
        }
        finally
        {
            first.ReleaseSecondScan();
            await coordinator.DisposeAsync();
            Directory.Delete(Path.GetDirectoryName(firstRoot)!, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceNormalizerDecodesFileUriAndDeduplicatesRoots()
    {
        WorkspaceNormalizer normalizer = new();

        WorkspaceContext workspace = normalizer.FromRoots(
            ["file:///C:/repo", @"C:\repo\", @"C:\other"]);

        Assert.Equal(WorkspaceContextKind.Normal, workspace.Kind);
        Assert.Equal(2, workspace.DisplayRoots.Count);
        Assert.StartsWith("Multiroot:", workspace.DisplayName, StringComparison.Ordinal);
    }

    private static SessionCatalogEntry Session(
        string catalogId,
        string nativeId,
        HookProvider provider,
        WorkspaceContext workspace,
        string title,
        IReadOnlyList<SessionFileBinding> files)
    {
        return new SessionCatalogEntry
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
            Workspace = workspace,
            Title = title,
            Files = files
        };
    }

    private static SessionFileBinding File(string path, SessionSourceKind kind) =>
        new(
            path,
            kind,
            DialectIds.UnknownTranscript,
            TranscriptFileRole.Main,
            DateTimeOffset.UnixEpoch,
            1);

    private sealed class RecordingLauncher : IExternalLauncher
    {
        public string Arguments { get; private set; } = string.Empty;

        public Task LaunchAsync(
            string fileName,
            string arguments,
            string? workingDirectory,
            bool useShellExecute,
            CancellationToken cancellationToken)
        {
            Arguments = arguments;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCatalogSource(
        string name,
        IReadOnlyList<SessionCatalogEntry> sessions) : ISessionCatalogSource
    {
        public string Name { get; } = name;

        public HookProvider Provider => HookProvider.Unknown;

        public IReadOnlyList<string> WatchRoots => [];

        public Task<SessionCatalogScanResult> ScanAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new SessionCatalogScanResult(sessions, true, []));
    }

    private sealed class MutableCatalogSource(
        IReadOnlyList<SessionCatalogEntry> sessions) : ISessionCatalogSource
    {
        public string Name => "mutable";

        public HookProvider Provider => HookProvider.Cursor;

        public IReadOnlyList<string> WatchRoots => [];

        public IReadOnlyList<SessionCatalogEntry> Sessions { get; set; } =
            sessions;

        public Task<SessionCatalogScanResult> ScanAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new SessionCatalogScanResult(Sessions, true, []));
    }

    private sealed class CountingCatalogSource(string root)
        : ISessionCatalogSource
    {
        private int _scanCount;

        public string Name => "counting";

        public HookProvider Provider => HookProvider.ClaudeCode;

        public IReadOnlyList<string> WatchRoots => [root];

        public int ScanCount => Volatile.Read(ref _scanCount);

        public Task<SessionCatalogScanResult> ScanAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _scanCount);
            return Task.FromResult(SessionCatalogScanResult.Empty);
        }
    }

    private sealed class BlockingCatalogSource(string root)
        : ISessionCatalogSource
    {
        private readonly TaskCompletionSource<bool> _secondScanStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseSecondScan =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _scanCount;
        private int _secondScanWasCancelled;

        public string Name => "blocking";

        public HookProvider Provider => HookProvider.Cursor;

        public IReadOnlyList<string> WatchRoots => [root];

        public int ScanCount => Volatile.Read(ref _scanCount);

        public TaskCompletionSource<bool> SecondScanStarted =>
            _secondScanStarted;

        public bool SecondScanWasCancelled =>
            Volatile.Read(ref _secondScanWasCancelled) != 0;

        public async Task<SessionCatalogScanResult> ScanAsync(
            CancellationToken cancellationToken)
        {
            int scan = Interlocked.Increment(ref _scanCount);
            if (scan != 2)
            {
                return SessionCatalogScanResult.Empty;
            }

            using CancellationTokenRegistration registration =
                cancellationToken.Register(
                    () => Interlocked.Exchange(
                        ref _secondScanWasCancelled,
                        1));
            _secondScanStarted.TrySetResult(true);
            await _releaseSecondScan.Task.WaitAsync(cancellationToken);
            return SessionCatalogScanResult.Empty;
        }

        public void ReleaseSecondScan() =>
            _releaseSecondScan.TrySetResult(true);
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        using CancellationTokenSource cancellation = new(timeout);
        while (!predicate())
        {
            await Task.Delay(50, cancellation.Token);
        }
    }
}
