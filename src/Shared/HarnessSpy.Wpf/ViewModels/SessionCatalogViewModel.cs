using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Wpf.ViewModels;

public sealed class SessionCatalogViewModel : ObservableObject, IDisposable
{
    private const int MaximumDeepSearchBytes = 16 * 1024 * 1024;

    private readonly SessionCatalogCoordinator _coordinator;
    private readonly ISessionOpener _sessionOpener;
    private readonly SessionTreeProjector _projector;
    private readonly ISessionRawSourceResolver _rawSourceResolver;
    private readonly IReadOnlyList<ISessionDetailHydrator> _detailHydrators;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshRequestGate = new(1, 1);
    private CancellationTokenSource? _rawSearchCancellation;
    private readonly List<string> _favoriteIds = [];
    private readonly Action<IReadOnlyList<string>>? _persistFavorites;
    private readonly HashSet<HookProvider> _enabledProviders = [];
    private readonly HashSet<SessionTreeNodeViewModel> _observedNodes = [];
    private readonly HashSet<string> _hydratingNodeIds =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionTreeNodeViewModel> _nodesById =
        new(StringComparer.Ordinal);
    private readonly List<SessionTreeNodeViewModel> _nodeOrder = [];
    private readonly Action<IReadOnlyList<HookProvider>>? _persistEnabledProviders;
    private readonly ConditionalWeakTable<
        SessionCatalogUpdatedEventArgs,
        TaskCompletionSource<bool>> _updateCompletions = new();

    // Harnesses offered by the filter combobox, in display order.
    private static readonly (HookProvider Provider, string DisplayName)[]
        FilterableProviders =
    [
        (HookProvider.Cursor, "Cursor"),
        (HookProvider.ClaudeCode, "Claude"),
        (HookProvider.GitHubCopilot, "Copilot")
    ];

    private SessionCatalogUpdatedEventArgs? _lastAppliedUpdate;
    private SessionCatalogUpdatedEventArgs? _lastRenderedUpdate;
    private bool _suppressFilterRerender;
    private bool _filterRerenderPending;
    private int _filterGeneration;
    private string _harnessFilterSummary = string.Empty;
    private SessionCatalogUpdatedEventArgs? _pendingUpdate;
    private bool _isProcessingUpdate;
    private bool _hasRenderedFinal;
    private SessionTreeNodeViewModel? _selectedNode;
    private string? _currentSearchNodeId;
    private string _searchQuery = string.Empty;
    private bool _searchHookNamesOnly;
    private string _searchStatus = string.Empty;
    private string _statusText = "Ready to discover provider sessions.";
    private string _warningText = string.Empty;
    private bool _isRefreshing;
    private bool _isInitialized;
    private bool _isDisposed;

    public SessionCatalogViewModel(
        SessionCatalogCoordinator coordinator,
        ISessionOpener sessionOpener,
        Dispatcher dispatcher,
        SessionTreeProjector? projector = null,
        SessionViewerPreferencesViewModel? preferences = null,
        ISessionRawSourceResolver? rawSourceResolver = null,
        IReadOnlyList<ISessionDetailHydrator>? detailHydrators = null,
        IReadOnlyList<string>? initialFavorites = null,
        Action<IReadOnlyList<string>>? persistFavorites = null,
        IReadOnlyList<HookProvider>? initialEnabledProviders = null,
        Action<IReadOnlyList<HookProvider>>? persistEnabledProviders = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(sessionOpener);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _coordinator = coordinator;
        _sessionOpener = sessionOpener;
        _dispatcher = dispatcher;
        _projector = projector ?? new SessionTreeProjector();
        Preferences = preferences ?? new SessionViewerPreferencesViewModel();
        _rawSourceResolver =
            rawSourceResolver ?? new SessionRawSourceResolver();
        _detailHydrators = detailHydrators ?? [];
        Inspector = new SessionInspectorViewModel(
            _rawSourceResolver,
            Preferences);
        _persistFavorites = persistFavorites;
        _persistEnabledProviders = persistEnabledProviders;
        if (initialFavorites is not null)
        {
            foreach (string id in initialFavorites)
            {
                if (!string.IsNullOrWhiteSpace(id) && !_favoriteIds.Contains(id))
                {
                    _favoriteIds.Add(id);
                }
            }
        }

        InitializeProviderFilters(initialEnabledProviders);
        _coordinator.SetEnabledProviders(_enabledProviders);

        _coordinator.Updated += Coordinator_Updated;
    }

    // Builds the harness filter options from the known harnesses, restoring the
    // previously saved selection. A null selection (no settings yet) enables
    // every harness so the tree shows everything by default.
    private void InitializeProviderFilters(
        IReadOnlyList<HookProvider>? initialEnabledProviders)
    {
        HashSet<HookProvider> initialSet = initialEnabledProviders is null
            ? [.. FilterableProviders.Select(static entry => entry.Provider)]
            : [.. initialEnabledProviders];

        _suppressFilterRerender = true;
        try
        {
            foreach ((HookProvider provider, string displayName) in FilterableProviders)
            {
                bool selected = initialSet.Contains(provider);
                if (selected)
                {
                    _enabledProviders.Add(provider);
                }

                ProviderFilters.Add(new ProviderFilterOption(
                    provider,
                    displayName,
                    selected,
                    OnProviderFilterChanged));
            }
        }
        finally
        {
            _suppressFilterRerender = false;
        }

        UpdateHarnessFilterSummary();
    }

    public ObservableCollection<SessionTreeNodeViewModel> Roots { get; } = [];

    public ObservableCollection<SessionTreeNodeViewModel> FavoriteRoots { get; } = [];

    public bool HasFavorites => FavoriteRoots.Count > 0;

    public SessionViewerPreferencesViewModel Preferences { get; }

    public SessionInspectorViewModel Inspector { get; }

    // Multi-select harness filter bound to the Session Viewer combobox.
    public ObservableCollection<ProviderFilterOption> ProviderFilters { get; } = [];

    // Human-readable summary of the current harness selection, shown on the
    // collapsed combobox (e.g. "All harnesses" or "Cursor, Claude").
    public string HarnessFilterSummary
    {
        get => _harnessFilterSummary;
        private set => SetProperty(ref _harnessFilterSummary, value);
    }

    public SessionTreeNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(CanOpenSelectedSession));
            }
        }
    }

    public bool HasSelection => SelectedNode is not null;

    public bool CanOpenSelectedSession =>
        CanOpenSession(SelectedNode);

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                ClearSearchMatch();
            }
        }
    }

    // When enabled, the Find box only matches each event's native hook name
    // (the payload's hook_event_name), ignoring headers, summaries, details,
    // and raw content.
    public bool SearchHookNamesOnly
    {
        get => _searchHookNamesOnly;
        set
        {
            if (SetProperty(ref _searchHookNamesOnly, value))
            {
                ClearSearchMatch();
            }
        }
    }

    public string SearchStatus
    {
        get => _searchStatus;
        private set => SetProperty(ref _searchStatus, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string WarningText
    {
        get => _warningText;
        private set
        {
            if (SetProperty(ref _warningText, value))
            {
                OnPropertyChanged(nameof(HasWarnings));
            }
        }
    }

    public bool HasWarnings => !string.IsNullOrWhiteSpace(WarningText);

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                OnPropertyChanged(nameof(IsComputing));
            }
        }
    }

    public bool IsComputing => IsRefreshing || _hydratingNodeIds.Count > 0;

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        if (_isInitialized || _isDisposed)
        {
            return;
        }

        _isInitialized = true;
        await RefreshAsync(
            new SessionRefreshRequest(
                SessionRefreshTrigger.Startup),
            cancellationToken).ConfigureAwait(true);
    }

    public async Task RefreshAsync(
        CancellationToken cancellationToken = default) =>
        await RefreshAsync(
            new SessionRefreshRequest(
                SessionRefreshTrigger.RefreshButton),
            cancellationToken).ConfigureAwait(true);

    public async Task ForceRefreshAsync(
        CancellationToken cancellationToken = default) =>
        await RefreshAsync(
            new SessionRefreshRequest(
                SessionRefreshTrigger.RefreshButton,
                Mode: SessionRefreshMode.ForceFull),
            cancellationToken).ConfigureAwait(true);

    private async Task RefreshAsync(
        SessionRefreshRequest request,
        CancellationToken cancellationToken)
    {
        if (_isDisposed)
        {
            return;
        }

        await _refreshRequestGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using CancellationTokenSource refreshCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetime.Token);

            await InvokeOnDispatcherAsync(() =>
            {
                IsRefreshing = true;
                StatusText = "Discovering provider sessions...";
            }).ConfigureAwait(false);

            try
            {
                // Batches are projected incrementally through Coordinator_Updated
                // as each provider source finishes, so the tree fills in as
                // sessions are discovered instead of after the slowest source.
                SessionCatalogUpdatedEventArgs update = await _coordinator
                    .RefreshAsync(request, refreshCancellation.Token)
                    .ConfigureAwait(false);
                await WaitForUpdateAppliedAsync(update).ConfigureAwait(false);
                await InvokeOnDispatcherAsync(
                    () => UpdateProviderScanStatuses(request, update))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (refreshCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                await InvokeOnDispatcherAsync(() =>
                {
                    StatusText =
                        $"Session discovery failed: {exception.Message}";
                    UpdateProviderScanStatuses(
                        request,
                        update: null,
                        error: exception.Message);
                }).ConfigureAwait(false);
            }
            finally
            {
                if (!_dispatcher.HasShutdownStarted)
                {
                    await InvokeOnDispatcherAsync(
                            () => IsRefreshing = false)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _refreshRequestGate.Release();
        }
    }

    private void UpdateProviderScanStatuses(
        SessionRefreshRequest request,
        SessionCatalogUpdatedEventArgs? update,
        string? error = null)
    {
        IReadOnlySet<HookProvider> providers =
            request.Providers ?? _enabledProviders;
        string timestamp = DateTime.Now.ToString("T");
        foreach (ProviderFilterOption option in ProviderFilters.Where(
                     option => providers.Contains(option.Provider)))
        {
            if (error is not null)
            {
                option.UpdateScanStatus($"Failed at {timestamp}");
                continue;
            }

            int sessionCount = update?.Sessions.Count(
                session => session.Provider == option.Provider) ?? 0;
            option.UpdateScanStatus(
                $"{timestamp} \u00b7 {sessionCount} session" +
                (sessionCount == 1 ? string.Empty : "s"));
        }
    }

    public void SelectNode(
        SessionTreeNodeViewModel? node,
        bool preserveSearch = false)
    {
        if (ReferenceEquals(SelectedNode, node))
        {
            return;
        }

        if (SelectedNode is not null)
        {
            SelectedNode.IsSelected = false;
        }

        SelectedNode = node;
        if (node is not null)
        {
            node.IsSelected = true;
        }

        _ = Inspector.SelectAsync(node, _lifetime.Token);
        _ = EnsureNodeSummaryAsync(node);

        if (!preserveSearch)
        {
            ClearSearchMatch();
        }
    }

    public SessionTreeNodeViewModel? FindNext(bool previous)
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            ClearSearchMatch();
            return null;
        }

        List<SessionTreeNodeViewModel> allNodes = [.. _nodeOrder];
        List<SessionTreeNodeViewModel> matches = allNodes
            .Where(node => SearchHookNamesOnly
                ? node.MatchesHookName(SearchQuery)
                : node.Matches(
                    SearchQuery,
                    Preferences.IncludeRawSourceInSearch))
            .ToList();
        if (matches.Count == 0)
        {
            _currentSearchNodeId = null;
            SearchStatus = "No match.";
            return null;
        }

        int currentIndex = _currentSearchNodeId is null
            ? -1
            : matches.FindIndex(node =>
                StringComparer.Ordinal.Equals(
                    node.StableId,
                    _currentSearchNodeId));
        int nextIndex;
        if (currentIndex >= 0)
        {
            nextIndex = previous
                ? (currentIndex - 1 + matches.Count) % matches.Count
                : (currentIndex + 1) % matches.Count;
        }
        else
        {
            int selectedIndex = SelectedNode is null
                ? -1
                : allNodes.IndexOf(SelectedNode);
            nextIndex = FindNearestMatchIndex(
                allNodes,
                matches,
                selectedIndex,
                previous);
        }

        SessionTreeNodeViewModel match = matches[nextIndex];
        _currentSearchNodeId = match.StableId;
        SearchStatus = $"{nextIndex + 1} of {matches.Count}";

        List<SessionTreeNodeViewModel>? path =
            SessionTreeNodeViewModel.FindAncestorPath(Roots, match);
        if (path is not null)
        {
            foreach (SessionTreeNodeViewModel ancestor in path.Take(path.Count - 1))
            {
                ancestor.IsExpanded = true;
            }
        }

        SelectNode(match, preserveSearch: true);
        return match;
    }

    public async Task<SessionTreeNodeViewModel?> FindNextRawAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            ClearSearchMatch();
            return null;
        }

        CancellationTokenSource searchCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _rawSearchCancellation,
            searchCancellation);
        previous?.Cancel();
        previous?.Dispose();

        List<RawSearchCandidate> candidates = BuildRawSearchCandidates();
        int selectedIndex = _currentSearchNodeId is null
            ? -1
            : candidates.FindIndex(candidate =>
                string.Equals(
                    candidate.StableId,
                    _currentSearchNodeId,
                    StringComparison.Ordinal));
        HashSet<SessionRawSourceReference> visited = [];
        SearchStatus = "Searching raw sources...";
        try
        {
            for (int offset = 1; offset <= candidates.Count; offset++)
            {
                searchCancellation.Token.ThrowIfCancellationRequested();
                int index =
                    (selectedIndex + offset + candidates.Count) %
                    candidates.Count;
                RawSearchCandidate candidate = candidates[index];
                SessionSourceProvenance provenance = candidate.Provenance;
                if (provenance.RawSource is
                        SessionRawSourceReference source &&
                    !visited.Add(source))
                {
                    continue;
                }

                SessionRawSourceContent content = await _rawSourceResolver
                    .LoadAsync(
                        provenance,
                        MaximumDeepSearchBytes,
                        searchCancellation.Token)
                    .ConfigureAwait(true);
                if (!content.Content.Contains(
                        SearchQuery,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                SessionTreeNodeViewModel? node =
                    await MaterializeRawSearchCandidateAsync(
                        candidate,
                        searchCancellation.Token).ConfigureAwait(true);
                if (node is null)
                {
                    continue;
                }

                List<SessionTreeNodeViewModel>? path =
                    SessionTreeNodeViewModel.FindAncestorPath(Roots, node);
                if (path is not null)
                {
                    foreach (SessionTreeNodeViewModel ancestor in path.Take(
                                 path.Count - 1))
                    {
                        ancestor.IsExpanded = true;
                    }
                }

                _currentSearchNodeId = node.StableId;
                SearchStatus = content.IsTruncated
                    ? "Raw match found in bounded source."
                    : "Raw match found.";
                SelectNode(node, preserveSearch: true);
                return node;
            }

            SearchStatus = "No raw-source match.";
            return null;
        }
        catch (OperationCanceledException)
            when (searchCancellation.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _rawSearchCancellation,
                null,
                searchCancellation);
            searchCancellation.Dispose();
        }
    }

    private List<RawSearchCandidate> BuildRawSearchCandidates()
    {
        List<RawSearchCandidate> candidates = [];
        IReadOnlyList<SessionCatalogEntry> sessions =
            _lastRenderedUpdate is null
                ? []
                : ApplyProviderFilter(_lastRenderedUpdate.Sessions);
        foreach (SessionCatalogEntry session in sessions)
        {
            string sessionId = $"session:{session.CatalogSessionId}";
            foreach (SessionSourceProvenance source in session.Sources)
            {
                candidates.Add(new RawSearchCandidate(
                    sessionId,
                    session.CatalogSessionId,
                    TurnId: null,
                    source));
            }

            foreach (SessionEventRecord item in session.SessionEvents)
            {
                string eventId =
                    $"{sessionId}:system-prompt:{item.Id}";
                candidates.Add(new RawSearchCandidate(
                    eventId,
                    session.CatalogSessionId,
                    TurnId: null,
                    item.Provenance));
            }

            foreach (SessionTurn turn in session.Turns)
            {
                foreach (SessionEventRecord item in turn.Events)
                {
                    string eventId =
                        $"{sessionId}:turn:{turn.Id}:event:{item.Id}";
                    candidates.Add(new RawSearchCandidate(
                        eventId,
                        session.CatalogSessionId,
                        turn.Id,
                        item.Provenance));
                    foreach (SessionSourceProvenance supplemental in
                             item.SupplementalProvenance)
                    {
                        candidates.Add(new RawSearchCandidate(
                            eventId,
                            session.CatalogSessionId,
                            turn.Id,
                            supplemental));
                    }
                }
            }
        }

        foreach (SessionPlanArtifact plan in
                 _lastRenderedUpdate?.Plans ?? [])
        {
            string stableId =
                plan.BoundCatalogSessionId is string sessionId
                    ? $"session:{sessionId}:plan:{plan.CatalogPlanId}"
                    : $"orphan-plan:{plan.CatalogPlanId}";
            foreach (SessionSourceProvenance source in plan.Sources)
            {
                candidates.Add(new RawSearchCandidate(
                    stableId,
                    plan.BoundCatalogSessionId,
                    TurnId: null,
                    source));
            }
        }

        return candidates;
    }

    private async Task<SessionTreeNodeViewModel?>
        MaterializeRawSearchCandidateAsync(
            RawSearchCandidate candidate,
            CancellationToken cancellationToken)
    {
        if (FindNode(candidate.StableId) is SessionTreeNodeViewModel existing)
        {
            return existing;
        }

        if (candidate.CatalogSessionId is not string catalogSessionId ||
            FindNode($"session:{catalogSessionId}") is not
                SessionTreeNodeViewModel sessionNode)
        {
            return FindNode(candidate.StableId);
        }

        await ExpandAndWaitAsync(
            sessionNode,
            cancellationToken).ConfigureAwait(true);
        if (candidate.TurnId is string turnId &&
            FindNode($"session:{catalogSessionId}:turn:{turnId}") is
                SessionTreeNodeViewModel turnNode)
        {
            await ExpandAndWaitAsync(
                turnNode,
                cancellationToken).ConfigureAwait(true);
            return FindNode(candidate.StableId) ?? turnNode;
        }

        return FindNode(candidate.StableId) ?? sessionNode;
    }

    private async Task ExpandAndWaitAsync(
        SessionTreeNodeViewModel node,
        CancellationToken cancellationToken)
    {
        node.IsExpanded = true;
        while (!node.AreChildrenLoaded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                cancellationToken).ConfigureAwait(true);
        }
    }

    private sealed record RawSearchCandidate(
        string StableId,
        string? CatalogSessionId,
        string? TurnId,
        SessionSourceProvenance Provenance);

    public bool CanOpenSession(SessionTreeNodeViewModel? node) =>
        node?.Session is SessionCatalogEntry session &&
        _sessionOpener.CanOpen(session);

    public async Task<SessionOpenResult> OpenSessionAsync(
        SessionTreeNodeViewModel? node,
        CancellationToken cancellationToken = default)
    {
        if (node?.Session is not SessionCatalogEntry session ||
            !_sessionOpener.CanOpen(session))
        {
            SessionOpenResult unsupported =
                new(false, "This session has no supported opener.");
            StatusText = unsupported.Error ??
                "This session has no supported opener.";
            return unsupported;
        }

        using CancellationTokenSource request =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
        SessionOpenResult result = await _sessionOpener
            .OpenAsync(session, request.Token)
            .ConfigureAwait(false);
        await InvokeOnDispatcherAsync(() =>
        {
            StatusText = result.Started
                ? $"Opened {session.Title} in {ProviderName(session)}."
                : result.Error ?? "The session could not be opened.";
        }).ConfigureAwait(false);
        return result;
    }

    public void ReportError(string message)
    {
        StatusText = message;
    }

    public bool CanAddToFavorites(SessionTreeNodeViewModel? node) =>
        node is not null &&
        (node.IsWorkspace || node.IsSession) &&
        !_favoriteIds.Contains(node.StableId);

    public bool IsFavorite(SessionTreeNodeViewModel? node) =>
        node is not null && _favoriteIds.Contains(node.StableId);

    public void AddToFavorites(SessionTreeNodeViewModel? node)
    {
        if (!CanAddToFavorites(node))
        {
            return;
        }

        _favoriteIds.Add(node!.StableId);
        RebuildFavorites();
        _persistFavorites?.Invoke([.. _favoriteIds]);
    }

    public void RemoveFromFavorites(SessionTreeNodeViewModel? node)
    {
        if (node is null || !_favoriteIds.Remove(node.StableId))
        {
            return;
        }

        RebuildFavorites();
        _persistFavorites?.Invoke([.. _favoriteIds]);
    }

    // A harness selection is also an explicit discovery action. Newly enabled
    // providers are scanned once; disabled providers are hidden and evicted
    // without touching provider storage.
    private void OnProviderFilterChanged(ProviderFilterOption option)
    {
        if (option.IsSelected)
        {
            _enabledProviders.Add(option.Provider);
        }
        else
        {
            _enabledProviders.Remove(option.Provider);
        }

        // Bump the generation so any projection already running with the old
        // selection discards itself instead of briefly flashing the now-hidden
        // harnesses into the tree before the re-filter lands.
        _filterGeneration++;
        UpdateHarnessFilterSummary();

        if (_suppressFilterRerender)
        {
            return;
        }

        _coordinator.SetEnabledProviders(
            _enabledProviders,
            evictDisabled: !Preferences.KeepDisabledProviderCache);
        TrimStoredUpdatesToEnabledProviders();
        _persistEnabledProviders?.Invoke([.. _enabledProviders]);
        EnqueueFilterRerender();
        if (option.IsSelected)
        {
            option.UpdateScanStatus("Scanning...");
            _ = RefreshHarnessSelectionAsync(option.Provider);
        }
        else
        {
            option.UpdateScanStatus("Disabled");
        }
    }

    private async Task RefreshHarnessSelectionAsync(HookProvider provider)
    {
        await RefreshAsync(
            new SessionRefreshRequest(
                SessionRefreshTrigger.HarnessSelection,
                new HashSet<HookProvider> { provider }),
            _lifetime.Token).ConfigureAwait(false);
    }

    private void TrimStoredUpdatesToEnabledProviders()
    {
        if (_lastRenderedUpdate is null)
        {
            return;
        }

        IReadOnlyList<SessionCatalogEntry> sessions =
            ApplyProviderFilter(_lastRenderedUpdate.Sessions);
        IReadOnlyList<SessionPlanArtifact> plans =
            FilterPlans(_lastRenderedUpdate.Plans, sessions);
        SessionCatalogUpdatedEventArgs trimmed = _lastRenderedUpdate with
        {
            Sessions = sessions,
            Plans = plans
        };
        _lastRenderedUpdate = trimmed;
        _lastAppliedUpdate = trimmed;
    }

    // Queues a harness re-filter on the same single-flight loop that projects
    // discovery updates. Serializing them prevents an in-flight scan projection
    // (captured before the toggle) from reconciling stale, unfiltered sessions
    // back into the tree after the filter has already been applied.
    private void EnqueueFilterRerender()
    {
        if (_isDisposed || _lastRenderedUpdate is null)
        {
            return;
        }

        _filterRerenderPending = true;
        if (_isProcessingUpdate)
        {
            return;
        }

        _ = ProcessPendingUpdatesAsync();
    }

    private void UpdateHarnessFilterSummary()
    {
        int total = ProviderFilters.Count;
        int selected = ProviderFilters.Count(static filter => filter.IsSelected);
        HarnessFilterSummary = selected switch
        {
            0 => "No harnesses",
            _ when selected == total => "All harnesses",
            _ => string.Join(
                ", ",
                ProviderFilters
                    .Where(static filter => filter.IsSelected)
                    .Select(static filter => filter.DisplayName))
        };
    }

    // Keeps only sessions whose harness is enabled. Harnesses that are not part
    // of the filter (for example Unknown) are always kept so unexpected sessions
    // are never silently hidden.
    private IReadOnlyList<SessionCatalogEntry> ApplyProviderFilter(
        IReadOnlyList<SessionCatalogEntry> sessions) =>
        [.. sessions.Where(session =>
            !IsFilterableProvider(session.Provider) ||
            _enabledProviders.Contains(session.Provider))];

    // Applies the harness filter to plans and demotes a bound plan to an orphan
    // when its session is filtered out, so a hidden session never silently hides
    // its plan too.
    private IReadOnlyList<SessionPlanArtifact> FilterPlans(
        IReadOnlyList<SessionPlanArtifact> plans,
        IReadOnlyList<SessionCatalogEntry> visibleSessions)
    {
        if (plans.Count == 0)
        {
            return [];
        }

        HashSet<string> visibleIds = visibleSessions
            .Select(static session => session.CatalogSessionId)
            .ToHashSet(StringComparer.Ordinal);

        return
        [
            .. plans
                .Where(plan =>
                    !IsFilterableProvider(plan.Provider) ||
                    _enabledProviders.Contains(plan.Provider))
                .Select(plan =>
                    plan.BoundCatalogSessionId is string sessionId &&
                    !visibleIds.Contains(sessionId)
                        ? plan with { BoundCatalogSessionId = null }
                        : plan)
        ];
    }

    private static bool IsFilterableProvider(HookProvider provider) =>
        FilterableProviders.Any(entry => entry.Provider == provider);

    // Re-projects the most recently rendered sessions through the current filter
    // without waiting for a new discovery pass, so toggling a harness updates
    // the tree immediately. Only ever runs from the single-flight loop.
    private async Task RerenderForFilterAsync()
    {
        if (_isDisposed || _lastRenderedUpdate is null)
        {
            return;
        }

        SessionCatalogUpdatedEventArgs update = _lastRenderedUpdate;
        await RenderProjectionAsync(
            update.Sessions,
            update.Plans,
            markFinalRendered: false,
            rebuildFavorites: true).ConfigureAwait(true);
    }

    // Favorites are stored by stable id and re-resolved against the freshly
    // projected tree after every update, so pinned workspaces and sessions
    // survive refreshes and keep the same content as the main tree.
    private void RebuildFavorites()
    {
        List<SessionTreeNodeViewModel> desired = [];
        if (_favoriteIds.Count > 0)
        {
            foreach (string id in _favoriteIds)
            {
                if (_nodesById.TryGetValue(
                        id,
                        out SessionTreeNodeViewModel? node))
                {
                    desired.Add(node);
                }
            }
        }

        // The favorites tree shows the very same node instances as the main
        // tree (already updated in place by the main reconcile), so only the
        // top-level membership and order need syncing here.
        for (int index = FavoriteRoots.Count - 1; index >= 0; index--)
        {
            if (!desired.Contains(FavoriteRoots[index]))
            {
                FavoriteRoots.RemoveAt(index);
            }
        }

        for (int index = 0; index < desired.Count; index++)
        {
            SessionTreeNodeViewModel node = desired[index];
            int currentIndex = FavoriteRoots.IndexOf(node);
            if (currentIndex < 0)
            {
                FavoriteRoots.Insert(Math.Min(index, FavoriteRoots.Count), node);
            }
            else if (currentIndex != index)
            {
                FavoriteRoots.Move(currentIndex, index);
            }
        }

        OnPropertyChanged(nameof(HasFavorites));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        foreach (SessionTreeNodeViewModel node in _observedNodes)
        {
            node.PropertyChanged -= TreeNode_PropertyChanged;
        }

        _observedNodes.Clear();
        _coordinator.Updated -= Coordinator_Updated;
        _lifetime.Cancel();
        CancellationTokenSource? rawSearch = Interlocked.Exchange(
            ref _rawSearchCancellation,
            null);
        rawSearch?.Cancel();
        rawSearch?.Dispose();
        Inspector.Dispose();
        _lifetime.Dispose();
    }

    private async void Coordinator_Updated(
        object? sender,
        SessionCatalogUpdatedEventArgs update)
    {
        try
        {
            await InvokeOnDispatcherAsync(() => EnqueueUpdate(update))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Coalesces incoming updates on the UI thread and runs a single projection
    // at a time. A burst of progress events (for example the Cursor scan
    // streaming hundreds of sessions) therefore never spawns hundreds of
    // concurrent tree projections; only the newest pending update is projected
    // once the current one finishes.
    private void EnqueueUpdate(SessionCatalogUpdatedEventArgs update)
    {
        if (_isDisposed)
        {
            SignalUpdateProcessed(update);
            return;
        }

        if (_pendingUpdate is not null &&
            !ReferenceEquals(_pendingUpdate, update))
        {
            // The superseded update will never be projected; release anyone
            // waiting on it so initialization gating cannot hang.
            SignalUpdateProcessed(_pendingUpdate);
        }

        _pendingUpdate = update;
        if (_isProcessingUpdate)
        {
            return;
        }

        _ = ProcessPendingUpdatesAsync();
    }

    private async Task ProcessPendingUpdatesAsync()
    {
        _isProcessingUpdate = true;
        try
        {
            // Discovery updates and harness-filter re-renders share this loop so
            // exactly one projection runs at a time. Pending updates take
            // priority (they already project through the current filter); a
            // filter re-render only runs once the queue is otherwise idle.
            while (true)
            {
                if (_pendingUpdate is SessionCatalogUpdatedEventArgs update)
                {
                    _pendingUpdate = null;
                    try
                    {
                        await ApplyUpdateAsync(update).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception exception)
                    {
                        StatusText =
                            $"Could not update the session tree: {exception.Message}";
                    }
                    finally
                    {
                        SignalUpdateProcessed(update);
                    }

                    continue;
                }

                if (_filterRerenderPending)
                {
                    _filterRerenderPending = false;
                    try
                    {
                        await RerenderForFilterAsync().ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception exception)
                    {
                        StatusText =
                            $"Could not update the session tree: {exception.Message}";
                    }

                    continue;
                }

                break;
            }
        }
        finally
        {
            _isProcessingUpdate = false;
        }
    }

    private void SignalUpdateProcessed(SessionCatalogUpdatedEventArgs update) =>
        _updateCompletions
            .GetValue(
                update,
                static _ => new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(true);

    private Task WaitForUpdateAppliedAsync(
        SessionCatalogUpdatedEventArgs update) =>
        _updateCompletions.GetValue(
            update,
            static _ => new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    // Always invoked on the UI thread through the single-flight loop above.
    private async Task ApplyUpdateAsync(SessionCatalogUpdatedEventArgs update)
    {
        if (_isDisposed || ReferenceEquals(_lastAppliedUpdate, update))
        {
            return;
        }

        // Partial (non-final) batches are only rendered while first populating
        // an empty tree, so the initial scan streams sessions in as providers
        // finish. Once a complete result has been shown, every later refresh
        // waits for its final batch and swaps atomically. This prevents a new
        // refresh cycle from momentarily dropping already-visible sessions
        // (for example the slow Cursor SQLite scan) before it completes.
        if (!update.IsFinal && _hasRenderedFinal)
        {
            return;
        }

        _lastAppliedUpdate = update;
        if (update.IsFinal &&
            _hasRenderedFinal &&
            !update.HasCatalogChanges)
        {
            UpdateStatus(update);
            return;
        }

        _lastRenderedUpdate = update;
        await RenderProjectionAsync(
            update.Sessions,
            update.Plans,
            markFinalRendered: update.IsFinal,
            rebuildFavorites: update.IsFinal).ConfigureAwait(true);

        if (_isDisposed)
        {
            return;
        }

        UpdateStatus(update);
    }

    // Projects the supplied sessions through the active harness filter and
    // reconciles the result into the tree in place, preserving expansion and
    // selection. Shared by discovery updates and by live harness-filter toggles.
    private async Task RenderProjectionAsync(
        IReadOnlyList<SessionCatalogEntry> sessions,
        IReadOnlyList<SessionPlanArtifact> plans,
        bool markFinalRendered,
        bool rebuildFavorites)
    {
        // Snapshot the current tree state on the UI thread before projecting.
        HashSet<string> expandedNodeIds = SessionTreeNodeViewModel
            .EnumerateDepthFirst(Roots)
            .Where(static node => node.IsExpanded)
            .Select(static node => node.StableId)
            .ToHashSet(StringComparer.Ordinal);
        string? selectedNodeId = SelectedNode?.StableId;
        bool isInitialProjection = Roots.Count == 0;

        // Capture the filter generation for this projection. If the user changes
        // the harness selection while the (potentially slow) projection runs off
        // the UI thread, this projection is stale and must not be committed.
        int filterGeneration = _filterGeneration;

        // Only the harnesses selected in the filter combobox are shown.
        IReadOnlyList<SessionCatalogEntry> visibleSessions =
            ApplyProviderFilter(sessions);
        IReadOnlyList<SessionPlanArtifact> visiblePlans =
            FilterPlans(plans, visibleSessions);

        // Build the view-model tree off the UI thread. Node view models are
        // plain observable objects, so constructing them on a worker thread is
        // safe and keeps timed or notification-driven refreshes from freezing
        // the interface while large catalogs are projected.
        IReadOnlyList<SessionTreeNodeViewModel> projected = await Task
            .Run(() => _projector.Project(
                visibleSessions,
                expandedNodeIds,
                expandWorkspaceRoots:
                    isInitialProjection &&
                    Preferences.ExpandWorkspacesOnStartup,
                plans: visiblePlans,
                lazy: true))
            .ConfigureAwait(true);

        if (_isDisposed)
        {
            return;
        }

        if (markFinalRendered)
        {
            _hasRenderedFinal = true;
        }

        // A harness toggle happened while this projection was being built, so it
        // reflects the previous selection. Drop it: a re-filter is already queued
        // on the single-flight loop and will render the current selection, so the
        // hidden harnesses never flash into the tree.
        if (filterGeneration != _filterGeneration)
        {
            return;
        }

        // Reconcile in place so existing workspace/session/turn nodes keep
        // their identity (and their expansion, selection, and scroll position)
        // while they are rehydrated with newly discovered detail.
        SessionTreeNodeViewModel.Reconcile(Roots, projected);
        RebuildNodeIndexAndSubscriptions();

        SessionTreeNodeViewModel? replacement = selectedNodeId is null
            ? null
            : FindNode(selectedNodeId);
        SelectNode(replacement, preserveSearch: true);
        OnPropertyChanged(nameof(CanOpenSelectedSession));

        // Favorites reference the same node instances and are already updated
        // in place by reconciliation, so only their membership needs syncing;
        // do it on final batches (and on explicit add/remove) to avoid churn.
        if (rebuildFavorites)
        {
            RebuildFavorites();
        }

        if (_currentSearchNodeId is not null &&
            !_nodesById.ContainsKey(_currentSearchNodeId))
        {
            ClearSearchMatch();
        }
    }

    public SessionTreeNodeViewModel? FindNode(string stableId) =>
        _nodesById.TryGetValue(
            stableId,
            out SessionTreeNodeViewModel? node)
            ? node
            : null;

    public async Task EnsureNodeSummaryAsync(
        SessionTreeNodeViewModel? node)
    {
        if (_isDisposed ||
            node is null ||
            node.NodeSummary is not null)
        {
            return;
        }

        NodeSummary? summary = await Task.Run(() =>
            node.Session is SessionCatalogEntry session && node.IsSession
                ? _projector.BuildSummary(session)
                : node.Turn is SessionTurn turn && node.IsTurn
                    ? _projector.BuildSummary(turn)
                    : null).ConfigureAwait(true);
        if (!_isDisposed && summary is not null)
        {
            node.SetNodeSummary(summary);
        }
    }

    private void RebuildNodeIndexAndSubscriptions()
    {
        foreach (SessionTreeNodeViewModel node in _observedNodes)
        {
            node.PropertyChanged -= TreeNode_PropertyChanged;
        }

        _observedNodes.Clear();
        _nodesById.Clear();
        _nodeOrder.Clear();
        foreach (SessionTreeNodeViewModel node in
                 SessionTreeNodeViewModel.EnumerateDepthFirst(Roots))
        {
            _nodesById.TryAdd(node.StableId, node);
            if (!node.IsPlaceholder)
            {
                _nodeOrder.Add(node);
            }

            if (node.IsSession || node.IsTurn)
            {
                node.PropertyChanged += TreeNode_PropertyChanged;
                _observedNodes.Add(node);
            }
        }
    }

    private async void TreeNode_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionTreeNodeViewModel.IsExpanded) ||
            sender is not SessionTreeNodeViewModel node ||
            _isDisposed)
        {
            return;
        }

        if (!node.IsExpanded)
        {
            UnloadNodeChildren(node);
            return;
        }

        if (!node.AreChildrenLoaded)
        {
            await HydrateNodeChildrenAsync(node).ConfigureAwait(true);
        }
    }

    private async Task HydrateNodeChildrenAsync(
        SessionTreeNodeViewModel node)
    {
        if (!_hydratingNodeIds.Add(node.StableId))
        {
            return;
        }
        OnPropertyChanged(nameof(IsComputing));

        try
        {
            SessionCatalogEntry? hydratedSession = null;
            if (node.IsSession &&
                node.Session is SessionCatalogEntry session)
            {
                ISessionDetailHydrator? hydrator =
                    _detailHydrators.FirstOrDefault(item =>
                        item.CanHydrate(session));
                if (hydrator is not null)
                {
                    SessionDetailHydrationResult result =
                        await hydrator.HydrateAsync(
                            session,
                            _lifetime.Token).ConfigureAwait(true);
                    if (_isDisposed)
                    {
                        return;
                    }

                    hydratedSession = result.Session;
                    ReplaceRenderedSession(hydratedSession);
                    AddHydrationWarnings(result.Warnings);
                }
            }

            HashSet<string> expandedNodeIds = _nodeOrder
                .Where(static item => item.IsExpanded)
                .Select(static item => item.StableId)
                .ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<SessionPlanArtifact> plans =
                _lastRenderedUpdate?.Plans ?? [];
            (
                IReadOnlyList<SessionTreeNodeViewModel> Children,
                NodeSummary? Summary,
                SessionTreeNodeViewModel? Replacement) hydration =
                await Task.Run(() =>
            {
                if (node.IsSession &&
                    (hydratedSession ?? node.Session) is
                        SessionCatalogEntry effectiveSession)
                {
                    SessionTreeNodeViewModel replacement =
                        _projector.ProjectSession(
                            effectiveSession,
                            expandedNodeIds,
                            plans);
                    return (
                        (IReadOnlyList<SessionTreeNodeViewModel>)
                            replacement.Children.ToArray(),
                        replacement.NodeSummary,
                        replacement);
                }

                if (node.IsTurn &&
                    node.Session is SessionCatalogEntry owningSession &&
                    node.Turn is SessionTurn turn)
                {
                    return (
                        _projector.ProjectTurnChildren(
                            owningSession,
                            turn,
                            expandedNodeIds,
                            plans),
                        (NodeSummary?)_projector.BuildSummary(turn),
                        (SessionTreeNodeViewModel?)null);
                }

                return (
                    (IReadOnlyList<SessionTreeNodeViewModel>)[],
                    null,
                    (SessionTreeNodeViewModel?)null);
            }).ConfigureAwait(true);

            if (_isDisposed || !node.IsExpanded)
            {
                return;
            }

            if (hydration.Replacement is not null)
            {
                node.UpdateFrom(hydration.Replacement);
            }

            SessionTreeNodeViewModel.Reconcile(
                node.Children,
                hydration.Children);
            node.SetChildrenLoaded(true);
            node.SetNodeSummary(hydration.Summary);
            RebuildNodeIndexAndSubscriptions();
        }
        catch (Exception exception)
        {
            StatusText =
                $"Could not load session details: {exception.Message}";
            node.Children.Clear();
            node.Children.Add(new SessionTreeNodeViewModel(
                $"{node.StableId}:load-error",
                "Details could not be loaded",
                SessionTreeNodeKind.Placeholder,
                node.Provider));
            node.SetChildrenLoaded(true);
            RebuildNodeIndexAndSubscriptions();
        }
        finally
        {
            _hydratingNodeIds.Remove(node.StableId);
            OnPropertyChanged(nameof(IsComputing));
        }
    }

    private void ReplaceRenderedSession(SessionCatalogEntry session)
    {
        if (_lastRenderedUpdate is null)
        {
            return;
        }

        _lastRenderedUpdate = _lastRenderedUpdate with
        {
            Sessions = _lastRenderedUpdate.Sessions
                .Select(item => string.Equals(
                    item.CatalogSessionId,
                    session.CatalogSessionId,
                    StringComparison.Ordinal)
                        ? session
                        : item)
                .ToArray()
        };
    }

    private void AddHydrationWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        WarningText = string.Join(
            Environment.NewLine,
            WarningText
                .Split(
                    Environment.NewLine,
                    StringSplitOptions.RemoveEmptyEntries)
                .Concat(warnings)
                .Distinct(StringComparer.Ordinal)
                .Take(20));
    }

    private void UnloadNodeChildren(SessionTreeNodeViewModel node)
    {
        if (!node.AreChildrenLoaded ||
            !node.IsSession && !node.IsTurn)
        {
            return;
        }

        node.Children.Clear();
        node.Children.Add(new SessionTreeNodeViewModel(
            $"{node.StableId}:placeholder",
            "Expand to load",
            SessionTreeNodeKind.Placeholder,
            node.Provider));
        node.SetChildrenLoaded(false);
        node.SetNodeSummary(null);
        RebuildNodeIndexAndSubscriptions();
    }

    private void UpdateStatus(SessionCatalogUpdatedEventArgs update)
    {
        int openCount = update.Sessions.Count(
            static session =>
                session.LifecycleState ==
                SessionLifecycleState.Open);
        string completeness = update.IsFinal
            ? (update.IsComplete ? string.Empty : " Some sources were incomplete.")
            : " Loading...";
        int planCount = update.Plans.Count;
        int orphanCount = update.Plans.Count(
            static plan => plan.BoundCatalogSessionId is null);
        string planSummary = planCount == 0
            ? string.Empty
            : $" {planCount} plan(s), {orphanCount} orphan.";
        StatusText =
            $"Found {update.Sessions.Count} session(s), {openCount} open, " +
            $"in {FormatDuration(update.Duration)}.{planSummary}{completeness}";
        WarningText = string.Join(
            Environment.NewLine,
            update.Warnings);
    }

    private Task InvokeOnDispatcherAsync(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(
            action,
            DispatcherPriority.DataBind,
            CancellationToken.None).Task;
    }

    private void ClearSearchMatch()
    {
        _currentSearchNodeId = null;
        SearchStatus = string.Empty;
    }

    private static int FindNearestMatchIndex(
        List<SessionTreeNodeViewModel> allNodes,
        List<SessionTreeNodeViewModel> matches,
        int selectedIndex,
        bool previous)
    {
        if (selectedIndex < 0)
        {
            return previous ? matches.Count - 1 : 0;
        }

        if (previous)
        {
            for (int index = matches.Count - 1; index >= 0; index--)
            {
                if (allNodes.IndexOf(matches[index]) <= selectedIndex)
                {
                    return index;
                }
            }

            return matches.Count - 1;
        }

        for (int index = 0; index < matches.Count; index++)
        {
            if (allNodes.IndexOf(matches[index]) >= selectedIndex)
            {
                return index;
            }
        }

        return 0;
    }

    private static string ProviderName(SessionCatalogEntry session) =>
        session.Provider switch
        {
            HookProvider.Cursor => "Cursor",
            HookProvider.ClaudeCode => "Claude Code",
            HookProvider.GitHubCopilot => "GitHub Copilot",
            _ => "the provider"
        };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds >= 1)
        {
            return $"{duration.TotalSeconds:0.##}s";
        }

        return $"{duration.TotalMilliseconds:0}ms";
    }
}
