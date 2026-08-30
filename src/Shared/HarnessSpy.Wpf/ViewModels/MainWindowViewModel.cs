using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;
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
    // In-flight beforeShellExecution/beforeMCPExecution nodes waiting for their
    // matching after hook, keyed by generation and hook kind.
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
            InsertChronologically(sessionNode.Children, CreateObservationNode(observation));
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

        // Not nested: add as a direct child of the generation, at its correct
        // chronological position.
        InsertChronologically(generationNode.Children, observationNode);
        generationNode.IsExpanded = true;

        // Track new in-flight pre nodes for later pairing.
        TrackInFlightNode(observation, observationNode, generationNode);

        generationNode.RecomputeGeneration();
        sessionNode.RecomputeSession();

        if (observation.IsStop &&
            observation.Interpretation.ParticipatesInDerivedTurns &&
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

        // Surfaces without an exact turn id (Copilot CLI/VS Code) derive turns
        // from prompt/stop boundaries, declared entirely through traits.
        if (!observation.Interpretation.ParticipatesInDerivedTurns ||
            observation.IsSessionLifecycle)
        {
            return null;
        }

        string sessionKey = observation.ProviderScopedSessionId;
        if (observation.Interpretation.StartsDerivedTurn)
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
        ObservationInterpretation interpretation = observation.Interpretation;
        switch (interpretation.Role)
        {
            case ObservationRole.PromptTransformed:
                return TryNestPromptTransformed(observationNode, generationNode);

            case ObservationRole.ToolSuccess:
                return TryNestCompletion(observation, observationNode, generationNode, expand: false);

            case ObservationRole.ToolFailure:
            case ObservationRole.PermissionDenied:
                return TryNestCompletion(observation, observationNode, generationNode, expand: true);

            case ObservationRole.ToolBatch:
                return TryNestToolBatch(observation, observationNode, generationNode);

            case ObservationRole.SubagentStop:
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

            case ObservationRole.InnerExecutionStart:
            {
                if (interpretation.InnerExecutionOwnerTool is null ||
                    interpretation.InnerExecutionKind is null)
                {
                    return false;
                }

                TreeNodeViewModel? parent = FindInFlightPreByExecutionEvidence(
                    generationNode,
                    interpretation.InnerExecutionOwnerTool,
                    interpretation.InnerExecutionKind,
                    observation,
                    InnerScorer(interpretation.InnerCategory));
                if (parent is not null)
                {
                    parent.Children.Add(observationNode);
                    TrackInnerPre(generationNode, interpretation.InnerExecutionKind, observationNode);
                    return true;
                }

                return false;
            }

            case ObservationRole.InnerExecutionEnd:
            {
                if (interpretation.InnerExecutionKind is null)
                {
                    return false;
                }

                Func<HookObservation, HookObservation, int> scorer =
                    InnerScorer(interpretation.InnerCategory);
                TreeNodeViewModel? innerPre = FindAndRemoveInnerPre(
                    generationNode,
                    interpretation.InnerExecutionKind,
                    observation,
                    scorer);
                if (innerPre is not null)
                {
                    innerPre.Children.Add(observationNode);
                    innerPre.IsExpanded = true;
                    return true;
                }

                if (interpretation.InnerExecutionOwnerTool is null)
                {
                    return false;
                }

                TreeNodeViewModel? fallbackParent = FindInFlightPreByExecutionEvidence(
                    generationNode,
                    interpretation.InnerExecutionOwnerTool,
                    interpretation.InnerExecutionKind,
                    observation,
                    scorer);
                if (fallbackParent is not null)
                {
                    fallbackParent.Children.Add(observationNode);
                    return true;
                }

                return false;
            }

            case ObservationRole.FileAccess:
            {
                // Several file calls can be in flight at once, so match on the
                // file target rather than arrival order. Fall back to tool-name
                // matching only when a single owning call is unambiguous.
                if (interpretation.InnerExecutionKind is null ||
                    interpretation.FileAccessOwnerTools.Count == 0)
                {
                    return false;
                }

                TreeNodeViewModel? parent = FindInFlightPreByExecutionEvidence(
                    generationNode,
                    interpretation.FileAccessOwnerTools,
                    interpretation.InnerExecutionKind,
                    observation,
                    ToolCorrelationMatcher.ScoreFileTargetExecution)
                    ?? FindSoleInFlightPreAwaitingInner(
                        generationNode,
                        interpretation.FileAccessOwnerTools,
                        interpretation.InnerExecutionKind);
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

    // The transformed prompt is the runtime's rewrite of the submitted prompt,
    // so it reads as a child of the prompt it derives from rather than a sibling.
    private static bool TryNestPromptTransformed(
        TreeNodeViewModel observationNode,
        TreeNodeViewModel generationNode)
    {
        foreach (TreeNodeViewModel child in generationNode.Children)
        {
            if (child.Observation?.Interpretation.Role == ObservationRole.PromptSubmitted)
            {
                child.Children.Add(observationNode);
                child.IsExpanded = true;
                return true;
            }
        }

        return false;
    }

    // Nests a tool completion (success/failure/permission-denied) under its
    // matching in-flight tool request by scoped tool-call evidence.
    private bool TryNestCompletion(
        HookObservation observation,
        TreeNodeViewModel observationNode,
        TreeNodeViewModel generationNode,
        bool expand)
    {
        TreeNodeViewModel? preNode =
            observation.Interpretation.MatchStrategy == ToolCallMatchStrategy.ToolSignature
                ? FindInFlightPreByToolSignature(generationNode, observation)
                : observation.ToolUseId is not null
                    ? FindInFlightPreByToolCall(generationNode, observation)
                    : null;
        if (preNode is null)
        {
            return false;
        }

        preNode.Children.Add(observationNode);
        preNode.IsExpanded = expand;
        UpdatePreNodeSummary(preNode, observation);
        if (expand)
        {
            RemoveTrackedInnerPre(generationNode, preNode);
        }

        TryFormParallelWave(generationNode, preNode);
        return true;
    }

    // Claude's PostToolBatch fires after the matching PostToolUse has already
    // completed its PreToolUse, so it must match against every pre in the
    // generation (completed or not) rather than only in-flight ones. Claude's
    // tool_use_id is unique per call, so exact id equality alone is enough to
    // pick the parent, unlike Cursor's reused-id case elsewhere in this file.
    // A batch of one call nests under that call's PreToolUse, mirroring
    // PostToolUse; a batch of several instead groups its matching PreToolUse
    // nodes under a synthetic parent (see GroupToolBatch) and is then inserted
    // chronologically like any other sibling.
    private static bool TryNestToolBatch(
        HookObservation observation,
        TreeNodeViewModel observationNode,
        TreeNodeViewModel generationNode)
    {
        IReadOnlyList<string> toolCallIds = observation.BatchToolCallIds;
        if (toolCallIds.Count == 1)
        {
            TreeNodeViewModel? preNode = FindPreNodeByToolCallId(generationNode, toolCallIds[0]);
            if (preNode is null)
            {
                return false;
            }

            preNode.Children.Add(observationNode);
            return true;
        }

        if (toolCallIds.Count > 1)
        {
            GroupToolBatch(observationNode, generationNode, toolCallIds);
        }

        return false;
    }

    // Splices the batch node in as the parent of every one of its matching
    // PreToolUse nodes, carrying the real PostToolBatch observation the same
    // way TryFormParallelWave splices in a synthetic ParallelWave for
    // overlapping-time calls. Every id must resolve to a pre node or nothing
    // is moved, so a partial match (e.g. a call outside this generation)
    // never misrepresents which calls the batch actually covers and the
    // event is left as an unmatched sibling instead.
    private static void GroupToolBatch(
        TreeNodeViewModel observationNode,
        TreeNodeViewModel generationNode,
        IReadOnlyList<string> toolCallIds)
    {
        List<TreeNodeViewModel> matches = [];
        foreach (string id in toolCallIds)
        {
            TreeNodeViewModel? match = FindPreNodeByToolCallId(generationNode, id);
            if (match is null)
            {
                return;
            }

            matches.Add(match);
        }

        matches.Sort((a, b) => a.Observation!.ObservedAtUtc.CompareTo(b.Observation!.ObservedAtUtc));

        foreach (TreeNodeViewModel match in matches)
        {
            DetachToolCallNode(generationNode, match);
            observationNode.Children.Add(match);
        }
    }

    // Detaches a pre node wherever it currently lives — directly under the
    // generation, or inside a ParallelWave already formed (by overlapping
    // call timing) before the batch arrived — and unwraps/removes that wave
    // if pulling a member out leaves it with fewer than the two members a
    // wave requires.
    private static void DetachToolCallNode(TreeNodeViewModel generationNode, TreeNodeViewModel node)
    {
        if (generationNode.Children.Remove(node))
        {
            return;
        }

        foreach (TreeNodeViewModel child in generationNode.Children)
        {
            if (child.Kind != TreeNodeKind.ParallelWave || !child.Children.Remove(node))
            {
                continue;
            }

            if (child.Children.Count == 0)
            {
                generationNode.Children.Remove(child);
            }
            else if (child.Children.Count == 1)
            {
                TreeNodeViewModel remaining = child.Children[0];
                child.Children.Remove(remaining);
                int index = generationNode.Children.IndexOf(child);
                generationNode.Children.Remove(child);
                generationNode.Children.Insert(Math.Min(index, generationNode.Children.Count), remaining);
            }
            else
            {
                UpdateWaveNode(child);
            }

            return;
        }
    }

    private static TreeNodeViewModel? FindPreNodeByToolCallId(
        TreeNodeViewModel generationNode,
        string toolCallId)
    {
        foreach (TreeNodeViewModel child in EnumeratePreNodes(generationNode, onlyInFlight: false))
        {
            if (StringComparer.Ordinal.Equals(child.Observation!.ToolUseId, toolCallId))
            {
                return child;
            }
        }

        return null;
    }

    private static Func<HookObservation, HookObservation, int> InnerScorer(
        InnerExecutionCategory category) => category switch
    {
        InnerExecutionCategory.Shell => ToolCorrelationMatcher.ScoreShellExecution,
        InnerExecutionCategory.Mcp => ToolCorrelationMatcher.ScoreMcpExecution,
        _ => ToolCorrelationMatcher.ScoreFileTargetExecution
    };

    // Registers a newly created node so later matching events can nest under it.
    private void TrackInFlightNode(
        HookObservation observation,
        TreeNodeViewModel node,
        TreeNodeViewModel _)
    {
        if (observation.Interpretation.OpensSubagent &&
            observation.SubagentId is string subagentId)
        {
            _inFlightSubagents[subagentId] = node;
        }
    }

    // Cursor can reuse one tool_use_id for several calls in the same tool
    // batch. Match within the generation by id, name, and canonical tool_input.
    // If several candidates remain indistinguishable, leave the post orphaned
    // rather than asserting a false relationship.
    private static TreeNodeViewModel? FindInFlightPreByToolCall(
        TreeNodeViewModel generationNode,
        HookObservation postObservation)
    {
        List<TreeNodeViewModel> candidates = EnumerateInFlightPreNodes(generationNode)
            .Where(child =>
                StringComparer.Ordinal.Equals(
                    child.Observation!.ToolUseId,
                    postObservation.ToolUseId) &&
                StringComparer.Ordinal.Equals(
                    child.Observation.ToolName,
                    postObservation.ToolName))
            .ToList();

        return SelectUniqueBest(
            candidates,
            child => ToolCorrelationMatcher.ScoreGenericToolCall(
                child.Observation!,
                postObservation));
    }

    // Copilot CLI supplies no tool-use id, so a completion nests under the
    // earliest still-open request with the same tool name, preferring one whose
    // canonical toolArgs are identical. Sequential CLI execution makes this
    // arrival-order fallback correct even when the same tool runs twice.
    private static TreeNodeViewModel? FindInFlightPreByToolSignature(
        TreeNodeViewModel generationNode,
        HookObservation postObservation)
    {
        if (postObservation.ToolName is null)
        {
            return null;
        }

        List<TreeNodeViewModel> sameTool = EnumerateInFlightPreNodes(generationNode)
            .Where(child => StringComparer.Ordinal.Equals(
                child.Observation!.ToolName,
                postObservation.ToolName))
            .ToList();
        if (sameTool.Count == 0)
        {
            return null;
        }

        foreach (TreeNodeViewModel candidate in sameTool)
        {
            if (ToolCorrelationMatcher.CopilotToolArgsEqual(candidate.Observation!, postObservation))
            {
                return candidate;
            }
        }

        return sameTool[0];
    }

    private static IEnumerable<TreeNodeViewModel> EnumerateInFlightPreNodes(
        TreeNodeViewModel parent) => EnumeratePreNodes(parent, onlyInFlight: true);

    private static IEnumerable<TreeNodeViewModel> EnumeratePreNodes(
        TreeNodeViewModel parent,
        bool onlyInFlight)
    {
        foreach (TreeNodeViewModel child in parent.Children)
        {
            if (child.Kind == TreeNodeKind.ParallelWave)
            {
                foreach (TreeNodeViewModel nested in EnumeratePreNodes(child, onlyInFlight))
                {
                    yield return nested;
                }

                continue;
            }

            if (child.Observation?.Interpretation.OpensToolCall == true &&
                (!onlyInFlight || !IsCompletedToolCall(child)))
            {
                yield return child;
            }
        }
    }

    private static TreeNodeViewModel? FindInFlightPreByExecutionEvidence(
        TreeNodeViewModel generationNode,
        string toolName,
        string innerPreKind,
        HookObservation observation,
        Func<HookObservation, HookObservation, int> score)
        => FindInFlightPreByExecutionEvidence(
            generationNode,
            [toolName],
            innerPreKind,
            observation,
            score);

    private static TreeNodeViewModel? FindInFlightPreByExecutionEvidence(
        TreeNodeViewModel generationNode,
        IReadOnlyCollection<string> toolNames,
        string innerPreKind,
        HookObservation observation,
        Func<HookObservation, HookObservation, int> score)
    {
        List<TreeNodeViewModel> candidates = CollectInFlightPresAwaitingInner(
            generationNode,
            toolNames,
            innerPreKind);

        return SelectUniqueBest(
            candidates,
            child => score(child.Observation!, observation));
    }

    // Fallback for a file-access hook when paths can't be compared: only attach
    // when exactly one matching pre is still awaiting its inner hook, so we
    // never guess between concurrent calls.
    private static TreeNodeViewModel? FindSoleInFlightPreAwaitingInner(
        TreeNodeViewModel generationNode,
        IReadOnlyCollection<string> toolNames,
        string innerPreKind)
    {
        List<TreeNodeViewModel> candidates = CollectInFlightPresAwaitingInner(
            generationNode,
            toolNames,
            innerPreKind);

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static List<TreeNodeViewModel> CollectInFlightPresAwaitingInner(
        TreeNodeViewModel generationNode,
        IReadOnlyCollection<string> toolNames,
        string innerPreKind)
    {
        return EnumerateInFlightPreNodes(generationNode)
            .Where(child =>
                child.Observation!.ToolName is { } toolName &&
                toolNames.Contains(toolName, StringComparer.Ordinal) &&
                !child.Children.Any(grandchild =>
                    StringComparer.Ordinal.Equals(
                        grandchild.Observation?.Interpretation.InnerExecutionKind,
                        innerPreKind)))
            .ToList();
    }

    private static TreeNodeViewModel? SelectUniqueBest(
        IReadOnlyList<TreeNodeViewModel> candidates,
        Func<TreeNodeViewModel, int> score)
    {
        TreeNodeViewModel? best = null;
        int bestScore = ToolCorrelationMatcher.NoMatch;
        bool tied = false;

        foreach (TreeNodeViewModel candidate in candidates)
        {
            int candidateScore = score(candidate);
            if (candidateScore == ToolCorrelationMatcher.NoMatch)
            {
                continue;
            }

            if (candidateScore > bestScore)
            {
                best = candidate;
                bestScore = candidateScore;
                tied = false;
            }
            else if (candidateScore == bestScore)
            {
                tied = true;
            }
        }

        return tied ? null : best;
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

    private TreeNodeViewModel? FindAndRemoveInnerPre(
        TreeNodeViewModel generationNode,
        string hookEventName,
        HookObservation observation,
        Func<HookObservation, HookObservation, int> score)
    {
        string key = $"{generationNode.Key}\0{hookEventName}";
        if (!_inFlightInnerPre.TryGetValue(key, out List<TreeNodeViewModel>? list) || list.Count == 0)
        {
            return null;
        }

        TreeNodeViewModel? node = SelectUniqueBest(
            list,
            candidate => candidate.Observation is null
                ? ToolCorrelationMatcher.NoMatch
                : score(candidate.Observation, observation));
        if (node is null)
        {
            return null;
        }

        list.Remove(node);
        if (list.Count == 0)
        {
            _inFlightInnerPre.Remove(key);
        }

        return node;
    }

    private void RemoveTrackedInnerPre(
        TreeNodeViewModel generationNode,
        TreeNodeViewModel preNode)
    {
        foreach (TreeNodeViewModel child in preNode.Children)
        {
            ObservationInterpretation? childInterpretation = child.Observation?.Interpretation;
            if (childInterpretation is null ||
                childInterpretation.Role != ObservationRole.InnerExecutionStart ||
                childInterpretation.InnerExecutionKind is not string hookEventName ||
                childInterpretation.InnerCategory is not
                    (InnerExecutionCategory.Shell or InnerExecutionCategory.Mcp))
            {
                continue;
            }

            string key = $"{generationNode.Key}\0{hookEventName}";
            if (!_inFlightInnerPre.TryGetValue(key, out List<TreeNodeViewModel>? list))
            {
                continue;
            }

            list.Remove(child);
            if (list.Count == 0)
            {
                _inFlightInnerPre.Remove(key);
            }
        }
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

            if (child.Observation?.Interpretation.OpensToolCall != true)
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
            c.Observation?.Interpretation.Role is
                ObservationRole.ToolSuccess or
                ObservationRole.ToolFailure or
                ObservationRole.PermissionDenied);
    }

    private static DateTimeOffset GetCallEnd(TreeNodeViewModel preNode)
    {
        HookObservation? postObs = preNode.Children
            .Select(c => c.Observation)
            .FirstOrDefault(o => o?.Interpretation.Role is
                ObservationRole.ToolSuccess or
                ObservationRole.ToolFailure or
                ObservationRole.PermissionDenied);

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
        sessionNode.Status = observation.Interpretation.Role switch
        {
            ObservationRole.SessionEnd => SessionStatus.Ended,
            ObservationRole.TurnStop => SessionStatus.Stopped,
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

    // Deterministic chronological placement of a direct child, ordered by
    // (EffectiveTimestamp, IngestionOrdinal, EventId). Appended input that is
    // already in order lands at the end (identical to the previous behaviour);
    // a late-arriving event is reinserted at the correct position.
    private static void InsertChronologically(
        ObservableCollection<TreeNodeViewModel> children,
        TreeNodeViewModel node)
    {
        (DateTimeOffset time, long ordinal, Guid id) key = SortKey(node);
        for (int index = 0; index < children.Count; index++)
        {
            if (Compare(SortKey(children[index]), key) > 0)
            {
                children.Insert(index, node);
                return;
            }
        }

        children.Add(node);
    }

    // A node's chronological key: its own observation, or the earliest
    // descendant for container nodes (turns, parallel waves).
    private static (DateTimeOffset Time, long Ordinal, Guid Id) SortKey(TreeNodeViewModel node)
    {
        if (node.Observation is { } observation)
        {
            // Session brackets sort to the extremes of the session regardless of
            // the provider's own timestamps: Copilot CLI, for instance, emits
            // sessionStart a couple of seconds after the first prompt, which
            // would otherwise drop the "new session" node to the bottom.
            DateTimeOffset time = observation.Interpretation.Role switch
            {
                ObservationRole.SessionStart => DateTimeOffset.MinValue,
                ObservationRole.SessionEnd => DateTimeOffset.MaxValue,
                _ => observation.EffectiveTimestamp
            };
            return (time, observation.IngestionOrdinal, observation.EventId);
        }

        (DateTimeOffset Time, long Ordinal, Guid Id) best =
            (DateTimeOffset.MaxValue, long.MaxValue, Guid.Empty);
        bool found = false;
        foreach (TreeNodeViewModel child in node.Children)
        {
            (DateTimeOffset, long, Guid) childKey = SortKey(child);
            if (!found || Compare(childKey, best) < 0)
            {
                best = childKey;
                found = true;
            }
        }

        return best;
    }

    private static int Compare(
        (DateTimeOffset Time, long Ordinal, Guid Id) left,
        (DateTimeOffset Time, long Ordinal, Guid Id) right)
    {
        int byTime = left.Time.CompareTo(right.Time);
        if (byTime != 0)
        {
            return byTime;
        }

        int byOrdinal = left.Ordinal.CompareTo(right.Ordinal);
        return byOrdinal != 0 ? byOrdinal : left.Id.CompareTo(right.Id);
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
