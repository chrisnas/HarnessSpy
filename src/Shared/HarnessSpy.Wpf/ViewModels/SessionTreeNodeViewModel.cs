using System.Collections.ObjectModel;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Wpf.ViewModels;

public enum SessionTreeNodeKind
{
    Folder,
    Workspace,
    Session,
    Turn,
    Event,
    ParallelGroup,
    Plan,
    PlanActivity,
    OrphanPlansRoot,
    PlanGroup
}

public sealed record SessionDetailRow(string Name, string Value);

public sealed class SessionTreeNodeViewModel : ObservableObject
{
    private const string InputArrow = "\u2192";
    private const string OutputArrow = "\u2190";
    private const string ParallelGlyph = "\u2225";

    private bool _isExpanded;
    private bool _isSelected;

    public SessionTreeNodeViewModel(
        string stableId,
        string header,
        SessionTreeNodeKind kind,
        HookProvider provider,
        string summary = "",
        string? hoverText = null,
        WorkspaceContext? workspace = null,
        SessionCatalogEntry? session = null,
        SessionTurn? turn = null,
        SessionEventRecord? eventRecord = null,
        IReadOnlyList<SessionDetailRow>? details = null,
        IReadOnlyList<SessionDetailRow>? provenance = null,
        string? rawSource = null,
        NodeSummary? nodeSummary = null,
        SessionPlanArtifact? plan = null,
        SessionPlanActivity? planActivity = null)
    {
        StableId = stableId;
        Header = header;
        Kind = kind;
        Provider = provider;
        Summary = summary;
        HoverText = hoverText;
        Workspace = workspace;
        Session = session;
        Turn = turn;
        EventRecord = eventRecord;
        Details = details ?? [];
        Provenance = provenance ?? [];
        RawSource = rawSource ?? string.Empty;
        NodeSummary = nodeSummary;
        Plan = plan;
        PlanActivity = planActivity;
    }

    public string StableId { get; }

    // Header, summary, and the underlying data are mutable so a node can be
    // rehydrated in place: a skeleton node (id + workspace + lifecycle) is
    // created first and later filled in with its title, turns, and summary as
    // the provider scan produces richer information.
    public string Header { get; private set; }

    public string Summary { get; private set; }

    public string? HoverText { get; private set; }

    public SessionTreeNodeKind Kind { get; }

    public HookProvider Provider { get; }

    public WorkspaceContext? Workspace { get; private set; }

    public SessionCatalogEntry? Session { get; private set; }

    public SessionTurn? Turn { get; private set; }

    public SessionEventRecord? EventRecord { get; private set; }

    public SessionPlanArtifact? Plan { get; private set; }

    public SessionPlanActivity? PlanActivity { get; private set; }

    // For a plan-activity node, the StableId of the canonical plan node it links
    // to, so selecting the activity can navigate to the plan.
    public string? LinkedPlanNodeId { get; set; }

    public IReadOnlyList<SessionDetailRow> Details { get; private set; }

    public IReadOnlyList<SessionDetailRow> Provenance { get; private set; }

    public string RawSource { get; private set; }

    public NodeSummary? NodeSummary { get; private set; }

    public ObservableCollection<SessionTreeNodeViewModel> Children { get; } = [];

    // Copies the display state and payload from a freshly projected node with
    // the same StableId, leaving identity (StableId/Kind/Provider), expansion,
    // and selection untouched. Only properties whose visible value changed
    // raise notifications, avoiding a full WPF rebind on every refresh.
    public void UpdateFrom(SessionTreeNodeViewModel other)
    {
        bool wasOpenSession = IsOpenSession;
        bool wasClosedSession = IsClosedSession;
        bool wasThinking = IsThinking;
        bool wasResponse = IsResponse;
        bool wasPrompt = IsPrompt;
        bool wasToolRequest = IsToolRequest;
        bool wasToolResult = IsToolResult;
        bool wasMcp = IsMcp;
        bool wasFailure = IsFailure;
        bool wasSkill = IsSkill;
        string oldHeaderPrefix = HeaderPrefix;
        bool hadHoverText = HasHoverText;
        bool hadSimpleTooltip = HasSimpleTooltip;
        bool hadNodeSummary = HasNodeSummary;
        bool hadDashboardHover = HasDashboardHover;
        bool hadRawSource = HasRawSource;
        bool hadProvenance = HasProvenance;

        if (!string.Equals(Header, other.Header, StringComparison.Ordinal))
        {
            Header = other.Header;
            OnPropertyChanged(nameof(Header));
        }

        if (!string.Equals(Summary, other.Summary, StringComparison.Ordinal))
        {
            Summary = other.Summary;
            OnPropertyChanged(nameof(Summary));
        }

        if (!string.Equals(
            HoverText,
            other.HoverText,
            StringComparison.Ordinal))
        {
            HoverText = other.HoverText;
            OnPropertyChanged(nameof(HoverText));
        }

        Workspace = other.Workspace;
        Session = other.Session;
        Turn = other.Turn;
        EventRecord = other.EventRecord;
        Plan = other.Plan;
        PlanActivity = other.PlanActivity;
        LinkedPlanNodeId = other.LinkedPlanNodeId;

        if (!Details.SequenceEqual(other.Details))
        {
            Details = other.Details;
            OnPropertyChanged(nameof(Details));
        }

        if (!Provenance.SequenceEqual(other.Provenance))
        {
            Provenance = other.Provenance;
            OnPropertyChanged(nameof(Provenance));
        }

        if (!string.Equals(
            RawSource,
            other.RawSource,
            StringComparison.Ordinal))
        {
            RawSource = other.RawSource;
            OnPropertyChanged(nameof(RawSource));
        }

        if (!SummariesEqual(NodeSummary, other.NodeSummary))
        {
            NodeSummary = other.NodeSummary;
            OnPropertyChanged(nameof(NodeSummary));
        }

        NotifyIfChanged(
            nameof(HasHoverText),
            hadHoverText,
            HasHoverText);
        NotifyIfChanged(
            nameof(HasSimpleTooltip),
            hadSimpleTooltip,
            HasSimpleTooltip);
        NotifyIfChanged(
            nameof(HasNodeSummary),
            hadNodeSummary,
            HasNodeSummary);
        NotifyIfChanged(
            nameof(HasDashboardHover),
            hadDashboardHover,
            HasDashboardHover);
        NotifyIfChanged(
            nameof(HasRawSource),
            hadRawSource,
            HasRawSource);
        NotifyIfChanged(
            nameof(HasProvenance),
            hadProvenance,
            HasProvenance);
        NotifyIfChanged(
            nameof(IsOpenSession),
            wasOpenSession,
            IsOpenSession);
        NotifyIfChanged(
            nameof(IsClosedSession),
            wasClosedSession,
            IsClosedSession);
        NotifyIfChanged(nameof(IsThinking), wasThinking, IsThinking);
        NotifyIfChanged(nameof(IsResponse), wasResponse, IsResponse);
        NotifyIfChanged(nameof(IsPrompt), wasPrompt, IsPrompt);
        NotifyIfChanged(
            nameof(IsToolRequest),
            wasToolRequest,
            IsToolRequest);
        NotifyIfChanged(
            nameof(IsToolResult),
            wasToolResult,
            IsToolResult);
        NotifyIfChanged(nameof(IsMcp), wasMcp, IsMcp);
        NotifyIfChanged(nameof(IsFailure), wasFailure, IsFailure);
        NotifyIfChanged(nameof(IsSkill), wasSkill, IsSkill);
        if (!string.Equals(
            oldHeaderPrefix,
            HeaderPrefix,
            StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(HeaderPrefix));
        }
    }

    // Reconciles a live child collection toward the desired one by StableId:
    // existing nodes are updated in place and recursed, missing nodes are
    // removed, new nodes are inserted, and order is aligned to the desired
    // sequence. Preserves node identity so expansion, selection, and scroll
    // survive rehydration.
    public static void Reconcile(
        ObservableCollection<SessionTreeNodeViewModel> live,
        IReadOnlyList<SessionTreeNodeViewModel> desired)
    {
        HashSet<string> desiredIds = desired
            .Select(static node => node.StableId)
            .ToHashSet(StringComparer.Ordinal);
        for (int index = live.Count - 1; index >= 0; index--)
        {
            if (!desiredIds.Contains(live[index].StableId))
            {
                live.RemoveAt(index);
            }
        }

        Dictionary<string, SessionTreeNodeViewModel> liveById =
            new(StringComparer.Ordinal);
        foreach (SessionTreeNodeViewModel node in live)
        {
            liveById.TryAdd(node.StableId, node);
        }

        for (int index = 0; index < desired.Count; index++)
        {
            SessionTreeNodeViewModel desiredNode = desired[index];
            if (liveById.TryGetValue(
                desiredNode.StableId,
                out SessionTreeNodeViewModel? existing))
            {
                existing.UpdateFrom(desiredNode);
                Reconcile(existing.Children, desiredNode.Children);

                int currentIndex = live.IndexOf(existing);
                if (currentIndex != index && currentIndex >= 0)
                {
                    live.Move(currentIndex, index);
                }
            }
            else
            {
                int insertAt = Math.Min(index, live.Count);
                live.Insert(insertAt, desiredNode);
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsFolder => Kind == SessionTreeNodeKind.Folder;

    public bool IsWorkspace => Kind == SessionTreeNodeKind.Workspace;

    public bool IsSession => Kind == SessionTreeNodeKind.Session;

    public bool IsTurn => Kind == SessionTreeNodeKind.Turn;

    public bool IsEvent => Kind == SessionTreeNodeKind.Event;

    public bool IsParallelGroup => Kind == SessionTreeNodeKind.ParallelGroup;

    public bool IsPlan => Kind == SessionTreeNodeKind.Plan;

    public bool IsPlanActivity => Kind == SessionTreeNodeKind.PlanActivity;

    public bool IsOrphanPlansRoot => Kind == SessionTreeNodeKind.OrphanPlansRoot;

    public bool IsPlanGroup => Kind == SessionTreeNodeKind.PlanGroup;

    public bool HasHoverText => !string.IsNullOrWhiteSpace(HoverText);

    public bool HasRawSource => !string.IsNullOrWhiteSpace(RawSource);

    public bool HasProvenance => Provenance.Count > 0;

    public bool HasNodeSummary => NodeSummary is not null;

    // Session and turn nodes show the rich, scrollable summary in an
    // interactive popup (like the xxxSpy apps' dashboard hover).
    public bool HasDashboardHover => HasNodeSummary;

    // Message-style nodes (responses, thoughts, tool output) show their full
    // text in a plain tooltip; dashboard nodes use the popup instead so the two
    // never appear at once.
    public bool HasSimpleTooltip => HasHoverText && !HasNodeSummary;

    public bool IsOpenSession =>
        IsSession && Session?.LifecycleState == SessionLifecycleState.Open;

    public bool IsClosedSession =>
        IsSession && Session?.LifecycleState == SessionLifecycleState.Closed;

    public bool IsThinking =>
        EventRecord?.Role == ObservationRole.AgentThought ||
        EventRecord?.EventKind == CanonicalEventKind.AssistantThought;

    public bool IsResponse =>
        EventRecord?.Role == ObservationRole.AgentResponse ||
        EventRecord?.EventKind == CanonicalEventKind.AssistantMessage;

    public bool IsPrompt =>
        EventRecord?.Role is ObservationRole.PromptSubmitted or ObservationRole.PromptTransformed ||
        EventRecord?.EventKind is CanonicalEventKind.PromptSubmitted or CanonicalEventKind.PromptTransformed;

    public bool IsToolRequest =>
        EventRecord?.Role == ObservationRole.ToolRequest ||
        EventRecord?.EventKind == CanonicalEventKind.ToolRequested;

    public bool IsToolResult =>
        EventRecord?.Role is ObservationRole.ToolSuccess or ObservationRole.ToolFailure ||
        EventRecord?.EventKind is CanonicalEventKind.ToolSucceeded or CanonicalEventKind.ToolFailed;

    public bool IsMcp =>
        EventRecord?.ToolKind == CanonicalToolKind.Mcp ||
        !string.IsNullOrWhiteSpace(EventRecord?.McpServerName);

    public bool IsFailure =>
        EventRecord?.IsFailure == true ||
        EventRecord?.Tone == ObservationTone.Failure ||
        EventRecord?.Role == ObservationRole.ToolFailure ||
        EventRecord?.EventKind is
            CanonicalEventKind.ToolFailed or
            CanonicalEventKind.RuntimeError or
            CanonicalEventKind.PermissionDenied;

    // A node carries skill evidence when the provider tied a skill to it:
    // Cursor attaches it to the prompt, Claude to the "Skill" tool_use, and
    // Copilot to its skill.* lifecycle events. Keying the hint on evidence
    // presence (not event type) keeps it uniform across providers.
    public bool IsSkill => EventRecord?.Skill is not null;

    public string HeaderPrefix
    {
        get
        {
            if (IsParallelGroup)
            {
                return ParallelGlyph;
            }

            return EventRecord?.Direction switch
            {
                ObservationDirection.Input => InputArrow,
                ObservationDirection.Output => OutputArrow,
                _ => string.Empty
            };
        }
    }

    public string ProviderDisplayName => Provider switch
    {
        HookProvider.Cursor => "Cursor",
        HookProvider.ClaudeCode => "Claude Code",
        HookProvider.GitHubCopilot => "GitHub Copilot",
        _ => "Unknown harness"
    };

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        if (Contains(Header, query) ||
            Contains(Summary, query) ||
            Contains(HoverText, query) ||
            Contains(RawSource, query))
        {
            return true;
        }

        if (Plan is SessionPlanArtifact plan &&
            (Contains(plan.Title, query) ||
                Contains(plan.CurrentMarkdown, query) ||
                Contains(plan.PrimaryPath, query)))
        {
            return true;
        }

        return Details.Any(row =>
                Contains(row.Name, query) || Contains(row.Value, query)) ||
            Provenance.Any(row =>
                Contains(row.Name, query) || Contains(row.Value, query));
    }

    // Restricts a search to the event's native hook name (the payload's
    // hook_event_name), ignoring header, summary, details, provenance, and raw
    // content. Nodes without an underlying event (workspaces, sessions, turns,
    // plans) never match.
    public bool MatchesHookName(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        return Contains(EventRecord?.NativeName, query);
    }

    public static List<SessionTreeNodeViewModel>? FindAncestorPath(
        IEnumerable<SessionTreeNodeViewModel> roots,
        SessionTreeNodeViewModel target)
    {
        List<SessionTreeNodeViewModel> path = [];
        return TryBuildAncestorPath(roots, target, path) ? path : null;
    }

    public static IEnumerable<SessionTreeNodeViewModel> EnumerateDepthFirst(
        IEnumerable<SessionTreeNodeViewModel> roots)
    {
        foreach (SessionTreeNodeViewModel node in roots)
        {
            yield return node;
            foreach (SessionTreeNodeViewModel child in EnumerateDepthFirst(node.Children))
            {
                yield return child;
            }
        }
    }

    private static bool TryBuildAncestorPath(
        IEnumerable<SessionTreeNodeViewModel> nodes,
        SessionTreeNodeViewModel target,
        List<SessionTreeNodeViewModel> path)
    {
        foreach (SessionTreeNodeViewModel node in nodes)
        {
            path.Add(node);
            if (ReferenceEquals(node, target) ||
                TryBuildAncestorPath(node.Children, target, path))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    private void NotifyIfChanged(
        string propertyName,
        bool before,
        bool after)
    {
        if (before != after)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private static bool SummariesEqual(
        NodeSummary? first,
        NodeSummary? second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (first is null || second is null)
        {
            return false;
        }

        return first.IsSession == second.IsSession &&
            first.TurnCount == second.TurnCount &&
            first.AbortedTurnCount == second.AbortedTurnCount &&
            first.IsAborted == second.IsAborted &&
            first.WallTime == second.WallTime &&
            first.ToolCallCount == second.ToolCallCount &&
            first.McpCallCount == second.McpCallCount &&
            first.ThoughtCount == second.ThoughtCount &&
            first.CompactionCount == second.CompactionCount &&
            first.ThoughtDurationMs.Equals(second.ThoughtDurationMs) &&
            first.ThoughtCharacterCount == second.ThoughtCharacterCount &&
            first.InputTokens == second.InputTokens &&
            first.OutputTokens == second.OutputTokens &&
            first.CacheReadTokens == second.CacheReadTokens &&
            first.CacheWriteTokens == second.CacheWriteTokens &&
            string.Equals(
                first.Badge,
                second.Badge,
                StringComparison.Ordinal) &&
            string.Equals(
                first.TokenLine,
                second.TokenLine,
                StringComparison.Ordinal) &&
            StringListsEqual(first.Skills, second.Skills) &&
            StringListsEqual(first.Commands, second.Commands) &&
            DurationRowsEqual(first.Tools, second.Tools) &&
            DurationRowsEqual(first.McpCalls, second.McpCalls) &&
            DurationRowsEqual(first.Thoughts, second.Thoughts) &&
            FileRowsEqual(first.ReadFiles, second.ReadFiles) &&
            FileRowsEqual(first.WrittenFiles, second.WrittenFiles) &&
            FileRowsEqual(first.DeletedFiles, second.DeletedFiles) &&
            SubagentsEqual(first.Subagents, second.Subagents) &&
            KpisEqual(first.Kpis, second.Kpis);
    }

    private static bool StringListsEqual(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second) =>
        first.SequenceEqual(second, StringComparer.Ordinal);

    private static bool DurationRowsEqual(
        IReadOnlyList<CountedDurationRow> first,
        IReadOnlyList<CountedDurationRow> second) =>
        first.Count == second.Count &&
        first.Zip(second).All(static pair =>
            string.Equals(
                pair.First.Name,
                pair.Second.Name,
                StringComparison.Ordinal) &&
            pair.First.Count == pair.Second.Count &&
            pair.First.DurationMs.Equals(pair.Second.DurationMs) &&
            pair.First.Share.Equals(pair.Second.Share));

    private static bool FileRowsEqual(
        IReadOnlyList<FileAccessRow> first,
        IReadOnlyList<FileAccessRow> second) =>
        first.Count == second.Count &&
        first.Zip(second).All(static pair =>
            string.Equals(
                pair.First.FullPath,
                pair.Second.FullPath,
                StringComparison.OrdinalIgnoreCase));

    private static bool SubagentsEqual(
        IReadOnlyList<SubagentSummary> first,
        IReadOnlyList<SubagentSummary> second) =>
        first.Count == second.Count &&
        first.Zip(second).All(static pair =>
            string.Equals(
                pair.First.Type,
                pair.Second.Type,
                StringComparison.Ordinal) &&
            pair.First.DurationMs.Equals(pair.Second.DurationMs) &&
            string.Equals(
                pair.First.Status,
                pair.Second.Status,
                StringComparison.Ordinal) &&
            string.Equals(
                pair.First.TaskPreview,
                pair.Second.TaskPreview,
                StringComparison.Ordinal) &&
            string.Equals(
                pair.First.LastMessagePreview,
                pair.Second.LastMessagePreview,
                StringComparison.Ordinal));

    private static bool KpisEqual(
        IReadOnlyList<KpiItem> first,
        IReadOnlyList<KpiItem> second) =>
        first.Count == second.Count &&
        first.Zip(second).All(static pair =>
            string.Equals(
                pair.First.Label,
                pair.Second.Label,
                StringComparison.Ordinal) &&
            string.Equals(
                pair.First.Value,
                pair.Second.Value,
                StringComparison.Ordinal) &&
            pair.First.IsWarning == pair.Second.IsWarning);

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}
