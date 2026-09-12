using System.IO;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Wpf.ViewModels;

public sealed class SessionTreeProjector
{
    private readonly SessionNodeSummaryBuilder _summaryBuilder;

    public SessionTreeProjector(SessionNodeSummaryBuilder? summaryBuilder = null)
    {
        _summaryBuilder = summaryBuilder ?? new SessionNodeSummaryBuilder();
    }

    public IReadOnlyList<SessionTreeNodeViewModel> Project(
        IReadOnlyList<SessionCatalogEntry> sessions,
        IReadOnlySet<string> expandedNodeIds,
        bool expandWorkspaceRoots,
        IReadOnlyList<SessionPlanArtifact>? plans = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(expandedNodeIds);

        PlanProjectionContext planContext = BuildPlanContext(plans ?? []);

        // Build one node per workspace (with its sessions), then arrange the
        // rooted workspaces into their real folder hierarchy so shared parent
        // directories become grouping nodes instead of a flat sibling list.
        List<WorkspacePlacement> placements = [];
        IEnumerable<IGrouping<string, SessionCatalogEntry>> workspaceGroups = sessions
            .GroupBy(
                static session => session.Workspace.Key,
                StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, SessionCatalogEntry> group in workspaceGroups)
        {
            SessionCatalogEntry firstSession = group.First();
            WorkspaceContext workspace = firstSession.Workspace;
            string workspaceId = $"workspace:{workspace.Key}";
            SessionCatalogEntry[] workspaceSessions = group
                .OrderByDescending(static session => session.LastActivityAtUtc)
                .ThenBy(static session => session.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static session => session.NativeSessionId, StringComparer.Ordinal)
                .ToArray();

            string? rootPath = RootedWorkspacePath(workspace);
            string header = rootPath is null
                ? workspace.DisplayName
                : LeafName(rootPath) ?? workspace.DisplayName;

            SessionTreeNodeViewModel workspaceNode = new(
                workspaceId,
                header,
                SessionTreeNodeKind.Workspace,
                HookProvider.Unknown,
                BuildWorkspaceSummary(workspaceSessions),
                string.Join(Environment.NewLine, workspace.DisplayRoots),
                workspace,
                details: BuildWorkspaceDetails(workspace, workspaceSessions))
            {
                IsExpanded = expandWorkspaceRoots ||
                    expandedNodeIds.Contains(workspaceId)
            };

            foreach (SessionCatalogEntry session in workspaceSessions)
            {
                workspaceNode.Children.Add(
                    BuildSessionNode(session, expandedNodeIds, planContext));
            }

            DateTimeOffset? maxActivity = workspaceSessions
                .Max(static session => session.LastActivityAtUtc);
            int openCount = workspaceSessions.Count(
                static session => session.LifecycleState == SessionLifecycleState.Open);
            placements.Add(new WorkspacePlacement(
                rootPath,
                workspaceNode,
                maxActivity,
                workspaceSessions.Length,
                openCount));
        }

        FolderTrieNode root = new(string.Empty, string.Empty);
        List<WorkspacePlacement> special = [];
        foreach (WorkspacePlacement placement in placements)
        {
            if (placement.RootPath is null)
            {
                special.Add(placement);
            }
            else
            {
                InsertIntoTrie(root, placement);
            }
        }

        List<ProjectedNode> tops = [];
        foreach (FolderTrieNode child in root.Children.Values)
        {
            tops.Add(ConvertTrie(child, expandedNodeIds, expandWorkspaceRoots));
        }

        foreach (WorkspacePlacement placement in special)
        {
            tops.Add(new ProjectedNode(
                placement.Node,
                placement.MaxActivity,
                placement.Node.Header,
                placement.SessionCount,
                placement.OpenCount));
        }

        // Keep real folders/workspaces first, ordered by recent activity, and
        // push the "Unknown workspace"/"No workspace" buckets to the bottom.
        List<SessionTreeNodeViewModel> roots = tops
            .OrderBy(static item => IsUnknownGroup(item.Node) ? 1 : 0)
            .ThenByDescending(static item => item.MaxActivity)
            .ThenBy(static item => item.SortKey, StringComparer.OrdinalIgnoreCase)
            .Select(static item => item.Node)
            .ToList();

        SessionTreeNodeViewModel? orphanRoot =
            BuildOrphanPlansRoot(planContext.OrphanPlans, expandedNodeIds);
        if (orphanRoot is not null)
        {
            roots.Add(orphanRoot);
        }

        return roots;
    }

    private static bool IsUnknownGroup(SessionTreeNodeViewModel node) =>
        node.Workspace?.Kind is
            WorkspaceContextKind.Unknown or WorkspaceContextKind.NoWorkspace;

    private ProjectedNode ConvertTrie(
        FolderTrieNode node,
        IReadOnlySet<string> expandedNodeIds,
        bool expandDefault)
    {
        if (node.Workspace is WorkspacePlacement placement)
        {
            SessionTreeNodeViewModel workspaceNode = placement.Node;
            ProjectedNode self = new(
                workspaceNode,
                placement.MaxActivity,
                workspaceNode.Header,
                placement.SessionCount,
                placement.OpenCount);

            List<ProjectedNode> nested = OrderChildren(
                node,
                expandedNodeIds,
                expandDefault);
            if (nested.Count == 0)
            {
                // Common case: a leaf workspace with no descendant workspaces.
                return self;
            }

            // This path is both a workspace and an ancestor of other
            // workspaces (for example a repo opened in Cursor that also has
            // Claude sessions rooted in a subfolder). Presenting the sub
            // workspaces as siblings of this one inside a folder keeps them
            // visible instead of buried after this workspace's sessions.
            List<ProjectedNode> members = [self, .. nested];
            return BuildFolderNode(
                node.FullPath,
                node.Segment,
                members,
                expandedNodeIds,
                expandDefault);
        }

        // Pure folder: collapse single pure-folder chains (like VS Code's
        // compact folders) so "a > b > c" reads as one "a\b\c" node.
        List<string> labelParts = [node.Segment];
        FolderTrieNode current = node;
        while (current.Workspace is null && current.Children.Count == 1)
        {
            FolderTrieNode only = current.Children.Values.First();
            if (only.Workspace is not null)
            {
                break;
            }

            labelParts.Add(only.Segment);
            current = only;
        }

        List<ProjectedNode> children = OrderChildren(
            current,
            expandedNodeIds,
            expandDefault);
        return BuildFolderNode(
            current.FullPath,
            JoinSegments(labelParts),
            children,
            expandedNodeIds,
            expandDefault);
    }

    private ProjectedNode BuildFolderNode(
        string fullPath,
        string header,
        IReadOnlyList<ProjectedNode> members,
        IReadOnlySet<string> expandedNodeIds,
        bool expandDefault)
    {
        string folderId = $"folder:{fullPath.ToUpperInvariant()}";
        int folderSessions = members.Sum(static item => item.SessionCount);
        int folderOpen = members.Sum(static item => item.OpenCount);
        DateTimeOffset? folderActivity = members
            .Select(static item => item.MaxActivity)
            .Max();
        List<ProjectedNode> ordered = [.. members
            .OrderByDescending(static item => item.MaxActivity)
            .ThenBy(static item => item.SortKey, StringComparer.OrdinalIgnoreCase)];

        SessionTreeNodeViewModel folderNode = new(
            folderId,
            header,
            SessionTreeNodeKind.Folder,
            HookProvider.Unknown,
            BuildFolderSummary(folderSessions, folderOpen),
            fullPath,
            details: BuildFolderDetails(fullPath, folderSessions, folderOpen))
        {
            IsExpanded = expandDefault || expandedNodeIds.Contains(folderId)
        };
        foreach (ProjectedNode child in ordered)
        {
            folderNode.Children.Add(child.Node);
        }

        return new ProjectedNode(
            folderNode,
            folderActivity,
            header,
            folderSessions,
            folderOpen);
    }

    private List<ProjectedNode> OrderChildren(
        FolderTrieNode node,
        IReadOnlySet<string> expandedNodeIds,
        bool expandDefault)
    {
        return node.Children.Values
            .Select(child => ConvertTrie(child, expandedNodeIds, expandDefault))
            .OrderByDescending(static item => item.MaxActivity)
            .ThenBy(static item => item.SortKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void InsertIntoTrie(
        FolderTrieNode root,
        WorkspacePlacement placement)
    {
        FolderTrieNode current = root;
        foreach ((string segment, string fullPath) in SplitPath(placement.RootPath!))
        {
            if (!current.Children.TryGetValue(
                segment,
                out FolderTrieNode? next))
            {
                next = new FolderTrieNode(segment, fullPath);
                current.Children[segment] = next;
            }

            current = next;
        }

        current.Workspace ??= placement;
    }

    private static string? RootedWorkspacePath(WorkspaceContext workspace)
    {
        if (workspace.Kind != WorkspaceContextKind.Normal ||
            workspace.DisplayRoots.Count != 1)
        {
            return null;
        }

        string candidate = workspace.DisplayRoots[0];
        return Path.IsPathRooted(candidate) ? candidate : null;
    }

    private static string? LeafName(string path)
    {
        IReadOnlyList<(string Segment, string FullPath)> steps = SplitPath(path);
        return steps.Count > 0 ? steps[^1].Segment : null;
    }

    private static IReadOnlyList<(string Segment, string FullPath)> SplitPath(
        string path)
    {
        List<(string, string)> steps = [];
        string root = string.Empty;
        try
        {
            root = Path.GetPathRoot(path) ?? string.Empty;
        }
        catch (ArgumentException)
        {
        }

        string remainder;
        if (root.Length > 0)
        {
            steps.Add((root, root));
            remainder = path.Length > root.Length ? path[root.Length..] : string.Empty;
        }
        else
        {
            remainder = path;
        }

        string accumulated = root;
        foreach (string segment in remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            accumulated = JoinSegments([accumulated, segment]);
            steps.Add((segment, accumulated));
        }

        return steps;
    }

    private static string JoinSegments(IReadOnlyList<string> parts)
    {
        string result = string.Empty;
        foreach (string part in parts)
        {
            if (part.Length == 0)
            {
                continue;
            }

            if (result.Length == 0)
            {
                result = part;
                continue;
            }

            char last = result[^1];
            result = last is '\\' or '/'
                ? result + part
                : result + Path.DirectorySeparatorChar + part;
        }

        return result;
    }

    private static DateTimeOffset? Later(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return first > second ? first : second;
    }

    private static string BuildFolderSummary(int sessionCount, int openCount)
    {
        string summary =
            $"{sessionCount} session{(sessionCount == 1 ? string.Empty : "s")}";
        return openCount > 0
            ? $"{summary} \u00b7 {openCount} open"
            : summary;
    }

    private static IReadOnlyList<SessionDetailRow> BuildFolderDetails(
        string fullPath,
        int sessionCount,
        int openCount)
    {
        return
        [
            new("Folder", fullPath),
            new("Session count", sessionCount.ToString()),
            new("Open sessions", openCount.ToString())
        ];
    }

    private sealed record WorkspacePlacement(
        string? RootPath,
        SessionTreeNodeViewModel Node,
        DateTimeOffset? MaxActivity,
        int SessionCount,
        int OpenCount);

    private sealed record ProjectedNode(
        SessionTreeNodeViewModel Node,
        DateTimeOffset? MaxActivity,
        string SortKey,
        int SessionCount,
        int OpenCount);

    private sealed class FolderTrieNode(string segment, string fullPath)
    {
        public string Segment { get; } = segment;

        public string FullPath { get; } = fullPath;

        public Dictionary<string, FolderTrieNode> Children { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public WorkspacePlacement? Workspace { get; set; }
    }

    private SessionTreeNodeViewModel BuildSessionNode(
        SessionCatalogEntry session,
        IReadOnlySet<string> expandedNodeIds,
        PlanProjectionContext planContext)
    {
        string sessionId = $"session:{session.CatalogSessionId}";
        NodeSummary summary = _summaryBuilder.Build(session);
        SessionTreeNodeViewModel sessionNode = new(
            sessionId,
            SessionHeader(session),
            SessionTreeNodeKind.Session,
            session.Provider,
            summary.Badge,
            SessionHoverText(session),
            session.Workspace,
            session,
            details: BuildSessionDetails(session),
            provenance: BuildSessionProvenance(session),
            rawSource: FirstRawSource(session.Sources),
            nodeSummary: summary)
        {
            IsExpanded = expandedNodeIds.Contains(sessionId)
        };

        // Canonical bound plans appear directly under the session, before turns.
        if (planContext.BoundBySession.TryGetValue(
                session.CatalogSessionId,
                out List<SessionPlanArtifact>? boundPlans))
        {
            foreach (SessionPlanArtifact plan in boundPlans
                         .OrderBy(static item => item.CreatedAtUtc ?? DateTimeOffset.MaxValue)
                         .ThenBy(static item => item.Title, StringComparer.OrdinalIgnoreCase))
            {
                sessionNode.Children.Add(BuildPlanNode(
                    plan,
                    planContext.CanonicalNodeIdByPlan[plan.CatalogPlanId],
                    expandedNodeIds));
            }
        }

        foreach (SessionTurn turn in session.Turns
                     .OrderBy(static item => item.Number)
                     .ThenBy(static item => item.StartedAtUtc)
                     .ThenBy(static item => item.Id, StringComparer.Ordinal))
        {
            sessionNode.Children.Add(
                BuildTurnNode(session, turn, expandedNodeIds, planContext));
        }

        return sessionNode;
    }

    private SessionTreeNodeViewModel BuildTurnNode(
        SessionCatalogEntry session,
        SessionTurn turn,
        IReadOnlySet<string> expandedNodeIds,
        PlanProjectionContext planContext)
    {
        string turnId = $"session:{session.CatalogSessionId}:turn:{turn.Id}";
        NodeSummary summary = _summaryBuilder.Build(turn);
        string prompt = string.IsNullOrWhiteSpace(turn.Prompt)
            ? $"Turn {turn.Number}"
            : Preview(turn.Prompt, 180);
        string turnSummary = string.IsNullOrWhiteSpace(summary.Badge)
            ? $"Turn {turn.Number}"
            : $"Turn {turn.Number} \u00b7 {summary.Badge}";

        SessionTreeNodeViewModel turnNode = new(
            turnId,
            prompt,
            SessionTreeNodeKind.Turn,
            session.Provider,
            turnSummary,
            turn.Prompt,
            session.Workspace,
            session,
            turn,
            details: BuildTurnDetails(turn),
            provenance: BuildTurnProvenance(turn),
            rawSource: FirstRawSource(
                turn.Events.Select(static item => item.Provenance)),
            nodeSummary: summary)
        {
            IsExpanded = expandedNodeIds.Contains(turnId)
        };

        ProjectTurnEvents(
            turnNode,
            session,
            turn,
            expandedNodeIds,
            planContext);
        return turnNode;
    }

    private void ProjectTurnEvents(
        SessionTreeNodeViewModel turnNode,
        SessionCatalogEntry session,
        SessionTurn turn,
        IReadOnlySet<string> expandedNodeIds,
        PlanProjectionContext planContext)
    {
        SessionEventRecord[] records = turn.Events
            .Where(ShouldProjectEvent)
            .OrderBy(static item => item.Order)
            .ThenBy(static item => item.TimestampUtc)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();

        Dictionary<SessionEventRecord, SessionTreeNodeViewModel> nodes = [];
        Dictionary<SessionEventRecord, int> recordIndexes = [];

        for (int index = 0; index < records.Length; index++)
        {
            SessionEventRecord record = records[index];
            nodes[record] = BuildEventNode(
                session,
                turn,
                record,
                expandedNodeIds,
                planContext);
            recordIndexes[record] = index;
        }

        Dictionary<string, List<SessionEventRecord>> toolRequestsById = records
            .Where(IsToolRequest)
            .Where(static item => !string.IsNullOrWhiteSpace(item.ToolCallId))
            .GroupBy(static item => item.ToolCallId!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToList(),
                StringComparer.Ordinal);

        List<SessionTreeNodeViewModel> rootEvents = [];
        foreach (SessionEventRecord record in records)
        {
            SessionEventRecord? parent = FindParentRecord(
                record,
                toolRequestsById,
                recordIndexes);
            SessionTreeNodeViewModel node = nodes[record];
            if (parent is not null)
            {
                nodes[parent].Children.Add(node);
            }
            else
            {
                rootEvents.Add(node);
            }
        }

        foreach (SessionTreeNodeViewModel node in GroupParallelEvents(
                     rootEvents,
                     session,
                     turn,
                     expandedNodeIds))
        {
            turnNode.Children.Add(node);
        }
    }

    private SessionTreeNodeViewModel BuildEventNode(
        SessionCatalogEntry session,
        SessionTurn turn,
        SessionEventRecord record,
        IReadOnlySet<string> expandedNodeIds,
        PlanProjectionContext planContext)
    {
        string eventId =
            $"session:{session.CatalogSessionId}:turn:{turn.Id}:event:{record.Id}";

        // When an event is also a plan create/update, render it as a plan
        // activity node (linked to the canonical plan) instead of a duplicate
        // generic tool node. Any tool-result children remain attached.
        if (planContext.ActivityByEventId.TryGetValue(
                record.Id,
                out PlanActivityLink? link))
        {
            return new SessionTreeNodeViewModel(
                eventId,
                PlanActivityHeader(link.Activity),
                SessionTreeNodeKind.PlanActivity,
                session.Provider,
                EventSummary(record),
                EventHoverText(record) ?? link.Plan.Title,
                session.Workspace,
                session,
                turn,
                record,
                BuildPlanActivityDetails(link),
                BuildEventProvenance(record),
                record.Provenance.RawContent,
                plan: link.Plan,
                planActivity: link.Activity)
            {
                IsExpanded = expandedNodeIds.Contains(eventId),
                LinkedPlanNodeId = link.CanonicalNodeId
            };
        }

        return new SessionTreeNodeViewModel(
            eventId,
            EventHeader(record),
            SessionTreeNodeKind.Event,
            session.Provider,
            EventSummary(record),
            EventHoverText(record),
            session.Workspace,
            session,
            turn,
            record,
            BuildEventDetails(record),
            BuildEventProvenance(record),
            record.Provenance.RawContent)
        {
            IsExpanded = expandedNodeIds.Contains(eventId)
        };
    }

    private IReadOnlyList<SessionTreeNodeViewModel> GroupParallelEvents(
        IReadOnlyList<SessionTreeNodeViewModel> roots,
        SessionCatalogEntry session,
        SessionTurn turn,
        IReadOnlySet<string> expandedNodeIds)
    {
        Dictionary<string, List<SessionTreeNodeViewModel>> groups = roots
            .Where(static node =>
                !string.IsNullOrWhiteSpace(
                    node.EventRecord?.ParallelGroupId))
            .GroupBy(
                static node => node.EventRecord!.ParallelGroupId!,
                StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToList(),
                StringComparer.Ordinal);
        HashSet<string> emittedGroups = new(StringComparer.Ordinal);
        List<SessionTreeNodeViewModel> projected = [];

        foreach (SessionTreeNodeViewModel node in roots)
        {
            string? parallelGroupId = node.EventRecord?.ParallelGroupId;
            if (parallelGroupId is null ||
                !groups.TryGetValue(parallelGroupId, out List<SessionTreeNodeViewModel>? members))
            {
                projected.Add(node);
                continue;
            }

            if (!emittedGroups.Add(parallelGroupId))
            {
                continue;
            }

            string stableId =
                $"session:{session.CatalogSessionId}:turn:{turn.Id}:parallel:{parallelGroupId}";
            double durationMs = members.Sum(
                static item => item.EventRecord?.DurationMs ?? 0);
            SessionTreeNodeViewModel parallelNode = new(
                stableId,
                $"Parallel \u00b7 {members.Count} calls",
                SessionTreeNodeKind.ParallelGroup,
                session.Provider,
                durationMs > 0 ? FormatDuration(durationMs) : string.Empty,
                $"Native parallel group {parallelGroupId}",
                session.Workspace,
                session,
                turn,
                details:
                [
                    new SessionDetailRow("Parallel group ID", parallelGroupId),
                    new SessionDetailRow("Member count", members.Count.ToString()),
                    new SessionDetailRow(
                        "Members",
                        string.Join(", ", members.Select(static item => item.Header)))
                ],
                provenance: BuildParallelProvenance(members))
            {
                IsExpanded = expandedNodeIds.Contains(stableId)
            };

            foreach (SessionTreeNodeViewModel member in members)
            {
                parallelNode.Children.Add(member);
            }

            projected.Add(parallelNode);
        }

        return projected;
    }

    private static SessionEventRecord? FindParentRecord(
        SessionEventRecord record,
        IReadOnlyDictionary<string, List<SessionEventRecord>> toolRequestsById,
        IReadOnlyDictionary<SessionEventRecord, int> recordIndexes)
    {
        if (!IsToolRequest(record) &&
            !string.IsNullOrWhiteSpace(record.ToolCallId) &&
            toolRequestsById.TryGetValue(
                record.ToolCallId,
                out List<SessionEventRecord>? candidates))
        {
            return candidates.LastOrDefault(
                candidate => IsEarlier(candidate, record, recordIndexes));
        }

        return null;
    }

    private static bool IsEarlier(
        SessionEventRecord candidate,
        SessionEventRecord record,
        IReadOnlyDictionary<SessionEventRecord, int> recordIndexes) =>
        !ReferenceEquals(candidate, record) &&
        recordIndexes[candidate] < recordIndexes[record];

    private static PlanProjectionContext BuildPlanContext(
        IReadOnlyList<SessionPlanArtifact> plans)
    {
        Dictionary<string, List<SessionPlanArtifact>> boundBySession =
            new(StringComparer.Ordinal);
        Dictionary<string, string> canonicalById = new(StringComparer.Ordinal);
        Dictionary<string, PlanActivityLink> activityByEventId =
            new(StringComparer.Ordinal);
        List<SessionPlanArtifact> orphans = [];

        foreach (SessionPlanArtifact plan in plans)
        {
            string canonical;
            if (plan.BoundCatalogSessionId is string sessionId)
            {
                canonical = $"session:{sessionId}:plan:{plan.CatalogPlanId}";
                if (!boundBySession.TryGetValue(
                        sessionId,
                        out List<SessionPlanArtifact>? list))
                {
                    list = [];
                    boundBySession[sessionId] = list;
                }

                list.Add(plan);
            }
            else
            {
                canonical = $"orphan-plan:{plan.CatalogPlanId}";
                orphans.Add(plan);
            }

            canonicalById[plan.CatalogPlanId] = canonical;
            foreach (SessionPlanActivity activity in plan.Activities)
            {
                if (!string.IsNullOrWhiteSpace(activity.SourceEventId))
                {
                    activityByEventId[activity.SourceEventId!] =
                        new PlanActivityLink(activity, plan, canonical);
                }
            }
        }

        return new PlanProjectionContext(
            boundBySession,
            activityByEventId,
            canonicalById,
            orphans);
    }

    private SessionTreeNodeViewModel BuildPlanNode(
        SessionPlanArtifact plan,
        string stableId,
        IReadOnlySet<string> expandedNodeIds) =>
        new(
            stableId,
            PlanHeader(plan),
            SessionTreeNodeKind.Plan,
            plan.Provider,
            PlanSummaryBadge(plan),
            PlanHoverText(plan),
            plan.Workspace,
            details: BuildPlanDetails(plan),
            provenance: BuildPlanProvenance(plan),
            rawSource: plan.CurrentMarkdown ?? string.Empty,
            plan: plan)
        {
            IsExpanded = expandedNodeIds.Contains(stableId)
        };

    private SessionTreeNodeViewModel? BuildOrphanPlansRoot(
        IReadOnlyList<SessionPlanArtifact> orphans,
        IReadOnlySet<string> expandedNodeIds)
    {
        if (orphans.Count == 0)
        {
            return null;
        }

        const string rootId = "orphan-plans:root";
        int totalUpdates = orphans.Sum(static plan => plan.ObservedUpdateCount);
        SessionTreeNodeViewModel root = new(
            rootId,
            "Orphan Plans",
            SessionTreeNodeKind.OrphanPlansRoot,
            HookProvider.Unknown,
            $"{orphans.Count} plan{(orphans.Count == 1 ? string.Empty : "s")} " +
            $"\u00b7 {totalUpdates} update{(totalUpdates == 1 ? string.Empty : "s")}",
            details:
            [
                new SessionDetailRow("Orphan plans", orphans.Count.ToString()),
                new SessionDetailRow("Observed updates", totalUpdates.ToString())
            ])
        {
            IsExpanded = true
        };

        foreach (IGrouping<HookProvider, SessionPlanArtifact> providerGroup in orphans
                     .GroupBy(static plan => plan.Provider)
                     .OrderBy(static group => ProviderName(group.Key),
                         StringComparer.OrdinalIgnoreCase))
        {
            string providerId = $"orphan-plans:provider:{providerGroup.Key}";
            SessionTreeNodeViewModel providerNode = new(
                providerId,
                ProviderName(providerGroup.Key),
                SessionTreeNodeKind.PlanGroup,
                providerGroup.Key,
                $"{providerGroup.Count()} plan" +
                $"{(providerGroup.Count() == 1 ? string.Empty : "s")}")
            {
                IsExpanded = expandedNodeIds.Contains(providerId)
            };

            foreach (IGrouping<string, SessionPlanArtifact> workspaceGroup in providerGroup
                         .GroupBy(static plan => plan.Workspace.DisplayName)
                         .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                string workspaceId =
                    $"{providerId}:workspace:{workspaceGroup.Key}";
                SessionTreeNodeViewModel workspaceNode = new(
                    workspaceId,
                    workspaceGroup.Key,
                    SessionTreeNodeKind.PlanGroup,
                    providerGroup.Key,
                    $"{workspaceGroup.Count()} plan" +
                    $"{(workspaceGroup.Count() == 1 ? string.Empty : "s")}")
                {
                    IsExpanded = expandedNodeIds.Contains(workspaceId)
                };

                foreach (SessionPlanArtifact plan in workspaceGroup
                             .OrderByDescending(static item => item.LastModifiedAtUtc)
                             .ThenBy(static item => item.Title, StringComparer.OrdinalIgnoreCase))
                {
                    workspaceNode.Children.Add(BuildPlanNode(
                        plan,
                        $"orphan-plan:{plan.CatalogPlanId}",
                        expandedNodeIds));
                }

                providerNode.Children.Add(workspaceNode);
            }

            root.Children.Add(providerNode);
        }

        return root;
    }

    private static string PlanHeader(SessionPlanArtifact plan) =>
        string.IsNullOrWhiteSpace(plan.Title)
            ? "Plan"
            : Preview(plan.Title, 140);

    private static string PlanSummaryBadge(SessionPlanArtifact plan)
    {
        int count = plan.ObservedUpdateCount;
        string text = count == 1
            ? "1 observed update"
            : $"{count} observed updates";
        return plan.HasIncompleteRevisionHistory
            ? text + " \u00b7 partial"
            : text;
    }

    private static string PlanHoverText(SessionPlanArtifact plan)
    {
        List<string> lines = [ProviderName(plan.Provider)];
        if (!string.IsNullOrWhiteSpace(plan.PrimaryPath))
        {
            lines.Add(plan.PrimaryPath!);
        }

        lines.Add(PlanSummaryBadge(plan));
        return string.Join(Environment.NewLine, lines);
    }

    private static string PlanActivityHeader(SessionPlanActivity activity) =>
        activity.Kind switch
        {
            SessionPlanActivityKind.Created => "Plan created",
            SessionPlanActivityKind.Updated => "Plan updated",
            _ => "Plan referenced"
        };

    private static IReadOnlyList<SessionDetailRow> BuildPlanDetails(
        SessionPlanArtifact plan)
    {
        List<SessionDetailRow> details =
        [
            new("Title", plan.Title),
            new("Plan ID", plan.CatalogPlanId),
            new("Harness", ProviderName(plan.Provider)),
            new("Binding", plan.BindingReason.ToString()),
            new("Binding evidence", plan.BindingEvidence.ToString()),
            new("Bound session", plan.BoundCatalogSessionId ?? "\u2014"),
            new("Bound turn", plan.BoundTurnId ?? "\u2014"),
            new("Observed updates", plan.ObservedUpdateCount.ToString()),
            new(
                "Revision history",
                plan.HasIncompleteRevisionHistory ? "Partial" : "Complete"),
            new("Revision count", plan.Revisions.Count.ToString()),
            new("Workspace", plan.Workspace.DisplayName),
            new("Path", plan.PrimaryPath ?? "\u2014"),
            new("Created", FormatTimestamp(plan.CreatedAtUtc)),
            new("Last modified", FormatTimestamp(plan.LastModifiedAtUtc))
        ];

        for (int index = 0; index < plan.AlternatePaths.Count; index++)
        {
            details.Add(new SessionDetailRow(
                $"Alternate path {index + 1}",
                plan.AlternatePaths[index]));
        }

        foreach (SessionPlanRevision revision in plan.Revisions)
        {
            string kind = revision.Sequence == 0 ? "created" : "updated";
            string materialized = revision.IsMaterialized ? string.Empty : " (opaque)";
            details.Add(new SessionDetailRow(
                $"Revision {revision.Sequence}",
                $"{kind}{materialized} \u00b7 {FormatTimestamp(revision.CapturedAtUtc)}"));
        }

        foreach ((string key, string? value) in plan.Metadata
                     .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            details.Add(new SessionDetailRow($"Metadata: {key}", value ?? "\u2014"));
        }

        return details;
    }

    private static IReadOnlyList<SessionDetailRow> BuildPlanProvenance(
        SessionPlanArtifact plan)
    {
        List<SessionDetailRow> rows = [];
        for (int index = 0; index < plan.Sources.Count; index++)
        {
            SessionSourceProvenance source = plan.Sources[index];
            rows.Add(new SessionDetailRow(
                $"Source {index + 1}",
                $"{source.SourceKind} \u00b7 {source.Format}"));
            rows.Add(new SessionDetailRow(
                $"Source {index + 1} path",
                source.Path));
        }

        return rows;
    }

    private static IReadOnlyList<SessionDetailRow> BuildPlanActivityDetails(
        PlanActivityLink link)
    {
        return
        [
            new("Activity", PlanActivityHeader(link.Activity)),
            new("Plan", link.Plan.Title),
            new("Plan ID", link.Plan.CatalogPlanId),
            new("Evidence", link.Activity.Evidence.ToString()),
            new(
                "Materialized",
                link.Activity.Content is not null ? "Yes" : "No")
        ];
    }

    private sealed record PlanActivityLink(
        SessionPlanActivity Activity,
        SessionPlanArtifact Plan,
        string CanonicalNodeId);

    private sealed record PlanProjectionContext(
        IReadOnlyDictionary<string, List<SessionPlanArtifact>> BoundBySession,
        IReadOnlyDictionary<string, PlanActivityLink> ActivityByEventId,
        IReadOnlyDictionary<string, string> CanonicalNodeIdByPlan,
        IReadOnlyList<SessionPlanArtifact> OrphanPlans);

    private static IReadOnlyList<SessionDetailRow> BuildWorkspaceDetails(
        WorkspaceContext workspace,
        IReadOnlyCollection<SessionCatalogEntry> sessions)
    {
        List<SessionDetailRow> details =
        [
            new("Name", workspace.DisplayName),
            new("Kind", workspace.Kind.ToString()),
            new("Session count", sessions.Count.ToString())
        ];
        for (int index = 0; index < workspace.DisplayRoots.Count; index++)
        {
            details.Add(new SessionDetailRow(
                $"Root {index + 1}",
                workspace.DisplayRoots[index]));
        }

        return details;
    }

    private static IReadOnlyList<SessionDetailRow> BuildSessionDetails(
        SessionCatalogEntry session)
    {
        List<SessionDetailRow> details =
        [
            new("Title", session.Title),
            new("Catalog session ID", session.CatalogSessionId),
            new("Native session ID", session.NativeSessionId),
            new("Harness", ProviderName(session.Provider)),
            new("Surface", session.Surface.ToString()),
            new("Lifecycle", session.LifecycleState.ToString()),
            new("Lifecycle evidence", session.LifecycleEvidence.ToString()),
            new("Selected in harness", session.IsSelectedInHarness ? "Yes" : "No"),
            new("Workspace", session.Workspace.DisplayName),
            new("Started", FormatTimestamp(session.StartedAtUtc)),
            new("Last activity", FormatTimestamp(session.LastActivityAtUtc)),
            new("Model", session.Model ?? "\u2014"),
            new("Mode", session.Mode ?? "\u2014"),
            new("Turn count", session.Turns.Count.ToString()),
            new("Source count", session.Sources.Count.ToString()),
            new("File count", session.Files.Count.ToString())
        ];

        foreach ((string key, string? value) in session.Metadata
                     .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            details.Add(new SessionDetailRow(
                $"Metadata: {key}",
                value ?? "\u2014"));
        }

        return details;
    }

    private static IReadOnlyList<SessionDetailRow> BuildTurnDetails(
        SessionTurn turn)
    {
        return
        [
            new("Turn", turn.Number.ToString()),
            new("Turn ID", turn.Id),
            new("Prompt", turn.Prompt),
            new("Started", FormatTimestamp(turn.StartedAtUtc)),
            new("Ended", FormatTimestamp(turn.EndedAtUtc)),
            new("Evidence", turn.Evidence.ToString()),
            new("Event count", turn.Events.Count.ToString())
        ];
    }

    private static IReadOnlyList<SessionDetailRow> BuildEventDetails(
        SessionEventRecord record)
    {
        List<SessionDetailRow> details =
        [
            new("Event ID", record.Id),
            new("Native name", record.NativeName),
            new("Role", record.Role.ToString()),
            new("Canonical event", record.EventKind.ToString()),
            new("Harness", ProviderName(record.Provider)),
            new("Surface", record.Surface.ToString()),
            new("Direction", record.Direction.ToString()),
            new("Tone", record.Tone.ToString()),
            new("Evidence", record.Evidence.ToString()),
            new("Timestamp", FormatTimestamp(record.TimestampUtc)),
            new("Order", record.Order.ToString()),
            new("Status", record.Status ?? "\u2014"),
            new("Model", record.Model ?? "\u2014"),
            new("Mode", record.Mode ?? "\u2014")
        ];

        AddDetail(details, "Turn ID", record.TurnId);
        AddDetail(details, "Parent ID", record.ParentId);
        AddDetail(details, "Assistant step ID", record.AssistantStepId);
        AddDetail(details, "Parallel group ID", record.ParallelGroupId);
        AddDetail(details, "Tool call ID", record.ToolCallId);
        AddDetail(details, "Tool name", record.ToolName);
        if (record.ToolKind != CanonicalToolKind.Unknown)
        {
            details.Add(new SessionDetailRow(
                "Tool kind",
                record.ToolKind.ToString()));
        }

        AddDetail(details, "MCP server", record.McpServerName);
        AddDetail(details, "MCP tool", record.McpToolName);
        AddDetail(details, "Prompt", record.PromptText);
        AddDetail(details, "Text", record.Text);
        AddDetail(details, "Agent ID", record.AgentId);
        AddDetail(details, "Agent type", record.AgentType);
        AddDetail(details, "Task", record.Task);

        if (record.DurationMs is double duration)
        {
            details.Add(new SessionDetailRow(
                "Duration",
                FormatDuration(duration)));
        }

        details.Add(new SessionDetailRow(
            "Failure",
            record.IsFailure ? "Yes" : "No"));
        details.Add(new SessionDetailRow(
            "Aborted",
            record.IsAborted ? "Yes" : "No"));

        for (int index = 0; index < record.TargetPaths.Count; index++)
        {
            details.Add(new SessionDetailRow(
                $"Target path {index + 1}",
                record.TargetPaths[index]));
        }

        foreach (UsageMeasurement usage in record.UsageMeasurements)
        {
            details.Add(new SessionDetailRow(
                $"Usage: {usage.Name}",
                $"{usage.Value:N0} {usage.Unit} \u00b7 {usage.Scope} \u00b7 {usage.Behavior}"));
        }

        if (record.Skill is SkillEvidence skill)
        {
            details.Add(new SessionDetailRow(
                "Skill",
                $"{skill.SkillName} \u00b7 {skill.Stage} \u00b7 {skill.Evidence}"));
        }

        return details;
    }

    private static IReadOnlyList<SessionDetailRow> BuildSessionProvenance(
        SessionCatalogEntry session)
    {
        List<SessionDetailRow> rows = [];
        for (int index = 0; index < session.Sources.Count; index++)
        {
            SessionSourceProvenance source = session.Sources[index];
            rows.Add(new SessionDetailRow(
                $"Source {index + 1}",
                $"{source.SourceKind} \u00b7 {source.Format}"));
            rows.Add(new SessionDetailRow(
                $"Source {index + 1} path",
                source.Path));
            AddSourceLocation(rows, $"Source {index + 1}", source);
        }

        for (int index = 0; index < session.Files.Count; index++)
        {
            SessionFileBinding file = session.Files[index];
            rows.Add(new SessionDetailRow(
                $"File {index + 1}",
                $"{file.SourceKind} \u00b7 {file.Role} \u00b7 {file.DialectId}"));
            rows.Add(new SessionDetailRow(
                $"File {index + 1} path",
                file.Path));
        }

        return rows;
    }

    private static IReadOnlyList<SessionDetailRow> BuildTurnProvenance(
        SessionTurn turn)
    {
        return turn.Events
            .Select(static item => item.Provenance)
            .GroupBy(
                static source => $"{source.SourceKind}\0{source.Path}",
                StringComparer.OrdinalIgnoreCase)
            .Select((group, index) =>
            {
                SessionSourceProvenance source = group.First();
                return new SessionDetailRow(
                    $"Source {index + 1}",
                    $"{source.SourceKind} \u00b7 {source.Path}");
            })
            .ToArray();
    }

    private static IReadOnlyList<SessionDetailRow> BuildEventProvenance(
        SessionEventRecord record)
    {
        SessionSourceProvenance source = record.Provenance;
        List<SessionDetailRow> rows =
        [
            new("Source kind", source.SourceKind.ToString()),
            new("Path", source.Path),
            new("Format", source.Format)
        ];
        AddSourceLocation(rows, "Source", source);
        return rows;
    }

    private static IReadOnlyList<SessionDetailRow> BuildParallelProvenance(
        IReadOnlyList<SessionTreeNodeViewModel> members)
    {
        return members
            .Select(static item => item.EventRecord?.Provenance)
            .Where(static item => item is not null)
            .Cast<SessionSourceProvenance>()
            .GroupBy(
                static source => $"{source.SourceKind}\0{source.Path}",
                StringComparer.OrdinalIgnoreCase)
            .Select((group, index) =>
            {
                SessionSourceProvenance source = group.First();
                return new SessionDetailRow(
                    $"Source {index + 1}",
                    $"{source.SourceKind} \u00b7 {source.Path}");
            })
            .ToArray();
    }

    private static void AddSourceLocation(
        ICollection<SessionDetailRow> rows,
        string prefix,
        SessionSourceProvenance source)
    {
        if (source.LineNumber is int lineNumber)
        {
            rows.Add(new SessionDetailRow(
                $"{prefix} line",
                lineNumber.ToString()));
        }

        if (source.ByteOffset is long byteOffset)
        {
            rows.Add(new SessionDetailRow(
                $"{prefix} byte offset",
                byteOffset.ToString()));
        }

        AddDetail(rows, $"{prefix} record ID", source.RecordId);
        AddDetail(rows, $"{prefix} parent record ID", source.ParentRecordId);
        AddDetail(rows, $"{prefix} database key", source.DatabaseKey);
        AddDetail(rows, $"{prefix} contract", source.ContractVersion);
    }

    private static string EventHeader(SessionEventRecord record)
    {
        string? content = FirstText(record);
        string contentSuffix = string.IsNullOrWhiteSpace(content)
            ? string.Empty
            : $" \u00b7 {Preview(content, 140)}";

        if (record.Role == ObservationRole.AgentThought ||
            record.EventKind == CanonicalEventKind.AssistantThought)
        {
            return "Thinking" + contentSuffix;
        }

        if (record.Role == ObservationRole.AgentResponse ||
            record.EventKind == CanonicalEventKind.AssistantMessage)
        {
            // The "Response" label is dropped: the node shows the response text
            // directly (blue, non-bold) and the full text lives in the tooltip.
            return string.IsNullOrWhiteSpace(content)
                ? "Response"
                : Preview(content, 140);
        }

        if (IsToolRequest(record))
        {
            return IsMcp(record)
                ? $"MCP \u00b7 {McpName(record)}"
                : $"Tool \u00b7 {ToolName(record)}";
        }

        if (record.Role is ObservationRole.ToolSuccess or ObservationRole.ToolFailure ||
            record.EventKind is CanonicalEventKind.ToolSucceeded or CanonicalEventKind.ToolFailed)
        {
            bool failed = record.IsFailure ||
                record.Role == ObservationRole.ToolFailure ||
                record.EventKind == CanonicalEventKind.ToolFailed;
            if (failed)
            {
                return "Failed" + contentSuffix;
            }

            // The "Result" label is dropped: successful tool results show their
            // output directly, falling back to status only when there is none.
            if (!string.IsNullOrWhiteSpace(content))
            {
                return Preview(content, 140);
            }

            return string.IsNullOrWhiteSpace(record.Status)
                ? "Result"
                : record.Status;
        }

        return record.Role switch
        {
            ObservationRole.PromptTransformed => "Transformed prompt" + contentSuffix,
            ObservationRole.PermissionRequest => "Permission requested" + contentSuffix,
            ObservationRole.PermissionDenied => "Permission denied" + contentSuffix,
            ObservationRole.SubagentStart => "Subagent started" + contentSuffix,
            ObservationRole.SubagentStop => "Subagent completed" + contentSuffix,
            ObservationRole.CompactionStart => "Compaction started",
            ObservationRole.CompactionEnd => "Compaction completed",
            ObservationRole.RuntimeError => "Runtime error" + contentSuffix,
            _ => Humanize(record.NativeName) + contentSuffix
        };
    }

    private static string EventSummary(SessionEventRecord record)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(record.Status))
        {
            parts.Add(record.Status);
        }

        if (record.DurationMs is > 0)
        {
            parts.Add(FormatDuration(record.DurationMs.Value));
        }

        if (record.TimestampUtc is DateTimeOffset timestamp)
        {
            parts.Add(timestamp.ToLocalTime().ToString("HH:mm:ss"));
        }

        return string.Join(" \u00b7 ", parts);
    }

    private static string? EventHoverText(SessionEventRecord record) =>
        FirstText(record) ?? record.Task;

    private static string? FirstText(SessionEventRecord record) =>
        record.Text ?? record.PromptText;

    private static string SessionHeader(SessionCatalogEntry session)
    {
        if (!string.IsNullOrWhiteSpace(session.Title))
        {
            return Preview(session.Title, 140);
        }

        return session.NativeSessionId;
    }

    private static string SessionHoverText(SessionCatalogEntry session)
    {
        List<string> lines =
        [
            ProviderName(session.Provider),
            session.NativeSessionId,
            session.Workspace.DisplayName
        ];
        if (session.LastActivityAtUtc is DateTimeOffset lastActivity)
        {
            lines.Add($"Last activity: {lastActivity.ToLocalTime():g}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildWorkspaceSummary(
        IReadOnlyCollection<SessionCatalogEntry> sessions)
    {
        int openCount = sessions.Count(
            static item => item.LifecycleState == SessionLifecycleState.Open);
        string summary =
            $"{sessions.Count} session{(sessions.Count == 1 ? string.Empty : "s")}";
        return openCount > 0
            ? $"{summary} \u00b7 {openCount} open"
            : summary;
    }

    private static string FirstRawSource(
        IEnumerable<SessionSourceProvenance> sources) =>
        sources
            .Select(static source => source.RawContent)
            .FirstOrDefault(static raw => !string.IsNullOrWhiteSpace(raw)) ??
        string.Empty;

    private static string ProviderName(HookProvider provider) => provider switch
    {
        HookProvider.Cursor => "Cursor",
        HookProvider.ClaudeCode => "Claude Code",
        HookProvider.GitHubCopilot => "GitHub Copilot",
        _ => "Unknown harness"
    };

    private static string ToolName(SessionEventRecord record) =>
        record.ToolName ?? record.ToolKind.ToString();

    private static string McpName(SessionEventRecord record)
    {
        string? server = record.McpServerName;
        string? tool = record.McpToolName ?? record.ToolName;
        return (server, tool) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{server} / {tool}",
            (_, { Length: > 0 }) => tool,
            ({ Length: > 0 }, _) => server,
            _ => "Unknown tool"
        };
    }

    private static bool IsToolRequest(SessionEventRecord record) =>
        record.Role == ObservationRole.ToolRequest ||
        record.EventKind == CanonicalEventKind.ToolRequested;

    private static bool ShouldProjectEvent(SessionEventRecord record)
    {
        if (record.Role == ObservationRole.PromptSubmitted ||
            record.EventKind == CanonicalEventKind.PromptSubmitted)
        {
            return false;
        }

        bool structuralOnly =
            (record.Role is ObservationRole.Generic or ObservationRole.Message) &&
            record.EventKind == CanonicalEventKind.ProviderSpecific &&
            string.IsNullOrWhiteSpace(record.Text) &&
            string.IsNullOrWhiteSpace(record.PromptText) &&
            string.IsNullOrWhiteSpace(record.ToolName) &&
            string.IsNullOrWhiteSpace(record.Status) &&
            record.Skill is null;
        return !structuralOnly;
    }

    private static bool IsMcp(SessionEventRecord record) =>
        record.ToolKind == CanonicalToolKind.Mcp ||
        !string.IsNullOrWhiteSpace(record.McpServerName);

    private static string Preview(string value, int maximumLength)
    {
        string oneLine = value.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= maximumLength
            ? oneLine
            : oneLine[..maximumLength].TrimEnd() + "\u2026";
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Event";
        }

        Span<char> buffer = stackalloc char[value.Length * 2];
        int written = 0;
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (index > 0 &&
                char.IsUpper(current) &&
                !char.IsWhiteSpace(value[index - 1]))
            {
                buffer[written++] = ' ';
            }

            buffer[written++] = current;
        }

        string result = new(buffer[..written]);
        return char.ToUpperInvariant(result[0]) + result[1..];
    }

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value is null
            ? "\u2014"
            : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");

    private static string FormatDuration(double milliseconds) =>
        FormatDuration(TimeSpan.FromMilliseconds(milliseconds));

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        if (duration.TotalSeconds >= 1)
        {
            return $"{duration.TotalSeconds:0.#}s";
        }

        return $"{duration.TotalMilliseconds:0}ms";
    }

    private static void AddDetail(
        ICollection<SessionDetailRow> details,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            details.Add(new SessionDetailRow(name, value));
        }
    }
}
