using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Plans;
using HarnessSpy.Core.Sessions.Process;

namespace HarnessSpy.Core.Services;

public sealed record SessionCatalogUpdatedEventArgs(
    IReadOnlyList<SessionCatalogEntry> Sessions,
    IReadOnlyList<string> Warnings,
    bool IsComplete,
    TimeSpan Duration,
    bool IsFinal = true,
    bool HasCatalogChanges = true)
{
    // Bound and orphan plan artifacts assembled for this update. An artifact
    // whose BoundCatalogSessionId is null is an orphan.
    public IReadOnlyList<Sessions.Plans.SessionPlanArtifact> Plans { get; init; } = [];
}

public sealed class SessionCatalogCoordinator : IAsyncDisposable
{
    private readonly IReadOnlyList<ISessionCatalogSource> _sources;
    private readonly IReadOnlyList<ISessionMetadataEnricher> _enrichers;
    private readonly IReadOnlyList<IRunningSessionProbe> _probes;
    private readonly SessionCatalogMerger _merger;
    private readonly ProcessSessionCorrelator _correlator;
    private readonly SessionPlanCatalogAssembler _planAssembler = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _requestGate = new();
    private readonly TimeSpan _recoveryInterval;
    private readonly SessionCatalogScanResult?[] _cachedSourceResults;
    private readonly HashSet<int> _queuedSourceIndices = [];
    private DebouncedFileSystemWatcher? _watcher;
    private Timer? _recoveryTimer;
    private CancellationTokenSource? _backgroundRefreshCancellation;
    private CatalogFingerprint _lastPublishedFingerprint;
    private bool _hasPublishedFinal;
    private volatile bool _backgroundRefreshRunning;
    private bool _disposed;

    public SessionCatalogCoordinator(
        IEnumerable<ISessionCatalogSource> sources,
        IEnumerable<IRunningSessionProbe>? probes = null,
        IEnumerable<ISessionMetadataEnricher>? enrichers = null,
        TimeSpan? recoveryInterval = null,
        SessionCatalogMerger? merger = null,
        ProcessSessionCorrelator? correlator = null)
    {
        _sources = [.. sources];
        _probes = probes is null ? [] : [.. probes];
        _enrichers = enrichers is null ? [] : [.. enrichers];
        _recoveryInterval = recoveryInterval ?? TimeSpan.FromMinutes(1);
        _merger = merger ?? new SessionCatalogMerger();
        _correlator = correlator ?? new ProcessSessionCorrelator();
        _cachedSourceResults = new SessionCatalogScanResult?[_sources.Count];
    }

    public event EventHandler<SessionCatalogUpdatedEventArgs>? Updated;

    public Task<SessionCatalogUpdatedEventArgs> RefreshAsync(
        CancellationToken cancellationToken = default) =>
        RefreshAsync(
            Enumerable.Range(0, _sources.Count).ToHashSet(),
            cancellationToken);

    private async Task<SessionCatalogUpdatedEventArgs> RefreshAsync(
        IReadOnlySet<int> sourceIndices,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            // Probe running processes once up front. The result is independent
            // of which provider files have been scanned so far, so every
            // incremental batch can already carry accurate open/closed state.
            RunningSessionHint[] hints = [];
            if (_probes.Count > 0)
            {
                IReadOnlyList<RunningSessionHint>[] probeResults = await Task.WhenAll(
                    _probes.Select(probe => probe.ProbeAsync(cancellationToken)))
                    .ConfigureAwait(false);
                hints = [.. probeResults.SelectMany(static value => value)];
            }

            int sourceCount = _sources.Count;
            int selectedSourceCount = sourceIndices.Count;

            // The freshest sessions known for each source: streaming sources
            // update their slot as they discover sessions. Sources unaffected
            // by a file notification keep their cached authoritative result,
            // so a Claude/Copilot edit does not force another Cursor scan.
            IReadOnlyList<SessionCatalogEntry>[] sessionsBySource =
                new IReadOnlyList<SessionCatalogEntry>[sourceCount];
            for (int index = 0; index < sourceCount; index++)
            {
                sessionsBySource[index] =
                    _cachedSourceResults[index]?.Sessions ??
                    Array.Empty<SessionCatalogEntry>();
            }

            int completedCount = 0;

            // All source scans feed one ordered channel, so a single reader can
            // merge and publish updates without cross-thread races. Streaming
            // sources push progress snapshots; every source pushes a final
            // completion event.
            Channel<ScanEvent> channel = Channel.CreateUnbounded<ScanEvent>(
                new UnboundedChannelOptions
                {
                    SingleReader = true
                });

            List<Task> scanTasks = [];
            foreach (int index in sourceIndices)
            {
                int sourceIndex = index;
                ISessionCatalogSource source = _sources[sourceIndex];
                scanTasks.Add(Task.Run(
                    async () =>
                    {
                        SourceProgress reporter = new(channel.Writer, sourceIndex);
                        SessionCatalogScanResult scan = await ScanSafeAsync(
                            source,
                            reporter,
                            cancellationToken).ConfigureAwait(false);
                        channel.Writer.TryWrite(
                            new ScanEvent(sourceIndex, scan, null));
                    },
                    cancellationToken));
            }

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await Task.WhenAll(scanTasks).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Faulted or cancelled scans still complete the channel
                        // so the reader loop can exit cleanly.
                    }
                    finally
                    {
                        channel.Writer.TryComplete();
                    }
                },
                CancellationToken.None);

            SessionCatalogUpdatedEventArgs? lastResult = null;
            await foreach (ScanEvent scanEvent in channel.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (scanEvent.Result is SessionCatalogScanResult completedScan)
                {
                    _cachedSourceResults[scanEvent.SourceIndex] = completedScan;
                    sessionsBySource[scanEvent.SourceIndex] = completedScan.Sessions;
                    completedCount++;
                }
                else if (scanEvent.Partial is IReadOnlyList<SessionCatalogEntry> partial)
                {
                    sessionsBySource[scanEvent.SourceIndex] = partial;
                }

                bool isFinal = completedCount == selectedSourceCount;
                IReadOnlyList<SessionCatalogEntry> sessions = _merger.Merge(
                    sessionsBySource.SelectMany(static value => value));
                if (isFinal)
                {
                    foreach (ISessionMetadataEnricher enricher in _enrichers)
                    {
                        sessions = await enricher
                            .EnrichAsync(sessions, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                if (hints.Length > 0)
                {
                    sessions = _correlator.Apply(sessions, hints);
                }

                IReadOnlyList<SessionPlanArtifact> plans = isFinal
                    ? AssemblePlans(sessions)
                    : [];
                bool hasCatalogChanges = true;
                if (isFinal)
                {
                    CatalogFingerprint fingerprint =
                        CatalogFingerprint.Create(sessions, plans);
                    hasCatalogChanges =
                        !_hasPublishedFinal ||
                        fingerprint != _lastPublishedFingerprint;
                    _lastPublishedFingerprint = fingerprint;
                    _hasPublishedFinal = true;
                }

                bool allComplete = _cachedSourceResults.All(
                    static result => result?.IsComplete == true);
                lastResult = new SessionCatalogUpdatedEventArgs(
                    sessions,
                    CollectWarnings(),
                    isFinal && allComplete,
                    stopwatch.Elapsed,
                    IsFinal: isFinal,
                    HasCatalogChanges: hasCatalogChanges)
                {
                    Plans = plans
                };
                Updated?.Invoke(this, lastResult);

                if (isFinal)
                {
                    break;
                }
            }

            if (lastResult is null)
            {
                IReadOnlyList<SessionCatalogEntry> sessions = _merger.Merge(
                    _cachedSourceResults
                        .Where(static result => result is not null)
                        .SelectMany(static result => result!.Sessions));
                IReadOnlyList<SessionPlanArtifact> plans = AssemblePlans(sessions);
                CatalogFingerprint fingerprint =
                    CatalogFingerprint.Create(sessions, plans);
                bool hasCatalogChanges =
                    !_hasPublishedFinal ||
                    fingerprint != _lastPublishedFingerprint;
                _lastPublishedFingerprint = fingerprint;
                _hasPublishedFinal = true;
                lastResult = new SessionCatalogUpdatedEventArgs(
                    sessions,
                    CollectWarnings(),
                    _cachedSourceResults.All(
                        static result => result?.IsComplete == true),
                    stopwatch.Elapsed,
                    IsFinal: true,
                    HasCatalogChanges: hasCatalogChanges)
                {
                    Plans = plans
                };
                Updated?.Invoke(this, lastResult);
            }

            return lastResult;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // One entry on the scan channel: either a streaming progress snapshot
    // (Partial set) or a source's final completion (Result).
    private sealed record ScanEvent(
        int SourceIndex,
        SessionCatalogScanResult? Result,
        IReadOnlyList<SessionCatalogEntry>? Partial);

    private sealed class SourceProgress(
        ChannelWriter<ScanEvent> writer,
        int sourceIndex)
        : IProgress<IReadOnlyList<SessionCatalogEntry>>
    {
        public void Report(IReadOnlyList<SessionCatalogEntry> value)
        {
            writer.TryWrite(new ScanEvent(sourceIndex, null, value));
        }
    }

    private readonly record struct CatalogFingerprint(
        int SessionCount,
        int TurnCount,
        int EventCount,
        int PlanCount,
        int ContentHash)
    {
        public static CatalogFingerprint Create(
            IReadOnlyList<SessionCatalogEntry> sessions,
            IReadOnlyList<SessionPlanArtifact> plans)
        {
            HashCode hash = new();
            int turnCount = 0;
            int eventCount = 0;

            foreach (SessionCatalogEntry session in sessions.OrderBy(
                         static item => item.CatalogSessionId,
                         StringComparer.Ordinal))
            {
                hash.Add(session.CatalogSessionId, StringComparer.Ordinal);
                hash.Add(session.NativeSessionId, StringComparer.Ordinal);
                hash.Add(session.Provider);
                hash.Add(session.Surface);
                hash.Add(session.Workspace.Key, StringComparer.Ordinal);
                hash.Add(session.Workspace.DisplayName, StringComparer.Ordinal);
                foreach (string root in session.Workspace.DisplayRoots)
                {
                    hash.Add(root, StringComparer.OrdinalIgnoreCase);
                }

                hash.Add(session.Title, StringComparer.Ordinal);
                hash.Add(session.StartedAtUtc);
                hash.Add(session.LastActivityAtUtc);
                hash.Add(session.Model, StringComparer.Ordinal);
                hash.Add(session.Mode, StringComparer.Ordinal);
                hash.Add(session.LifecycleState);
                hash.Add(session.LifecycleEvidence);
                hash.Add(session.IsSelectedInHarness);

                foreach (SessionFileBinding file in session.Files.OrderBy(
                             static item => item.Path,
                             StringComparer.OrdinalIgnoreCase))
                {
                    hash.Add(file.Path, StringComparer.OrdinalIgnoreCase);
                    hash.Add(file.SourceKind);
                    hash.Add(file.DialectId, StringComparer.Ordinal);
                    hash.Add(file.Role);
                    hash.Add(file.LastWriteTimeUtc);
                    hash.Add(file.Length);
                    hash.Add(file.AgentId, StringComparer.Ordinal);
                    hash.Add(file.ParentSessionId, StringComparer.Ordinal);
                }

                foreach ((string key, string? value) in session.Metadata.OrderBy(
                             static item => item.Key,
                             StringComparer.Ordinal))
                {
                    hash.Add(key, StringComparer.Ordinal);
                    hash.Add(value, StringComparer.Ordinal);
                }

                foreach (SessionTurn turn in session.Turns)
                {
                    turnCount++;
                    hash.Add(turn.Id, StringComparer.Ordinal);
                    hash.Add(turn.Number);
                    hash.Add(turn.Prompt, StringComparer.Ordinal);
                    hash.Add(turn.StartedAtUtc);
                    hash.Add(turn.EndedAtUtc);
                    hash.Add(turn.Evidence);

                    foreach (SessionEventRecord item in turn.Events)
                    {
                        eventCount++;
                        AddEvent(ref hash, item);
                    }
                }
            }

            foreach (SessionPlanArtifact plan in plans.OrderBy(
                         static item => item.CatalogPlanId,
                         StringComparer.Ordinal))
            {
                AddPlan(ref hash, plan);
            }

            return new CatalogFingerprint(
                sessions.Count,
                turnCount,
                eventCount,
                plans.Count,
                hash.ToHashCode());
        }

        private static void AddPlan(ref HashCode hash, SessionPlanArtifact plan)
        {
            hash.Add(plan.CatalogPlanId, StringComparer.Ordinal);
            hash.Add(plan.Provider);
            hash.Add(plan.BoundCatalogSessionId, StringComparer.Ordinal);
            hash.Add(plan.BoundTurnId, StringComparer.Ordinal);
            hash.Add(plan.BindingReason);
            hash.Add(plan.BindingEvidence);
            hash.Add(plan.PrimaryPath, StringComparer.OrdinalIgnoreCase);
            hash.Add(plan.CurrentContentHash, StringComparer.Ordinal);
            hash.Add(plan.ObservedUpdateCount);
            hash.Add(plan.HasIncompleteRevisionHistory);
            hash.Add(plan.Title, StringComparer.Ordinal);
            hash.Add(plan.Workspace.Key, StringComparer.Ordinal);
            foreach (SessionPlanRevision revision in plan.Revisions)
            {
                hash.Add(revision.Sequence);
                hash.Add(revision.NormalizedContentHash, StringComparer.Ordinal);
                hash.Add(revision.TurnId, StringComparer.Ordinal);
                hash.Add(revision.IsMaterialized);
            }

            foreach (SessionPlanActivity activity in plan.Activities)
            {
                hash.Add(activity.Id, StringComparer.Ordinal);
                hash.Add(activity.Kind);
                hash.Add(activity.TurnId, StringComparer.Ordinal);
            }
        }

        private static void AddEvent(
            ref HashCode hash,
            SessionEventRecord item)
        {
            hash.Add(item.Id, StringComparer.Ordinal);
            hash.Add(item.NativeName, StringComparer.Ordinal);
            hash.Add(item.Role);
            hash.Add(item.EventKind);
            hash.Add(item.ToolKind);
            hash.Add(item.Direction);
            hash.Add(item.Tone);
            hash.Add(item.Evidence);
            hash.Add(item.TimestampUtc);
            hash.Add(item.Order);
            hash.Add(item.TurnId, StringComparer.Ordinal);
            hash.Add(item.ParentId, StringComparer.Ordinal);
            hash.Add(item.AssistantStepId, StringComparer.Ordinal);
            hash.Add(item.ParallelGroupId, StringComparer.Ordinal);
            hash.Add(item.ToolCallId, StringComparer.Ordinal);
            hash.Add(item.ToolName, StringComparer.Ordinal);
            hash.Add(item.McpServerName, StringComparer.Ordinal);
            hash.Add(item.McpToolName, StringComparer.Ordinal);
            hash.Add(item.PromptText, StringComparer.Ordinal);
            hash.Add(item.Text, StringComparer.Ordinal);
            hash.Add(item.Model, StringComparer.Ordinal);
            hash.Add(item.Mode, StringComparer.Ordinal);
            hash.Add(item.Status, StringComparer.Ordinal);
            hash.Add(item.AgentId, StringComparer.Ordinal);
            hash.Add(item.AgentType, StringComparer.Ordinal);
            hash.Add(item.Task, StringComparer.Ordinal);
            hash.Add(item.DurationMs);
            hash.Add(item.IsFailure);
            hash.Add(item.IsAborted);
            hash.Add(item.IsParallelCandidate);
            hash.Add(item.ExcludeFromSummary);
            foreach (string path in item.TargetPaths)
            {
                hash.Add(path, StringComparer.OrdinalIgnoreCase);
            }

            foreach (UsageMeasurement usage in item.UsageMeasurements)
            {
                hash.Add(usage);
            }

            hash.Add(item.Skill);
            hash.Add(item.Provenance.Path, StringComparer.OrdinalIgnoreCase);
            hash.Add(item.Provenance.LineNumber);
            hash.Add(item.Provenance.ByteOffset);
            hash.Add(item.Provenance.RecordId, StringComparer.Ordinal);
            hash.Add(item.Provenance.DatabaseKey, StringComparer.Ordinal);
            hash.Add(item.Provenance.RawContent.Length);
        }
    }

    private IReadOnlyList<SessionPlanArtifact> AssemblePlans(
        IReadOnlyList<SessionCatalogEntry> sessions)
    {
        List<SessionPlanArtifact> artifacts = [];
        List<SessionPlanActivity> activities = [];
        foreach (SessionCatalogScanResult? result in _cachedSourceResults)
        {
            if (result is null)
            {
                continue;
            }

            artifacts.AddRange(result.PlanFragment.Artifacts);
            activities.AddRange(result.PlanFragment.Activities);
        }

        if (artifacts.Count == 0 && activities.Count == 0)
        {
            return [];
        }

        return _planAssembler.Assemble(
            sessions,
            new SessionPlanCatalogFragment
            {
                Artifacts = artifacts,
                Activities = activities
            });
    }

    private string[] CollectWarnings() =>
        _cachedSourceResults
            .Where(static result => result is not null)
            .SelectMany(static result => result!.Warnings)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public void StartWatching()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watcher is not null)
        {
            return;
        }

        _watcher = new DebouncedFileSystemWatcher(
            _sources.SelectMany(static source => source.WatchRoots),
            changedPaths => QueueRefresh(
                changedPaths,
                isRecovery: false));
        _watcher.Start();

        _recoveryTimer = new Timer(
            _ => QueueRefresh([], isRecovery: true),
            null,
            _recoveryInterval,
            _recoveryInterval);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _recoveryTimer?.Dispose();

        CancellationTokenSource? pending;
        lock (_requestGate)
        {
            pending = _backgroundRefreshCancellation;
            _backgroundRefreshCancellation = null;
            _queuedSourceIndices.Clear();
        }

        if (pending is not null)
        {
            try
            {
                await pending.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        await _refreshGate.WaitAsync().ConfigureAwait(false);
        _refreshGate.Release();
        _refreshGate.Dispose();
    }

    private void QueueRefresh(
        IReadOnlyCollection<string> changedPaths,
        bool isRecovery)
    {
        if (_disposed || !ShouldRefresh(changedPaths))
        {
            return;
        }

        // A recovery tick is only a fallback for missed notifications. Never
        // queue another full scan while any refresh is already active/queued;
        // otherwise a slow Cursor scan and a short timer interval can create a
        // permanent back-to-back rescan loop.
        if (isRecovery &&
            (_backgroundRefreshRunning || _refreshGate.CurrentCount == 0))
        {
            return;
        }

        IReadOnlySet<int> sourceIndices = SourceIndicesFor(changedPaths);
        if (sourceIndices.Count == 0)
        {
            return;
        }

        CancellationTokenSource? requestToStart = null;
        lock (_requestGate)
        {
            if (_disposed)
            {
                return;
            }

            _queuedSourceIndices.UnionWith(sourceIndices);
            if (!_backgroundRefreshRunning)
            {
                _backgroundRefreshRunning = true;
                requestToStart = new CancellationTokenSource();
                _backgroundRefreshCancellation = requestToStart;
            }
        }

        if (requestToStart is not null)
        {
            _ = RefreshQueuedAsync(requestToStart);
        }
    }

    private async Task RefreshQueuedAsync(CancellationTokenSource request)
    {
        try
        {
            while (!request.IsCancellationRequested)
            {
                HashSet<int> sourceIndices;
                lock (_requestGate)
                {
                    if (_disposed || request.IsCancellationRequested)
                    {
                        return;
                    }

                    sourceIndices = [.. _queuedSourceIndices];
                    _queuedSourceIndices.Clear();
                }

                if (sourceIndices.Count == 0)
                {
                    return;
                }

                try
                {
                    await RefreshAsync(sourceIndices, request.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (request.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                }

                lock (_requestGate)
                {
                    if (_disposed ||
                        request.IsCancellationRequested ||
                        _queuedSourceIndices.Count == 0)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            CancellationTokenSource? replacement = null;
            lock (_requestGate)
            {
                if (ReferenceEquals(_backgroundRefreshCancellation, request))
                {
                    _backgroundRefreshCancellation = null;
                    _backgroundRefreshRunning = false;
                    if (!_disposed && _queuedSourceIndices.Count > 0)
                    {
                        _backgroundRefreshRunning = true;
                        replacement = new CancellationTokenSource();
                        _backgroundRefreshCancellation = replacement;
                    }
                }
            }

            request.Dispose();
            if (replacement is not null)
            {
                _ = RefreshQueuedAsync(replacement);
            }
        }
    }

    private bool ShouldRefresh(IReadOnlyCollection<string> changedPaths)
    {
        if (changedPaths.Count == 0)
        {
            return true;
        }

        foreach (string path in changedPaths)
        {
            string fileName = Path.GetFileName(path);
            string extension = Path.GetExtension(path);
            if (extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".vscdb", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".vscdb-wal", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("workspace.yaml", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("workspace.json", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("sessions-index.json", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private IReadOnlySet<int> SourceIndicesFor(
        IReadOnlyCollection<string> changedPaths)
    {
        if (changedPaths.Count == 0)
        {
            return Enumerable.Range(0, _sources.Count).ToHashSet();
        }

        HashSet<int> sourceIndices = [];
        for (int index = 0; index < _sources.Count; index++)
        {
            ISessionCatalogSource source = _sources[index];
            if (changedPaths.Any(changedPath =>
                    source.WatchRoots.Any(root =>
                        IsPathWithinRoot(changedPath, root))))
            {
                sourceIndices.Add(index);
            }
        }

        return sourceIndices;
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        try
        {
            string fullPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path));
            string fullRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(root));
            if (string.Equals(
                fullPath,
                fullRoot,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string rootPrefix =
                fullRoot + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static async Task<SessionCatalogScanResult> ScanSafeAsync(
        ISessionCatalogSource source,
        IProgress<IReadOnlyList<SessionCatalogEntry>>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            return source is IStreamingSessionCatalogSource streaming
                ? await streaming
                    .ScanAsync(progress, cancellationToken)
                    .ConfigureAwait(false)
                : await source.ScanAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                System.Text.Json.JsonException or
                InvalidDataException or
                Microsoft.Data.Sqlite.SqliteException or
                YamlDotNet.Core.YamlException)
        {
            return new SessionCatalogScanResult(
                [],
                false,
                [$"{source.Name}: {exception.Message}"]);
        }
    }
}
