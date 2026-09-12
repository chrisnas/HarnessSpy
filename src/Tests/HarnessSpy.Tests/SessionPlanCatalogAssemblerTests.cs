using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Tests;

public sealed class SessionPlanCatalogAssemblerTests
{
    private readonly SessionPlanContentNormalizer _normalizer = new();
    private readonly SessionPlanCatalogAssembler _assembler = new();

    [Fact]
    public void ExplicitPathActivityBindsPlanToSession()
    {
        SessionCatalogEntry session = Session("claude-code:claude-code:s1");
        SessionPlanArtifact artifact = Artifact(
            "claude-code:plan:foo",
            HookProvider.ClaudeCode,
            path: @"C:\plans\foo.md",
            content: "body");
        SessionPlanActivity activity = PathActivity(
            session.CatalogSessionId,
            @"C:\plans\foo.md",
            "turn-1");

        SessionPlanArtifact result = Assert.Single(_assembler.Assemble(
            [session],
            Fragment(artifact, activity)));

        Assert.Equal(session.CatalogSessionId, result.BoundCatalogSessionId);
        Assert.Equal("turn-1", result.BoundTurnId);
        Assert.Equal(SessionPlanBindingReason.ExplicitPlanPath, result.BindingReason);
        Assert.Equal(InferenceEvidence.Observed, result.BindingEvidence);
    }

    [Fact]
    public void StructuredContentMatchBindsWithCorroboratedEvidence()
    {
        SessionCatalogEntry session = Session("cursor:s1");
        SessionPlanArtifact artifact = Artifact(
            "cursor:plan:foo",
            HookProvider.Cursor,
            path: null,
            content: "body",
            structuredKey: "key-1");
        SessionPlanActivity activity = new()
        {
            Id = "act-created",
            Kind = SessionPlanActivityKind.Created,
            Provider = HookProvider.Cursor,
            CatalogSessionId = session.CatalogSessionId,
            TurnId = "turn-1",
            SourceEventId = "evt-1",
            Order = 1,
            Locator = new SessionPlanLocator(StructuredKey: "key-1"),
            Content = _normalizer.Snapshot("body"),
            Provenance = Provenance()
        };

        SessionPlanArtifact result = Assert.Single(_assembler.Assemble(
            [session],
            Fragment(artifact, activity)));

        Assert.Equal(session.CatalogSessionId, result.BoundCatalogSessionId);
        Assert.Equal(
            SessionPlanBindingReason.CursorStructuredContentMatch,
            result.BindingReason);
        Assert.Equal(InferenceEvidence.Corroborated, result.BindingEvidence);
    }

    [Fact]
    public void ConflictingCandidateSessionsRemainOrphan()
    {
        SessionCatalogEntry first = Session("cursor:s1");
        SessionCatalogEntry second = Session("cursor:s2");
        SessionPlanArtifact artifact = Artifact(
            "cursor:plan:foo",
            HookProvider.Cursor,
            path: null,
            content: "body",
            structuredKey: "key-1");
        SessionPlanActivity a1 = StructuredActivity(first.CatalogSessionId, "key-1", "evt-1");
        SessionPlanActivity a2 = StructuredActivity(second.CatalogSessionId, "key-1", "evt-2");

        SessionPlanArtifact result = Assert.Single(_assembler.Assemble(
            [first, second],
            Fragment(artifact, a1, a2)));

        Assert.Null(result.BoundCatalogSessionId);
        Assert.Equal(SessionPlanBindingReason.Ambiguous, result.BindingReason);
        Assert.Equal(InferenceEvidence.Ambiguous, result.BindingEvidence);
    }

    [Fact]
    public void CopilotDirectorySuggestionBindsPlan()
    {
        SessionCatalogEntry session = Session("github-copilot:copilot-cli:s1");
        SessionPlanArtifact artifact = Artifact(
            "github-copilot:plan:s1",
            HookProvider.GitHubCopilot,
            path: @"C:\state\s1\plan.md",
            content: "body") with
        {
            SuggestedCatalogSessionId = session.CatalogSessionId,
            SuggestedBindingReason = SessionPlanBindingReason.CopilotSessionDirectory,
            SuggestedBindingEvidence = InferenceEvidence.Observed
        };

        SessionPlanArtifact result = Assert.Single(_assembler.Assemble(
            [session],
            new SessionPlanCatalogFragment { Artifacts = [artifact] }));

        Assert.Equal(session.CatalogSessionId, result.BoundCatalogSessionId);
        Assert.Equal(
            SessionPlanBindingReason.CopilotSessionDirectory,
            result.BindingReason);
    }

    [Fact]
    public void ReadOnlyReferenceDoesNotBind()
    {
        SessionCatalogEntry session = Session("claude-code:claude-code:s1");
        SessionPlanArtifact artifact = Artifact(
            "claude-code:plan:foo",
            HookProvider.ClaudeCode,
            path: @"C:\plans\foo.md",
            content: "body");
        SessionPlanActivity read = new()
        {
            Id = "act-read",
            Kind = SessionPlanActivityKind.Referenced,
            Provider = HookProvider.ClaudeCode,
            CatalogSessionId = session.CatalogSessionId,
            TurnId = "turn-1",
            SourceEventId = "evt-1",
            Locator = new SessionPlanLocator(Path: @"C:\plans\foo.md"),
            Provenance = Provenance()
        };

        SessionPlanArtifact result = Assert.Single(_assembler.Assemble(
            [session],
            Fragment(artifact, read)));

        Assert.Null(result.BoundCatalogSessionId);
    }

    [Fact]
    public void SessionScopedContentAddsRevisionForSolePlan()
    {
        SessionCatalogEntry session = Session("claude-code:claude-code:s1");
        SessionPlanArtifact artifact = Artifact(
            "claude-code:plan:foo",
            HookProvider.ClaudeCode,
            path: @"C:\plans\foo.md",
            content: "B");
        SessionPlanActivity pathActivity = PathActivity(
            session.CatalogSessionId,
            @"C:\plans\foo.md",
            "turn-1");
        // ExitPlanMode content (path-less) providing an earlier body A.
        SessionPlanActivity exitPlan = new()
        {
            Id = "act-exit",
            Kind = SessionPlanActivityKind.Updated,
            Provider = HookProvider.ClaudeCode,
            CatalogSessionId = session.CatalogSessionId,
            TurnId = "turn-1",
            SourceEventId = "evt-exit",
            Order = 1,
            Locator = new SessionPlanLocator(),
            Content = _normalizer.Snapshot("A"),
            EstablishesOwnership = true,
            Provenance = Provenance()
        };

        SessionPlanArtifact result = Assert.Single(_assembler.Assemble(
            [session],
            Fragment(artifact, pathActivity, exitPlan)));

        Assert.Equal(session.CatalogSessionId, result.BoundCatalogSessionId);
        // A (exit-plan) then B (current file) => one observed update.
        Assert.Equal(1, result.ObservedUpdateCount);
    }

    private static SessionPlanCatalogFragment Fragment(
        SessionPlanArtifact artifact,
        params SessionPlanActivity[] activities) =>
        new()
        {
            Artifacts = [artifact],
            Activities = activities
        };

    private SessionPlanArtifact Artifact(
        string planId,
        HookProvider provider,
        string? path,
        string content,
        string? structuredKey = null) =>
        new()
        {
            CatalogPlanId = planId,
            Provider = provider,
            Surface = HookSurface.Unknown,
            Title = "Plan",
            PrimaryPath = path,
            CurrentMarkdown = content,
            RevisionSnapshot = _normalizer.Snapshot(content),
            CurrentContentHash = _normalizer.Snapshot(content)?.ContentHash,
            StructuredKey = structuredKey,
            Sources = [Provenance()]
        };

    private SessionPlanActivity PathActivity(
        string sessionId,
        string path,
        string turnId) =>
        new()
        {
            Id = $"act-path:{path}",
            Kind = SessionPlanActivityKind.Updated,
            Provider = HookProvider.ClaudeCode,
            CatalogSessionId = sessionId,
            TurnId = turnId,
            SourceEventId = "evt-path",
            Order = 0,
            Locator = new SessionPlanLocator(Path: path),
            Content = null,
            HasOpaqueResult = true,
            Provenance = Provenance()
        };

    private SessionPlanActivity StructuredActivity(
        string sessionId,
        string key,
        string eventId) =>
        new()
        {
            Id = $"act-key:{eventId}",
            Kind = SessionPlanActivityKind.Created,
            Provider = HookProvider.Cursor,
            CatalogSessionId = sessionId,
            TurnId = "turn-1",
            SourceEventId = eventId,
            Locator = new SessionPlanLocator(StructuredKey: key),
            Content = _normalizer.Snapshot("body"),
            Provenance = Provenance()
        };

    private static SessionCatalogEntry Session(string catalogId) =>
        new()
        {
            CatalogSessionId = catalogId,
            NativeSessionId = catalogId,
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Workspace = WorkspaceContext.Unknown,
            Title = catalogId,
            Turns = []
        };

    private static SessionSourceProvenance Provenance() =>
        new(SessionSourceKind.CursorPlanMarkdown, @"C:\plan.md", "markdown", "raw");
}
