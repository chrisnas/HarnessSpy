using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Plans;

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
    private readonly SessionCatalogMerger _merger;
    private readonly SessionPlanCatalogAssembler _planAssembler = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _providerGate = new();
    private readonly SessionCatalogScanResult?[] _cachedSourceResults;
    private readonly SessionSourceSnapshot?[] _sourceSnapshots;
    private readonly HashSet<HookProvider> _enabledProviders;
    private readonly SessionSourceSnapshotter _snapshotter;
    private readonly SessionCatalogCompactor? _compactor;
    private CatalogFingerprint _lastPublishedFingerprint;
    private bool _hasPublishedFinal;
    private bool _disposed;

    public SessionCatalogCoordinator(
        IEnumerable<ISessionCatalogSource> sources,
        IEnumerable<ISessionMetadataEnricher>? enrichers = null,
        IEnumerable<HookProvider>? enabledProviders = null,
        SessionSourceSnapshotter? snapshotter = null,
        SessionCatalogCompactor? compactor = null,
        SessionCatalogMerger? merger = null)
    {
        _sources = [.. sources];
        _enrichers = enrichers is null ? [] : [.. enrichers];
        _merger = merger ?? new SessionCatalogMerger();
        _cachedSourceResults = new SessionCatalogScanResult?[_sources.Count];
        _sourceSnapshots = new SessionSourceSnapshot?[_sources.Count];
        _snapshotter = snapshotter ?? new SessionSourceSnapshotter();
        _compactor = compactor;
        _enabledProviders = enabledProviders is null
            ? [.. _sources.Select(static source => source.Provider)]
            : [.. enabledProviders];
    }

    public event EventHandler<SessionCatalogUpdatedEventArgs>? Updated;

    public Task<SessionCatalogUpdatedEventArgs> RefreshAsync(
        CancellationToken cancellationToken = default) =>
        RefreshAsync(
            new SessionRefreshRequest(
                SessionRefreshTrigger.RefreshButton),
            cancellationToken);

    public Task<SessionCatalogUpdatedEventArgs> RefreshAsync(
        SessionRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RefreshAsync(
            SourceIndicesFor(request),
            request.Mode,
            cancellationToken);
    }

    public void SetEnabledProviders(
        IEnumerable<HookProvider> providers,
        bool evictDisabled = true)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_providerGate)
        {
            _enabledProviders.Clear();
            _enabledProviders.UnionWith(providers);
            if (evictDisabled)
            {
                for (int index = 0; index < _sources.Count; index++)
                {
                    if (!_enabledProviders.Contains(_sources[index].Provider))
                    {
                        _cachedSourceResults[index] = null;
                        _sourceSnapshots[index] = null;
                    }
                }
            }

            _hasPublishedFinal = false;
        }
    }

    private async Task<SessionCatalogUpdatedEventArgs> RefreshAsync(
        IReadOnlySet<int> sourceIndices,
        SessionRefreshMode refreshMode,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            int sourceCount = _sources.Count;
            HashSet<int> enabledSourceIndices = EnabledSourceIndices();
            int[] selectedIndices = sourceIndices
                .Where(enabledSourceIndices.Contains)
                .Distinct()
                .ToArray();
            int selectedSourceCount = selectedIndices.Length;

            // The freshest sessions known for each source: streaming sources
            // update their slot as they discover sessions. Enabled sources not
            // selected by this explicit request keep their cached result.
            IReadOnlyList<SessionCatalogEntry>[] sessionsBySource =
                new IReadOnlyList<SessionCatalogEntry>[sourceCount];
            for (int index = 0; index < sourceCount; index++)
            {
                sessionsBySource[index] = enabledSourceIndices.Contains(index)
                    ? CachedSourceResult(index)?.Sessions ??
                        Array.Empty<SessionCatalogEntry>()
                    : Array.Empty<SessionCatalogEntry>();
            }

            int completedCount = 0;

            // All source scans feed one ordered channel, so a single reader can
            // merge and publish updates without cross-thread races. Streaming
            // sources push progress snapshots; every source pushes a final
            // completion event.
            Channel<ScanEvent> channel = Channel.CreateBounded<ScanEvent>(
                new BoundedChannelOptions(Math.Max(4, sourceCount * 2))
                {
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.Wait
                });

            List<Task> scanTasks = [];
            foreach (int index in selectedIndices)
            {
                int sourceIndex = index;
                ISessionCatalogSource source = _sources[sourceIndex];
                scanTasks.Add(Task.Run(
                    async () =>
                    {
                        SessionSourceSnapshot snapshot = await _snapshotter
                            .CaptureAsync(source, cancellationToken)
                            .ConfigureAwait(false);
                        SessionCatalogScanResult? cached =
                            CachedSourceResult(sourceIndex);
                        if (refreshMode == SessionRefreshMode.CacheAware &&
                            snapshot.CanValidate &&
                            cached?.IsComplete == true &&
                            snapshot.Equals(
                                CachedSourceSnapshot(sourceIndex)))
                        {
                            await channel.Writer.WriteAsync(
                                new ScanEvent(
                                    sourceIndex,
                                    cached,
                                    null,
                                    snapshot,
                                    PerformedScan: false),
                                cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        SourceProgress reporter = new(channel.Writer, sourceIndex);
                        SessionCatalogScanResult scan = await ScanSafeAsync(
                            source,
                            reporter,
                            cancellationToken).ConfigureAwait(false);
                        scan = _compactor?.Compact(scan) ?? scan;
                        await channel.Writer.WriteAsync(
                            new ScanEvent(
                                sourceIndex,
                                scan,
                                null,
                                snapshot,
                                PerformedScan: true),
                            cancellationToken).ConfigureAwait(false);
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
            bool performedSourceScan = false;
            await foreach (ScanEvent scanEvent in channel.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                performedSourceScan |= scanEvent.PerformedScan;
                if (scanEvent.Result is SessionCatalogScanResult completedScan)
                {
                    if (IsSourceEnabled(scanEvent.SourceIndex))
                    {
                        SetCachedSourceState(
                            scanEvent.SourceIndex,
                            completedScan,
                            scanEvent.Snapshot);
                        sessionsBySource[scanEvent.SourceIndex] =
                            completedScan.Sessions;
                    }
                    else
                    {
                        sessionsBySource[scanEvent.SourceIndex] =
                            Array.Empty<SessionCatalogEntry>();
                    }

                    completedCount++;
                }
                else if (
                    IsSourceEnabled(scanEvent.SourceIndex) &&
                    scanEvent.Partial is IReadOnlyList<SessionCatalogEntry> partial)
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

                IReadOnlyList<SessionPlanArtifact> plans = isFinal
                    ? AssemblePlans(sessions)
                    : [];
                bool hasCatalogChanges = true;
                if (isFinal)
                {
                    if (!performedSourceScan &&
                        _enrichers.Count == 0 &&
                        HasPublishedFinal())
                    {
                        hasCatalogChanges = false;
                    }
                    else
                    {
                        CatalogFingerprint fingerprint =
                            CatalogFingerprint.Create(sessions, plans);
                        hasCatalogChanges = UpdateFingerprint(fingerprint);
                    }
                }

                bool allComplete = AllEnabledSourcesComplete();
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
                    EnabledSourceResults()
                        .Where(static result => result is not null)
                        .SelectMany(static result => result!.Sessions));
                IReadOnlyList<SessionPlanArtifact> plans = AssemblePlans(sessions);
                CatalogFingerprint fingerprint =
                    CatalogFingerprint.Create(sessions, plans);
                bool hasCatalogChanges = UpdateFingerprint(fingerprint);
                lastResult = new SessionCatalogUpdatedEventArgs(
                    sessions,
                    CollectWarnings(),
                    AllEnabledSourcesComplete(),
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
        IReadOnlyList<SessionCatalogEntry>? Partial,
        SessionSourceSnapshot? Snapshot,
        bool PerformedScan);

    private sealed class SourceProgress(
        ChannelWriter<ScanEvent> writer,
        int sourceIndex)
        : IProgress<IReadOnlyList<SessionCatalogEntry>>
    {
        public void Report(IReadOnlyList<SessionCatalogEntry> value)
        {
            writer.TryWrite(
                new ScanEvent(
                    sourceIndex,
                    null,
                    value,
                    null,
                    PerformedScan: true));
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

                foreach (SessionEventRecord item in session.SessionEvents)
                {
                    eventCount++;
                    AddEvent(ref hash, item);
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
            hash.Add(
                item.SystemPrompt?.ContentHash,
                StringComparer.Ordinal);
            hash.Add(item.SystemPrompt?.PartCount);
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
            hash.Add(
                item.Provenance.RawContent?.Length ??
                item.Provenance.RawSource?.ByteLength ??
                item.Provenance.RawSource?.SourceLength);
        }
    }

    private IReadOnlyList<SessionPlanArtifact> AssemblePlans(
        IReadOnlyList<SessionCatalogEntry> sessions)
    {
        List<SessionPlanArtifact> artifacts = [];
        List<SessionPlanActivity> activities = [];
        foreach (SessionCatalogScanResult? result in EnabledSourceResults())
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
        EnabledSourceResults()
            .Where(static result => result is not null)
            .SelectMany(static result => result!.Warnings)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        _refreshGate.Release();
        _refreshGate.Dispose();
        lock (_providerGate)
        {
            Array.Clear(_cachedSourceResults);
            Array.Clear(_sourceSnapshots);
            _enabledProviders.Clear();
        }
    }

    private IReadOnlySet<int> SourceIndicesFor(
        SessionRefreshRequest request)
    {
        lock (_providerGate)
        {
            IReadOnlySet<HookProvider> requestedProviders =
                request.Providers ?? _enabledProviders;
            HashSet<int> indices = [];
            for (int index = 0; index < _sources.Count; index++)
            {
                HookProvider provider = _sources[index].Provider;
                if (_enabledProviders.Contains(provider) &&
                    requestedProviders.Contains(provider))
                {
                    indices.Add(index);
                }
            }

            return indices;
        }
    }

    private HashSet<int> EnabledSourceIndices()
    {
        lock (_providerGate)
        {
            HashSet<int> indices = [];
            for (int index = 0; index < _sources.Count; index++)
            {
                if (_enabledProviders.Contains(_sources[index].Provider))
                {
                    indices.Add(index);
                }
            }

            return indices;
        }
    }

    private SessionCatalogScanResult? CachedSourceResult(int sourceIndex)
    {
        lock (_providerGate)
        {
            return _cachedSourceResults[sourceIndex];
        }
    }

    private SessionSourceSnapshot? CachedSourceSnapshot(int sourceIndex)
    {
        lock (_providerGate)
        {
            return _sourceSnapshots[sourceIndex];
        }
    }

    private void SetCachedSourceState(
        int sourceIndex,
        SessionCatalogScanResult result,
        SessionSourceSnapshot? snapshot)
    {
        lock (_providerGate)
        {
            if (_enabledProviders.Contains(_sources[sourceIndex].Provider))
            {
                _cachedSourceResults[sourceIndex] = result;
                _sourceSnapshots[sourceIndex] = snapshot;
            }
        }
    }

    private bool IsSourceEnabled(int sourceIndex)
    {
        lock (_providerGate)
        {
            return _enabledProviders.Contains(
                _sources[sourceIndex].Provider);
        }
    }

    private SessionCatalogScanResult?[] EnabledSourceResults()
    {
        lock (_providerGate)
        {
            return _cachedSourceResults
                .Where((_, index) =>
                    _enabledProviders.Contains(_sources[index].Provider))
                .ToArray();
        }
    }

    private bool AllEnabledSourcesComplete()
    {
        lock (_providerGate)
        {
            for (int index = 0; index < _sources.Count; index++)
            {
                if (_enabledProviders.Contains(_sources[index].Provider) &&
                    _cachedSourceResults[index]?.IsComplete != true)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private bool UpdateFingerprint(CatalogFingerprint fingerprint)
    {
        lock (_providerGate)
        {
            bool hasChanges =
                !_hasPublishedFinal ||
                fingerprint != _lastPublishedFingerprint;
            _lastPublishedFingerprint = fingerprint;
            _hasPublishedFinal = true;
            return hasChanges;
        }
    }

    private bool HasPublishedFinal()
    {
        lock (_providerGate)
        {
            return _hasPublishedFinal;
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
