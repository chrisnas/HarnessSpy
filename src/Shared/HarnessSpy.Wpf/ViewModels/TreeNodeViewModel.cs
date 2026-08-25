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

    // Directional glyph shown ahead of observation nodes. "before"/"pre" hooks
    // point right (input), "after"/"post" hooks point left (result); the blue
    // afterAgentThought node intentionally has no arrow.
    public string HeaderPrefix
    {
        get
        {
            string? name = Observation?.HookEventName;
            if (string.IsNullOrEmpty(name) || IsAgentThought)
            {
                return string.Empty;
            }

            if (name.StartsWith("before", StringComparison.Ordinal) ||
                name.StartsWith("pre", StringComparison.Ordinal))
            {
                return RightArrow;
            }

            if (name.StartsWith("after", StringComparison.Ordinal) ||
                name.StartsWith("post", StringComparison.Ordinal))
            {
                return LeftArrow;
            }

            return string.Empty;
        }
    }

    public bool IsAgentThought =>
        Observation is not null &&
        StringComparer.Ordinal.Equals(Observation.HookEventName, "afterAgentThought");

    public bool IsAgentResponse =>
        Observation is not null &&
        StringComparer.Ordinal.Equals(Observation.HookEventName, "afterAgentResponse");

    public bool IsStop => Observation?.IsStop ?? false;

    public bool IsAbortedStop => Observation?.IsAbortedStop ?? false;

    public bool IsPreCompact =>
        Observation is not null &&
        StringComparer.Ordinal.Equals(Observation.HookEventName, "preCompact");

    public bool IsFailure =>
        Observation is not null &&
        StringComparer.Ordinal.Equals(Observation.HookEventName, "postToolUseFailure");

    public bool IsMcpExecution =>
        Observation is not null &&
        (StringComparer.Ordinal.Equals(Observation.HookEventName, "beforeMCPExecution") ||
         StringComparer.Ordinal.Equals(Observation.HookEventName, "afterMCPExecution"));

    // Blue/purple/orange node labels need a light foreground on the selection
    // highlight so they stay readable when selected in the tree.
    public bool UsesLightForegroundWhenSelected =>
        IsAgentThought || IsParallelWave || IsStop || IsPreCompact;

    // The full assistant/thinking text, shown as a hover tooltip on
    // afterAgentResponse and afterAgentThought nodes only.
    public string? HoverText =>
        IsAgentThought || IsAgentResponse ? Observation?.Text : null;

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

        NodeSummary = NodeSummaryBuilder.Build(Children, isSession: false, turnCount: 0, abortedTurnCount: 0);
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

        NodeSummary = NodeSummaryBuilder.Build(Children, isSession: true, turnCount, abortedTurnCount);
        Summary = NodeSummary.Badge;
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
