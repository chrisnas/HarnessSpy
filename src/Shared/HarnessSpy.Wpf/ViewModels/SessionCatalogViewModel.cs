using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Wpf.ViewModels;

public sealed class SessionCatalogViewModel : ObservableObject, IDisposable
{
    private readonly SessionCatalogCoordinator _coordinator;
    private readonly ISessionOpener _sessionOpener;
    private readonly SessionTreeProjector _projector;
    private readonly Dispatcher _dispatcher;
    private readonly bool _watchEnabled;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<string> _favoriteIds = [];
    private readonly Action<IReadOnlyList<string>>? _persistFavorites;
    private readonly HashSet<HookProvider> _enabledProviders = [];
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
        bool watchEnabled,
        Dispatcher dispatcher,
        SessionTreeProjector? projector = null,
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
        _watchEnabled = watchEnabled;
        _dispatcher = dispatcher;
        _projector = projector ?? new SessionTreeProjector();
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
        private set => SetProperty(ref _isRefreshing, value);
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        if (_isInitialized || _isDisposed)
        {
            return;
        }

        _isInitialized = true;
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
        if (_watchEnabled && !_isDisposed)
        {
            _coordinator.StartWatching();
        }
    }

    public async Task RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed || IsRefreshing)
        {
            return;
        }

        using CancellationTokenSource request =
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
                .RefreshAsync(request.Token)
                .ConfigureAwait(false);
            await WaitForUpdateAppliedAsync(update).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (request.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await InvokeOnDispatcherAsync(() =>
            {
                StatusText = $"Session discovery failed: {exception.Message}";
            }).ConfigureAwait(false);
        }
        finally
        {
            if (!_dispatcher.HasShutdownStarted)
            {
                await InvokeOnDispatcherAsync(() => IsRefreshing = false)
                    .ConfigureAwait(false);
            }
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

        List<SessionTreeNodeViewModel> allNodes =
            SessionTreeNodeViewModel.EnumerateDepthFirst(Roots).ToList();
        List<SessionTreeNodeViewModel> matches = allNodes
            .Where(node => SearchHookNamesOnly
                ? node.MatchesHookName(SearchQuery)
                : node.Matches(SearchQuery))
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

    // Raised when a harness checkbox is toggled: update the enabled set, refresh
    // the summary, persist the selection, and re-filter the tree in place.
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

        _persistEnabledProviders?.Invoke([.. _enabledProviders]);
        EnqueueFilterRerender();
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
            Dictionary<string, SessionTreeNodeViewModel> byId =
                SessionTreeNodeViewModel
                    .EnumerateDepthFirst(Roots)
                    .Where(node => _favoriteIds.Contains(node.StableId))
                    .GroupBy(static node => node.StableId, StringComparer.Ordinal)
                    .ToDictionary(
                        static group => group.Key,
                        static group => group.First(),
                        StringComparer.Ordinal);
            foreach (string id in _favoriteIds)
            {
                if (byId.TryGetValue(id, out SessionTreeNodeViewModel? node))
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
        _coordinator.Updated -= Coordinator_Updated;
        _lifetime.Cancel();
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
        _lastRenderedUpdate = update;
        if (update.IsFinal &&
            _hasRenderedFinal &&
            !update.HasCatalogChanges)
        {
            UpdateStatus(update);
            return;
        }

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
                expandWorkspaceRoots: isInitialProjection,
                plans: visiblePlans))
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

        SessionTreeNodeViewModel? replacement = selectedNodeId is null
            ? null
            : SessionTreeNodeViewModel
                .EnumerateDepthFirst(Roots)
                .FirstOrDefault(node =>
                    StringComparer.Ordinal.Equals(
                        node.StableId,
                        selectedNodeId));
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
            !SessionTreeNodeViewModel
                .EnumerateDepthFirst(Roots)
                .Any(node => StringComparer.Ordinal.Equals(
                    node.StableId,
                    _currentSearchNodeId)))
        {
            ClearSearchMatch();
        }
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
            update.Warnings.Where(IsRelevantWarning));
    }

    // Discovery-guard limits are intentionally generous in Session Viewer, so
    // their advisory "time/size limit reached" and "skipped oversized value"
    // messages are noise and are never surfaced to the user.
    private static bool IsRelevantWarning(string warning)
    {
        string text = warning ?? string.Empty;
        return !(text.Contains("time limit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("oversized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("directory limit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("file limit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains(
                "discovery warnings were suppressed",
                StringComparison.OrdinalIgnoreCase));
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
