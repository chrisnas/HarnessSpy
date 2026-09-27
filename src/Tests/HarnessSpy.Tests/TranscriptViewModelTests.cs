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
    public void CursorAttachedSkillEnrichesCanonicalPromptWithoutDuplicateNode()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();
        HookObservation hook = CursorHook(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"prompt":"analyze"}""");
        Apply(viewModel, reconciler, hook);

        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.CursorTranscript);
        HookObservation transcript = Assert.Single(parser.Parse(CursorLine(
            """
            {"role":"user","message":{"content":[{"type":"text","text":"<user_query>analyze</user_query>\n<manually_attached_skills>\nname: demo-skill\nPath: C:/skills/demo-skill/SKILL.md\n</manually_attached_skills>"}]}}
            """,
            "c1")));
        Apply(viewModel, reconciler, transcript);

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel prompt = Assert.Single(turn.Children);
        Assert.Equal("beforeSubmitPrompt", prompt.Observation!.HookEventName);
        Assert.Equal(
            TranscriptRelationshipKind.AttachmentForPrompt,
            Assert.Single(prompt.Evidence).Relationship);
        SkillSummaryRow skill = Assert.Single(turn.NodeSummary!.SkillDetails);
        Assert.Equal("demo-skill", skill.Name);
        Assert.Contains(SkillEvidenceStage.Attached, skill.Stages);
    }

    [Fact]
    public void CursorStepGenerationAndBaseThoughtProduceOneTurnAndNode()
    {
        const string turnId = "9c036778-0b3f-4a42-b0de-b733bf08c012";
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();

        Apply(viewModel, reconciler, CursorHook(
            $$"""{"hook_event_name":"beforeSubmitPrompt","conversation_id":"c1","generation_id":"{{turnId}}","workspace_roots":["C:\\Repo"],"prompt":"analyze"}"""));
        Apply(viewModel, reconciler, CursorHook(
            $$"""{"hook_event_name":"afterAgentThought","conversation_id":"c1","generation_id":"{{turnId}}","workspace_roots":["C:\\Repo"],"text":"inspect the dump","duration_ms":2}"""));
        Apply(viewModel, reconciler, CursorHook(
            $$"""{"hook_event_name":"afterAgentThought","conversation_id":"c1","generation_id":"{{turnId}}-0-9ns5","workspace_roots":["C:\\Repo"],"text":"inspect the dump","duration_ms":2}"""));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        Assert.Equal(turnId, turn.GenerationId);
        Assert.Single(
            Descendants(turn),
            node => node.Observation?.Interpretation.Role ==
                ObservationRole.AgentThought);
    }

    [Fact]
    public void CursorTranscriptInteractionJoinsPromptHookTurn()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();
        Apply(viewModel, reconciler, CursorHook(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"prompt":"analyze"}"""));

        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.CursorTranscript);
        foreach (HookObservation prompt in parser.Parse(CursorLine(
                     """{"role":"user","message":{"content":[{"type":"text","text":"<user_query>analyze</user_query>"}]}}""",
                     "c1",
                     "transcript-cursor-turn:1")))
        {
            Apply(viewModel, reconciler, prompt);
        }

        foreach (HookObservation fragment in parser.Parse(CursorLine(
                     """{"role":"assistant","message":{"content":[{"type":"text","text":"checking files"},{"type":"tool_use","name":"Glob","input":{"target_directory":"C:\\Repo","glob_pattern":"**/*"}}]}}""",
                     "c1",
                     "transcript-cursor-turn:1")))
        {
            Apply(viewModel, reconciler, fragment);
        }

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        Assert.Equal("g1", turn.GenerationId);
        Assert.Contains(
            Descendants(turn),
            node => node.Observation?.IsTranscriptSourced == true &&
                node.Observation.ToolName == "Glob");
        Assert.DoesNotContain(
            Assert.Single(viewModel.Roots).Children.Single().Children,
            node => node.GenerationId == "transcript-cursor-turn:1");
    }

    [Fact]
    public void CursorOrphanMcpHooksFormOneLifecycleAndAdoptTranscript()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();
        HookObservation before = CursorHook(
            """{"hook_event_name":"beforeMCPExecution","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"tool_name":"get_duplicated_strings","mcp_server_name":"dotnet-dstrings","tool_input":"{\"dumpPath\":\"C:\\\\dump.dmp\"}"}""");
        Apply(viewModel, reconciler, before);
        Apply(viewModel, reconciler, CursorHook(
            """{"hook_event_name":"afterMCPExecution","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"tool_name":"get_duplicated_strings","mcp_server_name":"dotnet-dstrings","tool_input":"{\"dumpPath\":\"C:\\\\dump.dmp\"}","duration":25}"""));
        Apply(viewModel, reconciler, CursorHook(
            """{"hook_event_name":"postToolUse","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"tool_name":"MCP:get_duplicated_strings","tool_input":{"dumpPath":"C:\\dump.dmp"},"tool_use_id":"m1","duration":25}"""));

        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.CursorTranscript);
        HookObservation transcript = Assert.Single(parser.Parse(CursorLine(
            """{"role":"assistant","message":{"content":[{"type":"tool_use","name":"CallDynamicTool","input":{"namespace":"user-dotnet-dstrings","toolName":"get_duplicated_strings","arguments":{"dumpPath":"C:\\dump.dmp"}}}]}}""",
            "c1",
            "transcript-cursor-turn:1")));
        Apply(viewModel, reconciler, transcript);

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel owner = Assert.Single(
            turn.Children,
            node => node.Observation?.EventId == before.EventId);
        Assert.Contains(
            owner.Children,
            node => node.Observation?.HookEventName ==
                "afterMCPExecution");
        Assert.Contains(
            owner.Children,
            node => node.Observation?.HookEventName == "postToolUse");
        Assert.Contains(
            owner.Children,
            node => node.Observation?.HookEventName ==
                "CallDynamicTool");
        Assert.DoesNotContain(
            turn.Children,
            node => node.Observation?.HookEventName is
                "afterMCPExecution" or "postToolUse" or "CallDynamicTool");

        // The single MCP lifecycle counts once; the adopted transcript
        // CallDynamicTool does not add a second MCP call or a native tool.
        Assert.Equal(1, turn.NodeSummary!.McpCallCount);
        Assert.Equal(0, turn.NodeSummary!.ToolCallCount);
    }

    [Fact]
    public void CursorOrphanShellHooksFormOneLifecycleAndAdoptTranscript()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();
        HookObservation before = CursorHook(
            """{"hook_event_name":"beforeShellExecution","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"command":"dotnet test","cwd":"C:\\Repo"}""");
        Apply(viewModel, reconciler, before);
        Apply(viewModel, reconciler, CursorHook(
            """{"hook_event_name":"afterShellExecution","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"command":"dotnet test","cwd":"C:\\Repo","output":"passed","duration":25}"""));
        Apply(viewModel, reconciler, CursorHook(
            """{"hook_event_name":"postToolUse","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_input":{"command":"dotnet test","cwd":"C:\\Repo","timeout":30000},"tool_use_id":"s1","duration":25}"""));

        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.CursorTranscript);
        HookObservation transcript = Assert.Single(parser.Parse(CursorLine(
            """{"role":"assistant","message":{"content":[{"type":"tool_use","name":"Shell","input":{"command":"dotnet test","description":"Run tests"}}]}}""",
            "c1",
            "transcript-cursor-turn:1")));
        Apply(viewModel, reconciler, transcript);

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel owner = Assert.Single(
            turn.Children,
            node => node.Observation?.EventId == before.EventId);
        Assert.Contains(
            owner.Children,
            node => node.Observation?.HookEventName ==
                "afterShellExecution");
        Assert.Contains(
            owner.Children,
            node => node.Observation?.HookEventName == "postToolUse");
        Assert.Contains(
            owner.Children,
            node => node.Observation?.HookEventName == "Shell" &&
                node.Observation.IsTranscriptSourced);
    }

    [Fact]
    public void CursorAssistantStepGroupsUnmatchedParallelToolAndBindsText()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();
        HookObservation readHook = CursorHook(
            """{"hook_event_name":"preToolUse","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_input":{"file_path":"C:\\Repo\\Program.cs"},"tool_use_id":"r1"}""");
        Apply(viewModel, reconciler, readHook);

        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.CursorTranscript);
        foreach (HookObservation fragment in parser.Parse(CursorLine(
                     """{"role":"assistant","message":{"content":[{"type":"text","text":"Inspecting source files."},{"type":"tool_use","name":"Read","input":{"path":"C:\\Repo\\Program.cs"}},{"type":"tool_use","name":"Glob","input":{"target_directory":"C:\\Repo","glob_pattern":"**/*.cs"}}]}}""",
                     "c1",
                     "transcript-cursor-turn:1")))
        {
            Apply(viewModel, reconciler, fragment);
        }

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel wave = Assert.Single(
            turn.Children,
            node => node.Kind == TreeNodeKind.ParallelWave);
        Assert.Equal(2, wave.Children.Count);
        TreeNodeViewModel canonicalRead = Assert.Single(
            wave.Children,
            node => node.Observation?.EventId == readHook.EventId);
        Assert.Contains(
            wave.Children,
            node => node.Observation?.ToolName == "Glob" &&
                node.Observation.IsTranscriptSourced);
        Assert.Contains(
            canonicalRead.Evidence,
            evidence => evidence.Observation.Interpretation.Role ==
                ObservationRole.AgentThought);
        Assert.DoesNotContain(
            Descendants(turn),
            node => node.Observation?.IsTranscriptSourced == true &&
                node.Observation.Interpretation.Role ==
                    ObservationRole.AgentThought);
    }

    [Fact]
    public void CursorMixedParallelWaveCountsTranscriptToolWithoutFabricatedDuration()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();
        HookObservation readHook = CursorHook(
            """{"hook_event_name":"preToolUse","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_input":{"file_path":"C:\\Repo\\Program.cs"},"tool_use_id":"r1"}""");
        Apply(viewModel, reconciler, readHook);

        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.CursorTranscript);
        foreach (HookObservation fragment in parser.Parse(CursorLine(
                     """{"role":"assistant","message":{"content":[{"type":"text","text":"Inspecting source files."},{"type":"tool_use","name":"Read","input":{"path":"C:\\Repo\\Program.cs"}},{"type":"tool_use","name":"Glob","input":{"target_directory":"C:\\Repo","glob_pattern":"**/*.cs"}}]}}""",
                     "c1",
                     "transcript-cursor-turn:1")))
        {
            Apply(viewModel, reconciler, fragment);
        }

        HookObservation stop = CursorHook(
            """{"hook_event_name":"stop","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"status":"completed"}""");
        Apply(viewModel, reconciler, stop);

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel wave = Assert.Single(
            turn.Children,
            node => node.Kind == TreeNodeKind.ParallelWave);

        // The transcript-only Glob has no real clock, so a mixed wave must not
        // fabricate an hours-long duration from it.
        Assert.Equal(string.Empty, wave.Summary);

        // The matched hook Read plus the transcript-only Glob count once each.
        Assert.Equal(2, turn.NodeSummary!.ToolCallCount);

        // The transcript-only wave sorts before the turn's stop, never after it.
        TreeNodeViewModel stopNode = Assert.Single(
            turn.Children,
            node => node.Observation?.IsStop == true);
        Assert.True(turn.Children.IndexOf(wave) < turn.Children.IndexOf(stopNode));
    }

    [Fact]
    public void CursorAssistantStepJoinsExistingHookParallelWave()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();
        HookObservation first = CursorHook(
            """{"hook_event_name":"preToolUse","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_input":{"command":"dotnet test A"},"tool_use_id":"a"}""");
        HookObservation second = CursorHook(
            """{"hook_event_name":"preToolUse","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_input":{"command":"dotnet test B"},"tool_use_id":"b"}""");
        Apply(viewModel, reconciler, first);
        Apply(viewModel, reconciler, second);
        Apply(viewModel, reconciler, CursorHook(
            """{"hook_event_name":"postToolUse","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_input":{"command":"dotnet test A"},"tool_use_id":"a","duration":100}"""));
        Apply(viewModel, reconciler, CursorHook(
            """{"hook_event_name":"postToolUse","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_input":{"command":"dotnet test B"},"tool_use_id":"b","duration":100}"""));

        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.CursorTranscript);
        foreach (HookObservation fragment in parser.Parse(CursorLine(
                     """{"role":"assistant","message":{"content":[{"type":"tool_use","name":"Shell","input":{"command":"dotnet test A"}},{"type":"tool_use","name":"Shell","input":{"command":"dotnet test B"}},{"type":"tool_use","name":"Grep","input":{"pattern":"needle","path":"C:\\Repo"}}]}}""",
                     "c1",
                     "transcript-cursor-turn:1")))
        {
            Apply(viewModel, reconciler, fragment);
        }

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel wave = Assert.Single(
            turn.Children,
            node => node.Kind == TreeNodeKind.ParallelWave);
        Assert.Equal(3, wave.Children.Count);
        Assert.Contains(
            wave.Children,
            node => node.Observation?.ToolName == "Grep" &&
                node.Observation.IsTranscriptSourced);
        Assert.DoesNotContain(
            turn.Children,
            node => node.Observation?.ToolName == "Grep");
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
    public void RepeatedSystemPromptIsCoalescedInLiveSession()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(
                DialectIds.CopilotCliTranscript);

        for (int lineNumber = 1; lineNumber <= 2; lineNumber++)
        {
            HookObservation prompt = Assert.Single(parser.Parse(new TranscriptLine(
                $$$"""{"type":"system.message","id":"system-{{{lineNumber}}}","data":{"role":"system","content":"# Prompt\nUse C:\\repo","turnId":"1","interactionId":"i1"}}""",
                "C:/events.jsonl",
                lineNumber * 100,
                lineNumber,
                1,
                TranscriptFileRole.Main,
                HookProvider.GitHubCopilot,
                HookSurface.CopilotCli,
                DialectIds.CopilotCliTranscript,
                "GitHubCopilot:CopilotCli:c1",
                "c1",
                TurnHint: "transcript-interaction:i1")));
            Apply(viewModel, reconciler, prompt);
        }

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel systemPrompt = Assert.Single(
            session.Children,
            static node => node.IsSystemPrompt);
        Assert.Equal("# Prompt\nUse C:\\repo", systemPrompt.ReadableContent);
        Assert.Single(systemPrompt.Evidence);
        Assert.Equal("seen 2 times", systemPrompt.Summary);
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
        TreeNodeViewModel turn = OnlyTurn(viewModel);
        Assert.Equal(1, turn.NodeSummary!.ToolCallCount);
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
    public void ClaudeTranscriptParallelBatchCountsEachToolOnce()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.ClaudeTranscript);
        TranscriptLine transcriptLine = new(
            """
            {"type":"assistant","promptId":"p1","uuid":"step-1","sessionId":"s1","message":{"id":"message-1","role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test A"}},{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"dotnet test B"}}]}}
            """,
            "C:/t.jsonl",
            0,
            1,
            1,
            TranscriptFileRole.Main,
            HookProvider.ClaudeCode,
            HookSurface.ClaudeCode,
            DialectIds.ClaudeTranscript,
            $"{HookProvider.ClaudeCode}:{HookSurface.ClaudeCode}:s1",
            "s1");
        foreach (HookObservation transcriptTool in parser.Parse(transcriptLine))
        {
            Apply(viewModel, reconciler, transcriptTool);
        }

        Apply(viewModel, reconciler, ClaudeHook(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_input":{"command":"dotnet test A"},"tool_use_id":"t1"}"""));
        Apply(viewModel, reconciler, ClaudeHook(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_input":{"command":"dotnet test B"},"tool_use_id":"t2"}"""));
        Apply(viewModel, reconciler, ClaudeHook(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_input":{"command":"dotnet test A"},"tool_use_id":"t1","duration_ms":20}"""));
        Apply(viewModel, reconciler, ClaudeHook(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_input":{"command":"dotnet test B"},"tool_use_id":"t2","duration_ms":20}"""));
        Apply(viewModel, reconciler, ClaudeHook(
            """{"hook_event_name":"PostToolBatch","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_calls":[{"tool_name":"Bash","tool_use_id":"t1"},{"tool_name":"Bash","tool_use_id":"t2"}]}"""));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        Assert.Equal(2, turn.NodeSummary!.ToolCallCount);
        Assert.Equal(40, Assert.Single(turn.NodeSummary.Tools).DurationMs);
        Assert.DoesNotContain(
            turn.Children,
            node => node.Kind == TreeNodeKind.ParallelWave);

        TreeNodeViewModel batch = Assert.Single(
            turn.Children,
            node => node.Observation?.Interpretation.Role ==
                ObservationRole.ToolBatch);
        Assert.Equal(2, batch.Children.Count);
        Assert.Equal(
            2,
            Descendants(turn).Count(node =>
                node.Observation?.Interpretation.Role ==
                    ObservationRole.ToolRequest &&
                node.Observation.IsTranscriptSourced == false));
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
    public void SystemPromptSortsImmediatelyBeforeOwningTurn()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();
        HookObservation prompt = CopilotHook(
            "userPromptSubmitted",
            """{"sessionId":"c1","timestamp":1789809677000,"cwd":"C:\\Repo","prompt":"Inspect memory"}""");
        Apply(viewModel, reconciler, prompt);

        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(
                DialectIds.CopilotCliTranscript);
        HookObservation systemPrompt = Assert.Single(parser.Parse(new TranscriptLine(
            """{"type":"system.message","id":"system-1","data":{"role":"system","content":"# System","interactionId":"i1"}}""",
            "C:/events.jsonl",
            100,
            2,
            1,
            TranscriptFileRole.Main,
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            DialectIds.CopilotCliTranscript,
            "GitHubCopilot:CopilotCli:c1",
            "c1",
            ObservedAtUtc: DateTimeOffset.FromUnixTimeMilliseconds(
                1789809677100),
            TurnHint: "transcript-interaction:i1")));
        Apply(viewModel, reconciler, systemPrompt);

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        Assert.Equal(2, session.Children.Count);
        Assert.True(session.Children[0].IsSystemPrompt);
        Assert.Equal(TreeNodeKind.Generation, session.Children[1].Kind);
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

    [Fact]
    public void ClaudeTranscriptUsageAndCostReachLiveSummary()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.ClaudeTranscript);
        TranscriptLine thinkingLine = new(
            """{"type":"assistant","uuid":"a1","sessionId":"s1","message":{"role":"assistant","content":[{"type":"thinking","thinking":"inspect"}],"usage":{"input_tokens":100,"output_tokens":20,"cache_read_input_tokens":50,"output_tokens_details":{"thinking_tokens":7}}}}""",
            "C:/claude.jsonl",
            0,
            1,
            1,
            TranscriptFileRole.Main,
            HookProvider.ClaudeCode,
            HookSurface.ClaudeCode,
            DialectIds.ClaudeTranscript,
            "ClaudeCode:ClaudeCode:s1",
            "s1",
            TurnHint: "p1");
        Apply(viewModel, reconciler, Assert.Single(parser.Parse(thinkingLine)));

        TranscriptLine costLine = thinkingLine with
        {
            Raw = """{"type":"cost-state","sessionId":"s1","totalCostUSD":0.5,"totalLinesAdded":3,"totalDuration":1200}""",
            ByteOffset = 100,
            LineNumber = 2,
            TurnHint = null
        };
        Apply(viewModel, reconciler, Assert.Single(parser.Parse(costLine)));

        TreeNodeViewModel session = Assert.Single(Assert.Single(viewModel.Roots).Children);
        NodeSummary summary = session.NodeSummary!;
        Assert.Equal(100, summary.InputTokens);
        Assert.Equal(20, summary.OutputTokens);
        Assert.Equal(50, summary.CacheReadTokens);
        Assert.Equal(7, summary.ReasoningTokens);
        Assert.Contains(
            summary.Accounting,
            row => row.Name == "total_cost_usd" && row.Value == 500_000);
    }

    [Fact]
    public void CopilotShutdownAccountingReachesLiveSessionSummary()
    {
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.CopilotCliTranscript);
        HookObservation shutdown = Assert.Single(parser.Parse(new TranscriptLine(
            """{"type":"session.shutdown","id":"end-1","data":{"tokenDetails":{"input":90,"output":12,"reasoningTokens":4},"totalNanoAiu":77,"totalPremiumRequests":1}}""",
            "C:/events.jsonl",
            0,
            1,
            1,
            TranscriptFileRole.Main,
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            DialectIds.CopilotCliTranscript,
            "GitHubCopilot:CopilotCli:c1",
            "c1")));
        Apply(viewModel, reconciler, shutdown);

        TreeNodeViewModel session = Assert.Single(Assert.Single(viewModel.Roots).Children);
        Assert.Equal(90, session.NodeSummary!.InputTokens);
        Assert.Equal(12, session.NodeSummary.OutputTokens);
        Assert.Equal(4, session.NodeSummary.ReasoningTokens);
        Assert.Contains(
            session.NodeSummary.Accounting,
            row => row.Name == "totalNanoAiu" && row.Value == 77);
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

    private static TreeNodeViewModel OnlyTurn(MainWindowViewModel viewModel)
    {
        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        return Assert.Single(
            session.Children,
            child => child.Kind == TreeNodeKind.Generation);
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

    private static TranscriptLine CursorLine(
        string raw,
        string session,
        string? turnHint = null) =>
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
            session,
            TurnHint: turnHint);

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
