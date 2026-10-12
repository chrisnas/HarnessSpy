using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Opening;
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
    public async Task CoordinatorPreservesProviderLifecycleState()
    {
        SessionCatalogEntry session = Session(
            "copilot:active",
            "active",
            HookProvider.GitHubCopilot,
            WorkspaceContext.FromRoot(@"C:\repo"),
            "active",
            []) with
        {
            LifecycleState = SessionLifecycleState.Open,
            LifecycleEvidence = InferenceEvidence.Observed
        };
        await using SessionCatalogCoordinator coordinator = new(
            [new FakeCatalogSource("copilot", [session])]);

        SessionCatalogUpdatedEventArgs result =
            await coordinator.RefreshAsync();

        SessionCatalogEntry actual = Assert.Single(result.Sessions);
        Assert.Equal(SessionLifecycleState.Open, actual.LifecycleState);
        Assert.Equal(InferenceEvidence.Observed, actual.LifecycleEvidence);
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
            [sourceOne, sourceTwo]);
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
            [source]);

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
    public async Task ProviderFileChangesDoNotTriggerRefresh()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "HarnessSpy.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        CountingCatalogSource source = new(
            root,
            HookProvider.Cursor);
        await using SessionCatalogCoordinator coordinator = new(
            [source]);
        try
        {
            await coordinator.RefreshAsync();
            await System.IO.File.WriteAllTextAsync(
                Path.Combine(root, "session.jsonl"),
                "{}");
            await Task.Delay(TimeSpan.FromMilliseconds(750));

            Assert.Equal(1, source.ScanCount);
            await coordinator.RefreshAsync();
            Assert.Equal(2, source.ScanCount);
            await coordinator.RefreshAsync();
            Assert.Equal(2, source.ScanCount);
            await coordinator.RefreshAsync(
                new SessionRefreshRequest(
                    SessionRefreshTrigger.RefreshButton,
                    Mode: SessionRefreshMode.ForceFull));
            Assert.Equal(3, source.ScanCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CoordinatorScansOnlyExplicitEnabledProviders()
    {
        CountingCatalogSource cursor = new(
            "cursor",
            HookProvider.Cursor);
        CountingCatalogSource claude = new(
            "claude",
            HookProvider.ClaudeCode);
        await using SessionCatalogCoordinator coordinator = new(
            [cursor, claude],
            enabledProviders: [HookProvider.Cursor]);

        await coordinator.RefreshAsync(
            new SessionRefreshRequest(
                SessionRefreshTrigger.Startup));

        Assert.Equal(1, cursor.ScanCount);
        Assert.Equal(0, claude.ScanCount);

        coordinator.SetEnabledProviders(
            [HookProvider.Cursor, HookProvider.ClaudeCode]);
        await coordinator.RefreshAsync(
            new SessionRefreshRequest(
                SessionRefreshTrigger.HarnessSelection,
                new HashSet<HookProvider> { HookProvider.ClaudeCode }));

        Assert.Equal(1, cursor.ScanCount);
        Assert.Equal(1, claude.ScanCount);

        coordinator.SetEnabledProviders([HookProvider.ClaudeCode]);
        await coordinator.RefreshAsync(
            new SessionRefreshRequest(
                SessionRefreshTrigger.RefreshButton,
                Mode: SessionRefreshMode.ForceFull));

        Assert.Equal(1, cursor.ScanCount);
        Assert.Equal(2, claude.ScanCount);
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

    private sealed class CountingCatalogSource(
        string root,
        HookProvider provider)
        : ISessionCatalogSource
    {
        private int _scanCount;

        public string Name => "counting";

        public HookProvider Provider => provider;

        public IReadOnlyList<string> WatchRoots => [root];

        public int ScanCount => Volatile.Read(ref _scanCount);

        public Task<SessionCatalogScanResult> ScanAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _scanCount);
            return Task.FromResult(SessionCatalogScanResult.Empty);
        }
    }

}
