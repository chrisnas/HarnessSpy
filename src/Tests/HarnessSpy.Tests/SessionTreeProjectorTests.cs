using System.Collections.ObjectModel;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Wpf.ViewModels;

namespace HarnessSpy.Tests;

public sealed class SessionTreeProjectorTests
{
    [Fact]
    public void ReconcileRehydratesSessionNodeInPlace()
    {
        SessionSourceProvenance source = new(
            SessionSourceKind.CopilotEventsJsonl,
            @"C:\session\events.jsonl",
            "jsonl",
            "{}");
        SessionCatalogEntry skeleton = new()
        {
            CatalogSessionId = "copilot:session-1",
            NativeSessionId = "session-1",
            Provider = HookProvider.GitHubCopilot,
            Surface = HookSurface.CopilotCli,
            Workspace = WorkspaceContext.Unknown,
            Title = "session-1",
            Turns = [],
            Sources = [source]
        };
        SessionTreeProjector projector = new();
        ObservableCollection<SessionTreeNodeViewModel> roots = [];

        SessionTreeNodeViewModel.Reconcile(
            roots,
            projector.Project([skeleton], new HashSet<string>(), true));
        SessionTreeNodeViewModel workspaceNode = Assert.Single(roots);
        SessionTreeNodeViewModel sessionNode = Assert.Single(workspaceNode.Children);
        Assert.Empty(sessionNode.Children);

        SessionEventRecord prompt = Event(
            "prompt",
            ObservationRole.PromptSubmitted,
            CanonicalEventKind.PromptSubmitted,
            source) with
        {
            PromptText = "hello"
        };
        SessionEventRecord response = Event(
            "response",
            ObservationRole.AgentResponse,
            CanonicalEventKind.AssistantMessage,
            source) with
        {
            Text = "hi there"
        };
        SessionTurn turn = new(
            "turn-1",
            1,
            "hello",
            null,
            null,
            InferenceEvidence.Observed,
            [prompt, response]);
        SessionCatalogEntry enriched = skeleton with
        {
            Title = "Real title",
            Turns = [turn]
        };

        SessionTreeNodeViewModel.Reconcile(
            roots,
            projector.Project([enriched], new HashSet<string>(), false));

        SessionTreeNodeViewModel workspaceNode2 = Assert.Single(roots);
        SessionTreeNodeViewModel sessionNode2 = Assert.Single(workspaceNode2.Children);
        Assert.Same(sessionNode, sessionNode2);
        Assert.Equal("Real title", sessionNode2.Header);
        SessionTreeNodeViewModel turnNode = Assert.Single(sessionNode2.Children);
        Assert.True(turnNode.IsTurn);
    }

    [Fact]
    public void ProjectorShowsNestedWorkspaceAsSiblingUnderFolder()
    {
        SessionCatalogEntry parentWorkspace = WorkspaceSession(
            "cursor:parent",
            @"C:\github\chrisnas\HarnessSpy");
        SessionCatalogEntry childWorkspace = WorkspaceSession(
            "claude:child",
            @"C:\github\chrisnas\HarnessSpy\src");

        IReadOnlyList<SessionTreeNodeViewModel> roots = new SessionTreeProjector()
            .Project(
                [parentWorkspace, childWorkspace],
                new HashSet<string>(),
                true);

        SessionTreeNodeViewModel harnessFolder = Assert.Single(
            SessionTreeNodeViewModel.EnumerateDepthFirst(roots),
            node => node.IsFolder && node.Header == "HarnessSpy");
        Assert.Contains(
            harnessFolder.Children,
            node => node.IsWorkspace && node.Header == "HarnessSpy");
        Assert.Contains(
            harnessFolder.Children,
            node => node.IsWorkspace && node.Header == "src");
    }

    [Fact]
    public void ReconcileDoesNotNotifyUnchangedSessionNode()
    {
        SessionSourceProvenance source = new(
            SessionSourceKind.CopilotEventsJsonl,
            @"C:\session\events.jsonl",
            "jsonl",
            "{}");
        SessionCatalogEntry session = new()
        {
            CatalogSessionId = "copilot:unchanged",
            NativeSessionId = "unchanged",
            Provider = HookProvider.GitHubCopilot,
            Surface = HookSurface.CopilotCli,
            Workspace = WorkspaceContext.Unknown,
            Title = "Unchanged",
            Turns = [],
            Sources = [source]
        };
        SessionTreeProjector projector = new();
        ObservableCollection<SessionTreeNodeViewModel> roots = [];
        SessionTreeNodeViewModel.Reconcile(
            roots,
            projector.Project([session], new HashSet<string>(), true));
        SessionTreeNodeViewModel sessionNode =
            Assert.Single(Assert.Single(roots).Children);
        List<string?> notifications = [];
        sessionNode.PropertyChanged += (_, e) =>
            notifications.Add(e.PropertyName);

        SessionTreeNodeViewModel.Reconcile(
            roots,
            projector.Project([session], new HashSet<string>(), false));

        Assert.Empty(notifications);
    }

    [Fact]
    public void ProjectorBuildsWorkspaceSessionTurnAndParallelTools()
    {
        SessionSourceProvenance source = new(
            SessionSourceKind.CopilotEventsJsonl,
            @"C:\session\events.jsonl",
            "jsonl",
            "{}");
        SessionEventRecord prompt = Event(
            "prompt",
            ObservationRole.PromptSubmitted,
            CanonicalEventKind.PromptSubmitted,
            source) with
        {
            PromptText = "inspect two files"
        };
        SessionEventRecord firstTool = Event(
            "tool-1-request",
            ObservationRole.ToolRequest,
            CanonicalEventKind.ToolRequested,
            source) with
        {
            ToolCallId = "tool-1",
            ToolName = "view",
            ToolKind = CanonicalToolKind.FileRead,
            ParallelGroupId = "step-1",
            IsParallelCandidate = true
        };
        SessionEventRecord secondTool = Event(
            "tool-2-request",
            ObservationRole.ToolRequest,
            CanonicalEventKind.ToolRequested,
            source) with
        {
            ToolCallId = "tool-2",
            ToolName = "view",
            ToolKind = CanonicalToolKind.FileRead,
            ParallelGroupId = "step-1",
            IsParallelCandidate = true
        };
        SessionEventRecord result = Event(
            "tool-1-result",
            ObservationRole.ToolSuccess,
            CanonicalEventKind.ToolSucceeded,
            source) with
        {
            ToolCallId = "tool-1",
            ToolName = "view",
            ToolKind = CanonicalToolKind.FileRead,
            Text = "file contents"
        };
        SessionTurn turn = new(
            "turn-1",
            1,
            "inspect two files",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(2),
            InferenceEvidence.Observed,
            [prompt, firstTool, secondTool, result]);
        SessionCatalogEntry session = new()
        {
            CatalogSessionId = "copilot:session-1",
            NativeSessionId = "session-1",
            Provider = HookProvider.GitHubCopilot,
            Surface = HookSurface.CopilotCli,
            Workspace = WorkspaceContext.FromRoot(@"C:\repo"),
            Title = "Inspect files",
            LifecycleState = SessionLifecycleState.Open,
            LifecycleEvidence = InferenceEvidence.Observed,
            Turns = [turn],
            Sources = [source]
        };

        SessionTreeNodeViewModel folder = Assert.Single(
            new SessionTreeProjector().Project([session], new HashSet<string>(), true));
        SessionTreeNodeViewModel workspace = Assert.Single(folder.Children);
        SessionTreeNodeViewModel sessionNode = Assert.Single(workspace.Children);
        SessionTreeNodeViewModel turnNode = Assert.Single(sessionNode.Children);
        SessionTreeNodeViewModel parallel = Assert.Single(
            turnNode.Children,
            static node => node.IsParallelGroup);

        Assert.True(folder.IsFolder);
        Assert.True(workspace.IsWorkspace);
        Assert.True(sessionNode.IsSession);
        Assert.True(sessionNode.IsOpenSession);
        Assert.True(turnNode.IsTurn);
        Assert.Equal("inspect two files", turnNode.Header);
        Assert.Equal(2, parallel.Children.Count);
        SessionTreeNodeViewModel firstToolNode = Assert.Single(
            parallel.Children,
            static node => node.EventRecord?.ToolCallId == "tool-1");
        Assert.Contains(
            firstToolNode.Children,
            static node => node.EventRecord?.Id == "tool-1-result");
    }

    [Fact]
    public void ProjectorDoesNotTreatChronologicalParentIdAsSemanticNesting()
    {
        SessionSourceProvenance source = new(
            SessionSourceKind.CopilotEventsJsonl,
            "events.jsonl",
            "jsonl",
            "{}");
        SessionEventRecord thought = Event(
            "thought",
            ObservationRole.AgentThought,
            CanonicalEventKind.AssistantThought,
            source);
        SessionEventRecord response = Event(
            "response",
            ObservationRole.AgentResponse,
            CanonicalEventKind.AssistantMessage,
            source) with
        {
            ParentId = "thought",
            Text = "done"
        };
        SessionTurn turn = new(
            "turn",
            1,
            "go",
            null,
            null,
            InferenceEvidence.Observed,
            [thought, response]);
        SessionCatalogEntry session = new()
        {
            CatalogSessionId = "copilot:session",
            NativeSessionId = "session",
            Provider = HookProvider.GitHubCopilot,
            Surface = HookSurface.CopilotCli,
            Workspace = WorkspaceContext.Unknown,
            Title = "go",
            Turns = [turn]
        };

        SessionTreeNodeViewModel turnNode = new SessionTreeProjector()
            .Project([session], new HashSet<string>(), true)
            .Single()
            .Children.Single()
            .Children.Single();

        Assert.Equal(2, turnNode.Children.Count);
    }

    [Fact]
    public void ProjectorGroupsWorkspacesUnderSharedFolders()
    {
        SessionCatalogEntry harness = WorkspaceSession(
            "cursor:a",
            @"C:\dev\research\AI\HarnessSpy");
        SessionCatalogEntry talkOne = WorkspaceSession(
            "cursor:b",
            @"C:\Conferences\Talk1");
        SessionCatalogEntry talkTwo = WorkspaceSession(
            "cursor:c",
            @"C:\Conferences\Talk2");

        IReadOnlyList<SessionTreeNodeViewModel> roots = new SessionTreeProjector()
            .Project([harness, talkOne, talkTwo], new HashSet<string>(), true);

        SessionTreeNodeViewModel drive = Assert.Single(roots);
        Assert.True(drive.IsFolder);
        Assert.Equal(@"C:\", drive.Header);
        Assert.Equal(2, drive.Children.Count);

        SessionTreeNodeViewModel conferences = Assert.Single(
            drive.Children,
            static node => node.Header == "Conferences");
        Assert.True(conferences.IsFolder);
        Assert.Equal(2, conferences.Children.Count);
        Assert.All(conferences.Children, static node => Assert.True(node.IsWorkspace));

        SessionTreeNodeViewModel devBranch = Assert.Single(
            drive.Children,
            static node => node.Header == @"dev\research\AI");
        Assert.True(devBranch.IsFolder);
        SessionTreeNodeViewModel harnessWorkspace = Assert.Single(devBranch.Children);
        Assert.True(harnessWorkspace.IsWorkspace);
        Assert.Equal("HarnessSpy", harnessWorkspace.Header);
    }

    [Fact]
    public void MatchesHookNameLimitsSearchToEventNativeName()
    {
        SessionSourceProvenance source = new(
            SessionSourceKind.CopilotEventsJsonl,
            @"C:\session\events.jsonl",
            "jsonl",
            "{}");
        SessionEventRecord tool = Event(
            "beforeShellExecution",
            ObservationRole.ToolRequest,
            CanonicalEventKind.ToolRequested,
            source) with
        {
            ToolCallId = "tool-1",
            ToolName = "view",
            ToolKind = CanonicalToolKind.FileRead
        };
        SessionTurn turn = new(
            "turn-1",
            1,
            "inspect",
            null,
            null,
            InferenceEvidence.Observed,
            [tool]);
        SessionCatalogEntry session = new()
        {
            CatalogSessionId = "copilot:session-1",
            NativeSessionId = "session-1",
            Provider = HookProvider.GitHubCopilot,
            Surface = HookSurface.CopilotCli,
            Workspace = WorkspaceContext.FromRoot(@"C:\repo"),
            Title = "Inspect",
            Turns = [turn],
            Sources = [source]
        };

        IReadOnlyList<SessionTreeNodeViewModel> roots = new SessionTreeProjector()
            .Project([session], new HashSet<string>(), true);

        SessionTreeNodeViewModel eventNode = Assert.Single(
            SessionTreeNodeViewModel.EnumerateDepthFirst(roots),
            static node => node.IsEvent);
        SessionTreeNodeViewModel sessionNode = Assert.Single(
            SessionTreeNodeViewModel.EnumerateDepthFirst(roots),
            static node => node.IsSession);

        // The event's native hook name matches, case-insensitively.
        Assert.True(eventNode.MatchesHookName("beforeShellExecution"));
        Assert.True(eventNode.MatchesHookName("BEFORESHELL"));

        // The tool name shows in the header (so the full search finds it) but it
        // is not the hook name, so the hook-name-only search skips it.
        Assert.Contains("view", eventNode.Header, StringComparison.Ordinal);
        Assert.True(eventNode.Matches("view"));
        Assert.False(eventNode.MatchesHookName("view"));

        // Nodes without an underlying event never match by hook name.
        Assert.False(sessionNode.MatchesHookName("session-1"));
        Assert.False(eventNode.MatchesHookName("   "));
    }

    [Fact]
    public void ProjectorMarksNodesCarryingSkillEvidence()
    {
        SessionSourceProvenance source = new(
            SessionSourceKind.ClaudeTranscriptJsonl,
            @"C:\session\claude.jsonl",
            "jsonl",
            "{}");
        SessionEventRecord skillTool = Event(
            "skill-request",
            ObservationRole.ToolRequest,
            CanonicalEventKind.ToolRequested,
            source) with
        {
            ToolName = "Skill",
            Skill = new SkillEvidence(
                "dotnet-memory-analysis",
                SkillEvidenceStage.Invoked,
                InferenceEvidence.Observed)
        };
        SessionEventRecord plainTool = Event(
            "read-request",
            ObservationRole.ToolRequest,
            CanonicalEventKind.ToolRequested,
            source) with
        {
            ToolName = "Read",
            ToolKind = CanonicalToolKind.FileRead
        };
        SessionTurn turn = new(
            "turn-1",
            1,
            "find leaks",
            null,
            null,
            InferenceEvidence.Observed,
            [skillTool, plainTool]);
        SessionCatalogEntry session = new()
        {
            CatalogSessionId = "claude:session-1",
            NativeSessionId = "session-1",
            Provider = HookProvider.ClaudeCode,
            Surface = HookSurface.ClaudeCode,
            Workspace = WorkspaceContext.FromRoot(@"C:\repo"),
            Title = "Find leaks",
            Turns = [turn],
            Sources = [source]
        };

        IReadOnlyList<SessionTreeNodeViewModel> roots = new SessionTreeProjector()
            .Project([session], new HashSet<string>(), true);

        SessionTreeNodeViewModel skillNode = Assert.Single(
            SessionTreeNodeViewModel.EnumerateDepthFirst(roots),
            static node => node.EventRecord?.Id == "skill-request");
        SessionTreeNodeViewModel plainNode = Assert.Single(
            SessionTreeNodeViewModel.EnumerateDepthFirst(roots),
            static node => node.EventRecord?.Id == "read-request");

        Assert.True(skillNode.IsSkill);
        Assert.False(plainNode.IsSkill);
    }

    private static SessionCatalogEntry WorkspaceSession(
        string catalogId,
        string root)
    {
        SessionSourceProvenance source = new(
            SessionSourceKind.CopilotEventsJsonl,
            @"C:\session\events.jsonl",
            "jsonl",
            "{}");
        return new SessionCatalogEntry
        {
            CatalogSessionId = catalogId,
            NativeSessionId = catalogId,
            Provider = HookProvider.GitHubCopilot,
            Surface = HookSurface.CopilotCli,
            Workspace = WorkspaceContext.FromRoot(root),
            Title = catalogId,
            Turns = [],
            Sources = [source]
        };
    }

    private static SessionEventRecord Event(
        string id,
        ObservationRole role,
        CanonicalEventKind kind,
        SessionSourceProvenance source)
    {
        return new SessionEventRecord
        {
            Id = id,
            NativeName = id,
            Provider = HookProvider.GitHubCopilot,
            Surface = HookSurface.CopilotCli,
            Role = role,
            EventKind = kind,
            Provenance = source
        };
    }
}
