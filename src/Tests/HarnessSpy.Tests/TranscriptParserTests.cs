using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sources;

namespace HarnessSpy.Tests;

// Verifies each provider transcript dialect parser against a redacted fixture:
// exact native names, provenance, MCP metadata, opaque thinking, and skill
// evidence. Malformed/metadata rows never throw and never fabricate nodes.
public sealed class TranscriptParserTests
{
    [Fact]
    public void CursorParserPreservesNativeNamesAndSkillAndMcp()
    {
        List<HookObservation> observations = ParseFixture(
            DialectIds.CursorTranscript,
            HookProvider.Cursor,
            HookSurface.CursorIde,
            Fixture("Cursor", "transcript-sample.jsonl"));

        // Native tool names are preserved verbatim, including transcript-only ones.
        string[] toolNames = [.. observations
            .Where(o => o.Interpretation.Role == ObservationRole.ToolRequest)
            .Select(o => o.ToolName!)];
        Assert.Contains("Read", toolNames);
        Assert.Contains("Glob", toolNames);
        Assert.Contains("StrReplace", toolNames);
        Assert.Contains("TodoWrite", toolNames);
        Assert.Contains("GetDynamicTools", toolNames);
        Assert.Contains("CallDynamicTool", toolNames);

        // The manually attached skill is detected as attachment-stage evidence.
        HookObservation prompt = observations.First(o => o.Interpretation.Role == ObservationRole.PromptSubmitted);
        Assert.Equal("demo-skill", prompt.Interpretation.Skill!.SkillName);
        Assert.Equal(SkillEvidenceStage.Attached, prompt.Interpretation.Skill!.Stage);
        Assert.Equal("C:/skills/demo-skill/SKILL.md", prompt.Interpretation.Skill!.SourcePath);
        Assert.Contains("/demo-skill", prompt.SlashCommands);

        // The dynamic-tool call is toned as MCP.
        HookObservation dynamicCall = observations.First(o => o.ToolName == "CallDynamicTool");
        Assert.Equal(CanonicalToolKind.Mcp, dynamicCall.ToolKind);
        Assert.Equal("user-pstacks", dynamicCall.McpServerName);
        Assert.Equal("get_parallel_stacks", dynamicCall.McpToolName);
        Assert.True(dynamicCall.Interpretation.ExcludeFromSummary);
        Assert.NotNull(dynamicCall.AssistantStepId);

        // Every fragment carries transcript provenance with the dialect.
        Assert.All(observations, o =>
        {
            Assert.True(o.IsTranscriptSourced);
            Assert.Equal(DialectIds.CursorTranscript, o.Provenance!.DialectId);
            Assert.Equal("transcript-cursor-turn:1", o.GenerationId);
        });

        // The turn_ended row becomes a TurnStop.
        Assert.Contains(observations, o => o.Interpretation.Role == ObservationRole.TurnStop);
    }

    [Fact]
    public void CursorParserDropsOpaqueRedactionPlaceholder()
    {
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(
                DialectIds.CursorTranscript);
        IReadOnlyList<HookObservation> observations = parser.Parse(new TranscriptLine(
            """{"role":"assistant","message":{"content":[{"type":"text","text":"[REDACTED]"},{"type":"tool_use","name":"Shell","input":{"command":"dotnet test"}}]}}""",
            "C:/cursor.jsonl",
            0,
            1,
            1,
            TranscriptFileRole.Main,
            HookProvider.Cursor,
            HookSurface.CursorIde,
            DialectIds.CursorTranscript,
            "Cursor:CursorIde:c1",
            "c1",
            TurnHint: "transcript-cursor-turn:1"));

        HookObservation tool = Assert.Single(observations);
        Assert.Equal("Shell", tool.ToolName);
    }

    [Fact]
    public void ClaudeParserExtractsOpaqueThinkingUsageAndExactToolIds()
    {
        List<HookObservation> observations = ParseFixture(
            DialectIds.ClaudeTranscript,
            HookProvider.ClaudeCode,
            HookSurface.ClaudeCode,
            Fixture("ClaudeCode", "transcript-main-sample.jsonl"));

        HookObservation thinking = observations.First(o => o.Interpretation.Role == ObservationRole.AgentThought);
        Assert.Equal(InferenceEvidence.Opaque, thinking.Interpretation.Evidence);
        Assert.Contains(
            thinking.Interpretation.UsageMeasurements,
            m => m.Name == "thinking_tokens" && m.Value == 7);
        Assert.Contains(
            thinking.Interpretation.UsageMeasurements,
            m => m.Name == "input_tokens" && m.Behavior == UsageBehavior.CumulativeSnapshot);

        HookObservation toolUse = observations.First(o =>
            o.Interpretation.Role == ObservationRole.ToolRequest && o.ToolName == "Bash");
        Assert.Equal("t1", toolUse.ToolUseId);
        Assert.True(toolUse.IsEnrichmentOnly);

        HookObservation toolResult = observations.First(o => o.Interpretation.Role == ObservationRole.ToolSuccess);
        Assert.Equal("t1", toolResult.ToolUseId);

        // High-value duration/accounting rows become metadata-only evidence;
        // low-value mode rows remain raw capture only.
        Assert.Contains(observations, o => o.HookEventName == "turn_duration" && o.IsMetadataOnly);
        Assert.Contains(observations, o => o.HookEventName == "cost-state" && o.IsMetadataOnly);
        Assert.DoesNotContain(observations, o => o.HookEventName == "mode");
    }

    [Fact]
    public void ClaudeSubagentParserKeepsSidechainToolIds()
    {
        List<HookObservation> observations = ParseFixture(
            DialectIds.ClaudeTranscript,
            HookProvider.ClaudeCode,
            HookSurface.ClaudeCode,
            Fixture("ClaudeCode", "transcript-subagent-sample.jsonl"),
            role: TranscriptFileRole.Subagent,
            agentId: "a1");

        HookObservation toolUse = observations.First(o =>
            o.Interpretation.Role == ObservationRole.ToolRequest && o.ToolName == "Grep");
        Assert.Equal("st1", toolUse.ToolUseId);
    }

    [Fact]
    public void ClaudeParserProjectsReadableSystemPromptSnapshot()
    {
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(
                DialectIds.ClaudeTranscript);
        TranscriptLine line = new(
            """{"parentUuid":"parent-1","attachment":{"type":"prompt_snapshot","systemPrompt":["\n# First\r\nUse C:\\repo and regex \\\\d+\n","## Second\nKeep  two spaces"]},"type":"attachment","uuid":"snapshot-1","sessionId":"s1"}""",
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
            TurnHint: "prompt-1");

        HookObservation snapshot = Assert.Single(parser.Parse(line));

        Assert.Equal(ObservationRole.SystemPrompt, snapshot.Interpretation.Role);
        Assert.Equal(
            CanonicalEventKind.SystemPromptSnapshot,
            snapshot.EventKind);
        Assert.Equal(ObservationScope.Session, snapshot.Interpretation.Scope);
        Assert.Equal(
            "# First\nUse C:\\repo and regex \\\\d+\n\n" +
            "## Second\nKeep  two spaces",
            snapshot.Text);
        Assert.Equal(2, snapshot.SystemPrompt!.PartCount);
        Assert.Equal(64, snapshot.SystemPrompt.ContentHash.Length);
        Assert.Equal("prompt-1", snapshot.Provenance!.TurnId);
        Assert.Null(snapshot.Interpretation.HoverText);
    }

    [Fact]
    public void CopilotParserClassifiesMcpFromServerMetadata()
    {
        List<HookObservation> observations = ParseFixture(
            DialectIds.CopilotCliTranscript,
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            Fixture("CopilotCli", "transcript-events-sample.jsonl"));

        HookObservation request = observations.First(o =>
            o.Interpretation.Role == ObservationRole.ToolRequest);
        Assert.Equal("dotnet-dstrings", request.McpServerName);
        Assert.Equal(CanonicalToolKind.Mcp, request.ToolKind);
        Assert.Equal("call_1", request.ToolUseId);
        Assert.Equal("transcript-derived-1", request.GenerationId);

        // The flattened <server>-<tool> name is never split on the hyphen.
        Assert.Equal("dotnet-dstrings-get_duplicated_strings", request.ToolName);

        Assert.Contains(observations, o =>
            o.Interpretation.Role == ObservationRole.AgentThought &&
            o.Interpretation.Evidence == InferenceEvidence.Opaque);
        Assert.Contains(observations, o => o.Interpretation.Role == ObservationRole.PermissionRequest);
        Assert.Contains(
            observations,
            o =>
                o.HookEventName == "permission.completed" &&
                o.ToolKind == CanonicalToolKind.Mcp);
    }

    [Fact]
    public void CopilotParserProjectsOnlyConversationSystemMessages()
    {
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(
                DialectIds.CopilotCliTranscript);
        HookObservation snapshot = Assert.Single(parser.Parse(CopilotLine(
            """{"type":"system.message","id":"system-1","data":{"role":"system","content":"\n# Copilot\r\nUse C:\\repo and regex \\\\d+\n","turnId":"1","interactionId":"i1"}}""",
            "transcript-interaction:i1")));

        Assert.Equal(ObservationRole.SystemPrompt, snapshot.Interpretation.Role);
        Assert.Equal(
            CanonicalEventKind.SystemPromptSnapshot,
            snapshot.EventKind);
        Assert.Equal(ObservationScope.Session, snapshot.Interpretation.Scope);
        Assert.Equal(
            "# Copilot\nUse C:\\repo and regex \\\\d+",
            snapshot.Text);
        Assert.Equal(1, snapshot.SystemPrompt!.PartCount);

        IReadOnlyList<HookObservation> auxiliary = parser.Parse(CopilotLine(
            """{"type":"model.messages_snapshot","id":"model-1","data":{"messages":[{"role":"system","content":"Generate a title"}]}}""",
            "transcript-interaction:i1"));
        Assert.Empty(auxiliary);
    }

    [Fact]
    public void CopilotParserReadsVersion1086ExecutionAndPermissionFields()
    {
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.CopilotCliTranscript);

        HookObservation execution = Assert.Single(parser.Parse(CopilotLine(
            """{"type":"tool.execution_start","id":"e1","parentId":"p1","data":{"turnId":"4","interactionId":"i1","toolCallId":"call_1","toolName":"powershell","arguments":{"command":"dotnet test"}}}""",
            "derived-1")));
        Assert.Equal("powershell", execution.ToolName);
        Assert.Equal(CanonicalToolKind.Shell, execution.ToolKind);
        Assert.Equal("call_1", execution.ToolUseId);
        Assert.Equal("derived-1", execution.GenerationId);
        Assert.Equal("4", execution.Provenance!.TurnId);
        Assert.Equal("i1", execution.Provenance.InteractionId);

        HookObservation requested = Assert.Single(parser.Parse(CopilotLine(
            """{"type":"permission.requested","id":"e2","parentId":"e1","data":{"turnId":"4","interactionId":"i1","permissionRequest":{"kind":"shell","toolCallId":"call_1"}}}""",
            "derived-1")));
        Assert.Equal("call_1", requested.ToolUseId);
        Assert.Equal("derived-1", requested.GenerationId);

        HookObservation completed = Assert.Single(parser.Parse(CopilotLine(
            """{"type":"permission.completed","id":"e3","parentId":"e2","data":{"turnId":"4","interactionId":"i1","toolCallId":"call_1","kind":"approved"}}""",
            "derived-1")));
        Assert.Equal("call_1", completed.ToolUseId);
        Assert.Equal("derived-1", completed.GenerationId);

        HookObservation aborted = Assert.Single(parser.Parse(CopilotLine(
            """{"type":"tool.execution_complete","id":"e4","data":{"turnId":"4","interactionId":"i1","toolCallId":"call_1","toolName":"powershell","status":"cancelled"}}""",
            "derived-1")));
        Assert.Equal(ObservationRole.ToolFailure, aborted.Interpretation.Role);
        Assert.Equal("cancelled", aborted.Status);
    }

    [Fact]
    public void ClaudeEnrichmentFixtureProjectsSkillsDurationAndAccounting()
    {
        List<HookObservation> observations = ParseFixture(
            DialectIds.ClaudeTranscript,
            HookProvider.ClaudeCode,
            HookSurface.ClaudeCode,
            Fixture("ClaudeCode", "transcript-enrichment-sample.jsonl"));

        Assert.Contains(
            observations,
            observation =>
                observation.Interpretation.Role == ObservationRole.AgentThought &&
                observation.Interpretation.Evidence == InferenceEvidence.Opaque);
        Assert.Contains(
            observations,
            observation =>
                observation.Interpretation.Skill?.Stage == SkillEvidenceStage.Available);
        Assert.Contains(
            observations,
            observation =>
                observation.Interpretation.Skill?.Stage == SkillEvidenceStage.Invoked);
        Assert.Contains(
            observations,
            observation =>
                observation.HookEventName == "turn_duration" &&
                observation.IsMetadataOnly);
        Assert.Contains(
            observations.SelectMany(observation => observation.Interpretation.UsageMeasurements),
            measurement =>
                measurement.Name == "total_cost_usd" &&
                measurement.Value == 250_000);
    }

    [Fact]
    public void CopilotEnrichmentFixtureProjectsReasoningOutcomesAndAccounting()
    {
        List<HookObservation> observations = ParseFixture(
            DialectIds.CopilotCliTranscript,
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            Fixture("CopilotCli", "transcript-enrichment-sample.jsonl"));

        Assert.Contains(
            observations,
            observation =>
                observation.Interpretation.Role == ObservationRole.AgentThought &&
                observation.Text == "Inspect first.");
        Assert.Contains(
            observations,
            observation =>
                observation.Interpretation.Role == ObservationRole.ToolFailure &&
                observation.ToolUseId == "call-1");
        Assert.Contains(
            observations,
            observation =>
                observation.Interpretation.Role == ObservationRole.PermissionDenied);
        Assert.Contains(
            observations,
            observation =>
                observation.Interpretation.Role == ObservationRole.AgentResponse &&
                observation.Text == "The request was denied.");
        Assert.Contains(
            observations.SelectMany(observation => observation.Interpretation.UsageMeasurements),
            measurement =>
                measurement.Name == "totalNanoAiu" &&
                measurement.Value == 75);
    }

    [Fact]
    public void ClaudeFragmentAdoptsSessionFromRowWhenManifestSessionMissing()
    {
        // Reproduces the broken-tree case: a durable sidecar captured before
        // native-session capture (NativeSessionId null) must still merge into
        // the hooks' session by reading the row's own sessionId.
        ITranscriptDialectParser parser = TranscriptDialectParserRegistry.Resolve(DialectIds.ClaudeTranscript);
        string raw = "{\"type\":\"assistant\",\"promptId\":\"p1\",\"uuid\":\"x\",\"sessionId\":\"real-session\"," +
            "\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"S\"}]}}";
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
            "ClaudeCode:ClaudeCode:unknown",
            NativeSessionId: null);

        HookObservation thinking = Assert.Single(parser.Parse(line));
        Assert.Equal("real-session", thinking.SessionId);
        Assert.Equal("ClaudeCode:ClaudeCode:real-session", thinking.ProviderScopedSessionId);
    }

    [Fact]
    public void ClaudeAssistantRowWithoutPromptIdUsesTurnHint()
    {
        // Claude stamps promptId only on user rows, so an assistant row (which
        // carries thinking/text/tool_use) must inherit the turn from the hint.
        ITranscriptDialectParser parser = TranscriptDialectParserRegistry.Resolve(DialectIds.ClaudeTranscript);
        string raw = "{\"type\":\"assistant\",\"uuid\":\"x\",\"sessionId\":\"s1\",\"timestamp\":\"2026-08-30T15:29:05Z\"," +
            "\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"S\"}]}}";
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
            "ClaudeCode:ClaudeCode:s1",
            "s1",
            TurnHint: "p1");

        HookObservation thinking = Assert.Single(parser.Parse(line));
        Assert.Equal("p1", thinking.GenerationId);
    }

    [Fact]
    public void RowScannerReadsTimestampAndTurnId()
    {
        TranscriptRowScanner.RowMeta user = TranscriptRowScanner.Read(
            "{\"type\":\"user\",\"promptId\":\"p1\",\"timestamp\":\"2026-08-30T15:29:01Z\"}");
        Assert.Equal("p1", user.TurnId);
        Assert.NotNull(user.Timestamp);

        TranscriptRowScanner.RowMeta assistant = TranscriptRowScanner.Read(
            "{\"type\":\"assistant\",\"timestamp\":\"2026-08-30T15:29:05Z\"}");
        Assert.Null(assistant.TurnId);
        Assert.Null(assistant.Role);
        Assert.NotNull(assistant.Timestamp);

        TranscriptRowScanner.RowMeta cursor = TranscriptRowScanner.Read(
            """{"role":"assistant","message":{"content":[]}}""");
        Assert.Equal("assistant", cursor.Role);

        TranscriptRowScanner.RowMeta copilot = TranscriptRowScanner.Read(
            """{"type":"assistant.message","timestamp":"2026-09-19T09:21:19Z","data":{"turnId":"4","interactionId":"i1"}}""");
        Assert.Equal("assistant.message", copilot.RecordType);
        Assert.Equal("4", copilot.TurnId);
        Assert.Equal("i1", copilot.InteractionId);
        Assert.NotNull(copilot.Timestamp);
    }

    [Fact]
    public void CopilotTurnTrackerGroupsModelStepsByUserInteraction()
    {
        TranscriptTurnTracker tracker = new(DialectIds.CopilotCliTranscript);

        string? first = tracker.Observe(TranscriptRowScanner.Read(
            """{"type":"user.message","data":{"turnId":"0","interactionId":"i1"}}"""));
        string? laterStep = tracker.Observe(TranscriptRowScanner.Read(
            """{"type":"assistant.message","data":{"turnId":"16","interactionId":"i1"}}"""));
        string? second = tracker.Observe(TranscriptRowScanner.Read(
            """{"type":"user.message","data":{"turnId":"0","interactionId":"i2"}}"""));

        Assert.Equal("transcript-interaction:i1", first);
        Assert.Equal(first, laterStep);
        Assert.Equal("transcript-interaction:i2", second);
    }

    [Fact]
    public void CursorTurnTrackerGroupsRowsBetweenPromptAndTurnEnd()
    {
        TranscriptTurnTracker tracker =
            new(DialectIds.CursorTranscript);

        string? prompt = tracker.Observe(TranscriptRowScanner.Read(
            """{"role":"user","message":{"content":[]}}"""));
        string? assistant = tracker.Observe(TranscriptRowScanner.Read(
            """{"role":"assistant","message":{"content":[]}}"""));
        string? ended = tracker.Observe(TranscriptRowScanner.Read(
            """{"type":"turn_ended","status":"success"}"""));
        string? nextPrompt = tracker.Observe(TranscriptRowScanner.Read(
            """{"role":"user","message":{"content":[]}}"""));

        Assert.Equal("transcript-cursor-turn:1", prompt);
        Assert.Equal(prompt, assistant);
        Assert.Equal(prompt, ended);
        Assert.Equal("transcript-cursor-turn:2", nextPrompt);
    }

    [Fact]
    public void ClaudeParserProjectsRedactedThinkingAndCostAccounting()
    {
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.ClaudeTranscript);
        HookObservation thinking = Assert.Single(parser.Parse(new TranscriptLine(
            """{"type":"assistant","uuid":"a1","sessionId":"s1","message":{"role":"assistant","content":[{"type":"redacted_thinking"}],"usage":{"input_tokens":12,"output_tokens":3,"output_tokens_details":{"thinking_tokens":2}}}}""",
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
            TurnHint: "p1")));
        Assert.Equal(InferenceEvidence.Opaque, thinking.Interpretation.Evidence);
        Assert.Contains(
            thinking.Interpretation.UsageMeasurements,
            measurement => measurement.Name == "thinking_tokens" && measurement.Value == 2);

        HookObservation cost = Assert.Single(parser.Parse(new TranscriptLine(
            """{"type":"cost-state","sessionId":"s1","totalCostUSD":0.25,"totalLinesAdded":4,"totalDuration":1500}""",
            "C:/claude.jsonl",
            100,
            2,
            1,
            TranscriptFileRole.Main,
            HookProvider.ClaudeCode,
            HookSurface.ClaudeCode,
            DialectIds.ClaudeTranscript,
            "ClaudeCode:ClaudeCode:s1",
            "s1")));
        Assert.True(cost.IsMetadataOnly);
        Assert.Contains(
            cost.Interpretation.UsageMeasurements,
            measurement =>
                measurement.Name == "total_cost_usd" &&
                measurement.Value == 250_000 &&
                measurement.Unit == "micro-usd");
    }

    [Fact]
    public void CopilotParserProjectsReasoningOutcomesAndFinalUsage()
    {
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.CopilotCliTranscript);
        HookObservation reasoning = Assert.Single(parser.Parse(CopilotLine(
            """{"type":"assistant.reasoning","id":"r1","data":{"turnId":"1","interactionId":"i1","content":"inspect first","usage":{"reasoningTokens":5}}}""",
            "transcript-interaction:i1")));
        Assert.Equal(ObservationRole.AgentThought, reasoning.Interpretation.Role);
        Assert.Equal("inspect first", reasoning.Text);
        Assert.Contains(
            reasoning.Interpretation.UsageMeasurements,
            measurement => measurement.Name.Contains("reasoningTokens", StringComparison.Ordinal));

        HookObservation failure = Assert.Single(parser.Parse(CopilotLine(
            """{"type":"tool.execution_complete","id":"e1","data":{"turnId":"1","interactionId":"i1","toolCallId":"call-1","toolName":"powershell","success":false,"error":"exit 1","durationMs":25}}""",
            "transcript-interaction:i1")));
        Assert.Equal(ObservationRole.ToolFailure, failure.Interpretation.Role);
        Assert.Equal("call-1", failure.ToolUseId);
        Assert.Equal(25, failure.DurationMs);

        HookObservation denied = Assert.Single(parser.Parse(CopilotLine(
            """{"type":"permission.denied","id":"p1","data":{"turnId":"1","interactionId":"i1","toolCallId":"call-1","status":"denied"}}""",
            "transcript-interaction:i1")));
        Assert.Equal(ObservationRole.PermissionDenied, denied.Interpretation.Role);

        HookObservation shutdown = Assert.Single(parser.Parse(CopilotLine(
            """{"type":"session.shutdown","id":"s1","data":{"tokenDetails":{"input":100,"output":20,"reasoningTokens":5},"totalNanoAiu":42,"totalPremiumRequests":1}}""",
            turnHint: null!)));
        Assert.True(shutdown.IsMetadataOnly);
        Assert.Contains(
            shutdown.Interpretation.UsageMeasurements,
            measurement =>
                measurement.Name == "totalNanoAiu" &&
                measurement.Unit == "nano-AIU");
    }

    [Fact]
    public void MalformedRowIsIgnoredWithoutThrowing()
    {
        ITranscriptDialectParser parser = TranscriptDialectParserRegistry.Resolve(DialectIds.CursorTranscript);
        TranscriptLine line = Line("{ not valid json", HookProvider.Cursor, HookSurface.CursorIde, DialectIds.CursorTranscript);
        Assert.Empty(parser.Parse(line));
    }

    private static List<HookObservation> ParseFixture(
        string dialectId,
        HookProvider provider,
        HookSurface surface,
        string fixturePath,
        TranscriptFileRole role = TranscriptFileRole.Main,
        string? agentId = null)
    {
        ITranscriptDialectParser parser = TranscriptDialectParserRegistry.Resolve(dialectId);
        TranscriptTurnTracker turnTracker = new(dialectId);
        List<HookObservation> observations = [];
        int lineNumber = 1;
        foreach (string raw in File.ReadAllLines(fixturePath))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            TranscriptRowScanner.RowMeta metadata = TranscriptRowScanner.Read(raw);
            observations.AddRange(parser.Parse(new TranscriptLine(
                raw,
                fixturePath,
                lineNumber * 1000,
                lineNumber,
                1,
                role,
                provider,
                surface,
                dialectId,
                $"{provider}:{surface}:s1",
                "s1",
                agentId,
                ObservedAtUtc: metadata.Timestamp,
                TurnHint: turnTracker.Observe(metadata))));
            lineNumber++;
        }

        return observations;
    }

    private static TranscriptLine Line(string raw, HookProvider provider, HookSurface surface, string dialectId) =>
        new(raw, "C:/t.jsonl", 0, 1, 1, TranscriptFileRole.Main, provider, surface, dialectId, $"{provider}:{surface}:s1", "s1");

    private static TranscriptLine CopilotLine(string raw, string turnHint) =>
        new(
            raw,
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
            TurnHint: turnHint);

    private static string Fixture(string provider, string file) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", provider, file);
}
