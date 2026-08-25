using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;

namespace HarnessSpy.Wpf.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private const string EmptySelectionText = "Select a hook occurrence to inspect its payload.";
    private const string UnknownSession = "Unknown session";

    private readonly ReplayLoader _replayLoader;
    private readonly Dictionary<string, TreeNodeViewModel> _workspaceNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TreeNodeViewModel> _sessionNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TreeNodeViewModel> _generationNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TreeNodeViewModel> _sessionParents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _sessionSourceFiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadedSourceFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _derivedTurnCounters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activeDerivedTurns = new(StringComparer.Ordinal);

    // In-flight subagent nodes keyed by subagent_id.
    private readonly Dictionary<string, TreeNodeViewModel> _inFlightSubagents = new(StringComparer.Ordinal);
    // In-flight beforeShellExecution/beforeMCPExecution nodes waiting for their after,
    // keyed by "{generationKey}\0shell" or "{generationKey}\0mcp". Stack-like (last in).
    private readonly Dictionary<string, List<TreeNodeViewModel>> _inFlightInnerPre = new(StringComparer.Ordinal);
    private string _selectedPayloadText = EmptySelectionText;
    private IReadOnlyList<PayloadField> _selectedFields = [];
    private TreeNodeViewModel? _selectedNode;
    private string _searchQuery = string.Empty;
    private string _searchStatus = string.Empty;
    private string _statusText = "Waiting for hook activity.";
    private bool _isLoadingReplay;
    private NodeSearchMatch? _currentSearchMatch;

    public MainWindowViewModel()
        : this(new ReplayLoader())
    {
    }

    public MainWindowViewModel(ReplayLoader replayLoader)
    {
        _replayLoader = replayLoader;
    }

    public ObservableCollection<TreeNodeViewModel> Roots { get; } = [];

    public string SelectedPayloadText
    {
        get => _selectedPayloadText;
        private set => SetProperty(ref _selectedPayloadText, value);
    }

    // Per-hook name/value rows for the selected node, shown above the raw
    // payload. Empty for nodes without an observation (workspace/session) or
    // hooks that have no highlighted fields.
    public IReadOnlyList<PayloadField> SelectedFields
    {
        get => _selectedFields;
        private set
        {
            if (SetProperty(ref _selectedFields, value))
            {
                OnPropertyChanged(nameof(HasSelectedFields));
            }
        }
    }

    public bool HasSelectedFields => _selectedFields.Count > 0;

    public TreeNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                OnPropertyChanged(nameof(HasSelectedDashboard));
            }
        }
    }

    public bool HasSelectedDashboard =>
        SelectedNode is not null &&
        SelectedNode.HasDashboardHover;

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

    public bool IsLoadingReplay
    {
        get => _isLoadingReplay;
        private set
        {
            if (SetProperty(ref _isLoadingReplay, value))
            {
                OnPropertyChanged(nameof(CanBrowseReplayFolders));
            }
        }
    }

    public bool CanBrowseReplayFolders => !IsLoadingReplay;

    public async Task LoadFolderAsync(string folder, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            StatusText = "No replay folder selected.";
            return;
        }

        string folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderName))
        {
            folderName = folder;
        }

        IsLoadingReplay = true;
        StatusText = $"Loading replay events from {folderName}...";

        try
        {
            IReadOnlyList<HookObservation> observations = await _replayLoader
                .LoadAsync(folder, cancellationToken)
                .ConfigureAwait(true);

            int added = 0;
            int skipped = 0;
            foreach (HookObservation observation in observations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryMarkSourceFileLoaded(observation))
                {
                    skipped++;
                    continue;
                }

                AddObservation(observation);
                added++;
            }

            StatusText = skipped == 0
                ? $"Loaded {added} replay event(s) from {folderName}."
                : $"Loaded {added} replay event(s) from {folderName}; skipped {skipped} duplicate file(s).";
        }
        finally
        {
            IsLoadingReplay = false;
        }
    }

    public void AddObservation(HookObservation observation)
    {
        // workspaceOpen is workspace-scoped and never belongs to a session.
        if (observation.IsWorkspaceOpen)
        {
            TreeNodeViewModel workspaceOnlyNode = GetOrCreateWorkspaceNode(observation.Workspace);
            workspaceOnlyNode.Children.Add(CreateObservationNode(observation));
            workspaceOnlyNode.IsExpanded = true;
            return;
        }

        TreeNodeViewModel sessionNode = ResolveSessionNode(observation);
        sessionNode.IsExpanded = true;
        if (!observation.IsTabHook)
        {
            UpdateSessionStatus(sessionNode, observation);
        }

        TrackSessionSourceFile(sessionNode, observation);

        // Session-scoped events sit directly under the session: those without a
        // generation_id, plus sessionStart/sessionEnd, which bracket the whole
        // conversation and belong beside each other rather than inside a turn.
        // Tab completions are an editor surface, so they stay at session level
        // even when Cursor stamps them with a generation_id.
        // Turn-scoped events group beneath a generation node so each prompt
        // reads as one collapsible block.
        string? effectiveGenerationId = ResolveGenerationId(observation);
        if (effectiveGenerationId is null || observation.IsSessionLifecycle || observation.IsTabHook)
        {
            sessionNode.Children.Add(CreateObservationNode(observation));
            sessionNode.RecomputeSession();
            return;
        }

        TreeNodeViewModel generationNode = GetOrCreateGenerationNode(
            observation,
            sessionNode,
            effectiveGenerationId);
        TreeNodeViewModel observationNode = CreateObservationNode(observation);

        // Try to nest this observation under an in-flight parent.
        if (TryNestUnderParent(observation, observationNode, generationNode))
        {
            generationNode.IsExpanded = true;
            generationNode.RecomputeGeneration();
            sessionNode.RecomputeSession();
            return;
        }

        // Not nested: add as a direct child of the generation.
        generationNode.Children.Add(observationNode);
        generationNode.IsExpanded = true;

        // Track new in-flight pre nodes for later pairing.
        TrackInFlightNode(observation, observationNode, generationNode);

        generationNode.RecomputeGeneration();
        sessionNode.RecomputeSession();

        if (observation.IsStop &&
            observation.GenerationId is null)
        {
            _activeDerivedTurns.Remove(observation.ProviderScopedSessionId);
        }
    }

    private string? ResolveGenerationId(HookObservation observation)
    {
        if (observation.GenerationId is not null)
        {
            return observation.GenerationId;
        }

        if (observation.Provider != HookProvider.GitHubCopilot ||
            observation.IsSessionLifecycle)
        {
            return null;
        }

        string sessionKey = observation.ProviderScopedSessionId;
        if (observation.HookEventName == "beforeSubmitPrompt")
        {
            int next = _derivedTurnCounters.GetValueOrDefault(sessionKey) + 1;
            _derivedTurnCounters[sessionKey] = next;
            string derived = $"derived-{next}";
            _activeDerivedTurns[sessionKey] = derived;
            return derived;
        }

        return _activeDerivedTurns.GetValueOrDefault(sessionKey);
    }

    // Attempts to nest the observation under an in-flight parent (matching
    // preToolUse, subagentStart, or inner pre). Returns true if nested.
    private bool TryNestUnderParent(
        HookObservation observation,
        TreeNodeViewModel observationNode,
        TreeNodeViewModel generationNode)
    {
        switch (observation.HookEventName)
        {
            case "postToolUse":
            {
                string? toolUseId = observation.ToolUseId;
                TreeNodeViewModel? preNode = toolUseId is null
                    ? null
                    : FindInFlightPreByToolCall(generationNode, observation);
                if (preNode is not null)
                {
                    preNode.Children.Add(observationNode);
                    preNode.IsExpanded = false;
                    UpdatePreNodeSummary(preNode, observation);
                    TryFormParallelWave(generationNode, preNode);
                    return true;
                }

                return false;
            }

            case "postToolUseFailure":
            {
                string? toolUseId = observation.ToolUseId;
                TreeNodeViewModel? preNode = toolUseId is null
                    ? null
                    : FindInFlightPreByToolCall(generationNode, observation);
                if (preNode is not null)
                {
                    preNode.Children.Add(observationNode);
                    preNode.IsExpanded = true;
                    UpdatePreNodeSummary(preNode, observation);
                    TryFormParallelWave(generationNode, preNode);
                    return true;
                }

                return false;
            }

            case "subagentStop":
            {
                string? subagentId = observation.SubagentId;
                if (subagentId is not null &&
                    _inFlightSubagents.TryGetValue(subagentId, out TreeNodeViewModel? startNode))
                {
                    startNode.Children.Add(observationNode);
                    startNode.IsExpanded = true;
                    UpdatePreNodeSummary(startNode, observation);
                    _inFlightSubagents.Remove(subagentId);
                    return true;
                }

                return false;
            }

            case "beforeShellExecution":
            {
                TreeNodeViewModel? parent = FindInFlightPreByToolName(
                    generationNode, "Shell", observation.HookEventName);
                if (parent is not null)
                {
                    parent.Children.Add(observationNode);
                    TrackInnerPre(generationNode, observation.HookEventName, observationNode);
                    return true;
                }

                return false;
            }

            case "beforeMCPExecution":
            {
                string? mcpToolName = observation.ToolName;
                if (mcpToolName is null)
                {
                    return false;
                }

                TreeNodeViewModel? parent = FindInFlightPreByToolName(
                    generationNode, $"MCP:{mcpToolName}", observation.HookEventName);
                if (parent is not null)
                {
                    parent.Children.Add(observationNode);
                    TrackInnerPre(generationNode, observation.HookEventName, observationNode);
                    return true;
                }

                return false;
            }

            case "afterShellExecution":
            {
                TreeNodeViewModel? innerPre = PopInnerPre(generationNode, "beforeShellExecution");
                if (innerPre is not null)
                {
                    innerPre.Children.Add(observationNode);
                    innerPre.IsExpanded = true;
                    return true;
                }

                TreeNodeViewModel? fallbackParent = FindInFlightPreByToolName(
                    generationNode, "Shell", "beforeShellExecution");
                if (fallbackParent is not null)
                {
                    fallbackParent.Children.Add(observationNode);
                    return true;
                }

                return false;
            }

            case "afterMCPExecution":
            {
                string? mcpToolName = observation.ToolName;
                if (mcpToolName is null)
                {
                    return false;
                }

                TreeNodeViewModel? innerPre = PopInnerPre(generationNode, "beforeMCPExecution");
                if (innerPre is not null)
                {
                    innerPre.Children.Add(observationNode);
                    innerPre.IsExpanded = true;
                    return true;
                }

                TreeNodeViewModel? fallbackParent = FindInFlightPreByToolName(
                    generationNode, $"MCP:{mcpToolName}", "beforeMCPExecution");
                if (fallbackParent is not null)
                {
                    fallbackParent.Children.Add(observationNode);
                    return true;
                }

                return false;
            }

            case "beforeReadFile":
            {
                // Nest under the in-flight Read preToolUse if one exists.
                TreeNodeViewModel? parent = FindInFlightPreByToolName(generationNode, "Read", null);
                if (parent is not null)
                {
                    parent.Children.Add(observationNode);
                    return true;
                }

                return false;
            }

            case "afterFileEdit":
            {
                // Nest under an in-flight Write/StrReplace/EditNotebook preToolUse.
                TreeNodeViewModel? parent =
                    FindInFlightPreByToolName(generationNode, "Write", null) ??
                    FindInFlightPreByToolName(generationNode, "StrReplace", null) ??
                    FindInFlightPreByToolName(generationNode, "EditNotebook", null);
                if (parent is not null)
                {
                    parent.Children.Add(observationNode);
                    return true;
                }

                return false;
            }

            default:
                return false;
        }
    }

    // Registers a newly created node so later matching events can nest under it.
    private void TrackInFlightNode(
        HookObservation observation,
        TreeNodeViewModel node,
        TreeNodeViewModel _)
    {
        switch (observation.HookEventName)
        {
            case "subagentStart":
            {
                string? subagentId = observation.SubagentId;
                if (subagentId is not null)
                {
                    _inFlightSubagents[subagentId] = node;
                }

                break;
            }
        }
    }

    // Cursor can reuse one tool_use_id for several calls in the same tool
    // batch. Match within the generation by id, name, and (when available)
    // tool_input, choosing the oldest unmatched pre for indistinguishable calls.
    private static TreeNodeViewModel? FindInFlightPreByToolCall(
        TreeNodeViewModel generationNode,
        HookObservation postObservation)
    {
        foreach (TreeNodeViewModel child in generationNode.Children)
        {
            if (child.Kind == TreeNodeKind.ParallelWave)
            {
                TreeNodeViewModel? nested = FindInFlightPreByToolCall(child, postObservation);
                if (nested is not null)
                {
                    return nested;
                }

                continue;
            }

            HookObservation? preObservation = child.Observation;
            if (preObservation?.HookEventName != "preToolUse" ||
                IsCompletedToolCall(child) ||
                !StringComparer.Ordinal.Equals(preObservation.ToolUseId, postObservation.ToolUseId) ||
                !StringComparer.Ordinal.Equals(preObservation.ToolName, postObservation.ToolName) ||
                !ToolInputsMatch(preObservation, postObservation))
            {
                continue;
            }

            return child;
        }

        return null;
    }

    private static bool ToolInputsMatch(HookObservation preObservation, HookObservation postObservation)
    {
        bool hasPreInput = preObservation.Payload.TryGetProperty("tool_input", out JsonElement preInput);
        bool hasPostInput = postObservation.Payload.TryGetProperty("tool_input", out JsonElement postInput);

        // Older providers and some synthetic observations omit tool_input.
        return !hasPreInput || !hasPostInput || JsonElement.DeepEquals(preInput, postInput);
    }

    // Finds the most recent in-flight preToolUse node under generationNode whose
    // tool_name matches the given name and that hasn't already received its
    // inner pre of the specified kind.
    private TreeNodeViewModel? FindInFlightPreByToolName(
        TreeNodeViewModel generationNode,
        string toolName,
        string? innerPreKind)
    {
        // Walk generation children in reverse (most recent first).
        for (int i = generationNode.Children.Count - 1; i >= 0; i--)
        {
            TreeNodeViewModel child = generationNode.Children[i];
            if (child.Observation?.HookEventName != "preToolUse")
            {
                continue;
            }

            if (!StringComparer.Ordinal.Equals(child.Observation.ToolName, toolName))
            {
                continue;
            }

            // Check this pre is still in-flight (has no postToolUse child yet).
            if (IsCompletedToolCall(child))
            {
                continue;
            }

            // If looking for a specific inner pre, skip if one already exists.
            if (innerPreKind is not null &&
                child.Children.Any(c =>
                    StringComparer.Ordinal.Equals(c.Observation?.HookEventName, innerPreKind)))
            {
                continue;
            }

            return child;
        }

        return null;
    }

    private void TrackInnerPre(
        TreeNodeViewModel generationNode,
        string hookEventName,
        TreeNodeViewModel node)
    {
        string key = $"{generationNode.Key}\0{hookEventName}";
        if (!_inFlightInnerPre.TryGetValue(key, out List<TreeNodeViewModel>? list))
        {
            list = [];
            _inFlightInnerPre[key] = list;
        }

        list.Add(node);
    }

    private TreeNodeViewModel? PopInnerPre(
        TreeNodeViewModel generationNode,
        string hookEventName)
    {
        string key = $"{generationNode.Key}\0{hookEventName}";
        if (!_inFlightInnerPre.TryGetValue(key, out List<TreeNodeViewModel>? list) || list.Count == 0)
        {
            return null;
        }

        TreeNodeViewModel node = list[^1];
        list.RemoveAt(list.Count - 1);
        return node;
    }

    private static void UpdatePreNodeSummary(TreeNodeViewModel preNode, HookObservation postObservation)
    {
        if (postObservation.DurationMs is double ms)
        {
            preNode.Summary = HookObservation.FormatDuration(TimeSpan.FromMilliseconds(ms));
        }
        else if (preNode.Observation is not null)
        {
            TimeSpan elapsed = postObservation.ObservedAtUtc - preNode.Observation.ObservedAtUtc;
            if (elapsed > TimeSpan.Zero)
            {
                preNode.Summary = HookObservation.FormatDuration(elapsed);
            }
        }
    }

    // After a tool call completes, checks whether it overlaps with existing
    // parallel waves or sibling completed calls. Merges or creates wave nodes
    // as needed.
    private static void TryFormParallelWave(TreeNodeViewModel generationNode, TreeNodeViewModel completedPre)
    {
        DateTimeOffset preStart = completedPre.Observation!.ObservedAtUtc;
        DateTimeOffset preEnd = GetCallEnd(completedPre);

        // First: try to merge into an existing ParallelWave whose interval overlaps.
        foreach (TreeNodeViewModel child in generationNode.Children)
        {
            if (child.Kind != TreeNodeKind.ParallelWave)
            {
                continue;
            }

            DateTimeOffset waveStart = child.Children.Min(c => c.Observation!.ObservedAtUtc);
            DateTimeOffset waveEnd = child.Children.Max(c => GetCallEnd(c));

            if (preStart < waveEnd && preEnd > waveStart)
            {
                generationNode.Children.Remove(completedPre);
                child.Children.Add(completedPre);
                UpdateWaveNode(child);
                return;
            }
        }

        // Second: check generation-level completed siblings for overlap.
        List<TreeNodeViewModel> overlapping = [completedPre];
        foreach (TreeNodeViewModel child in generationNode.Children)
        {
            if (child == completedPre || child.Kind == TreeNodeKind.ParallelWave)
            {
                continue;
            }

            if (child.Observation?.HookEventName is not "preToolUse")
            {
                continue;
            }

            if (!IsCompletedToolCall(child))
            {
                continue;
            }

            DateTimeOffset sibStart = child.Observation!.ObservedAtUtc;
            DateTimeOffset sibEnd = GetCallEnd(child);

            if (sibStart < preEnd && sibEnd > preStart)
            {
                overlapping.Add(child);
            }
        }

        if (overlapping.Count < 2)
        {
            return;
        }

        // Sort by start time.
        overlapping.Sort((a, b) =>
            a.Observation!.ObservedAtUtc.CompareTo(b.Observation!.ObservedAtUtc));

        int insertIndex = generationNode.Children.IndexOf(overlapping[0]);

        string waveKey = $"wave\0{generationNode.Key}\0{overlapping[0].Observation!.ObservedAtUtc.Ticks}";
        var waveNode = new TreeNodeViewModel(waveKey, string.Empty, TreeNodeKind.ParallelWave)
        {
            IsExpanded = true
        };

        foreach (TreeNodeViewModel member in overlapping)
        {
            generationNode.Children.Remove(member);
            waveNode.Children.Add(member);
        }

        generationNode.Children.Insert(
            Math.Min(insertIndex, generationNode.Children.Count), waveNode);

        UpdateWaveNode(waveNode);
    }

    private static void UpdateWaveNode(TreeNodeViewModel waveNode)
    {
        int count = waveNode.Children.Count;
        DateTimeOffset waveStart = waveNode.Children.Min(c => c.Observation!.ObservedAtUtc);
        DateTimeOffset waveEnd = waveNode.Children.Max(c => GetCallEnd(c));
        TimeSpan waveDuration = waveEnd - waveStart;

        waveNode.Header = $"\u2225 Parallel \u00b7 {count} calls";
        waveNode.Summary = waveDuration > TimeSpan.Zero
            ? HookObservation.FormatDuration(waveDuration)
            : string.Empty;
    }

    private static bool IsCompletedToolCall(TreeNodeViewModel node)
    {
        return node.Children.Any(c =>
            c.Observation?.HookEventName is "postToolUse" or "postToolUseFailure");
    }

    private static DateTimeOffset GetCallEnd(TreeNodeViewModel preNode)
    {
        HookObservation? postObs = preNode.Children
            .Select(c => c.Observation)
            .FirstOrDefault(o => o?.HookEventName is "postToolUse" or "postToolUseFailure");

        if (postObs is not null)
        {
            return postObs.ObservedAtUtc;
        }

        if (preNode.Observation?.DurationMs is double ms)
        {
            return preNode.Observation.ObservedAtUtc.AddMilliseconds(ms);
        }

        return preNode.Observation?.ObservedAtUtc ?? DateTimeOffset.MinValue;
    }

    public bool CanDeleteSessionFiles(TreeNodeViewModel? node)
    {
        return node?.Kind == TreeNodeKind.Session &&
            _sessionSourceFiles.TryGetValue(node.Key, out HashSet<string>? files) &&
            files.Count > 0;
    }

    public bool TryDeleteSessionFiles(TreeNodeViewModel sessionNode, out int deletedCount, out string? error)
    {
        deletedCount = 0;
        error = null;

        if (!CanDeleteSessionFiles(sessionNode))
        {
            error = "The selected session has no replay files to delete.";
            return false;
        }

        string[] files = _sessionSourceFiles[sessionNode.Key].ToArray();
        List<string> failedFiles = [];
        foreach (string file in files)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    deletedCount++;
                }
            }
            catch (Exception exception)
            {
                failedFiles.Add($"{file}: {exception.Message}");
            }
        }

        if (failedFiles.Count > 0)
        {
            error = string.Join(Environment.NewLine, failedFiles);
            StatusText = $"Could not delete all replay files for {sessionNode.Header}.";
            return false;
        }

        foreach (string file in files)
        {
            _loadedSourceFiles.Remove(file);
        }

        RemoveSessionNode(sessionNode);
        StatusText = $"Deleted {deletedCount} replay file(s) for {sessionNode.Header}.";
        return true;
    }

    // A session sticks to wherever it was first created: once its id is known
    // we always reuse that node even if a later event reports a different (or
    // empty) workspace root.
    private TreeNodeViewModel ResolveSessionNode(HookObservation observation)
    {
        string scopedSessionId = observation.ProviderScopedSessionId;
        if (observation.SessionId is not null &&
            _sessionNodes.TryGetValue(scopedSessionId, out TreeNodeViewModel? existingSessionNode))
        {
            return existingSessionNode;
        }

        TreeNodeViewModel workspaceNode = GetOrCreateWorkspaceNode(observation.Workspace);
        workspaceNode.IsExpanded = true;

        string sessionId = observation.SessionId ?? UnknownSession;
        return GetOrCreateSessionNode(
            scopedSessionId,
            sessionId,
            workspaceNode,
            observation.SessionId is null);
    }

    private TreeNodeViewModel GetOrCreateGenerationNode(HookObservation observation, TreeNodeViewModel sessionNode)
        => GetOrCreateGenerationNode(
            observation,
            sessionNode,
            observation.GenerationId ?? "unknown-generation");

    private TreeNodeViewModel GetOrCreateGenerationNode(
        HookObservation observation,
        TreeNodeViewModel sessionNode,
        string generationId)
    {
        // generation_id is only unique within a conversation, so scope the key
        // to the owning session node.
        string generationKey = $"{sessionNode.Key}\0{generationId}";

        if (_generationNodes.TryGetValue(generationKey, out TreeNodeViewModel? node))
        {
            return node;
        }

        int turnNumber = sessionNode.Children.Count(child => child.Kind == TreeNodeKind.Generation) + 1;

        node = new TreeNodeViewModel(generationKey, $"Turn {turnNumber}", TreeNodeKind.Generation)
        {
            TurnNumber = turnNumber
        };

        _generationNodes.Add(generationKey, node);
        sessionNode.Children.Add(node);
        return node;
    }

    // Track the live state of a session from its hook stream: a fresh "stop"
    // marks it idle, "sessionEnd" retires it, and any other activity means the
    // agent is running again.
    private static void UpdateSessionStatus(TreeNodeViewModel sessionNode, HookObservation observation)
    {
        sessionNode.Status = observation.HookEventName switch
        {
            "sessionEnd" => SessionStatus.Ended,
            "stop" => SessionStatus.Stopped,
            _ => SessionStatus.Active
        };
    }

    public void SelectNode(TreeNodeViewModel? node, bool preserveSearch = false)
    {
        SelectedNode = node;
        if (HasSelectedDashboard)
        {
            SelectedPayloadText = EmptySelectionText;
            SelectedFields = [];
        }
        else
        {
            SelectedPayloadText = node?.Observation?.DisplayJson ?? EmptySelectionText;
            SelectedFields = node?.Observation?.DetailFields ?? [];
        }

        if (!preserveSearch)
        {
            ClearSearchMatch();
        }
    }

    public void ClearSearchMatch()
    {
        _currentSearchMatch = null;
        SearchStatus = string.Empty;
    }

    public NodeSearchMatch? FindNext(bool previous)
    {
        if (string.IsNullOrEmpty(SearchQuery))
        {
            SearchStatus = string.Empty;
            return null;
        }

        List<TreeNodeViewModel> nodes = CollectSearchableNodes(Roots);
        if (nodes.Count == 0)
        {
            SearchStatus = "No searchable nodes.";
            return null;
        }

        NodeSearchMatch? match = _currentSearchMatch is null
            ? FindFirstMatch(nodes, previous)
            : FindNextMatch(nodes, _currentSearchMatch, previous);

        if (match is null)
        {
            SearchStatus = "No match.";
            return null;
        }

        _currentSearchMatch = match;
        SearchStatus = match.Node.Header;
        return match;
    }

    private NodeSearchMatch? FindFirstMatch(List<TreeNodeViewModel> nodes, bool previous)
    {
        int startIndex = GetSearchStartIndex(nodes, previous);

        if (previous)
        {
            for (int offset = 0; offset < nodes.Count; offset++)
            {
                int index = (startIndex - offset + nodes.Count) % nodes.Count;
                NodeSearchMatch? match = FindLastInNode(nodes[index], SearchQuery);
                if (match is not null)
                {
                    return match;
                }
            }
        }
        else
        {
            for (int offset = 0; offset < nodes.Count; offset++)
            {
                int index = (startIndex + offset) % nodes.Count;
                NodeSearchMatch? match = FindFirstInNode(nodes[index], SearchQuery);
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return null;
    }

    private int GetSearchStartIndex(List<TreeNodeViewModel> searchableNodes, bool previous)
    {
        if (SelectedNode is null)
        {
            return 0;
        }

        int selectedIndex = searchableNodes.IndexOf(SelectedNode);
        if (selectedIndex >= 0)
        {
            return selectedIndex;
        }

        Dictionary<TreeNodeViewModel, int> visitOrder = BuildVisitOrder(Roots);
        if (!visitOrder.TryGetValue(SelectedNode, out int selectedOrder))
        {
            return previous ? searchableNodes.Count - 1 : 0;
        }

        if (previous)
        {
            for (int index = searchableNodes.Count - 1; index >= 0; index--)
            {
                if (visitOrder[searchableNodes[index]] <= selectedOrder)
                {
                    return index;
                }
            }

            return searchableNodes.Count - 1;
        }

        for (int index = 0; index < searchableNodes.Count; index++)
        {
            if (visitOrder[searchableNodes[index]] >= selectedOrder)
            {
                return index;
            }
        }

        return 0;
    }

    private static Dictionary<TreeNodeViewModel, int> BuildVisitOrder(IEnumerable<TreeNodeViewModel> roots)
    {
        Dictionary<TreeNodeViewModel, int> order = [];
        int nextOrder = 0;

        void Visit(IEnumerable<TreeNodeViewModel> nodes)
        {
            foreach (TreeNodeViewModel node in nodes)
            {
                order[node] = nextOrder++;
                if (node.Children.Count > 0)
                {
                    Visit(node.Children);
                }
            }
        }

        Visit(roots);
        return order;
    }

    private NodeSearchMatch? FindNextMatch(
        List<TreeNodeViewModel> nodes,
        NodeSearchMatch current,
        bool previous)
    {
        int nodeIndex = nodes.IndexOf(current.Node);
        if (nodeIndex < 0)
        {
            return null;
        }

        NodeSearchMatch? matchInNode = previous
            ? FindPreviousInNode(current.Node, SearchQuery, current)
            : FindNextInNode(current.Node, SearchQuery, current);
        if (matchInNode is not null)
        {
            return matchInNode;
        }

        if (previous)
        {
            for (int offset = 1; offset < nodes.Count; offset++)
            {
                int index = (nodeIndex - offset + nodes.Count) % nodes.Count;
                NodeSearchMatch? match = FindLastInNode(nodes[index], SearchQuery);
                if (match is not null)
                {
                    return match;
                }
            }
        }
        else
        {
            for (int offset = 1; offset < nodes.Count; offset++)
            {
                int index = (nodeIndex + offset) % nodes.Count;
                NodeSearchMatch? match = FindFirstInNode(nodes[index], SearchQuery);
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return previous
            ? FindLastInNode(current.Node, SearchQuery)
            : FindFirstInNode(current.Node, SearchQuery);
    }

    internal static List<TreeNodeViewModel> CollectSearchableNodes(IEnumerable<TreeNodeViewModel> roots)
    {
        List<TreeNodeViewModel> nodes = [];
        CollectSearchableNodes(roots, nodes);
        return nodes;
    }

    private static void CollectSearchableNodes(IEnumerable<TreeNodeViewModel> nodes, List<TreeNodeViewModel> results)
    {
        foreach (TreeNodeViewModel node in nodes)
        {
            if (node.Observation is not null)
            {
                results.Add(node);
            }

            if (node.Children.Count > 0)
            {
                CollectSearchableNodes(node.Children, results);
            }
        }
    }

    internal static NodeSearchMatch? FindFirstInNode(TreeNodeViewModel node, string query)
    {
        NodeSearchMatch? nameMatch = FindFirstNodeNameMatch(node, query);
        if (nameMatch is not null)
        {
            return nameMatch;
        }

        HookObservation observation = node.Observation!;
        IReadOnlyList<PayloadField> fields = observation.DetailFields;

        if (fields.Count > 0)
        {
            return FindFirstFieldMatch(node, fields, query);
        }

        string payload = observation.DisplayJson;
        int payloadIndex = payload.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (payloadIndex >= 0)
        {
            return new NodeSearchMatch(node, NodeSearchTarget.Payload, -1, payloadIndex, query.Length);
        }

        return null;
    }

    internal static NodeSearchMatch? FindLastInNode(TreeNodeViewModel node, string query)
    {
        HookObservation observation = node.Observation!;
        IReadOnlyList<PayloadField> fields = observation.DetailFields;

        if (fields.Count > 0)
        {
            NodeSearchMatch? fieldMatch = FindLastFieldMatch(node, fields, query);
            if (fieldMatch is not null)
            {
                return fieldMatch;
            }
        }
        else
        {
            string payload = observation.DisplayJson;
            int payloadIndex = payload.LastIndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (payloadIndex >= 0)
            {
                return new NodeSearchMatch(node, NodeSearchTarget.Payload, -1, payloadIndex, query.Length);
            }
        }

        return FindLastNodeNameMatch(node, query);
    }

    private static NodeSearchMatch? FindFirstNodeNameMatch(TreeNodeViewModel node, string query)
    {
        HookObservation observation = node.Observation!;

        int hookIndex = observation.HookEventName.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (hookIndex >= 0)
        {
            return new NodeSearchMatch(node, NodeSearchTarget.NodeName, 0, hookIndex, query.Length);
        }

        int headerIndex = node.Header.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (headerIndex >= 0)
        {
            return new NodeSearchMatch(node, NodeSearchTarget.NodeName, 1, headerIndex, query.Length);
        }

        return null;
    }

    private static NodeSearchMatch? FindLastNodeNameMatch(TreeNodeViewModel node, string query)
    {
        int headerIndex = node.Header.LastIndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (headerIndex >= 0)
        {
            return new NodeSearchMatch(node, NodeSearchTarget.NodeName, 1, headerIndex, query.Length);
        }

        HookObservation observation = node.Observation!;
        int hookIndex = observation.HookEventName.LastIndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (hookIndex >= 0)
        {
            return new NodeSearchMatch(node, NodeSearchTarget.NodeName, 0, hookIndex, query.Length);
        }

        return null;
    }

    private static NodeSearchMatch? FindFirstFieldMatch(
        TreeNodeViewModel node,
        IReadOnlyList<PayloadField> fields,
        string query,
        int startFieldIndex = 0)
    {
        for (int fieldIndex = startFieldIndex; fieldIndex < fields.Count; fieldIndex++)
        {
            PayloadField field = fields[fieldIndex];
            int nameIndex = field.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (nameIndex >= 0)
            {
                return new NodeSearchMatch(node, NodeSearchTarget.FieldName, fieldIndex, nameIndex, query.Length);
            }

            int valueIndex = field.Value.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (valueIndex >= 0)
            {
                return new NodeSearchMatch(node, NodeSearchTarget.FieldValue, fieldIndex, valueIndex, query.Length);
            }
        }

        return null;
    }

    internal static NodeSearchMatch? FindNextInNode(
        TreeNodeViewModel node,
        string query,
        NodeSearchMatch current)
    {
        HookObservation observation = node.Observation!;
        IReadOnlyList<PayloadField> fields = observation.DetailFields;

        if (current.Target == NodeSearchTarget.NodeName)
        {
            if (fields.Count > 0)
            {
                return FindFirstFieldMatch(node, fields, query);
            }

            string payloadFromName = observation.DisplayJson;
            int payloadIndexFromName = payloadFromName.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (payloadIndexFromName >= 0)
            {
                return new NodeSearchMatch(node, NodeSearchTarget.Payload, -1, payloadIndexFromName, query.Length);
            }

            return null;
        }

        if (fields.Count > 0)
        {
            if (current.Target is NodeSearchTarget.FieldName or NodeSearchTarget.FieldValue)
            {
                PayloadField currentField = fields[current.FieldIndex];
                string currentText = current.Target == NodeSearchTarget.FieldName
                    ? currentField.Name
                    : currentField.Value;
                int nextInField = currentText.IndexOf(
                    query,
                    current.Index + Math.Max(1, query.Length),
                    StringComparison.OrdinalIgnoreCase);
                if (nextInField >= 0)
                {
                    return new NodeSearchMatch(node, current.Target, current.FieldIndex, nextInField, query.Length);
                }

                for (int fieldIndex = current.FieldIndex + 1; fieldIndex < fields.Count; fieldIndex++)
                {
                    NodeSearchMatch? match = FindFirstFieldMatch(node, fields, query, fieldIndex);
                    if (match is not null)
                    {
                        return match;
                    }
                }
            }

            return null;
        }

        if (current.Target != NodeSearchTarget.Payload)
        {
            return null;
        }

        string payload = observation.DisplayJson;
        int payloadIndex = payload.IndexOf(
            query,
            current.Index + Math.Max(1, query.Length),
            StringComparison.OrdinalIgnoreCase);
        if (payloadIndex >= 0)
        {
            return new NodeSearchMatch(node, NodeSearchTarget.Payload, -1, payloadIndex, query.Length);
        }

        return null;
    }

    internal static NodeSearchMatch? FindPreviousInNode(
        TreeNodeViewModel node,
        string query,
        NodeSearchMatch current)
    {
        HookObservation observation = node.Observation!;
        IReadOnlyList<PayloadField> fields = observation.DetailFields;

        if (fields.Count > 0)
        {
            if (current.Target is NodeSearchTarget.FieldName or NodeSearchTarget.FieldValue)
            {
                PayloadField currentField = fields[current.FieldIndex];
                string currentText = current.Target == NodeSearchTarget.FieldName
                    ? currentField.Name
                    : currentField.Value;
                int previousInField = currentText.LastIndexOf(
                    query,
                    Math.Max(0, current.Index - 1),
                    StringComparison.OrdinalIgnoreCase);
                if (previousInField >= 0)
                {
                    return new NodeSearchMatch(node, current.Target, current.FieldIndex, previousInField, query.Length);
                }

                for (int fieldIndex = current.FieldIndex - 1; fieldIndex >= 0; fieldIndex--)
                {
                    NodeSearchMatch? match = FindLastFieldMatch(node, fields, query, fieldIndex);
                    if (match is not null)
                    {
                        return match;
                    }
                }

                return FindLastNodeNameMatch(node, query);
            }

            if (current.Target == NodeSearchTarget.NodeName)
            {
                return null;
            }

            return null;
        }

        if (current.Target == NodeSearchTarget.Payload)
        {
            string payload = observation.DisplayJson;
            int payloadIndex = payload.LastIndexOf(
                query,
                Math.Max(0, current.Index - 1),
                StringComparison.OrdinalIgnoreCase);
            if (payloadIndex >= 0)
            {
                return new NodeSearchMatch(node, NodeSearchTarget.Payload, -1, payloadIndex, query.Length);
            }

            return FindLastNodeNameMatch(node, query);
        }

        if (current.Target == NodeSearchTarget.NodeName)
        {
            return null;
        }

        return null;
    }

    private static NodeSearchMatch? FindLastFieldMatch(
        TreeNodeViewModel node,
        IReadOnlyList<PayloadField> fields,
        string query,
        int? fieldIndex = null)
    {
        int start = fieldIndex ?? fields.Count - 1;
        int end = fieldIndex ?? 0;

        for (int index = start; index >= end; index--)
        {
            PayloadField field = fields[index];
            int valueIndex = field.Value.LastIndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (valueIndex >= 0)
            {
                return new NodeSearchMatch(node, NodeSearchTarget.FieldValue, index, valueIndex, query.Length);
            }

            int nameIndex = field.Name.LastIndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (nameIndex >= 0)
            {
                return new NodeSearchMatch(node, NodeSearchTarget.FieldName, index, nameIndex, query.Length);
            }
        }

        return null;
    }

    private TreeNodeViewModel GetOrCreateWorkspaceNode(WorkspaceContext workspace)
    {
        if (_workspaceNodes.TryGetValue(workspace.Key, out TreeNodeViewModel? node))
        {
            return node;
        }

        node = new TreeNodeViewModel(
            workspace.Key,
            workspace.DisplayName,
            TreeNodeKind.Workspace);

        _workspaceNodes.Add(workspace.Key, node);
        Roots.Insert(0, node);
        return node;
    }

    private TreeNodeViewModel GetOrCreateSessionNode(
        string scopedSessionId,
        string sessionId,
        TreeNodeViewModel workspaceNode,
        bool isUnknownSession)
    {
        string sessionKey = isUnknownSession
            ? $"{workspaceNode.Key}\0{scopedSessionId}\0{UnknownSession}"
            : scopedSessionId;

        if (_sessionNodes.TryGetValue(sessionKey, out TreeNodeViewModel? node))
        {
            return node;
        }

        node = new TreeNodeViewModel(
            sessionKey,
            sessionId,
            TreeNodeKind.Session);

        _sessionNodes.Add(sessionKey, node);
        _sessionParents[sessionKey] = workspaceNode;
        workspaceNode.Children.Insert(0, node);
        return node;
    }

    private bool TryMarkSourceFileLoaded(HookObservation observation)
    {
        if (observation.SourceFilePath is null)
        {
            return true;
        }

        return _loadedSourceFiles.Add(observation.SourceFilePath);
    }

    private void TrackSessionSourceFile(TreeNodeViewModel sessionNode, HookObservation observation)
    {
        if (observation.SourceFilePath is null)
        {
            return;
        }

        if (!_sessionSourceFiles.TryGetValue(sessionNode.Key, out HashSet<string>? files))
        {
            files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _sessionSourceFiles.Add(sessionNode.Key, files);
        }

        files.Add(observation.SourceFilePath);
        sessionNode.MarkHasReplayFiles();
    }

    private void RemoveSessionNode(TreeNodeViewModel sessionNode)
    {
        _sessionNodes.Remove(sessionNode.Key);
        _sessionSourceFiles.Remove(sessionNode.Key);
        RemoveGenerationNodes(sessionNode);

        if (!_sessionParents.Remove(sessionNode.Key, out TreeNodeViewModel? workspaceNode))
        {
            return;
        }

        workspaceNode.Children.Remove(sessionNode);
        if (workspaceNode.Children.Count == 0)
        {
            _workspaceNodes.Remove(workspaceNode.Key);
            Roots.Remove(workspaceNode);
        }

        SelectedNode = null;
        SelectedPayloadText = EmptySelectionText;
        SelectedFields = [];
        ClearSearchMatch();
    }

    private void RemoveGenerationNodes(TreeNodeViewModel sessionNode)
    {
        string prefix = sessionNode.Key + "\0";
        string[] generationKeys = _generationNodes.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();

        foreach (string key in generationKeys)
        {
            _generationNodes.Remove(key);
        }
    }

    private static TreeNodeViewModel CreateObservationNode(HookObservation observation)
    {
        return new TreeNodeViewModel(
            observation.EventId == Guid.Empty ? Guid.NewGuid().ToString("N") : observation.EventId.ToString("N"),
            observation.OccurrenceHeader,
            TreeNodeKind.Observation,
            observation);
    }

}

public enum NodeSearchTarget
{
    NodeName,
    Payload,
    FieldName,
    FieldValue
}

public sealed record NodeSearchMatch(
    TreeNodeViewModel Node,
    NodeSearchTarget Target,
    int FieldIndex,
    int Index,
    int Length);
