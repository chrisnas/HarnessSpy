using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sources;
using HarnessSpy.Wpf.ViewModels;

namespace HarnessSpy.Tests;

// Verifies the shared view model applies reconciler changes: a hook stays the
// canonical node and a matching transcript row attaches as evidence rather than
// creating a duplicate timeline node.
public sealed class TranscriptViewModelTests
{
    [Fact]
    public void AttachEvidenceEnrichesCanonicalHookNodeWithoutDuplicating()
    {
        MainWindowViewModel viewModel = new();

        HookObservation hook = CursorHook(
            """{"hook_event_name":"preToolUse","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"t1"}""");
        viewModel.ApplyObservationChange(new ObservationChange(ObservationChangeKind.Add, hook));

        HookObservation transcript = TranscriptToolUse("c1", "t1", "Read");
        viewModel.ApplyObservationChange(new ObservationChange(
            ObservationChangeKind.AttachEvidence,
            transcript,
            hook.EventId,
            TranscriptRelationshipKind.EvidenceOf));

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel turn = Assert.Single(session.Children, c => c.Kind == TreeNodeKind.Generation);
        TreeNodeViewModel pre = Assert.Single(turn.Children);

        Assert.Equal("preToolUse", pre.Observation!.HookEventName);
        Assert.True(pre.HasEvidence);
        Assert.Equal(
            TranscriptRelationshipKind.EvidenceOf,
            Assert.Single(pre.Evidence).Relationship);
    }

    [Fact]
    public void UnmatchedTranscriptFragmentIsAddedAsItsOwnNode()
    {
        MainWindowViewModel viewModel = new();

        HookObservation thinking = TranscriptThinking("c1");
        viewModel.ApplyObservationChange(new ObservationChange(ObservationChangeKind.Add, thinking));

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        Assert.Contains(
            Descendants(session),
            node => node.Observation?.Interpretation.Role == ObservationRole.AgentThought &&
                    node.Observation.IsTranscriptSourced);
    }

    [Fact]
    public void HookFirstTranscriptToolNestsUnderPreToolUse()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();

        HookObservation pre = ClaudeHook(
            "{\"hook_event_name\":\"PreToolUse\",\"session_id\":\"s1\",\"prompt_id\":\"p1\",\"cwd\":\"C:\\\\Repo\",\"tool_name\":\"Bash\",\"tool_use_id\":\"t1\"}");
        Apply(viewModel, reconciler, pre);

        HookObservation transcriptTool = ClaudeTranscriptTool("s1", "t1", "Bash");
        Apply(viewModel, reconciler, transcriptTool);

        TreeNodeViewModel preNode = OnlyPreToolUse(viewModel);
        TreeNodeViewModel child = Assert.Single(preNode.Children);
        Assert.True(child.Observation!.IsTranscriptSourced);
        Assert.Equal("Bash", child.Observation.ToolName);
        Assert.True(preNode.HasEvidence);
    }

    [Fact]
    public void DuplicateTranscriptRowIsProjectedOnceAcrossReconcilers()
    {
        // Reproduces the reported duplication: the same transcript row reaching
        // the view model twice (e.g. startup replay plus live tailing, each with
        // its own reconciler) must create only one node.
        MainWindowViewModel viewModel = new();

        HookObservation pre = ClaudeHook(
            "{\"hook_event_name\":\"PreToolUse\",\"session_id\":\"s1\",\"prompt_id\":\"p1\",\"cwd\":\"C:\\\\Repo\",\"tool_name\":\"Bash\",\"tool_use_id\":\"t1\"}");
        viewModel.ApplyObservationChange(new ObservationChange(ObservationChangeKind.Add, pre));

        HookObservation transcriptTool = ClaudeTranscriptTool("s1", "t1", "Bash");
        // Apply the exact same transcript observation twice, as two independent
        // reconcilers would each emit an AttachEvidence for it.
        viewModel.ApplyObservationChange(new ObservationChange(
            ObservationChangeKind.AttachEvidence, transcriptTool, pre.EventId, TranscriptRelationshipKind.EvidenceOf));
        viewModel.ApplyObservationChange(new ObservationChange(
            ObservationChangeKind.AttachEvidence, transcriptTool, pre.EventId, TranscriptRelationshipKind.EvidenceOf));

        TreeNodeViewModel preNode = OnlyPreToolUse(viewModel);
        Assert.Single(preNode.Children);
    }

    [Fact]
    public void TranscriptToolResultNestsUnderPreToolUse()
    {
        MainWindowViewModel viewModel = new();

        HookObservation pre = ClaudeHook(
            "{\"hook_event_name\":\"PreToolUse\",\"session_id\":\"s1\",\"prompt_id\":\"p1\",\"cwd\":\"C:\\\\Repo\",\"tool_name\":\"Bash\",\"tool_use_id\":\"t1\"}");
        viewModel.ApplyObservationChange(new ObservationChange(ObservationChangeKind.Add, pre));

        HookObservation toolResult = ClaudeTranscriptToolResult("s1", "t1");
        viewModel.ApplyObservationChange(new ObservationChange(
            ObservationChangeKind.AttachEvidence, toolResult, pre.EventId, TranscriptRelationshipKind.ToolRequestResult));

        TreeNodeViewModel preNode = OnlyPreToolUse(viewModel);
        TreeNodeViewModel child = Assert.Single(preNode.Children);
        Assert.Equal("tool_result", child.Observation!.HookEventName);
        Assert.Equal(ObservationRole.ToolSuccess, child.Observation.Interpretation.Role);
    }

    [Fact]
    public void TranscriptFirstToolIsReparentedWhenHookArrives()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();

        HookObservation transcriptTool = ClaudeTranscriptTool("s1", "t1", "Bash");
        Apply(viewModel, reconciler, transcriptTool);

        HookObservation pre = ClaudeHook(
            "{\"hook_event_name\":\"PreToolUse\",\"session_id\":\"s1\",\"prompt_id\":\"p1\",\"cwd\":\"C:\\\\Repo\",\"tool_name\":\"Bash\",\"tool_use_id\":\"t1\"}");
        Apply(viewModel, reconciler, pre);

        TreeNodeViewModel preNode = OnlyPreToolUse(viewModel);
        TreeNodeViewModel child = Assert.Single(preNode.Children);
        Assert.True(child.Observation!.IsTranscriptSourced);
        Assert.Equal("Bash", child.Observation.ToolName);
        TranscriptEvidence evidence = Assert.Single(preNode.Evidence);
        Assert.Same(transcriptTool, evidence.Observation);
        Assert.True(evidence.Observation.IsTranscriptSourced);
    }

    [Fact]
    public void CopilotTranscriptFragmentJoinsItsDerivedHookTurn()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();

        HookObservation prompt = CopilotHook(
            "userPromptSubmitted",
            """{"sessionId":"c1","timestamp":1789809677000,"cwd":"C:\\Repo","prompt":"Inspect memory"}""");
        Apply(viewModel, reconciler, prompt);

        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.CopilotCliTranscript);
        HookObservation thinking = Assert.Single(parser.Parse(new TranscriptLine(
            """{"type":"assistant.message","id":"m1","timestamp":"2026-09-19T09:21:20Z","data":{"turnId":"7","interactionId":"i1","reasoningOpaque":"opaque"}}""",
            "C:/events.jsonl",
            0,
            1,
            1,
            TranscriptFileRole.Main,
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            DialectIds.CopilotCliTranscript,
            "GitHubCopilot:CopilotCli:c1",
            "c1",
            ObservedAtUtc: DateTimeOffset.Parse("2026-09-19T09:21:20Z"),
            TurnHint: "transcript-interaction:i1")));
        Apply(viewModel, reconciler, thinking);

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel turn = Assert.Single(session.Children);
        Assert.Equal(TreeNodeKind.Generation, turn.Kind);
        Assert.Contains(
            turn.Children,
            node => node.Observation?.Interpretation.Role == ObservationRole.AgentThought);
    }

    [Fact]
    public void CopilotResumeKeepsHistoricalTranscriptSeparateFromCurrentDerivedTurn()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();

        HookObservation currentPrompt = CopilotHook(
            "userPromptSubmitted",
            """{"sessionId":"c1","timestamp":1789811546400,"cwd":"C:\\Repo","prompt":"Current prompt"}""");
        Apply(viewModel, reconciler, currentPrompt);

        HookObservation historical = CopilotThinking(
            "transcript-interaction:old",
            "2026-09-19T09:21:20Z",
            "old");
        HookObservation current = CopilotThinking(
            "transcript-interaction:current",
            "2026-09-19T09:52:30Z",
            "current");
        Apply(viewModel, reconciler, historical);
        Apply(viewModel, reconciler, current);

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel[] turns = session.Children
            .Where(node => node.Kind == TreeNodeKind.Generation)
            .ToArray();
        Assert.Equal(2, turns.Length);

        Assert.Equal("transcript-interaction:old", turns[0].GenerationId);
        Assert.Equal("Turn 1", turns[0].Header);
        Assert.Equal("derived-1", turns[1].GenerationId);
        Assert.StartsWith("Turn 2 · Current prompt", turns[1].Header);
        Assert.Contains(
            turns[1].Children,
            node => node.Observation?.Provenance?.InteractionId == "current");
        Assert.DoesNotContain(
            turns[1].Children,
            node => node.Observation?.Provenance?.InteractionId == "old");
    }

    [Fact]
    public void CopilotLatePromptMergesOnlyNearestPendingTranscriptInteraction()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();

        HookObservation pending = CopilotThinking(
            "transcript-interaction:current",
            "2026-09-19T09:52:30Z",
            "current");
        HookObservation laterPending = CopilotThinking(
            "transcript-interaction:later",
            "2026-09-19T09:52:35Z",
            "later");
        Apply(viewModel, reconciler, pending);
        Apply(viewModel, reconciler, laterPending);

        HookObservation prompt = CopilotHook(
            "userPromptSubmitted",
            """{"sessionId":"c1","timestamp":1789811546400,"cwd":"C:\\Repo","prompt":"Current prompt"}""");
        Apply(viewModel, reconciler, prompt);

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel turn = Assert.Single(
            session.Children,
            node => node.GenerationId == "derived-1");
        Assert.Equal("derived-1", turn.GenerationId);
        Assert.StartsWith("Turn 1 · Current prompt", turn.Header);
        Assert.Contains(
            turn.Children,
            node => node.Observation?.Provenance?.InteractionId == "current");
        Assert.DoesNotContain(
            turn.Children,
            node => node.Observation?.Provenance?.InteractionId == "later");
        Assert.Contains(
            session.Children,
            node => node.GenerationId == "transcript-interaction:later");
    }

    [Fact]
    public void CopilotPromotionRemovesEmptyTranscriptTurnFromSessionSummary()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.CopilotCliTranscript);
        HookObservation request = Assert.Single(parser.Parse(new TranscriptLine(
            """{"type":"assistant.message","id":"m1","data":{"interactionId":"i1","turnId":"0","toolRequests":[{"toolCallId":"call_1","name":"powershell","arguments":{"command":"dotnet test"}}]}}""",
            "C:/events.jsonl",
            0,
            1,
            1,
            TranscriptFileRole.Main,
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            DialectIds.CopilotCliTranscript,
            "GitHubCopilot:CopilotCli:c1",
            "c1",
            TurnHint: "transcript-interaction:i1")));
        Apply(viewModel, reconciler, request);

        HookObservation hook = CopilotHook(
            "preToolUse",
            """{"sessionId":"c1","toolName":"powershell","toolArgs":{"command":"dotnet test"}}""");
        Apply(viewModel, reconciler, hook);

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        Assert.DoesNotContain(
            session.Children,
            node => node.Kind == TreeNodeKind.Generation);
        Assert.Equal(0, session.NodeSummary!.TurnCount);
        TreeNodeViewModel hookNode = Assert.Single(
            session.Children,
            node => node.Observation?.EventId == hook.EventId);
        Assert.Contains(
            hookNode.Children,
            node => node.Observation?.EventId == request.EventId);
    }

    private static void Apply(MainWindowViewModel viewModel, ObservationReconciler reconciler, HookObservation observation)
    {
        foreach (ObservationChange change in reconciler.Reconcile(observation))
        {
            viewModel.ApplyObservationChange(change);
        }
    }

    private static TreeNodeViewModel OnlyPreToolUse(MainWindowViewModel viewModel)
    {
        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel turn = Assert.Single(session.Children, c => c.Kind == TreeNodeKind.Generation);
        return Assert.Single(turn.Children, c => c.Observation?.HookEventName == "PreToolUse");
    }

    private static HookObservation ClaudeTranscriptTool(string session, string toolCallId, string toolName)
    {
        ITranscriptDialectParser parser = TranscriptDialectParserRegistry.Resolve(DialectIds.ClaudeTranscript);
        string raw = "{\"type\":\"assistant\",\"promptId\":\"p1\",\"uuid\":\"x\",\"sessionId\":\"" + session +
            "\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"" + toolCallId +
            "\",\"name\":\"" + toolName + "\",\"input\":{\"command\":\"dotnet test\"}}]}}";
        TranscriptLine line = new(
            raw,
            "C:/t.jsonl",
            0,
            1,
            1,
            TranscriptFileRole.Main,
            HookProvider.ClaudeCode,
            HookSurface.ClaudeCode,
            DialectIds.ClaudeTranscript,
            $"{HookProvider.ClaudeCode}:{HookSurface.ClaudeCode}:{session}",
            session);
        return Assert.Single(parser.Parse(line));
    }

    private static HookObservation ClaudeTranscriptToolResult(string session, string toolCallId)
    {
        ITranscriptDialectParser parser = TranscriptDialectParserRegistry.Resolve(DialectIds.ClaudeTranscript);
        string raw = "{\"type\":\"user\",\"promptId\":\"p1\",\"uuid\":\"r1\",\"sessionId\":\"" + session +
            "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"" + toolCallId +
            "\",\"content\":\"ok\"}]},\"toolUseResult\":{\"stdout\":\"ok\",\"stderr\":\"\",\"interrupted\":false}}";
        TranscriptLine line = new(
            raw,
            "C:/t.jsonl",
            10,
            2,
            1,
            TranscriptFileRole.Main,
            HookProvider.ClaudeCode,
            HookSurface.ClaudeCode,
            DialectIds.ClaudeTranscript,
            $"{HookProvider.ClaudeCode}:{HookSurface.ClaudeCode}:{session}",
            session);
        return Assert.Single(parser.Parse(line));
    }

    private static HookObservation ClaudeHook(string payloadJson)
    {
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        ObservationEnvelope envelope = new(
            ObservationEnvelope.CurrentIngressVersion,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            HookProvider.ClaudeCode,
            HookSurface.ClaudeCode,
            HookSurface.ClaudeCode,
            ObservationSourceKind.Hook,
            null,
            payload.RootElement.GetProperty("hook_event_name").GetString(),
            "test",
            null,
            "valid",
            payload.RootElement.Clone());
        string line = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(HookObservation.TryParse(line, out HookObservation? observation));
        return observation!;
    }

    private static IEnumerable<TreeNodeViewModel> Descendants(TreeNodeViewModel node)
    {
        foreach (TreeNodeViewModel child in node.Children)
        {
            yield return child;
            foreach (TreeNodeViewModel nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static HookObservation TranscriptToolUse(string session, string toolCallId, string toolName)
    {
        ITranscriptDialectParser parser = TranscriptDialectParserRegistry.Resolve(DialectIds.CursorTranscript);
        string raw = "{\"role\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"" +
            toolName + "\",\"input\":{}}]}}";
        return Assert.Single(parser.Parse(CursorLine(raw, session)));
    }

    private static HookObservation TranscriptThinking(string session)
    {
        ITranscriptDialectParser parser = TranscriptDialectParserRegistry.Resolve(DialectIds.CursorTranscript);
        string raw = "{\"role\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"thinking then acting\"},{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{}}]}}";
        return parser.Parse(CursorLine(raw, session))
            .First(o => o.Interpretation.Role == ObservationRole.AgentThought);
    }

    private static TranscriptLine CursorLine(string raw, string session) =>
        new(
            raw,
            "C:/t.jsonl",
            0,
            1,
            1,
            TranscriptFileRole.Main,
            HookProvider.Cursor,
            HookSurface.CursorIde,
            DialectIds.CursorTranscript,
            $"{HookProvider.Cursor}:{HookSurface.CursorIde}:{session}",
            session);

    private static HookObservation CursorHook(string payloadJson)
    {
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        ObservationEnvelope envelope = new(
            ObservationEnvelope.CurrentIngressVersion,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            HookProvider.Cursor,
            HookSurface.CursorIde,
            HookSurface.CursorIde,
            ObservationSourceKind.Hook,
            null,
            payload.RootElement.GetProperty("hook_event_name").GetString(),
            "test",
            null,
            "valid",
            payload.RootElement.Clone());
        string line = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(HookObservation.TryParse(line, out HookObservation? observation));
        return observation!;
    }

    private static HookObservation CopilotHook(string eventName, string payloadJson)
    {
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        ObservationEnvelope envelope = new(
            ObservationEnvelope.CurrentIngressVersion,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            HookSurface.CopilotCli,
            ObservationSourceKind.Hook,
            eventName,
            eventName,
            "test",
            null,
            "valid",
            payload.RootElement.Clone());
        string line = JsonSerializer.Serialize(
            envelope,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(HookObservation.TryParse(line, out HookObservation? observation));
        return observation!;
    }

    private static HookObservation CopilotThinking(
        string turnHint,
        string timestamp,
        string interactionId)
    {
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.CopilotCliTranscript);
        string raw = JsonSerializer.Serialize(new
        {
            type = "assistant.message",
            id = Guid.NewGuid().ToString("N"),
            timestamp,
            data = new
            {
                turnId = "0",
                interactionId,
                reasoningOpaque = "opaque"
            }
        });
        return Assert.Single(parser.Parse(new TranscriptLine(
            raw,
            "C:/events.jsonl",
            StringComparer.Ordinal.GetHashCode(raw),
            1,
            1,
            TranscriptFileRole.Main,
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            DialectIds.CopilotCliTranscript,
            "GitHubCopilot:CopilotCli:c1",
            "c1",
            ObservedAtUtc: DateTimeOffset.Parse(timestamp),
            TurnHint: turnHint)));
    }
}
