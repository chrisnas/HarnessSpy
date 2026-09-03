using System.Collections.ObjectModel;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Wpf.ViewModels;

public enum TreeNodeKind
{
    Workspace,
    Session,
    Generation,
    Observation,
    ParallelWave
}

// One transcript row attached to a canonical hook node as corroborating
// evidence, with the relationship that ties them together.
public sealed record TranscriptEvidence(
    HookObservation Observation,
    TranscriptRelationshipKind Relationship);

public enum SessionStatus
{
    Active,
    Stopped,
    Ended
}

public sealed class TreeNodeViewModel : ObservableObject
{
    private const string RightArrow = "\u2192";
    private const string LeftArrow = "\u2190";
    private const string ParallelGlyph = "\u2225";

    private bool _isExpanded;
    private SessionStatus _status = SessionStatus.Active;
    private string _header;
    private string _summary = string.Empty;
    private bool _hasReplayFiles;
    private NodeSummary? _nodeSummary;

    public TreeNodeViewModel(
        string key,
        string header,
        TreeNodeKind kind,
        HookObservation? observation = null)
    {
        Key = key;
        _header = header;
        Kind = kind;
        Observation = observation;
        if (kind is TreeNodeKind.Session or TreeNodeKind.Generation)
        {
            _nodeSummary = NodeSummary.CreateEmpty(isSession: kind == TreeNodeKind.Session);
        }
    }

    public string Key { get; }

    // Observable because a generation ("turn") node is created before its
    // prompt is known and gets relabelled as its events stream in.
    public string Header
    {
        get => _header;
        set => SetProperty(ref _header, value);
    }

    // Secondary badge shown next to session and generation headers.
    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    public NodeSummary? NodeSummary
    {
        get => _nodeSummary;
        private set => SetProperty(ref _nodeSummary, value);
    }

    public bool HasDashboardHover =>
        NodeSummary is not null && (IsSession || IsGeneration);

    public TreeNodeKind Kind { get; }

    // 1-based position of this turn within its session; only meaningful for
    // generation nodes.
    public int TurnNumber { get; init; }

    public HookObservation? Observation { get; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    // Transcript rows that enrich this canonical node without adding their own
    // timeline entry. Populated by the reconciler via AttachEvidence.
    public ObservableCollection<TranscriptEvidence> Evidence { get; } = [];

    public bool HasEvidence => Evidence.Count > 0;

    private bool _promotedFromTranscript;

    // True for a node whose content came from a provider transcript rather than
    // a hook. Used for a source badge in the inspector.
    public bool IsTranscriptSourced => Observation?.IsTranscriptSourced ?? false;

    // The correlation confidence of this node's own observation, shown as a
    // badge so heuristic transcript matches are never presented as exact.
    public string? CorrelationConfidence => Observation?.Interpretation.Evidence.ToString();

    public void AddEvidence(HookObservation observation, TranscriptRelationshipKind relationship)
    {
        Evidence.Add(new TranscriptEvidence(observation, relationship));
        OnPropertyChanged(nameof(HasEvidence));
    }

    // Records that a hook arrived and became the canonical primary for a node
    // that a transcript row had created first.
    public void MarkPromotedFromTranscript()
    {
        _promotedFromTranscript = true;
        OnPropertyChanged(nameof(WasPromotedFromTranscript));
    }

    public bool WasPromotedFromTranscript => _promotedFromTranscript;

    public bool IsSession => Kind == TreeNodeKind.Session;

    public bool IsGeneration => Kind == TreeNodeKind.Generation;

    public bool IsParallelWave => Kind == TreeNodeKind.ParallelWave;

    public bool HasReplayFiles
    {
        get => _hasReplayFiles;
        private set => SetProperty(ref _hasReplayFiles, value);
    }

    // Live indicator for a session: green while running, orange once stopped,
    // hidden after the session has ended.
    public SessionStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    // Directional glyph shown ahead of observation nodes, driven by the
    // provider-neutral direction trait rather than the native event name. A
    // PostToolBatch that grouped several PreToolUse nodes shows the same
    // parallel glyph as a ParallelWave instead of its usual output arrow.
    public string HeaderPrefix => IsToolBatchGroup
        ? ParallelGlyph
        : Observation?.Interpretation.Direction switch
        {
            ObservationDirection.Input => RightArrow,
            ObservationDirection.Output => LeftArrow,
            _ => string.Empty
        };

    // True for a PostToolBatch node that became the parent of 2+ matching
    // PreToolUse nodes, so it can be styled like a ParallelWave.
    public bool IsToolBatchGroup =>
        Observation?.Interpretation.Role == ObservationRole.ToolBatch && Children.Count > 0;

    public bool IsAgentThought =>
        Observation?.Interpretation.Role == ObservationRole.AgentThought;

    public bool IsAgentResponse =>
        Observation?.Interpretation.Role == ObservationRole.AgentResponse;

    public bool IsStop => Observation?.IsStop ?? false;

    public bool IsAbortedStop => Observation?.IsAbortedStop ?? false;

    public bool IsPreCompact =>
        Observation?.Interpretation.Role == ObservationRole.CompactionStart;

    // Both ends of a compaction (PreCompact/PostCompact) share the bold-red
    // styling so the whole compaction block reads as one unit.
    public bool IsCompaction =>
        Observation?.Interpretation.Role is
            ObservationRole.CompactionStart or ObservationRole.CompactionEnd;

    public bool IsFailure =>
        Observation?.Interpretation.Tone == ObservationTone.Failure;

    public bool IsMcpExecution =>
        Observation?.Interpretation.Tone == ObservationTone.Mcp;

    public bool IsPermission =>
        Observation?.Interpretation.Tone == ObservationTone.Permission;

    public bool IsPermissionDenied =>
        Observation?.Interpretation.Role == ObservationRole.PermissionDenied;

    // Bold coloured node labels (blue/purple/orange/green/red) need a light
    // foreground on the selection highlight so they stay readable when selected.
    public bool UsesLightForegroundWhenSelected =>
        IsAgentThought || IsParallelWave || IsToolBatchGroup || IsStop || IsCompaction ||
        IsPermission || IsPermissionDenied || IsTranscriptSourced;

    // The full assistant/thinking text, shown as a hover tooltip. The engine
    // decides which observations expose hover text (assistant/thinking output).
    public string? HoverText => Observation?.Interpretation.HoverText;

    public bool HasHoverText => !string.IsNullOrEmpty(HoverText) && !HasDashboardHover;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public void MarkHasReplayFiles()
    {
        HasReplayFiles = true;
    }

    // Rebuilds the turn label (from the prompt, once seen) and the summary
    // badge from the events currently under this generation node. Recomputing
    // from scratch keeps the counts correct regardless of event arrival order.
    public void RecomputeGeneration()
    {
        if (Kind != TreeNodeKind.Generation)
        {
            return;
        }

        NodeSummary = SummaryStrategies.Build(Children, isSession: false, turnCount: 0, abortedTurnCount: 0);
        Summary = NodeSummary.Badge;
        Header = FindPrompt(Children) is string prompt
            ? $"Turn {TurnNumber} · {BuildPromptPreview(prompt)}"
            : $"Turn {TurnNumber}";
    }

    public void RecomputeSession()
    {
        if (Kind != TreeNodeKind.Session)
        {
            return;
        }

        int turnCount = 0;
        int abortedTurnCount = 0;
        foreach (TreeNodeViewModel child in Children)
        {
            if (child.Kind != TreeNodeKind.Generation)
            {
                continue;
            }

            turnCount++;
            if (child.NodeSummary?.IsAborted == true)
            {
                abortedTurnCount++;
            }
        }

        NodeSummary = SummaryStrategies.Build(Children, isSession: true, turnCount, abortedTurnCount);
        Summary = NodeSummary.Badge;

        // Relabel the session with its opening prompt (like turn nodes) so the
        // tree reads by intent instead of an opaque conversation id. Falls back
        // to the id until the first prompt is seen.
        if (FindPrompt(Children) is string prompt)
        {
            Header = BuildPromptPreview(prompt);
        }
    }

    // Returns the chain of nodes from the containing root down to (and
    // including) target, or null if target is not reachable from roots. Pure
    // data traversal, no WPF calls, so it's cheap regardless of tree size.
    public static List<TreeNodeViewModel>? FindAncestorPath(
        IEnumerable<TreeNodeViewModel> roots,
        TreeNodeViewModel target)
    {
        List<TreeNodeViewModel> path = [];
        return TryBuildAncestorPath(roots, target, path) ? path : null;
    }

    private static bool TryBuildAncestorPath(
        IEnumerable<TreeNodeViewModel> nodes,
        TreeNodeViewModel target,
        List<TreeNodeViewModel> path)
    {
        foreach (TreeNodeViewModel node in nodes)
        {
            path.Add(node);

            if (ReferenceEquals(node, target) || TryBuildAncestorPath(node.Children, target, path))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    private static string? FindPrompt(IEnumerable<TreeNodeViewModel> nodes)
    {
        foreach (TreeNodeViewModel node in nodes)
        {
            if (node.Observation?.PromptText is string prompt)
            {
                return prompt;
            }

            if (node.Children.Count > 0 && FindPrompt(node.Children) is string nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static string BuildPromptPreview(string prompt)
    {
        string oneLine = prompt.ReplaceLineEndings(" ").Trim();
        const int maxLength = 60;
        return oneLine.Length <= maxLength
            ? oneLine
            : oneLine[..maxLength].TrimEnd() + "\u2026";
    }
}
