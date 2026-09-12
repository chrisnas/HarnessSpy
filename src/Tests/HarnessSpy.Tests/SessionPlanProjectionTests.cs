using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Plans;
using HarnessSpy.Wpf.ViewModels;

namespace HarnessSpy.Tests;

public sealed class SessionPlanProjectionTests
{
    [Fact]
    public void BoundPlanAppearsUnderSessionBeforeTurns()
    {
        SessionCatalogEntry session = SessionWithCreatePlan(out string eventId);
        SessionPlanArtifact plan = BoundPlan(
            session.CatalogSessionId,
            eventId,
            observedUpdates: 2);

        SessionTreeNodeViewModel folder = Assert.Single(
            new SessionTreeProjector().Project(
                [session],
                new HashSet<string>(),
                true,
                [plan]));
        SessionTreeNodeViewModel workspace = Assert.Single(folder.Children);
        SessionTreeNodeViewModel sessionNode = Assert.Single(workspace.Children);

        SessionTreeNodeViewModel first = sessionNode.Children[0];
        Assert.True(first.IsPlan);
        Assert.Equal("2 observed updates", first.Summary);
        Assert.Equal(
            $"session:{session.CatalogSessionId}:plan:{plan.CatalogPlanId}",
            first.StableId);
    }

    [Fact]
    public void CreatePlanEventBecomesPlanActivityLinkedToPlan()
    {
        SessionCatalogEntry session = SessionWithCreatePlan(out string eventId);
        SessionPlanArtifact plan = BoundPlan(
            session.CatalogSessionId,
            eventId,
            observedUpdates: 0);

        SessionTreeNodeViewModel sessionNode = new SessionTreeProjector()
            .Project([session], new HashSet<string>(), true, [plan])
            .Single()
            .Children.Single()
            .Children.Single();
        SessionTreeNodeViewModel turnNode = sessionNode.Children
            .Single(static node => node.IsTurn);

        SessionTreeNodeViewModel activityNode = Assert.Single(
            turnNode.Children,
            static node => node.IsPlanActivity);
        Assert.Equal("Plan created", activityNode.Header);
        Assert.Equal(
            $"session:{session.CatalogSessionId}:plan:{plan.CatalogPlanId}",
            activityNode.LinkedPlanNodeId);

        // The generic CreatePlan tool node is not duplicated.
        Assert.DoesNotContain(
            turnNode.Children,
            static node => node.IsEvent &&
                node.EventRecord?.ToolName == "CreatePlan");
    }

    [Fact]
    public void OrphanPlanAppearsUnderOrphanRootGroupedByProviderAndWorkspace()
    {
        SessionPlanArtifact orphan = new()
        {
            CatalogPlanId = "cursor:plan:orphan",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Title = "Orphan plan",
            Workspace = WorkspaceContext.Unknown,
            CurrentMarkdown = "orphan body",
            ObservedUpdateCount = 0
        };

        IReadOnlyList<SessionTreeNodeViewModel> roots = new SessionTreeProjector()
            .Project([], new HashSet<string>(), true, [orphan]);

        SessionTreeNodeViewModel orphanRoot = Assert.Single(
            roots,
            static node => node.IsOrphanPlansRoot);
        SessionTreeNodeViewModel providerGroup = Assert.Single(orphanRoot.Children);
        Assert.True(providerGroup.IsPlanGroup);
        Assert.Equal("Cursor", providerGroup.Header);
        SessionTreeNodeViewModel workspaceGroup = Assert.Single(providerGroup.Children);
        Assert.True(workspaceGroup.IsPlanGroup);
        SessionTreeNodeViewModel planNode = Assert.Single(workspaceGroup.Children);
        Assert.True(planNode.IsPlan);
        Assert.Equal("orphan-plan:cursor:plan:orphan", planNode.StableId);
    }

    [Fact]
    public void PlanNodeMatchesMarkdownSearch()
    {
        SessionPlanArtifact orphan = new()
        {
            CatalogPlanId = "cursor:plan:searchable",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Title = "Searchable plan",
            Workspace = WorkspaceContext.Unknown,
            CurrentMarkdown = "contains uniquemarker token",
            ObservedUpdateCount = 0
        };

        SessionTreeNodeViewModel planNode = new SessionTreeProjector()
            .Project([], new HashSet<string>(), true, [orphan])
            .Single(static node => node.IsOrphanPlansRoot)
            .Children.Single()
            .Children.Single()
            .Children.Single();

        Assert.True(planNode.Matches("uniquemarker"));
    }

    private static SessionCatalogEntry SessionWithCreatePlan(out string eventId)
    {
        SessionSourceProvenance source = new(
            SessionSourceKind.CursorTranscriptJsonl,
            @"C:\session.jsonl",
            "jsonl",
            "{}");
        eventId = "evt-createplan";
        SessionEventRecord prompt = new()
        {
            Id = "evt-prompt",
            NativeName = "user",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Role = ObservationRole.PromptSubmitted,
            EventKind = CanonicalEventKind.PromptSubmitted,
            Order = 0,
            TurnId = "turn-1",
            PromptText = "make a plan",
            Provenance = source
        };
        SessionEventRecord createPlan = new()
        {
            Id = eventId,
            NativeName = "CreatePlan",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Role = ObservationRole.ToolRequest,
            EventKind = CanonicalEventKind.ToolRequested,
            ToolKind = CanonicalToolKind.Task,
            ToolName = "CreatePlan",
            Order = 1,
            TurnId = "turn-1",
            Text = "{}",
            Provenance = source
        };
        SessionTurn turn = new(
            "turn-1",
            1,
            "make a plan",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(2),
            InferenceEvidence.Derived,
            [prompt, createPlan]);
        return new SessionCatalogEntry
        {
            CatalogSessionId = "cursor:s1",
            NativeSessionId = "s1",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Workspace = WorkspaceContext.FromRoot(@"C:\repo"),
            Title = "Session",
            Turns = [turn],
            Sources = [source]
        };
    }

    private static SessionPlanArtifact BoundPlan(
        string sessionId,
        string sourceEventId,
        int observedUpdates)
    {
        SessionPlanActivity activity = new()
        {
            Id = "act-created",
            Kind = SessionPlanActivityKind.Created,
            Provider = HookProvider.Cursor,
            CatalogSessionId = sessionId,
            TurnId = "turn-1",
            SourceEventId = sourceEventId,
            Order = 1,
            Provenance = new SessionSourceProvenance(
                SessionSourceKind.CursorTranscriptJsonl,
                @"C:\session.jsonl",
                "jsonl",
                "{}")
        };
        return new SessionPlanArtifact
        {
            CatalogPlanId = "cursor:plan:foo",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Title = "My plan",
            Workspace = WorkspaceContext.FromRoot(@"C:\repo"),
            PrimaryPath = @"C:\Users\me\.cursor\plans\foo.plan.md",
            CurrentMarkdown = "plan body",
            BoundCatalogSessionId = sessionId,
            BoundTurnId = "turn-1",
            BindingReason = SessionPlanBindingReason.CursorStructuredContentMatch,
            BindingEvidence = InferenceEvidence.Corroborated,
            Activities = [activity],
            ObservedUpdateCount = observedUpdates
        };
    }
}
