using HarnessSpy.Core.Models;
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
        Assert.Contains("/demo-skill", prompt.SlashCommands);

        // The dynamic-tool call is toned as MCP.
        HookObservation dynamicCall = observations.First(o => o.ToolName == "CallDynamicTool");
        Assert.Equal(CanonicalToolKind.Mcp, dynamicCall.ToolKind);

        // Every fragment carries transcript provenance with the dialect.
        Assert.All(observations, o =>
        {
            Assert.True(o.IsTranscriptSourced);
            Assert.Equal(DialectIds.CursorTranscript, o.Provenance!.DialectId);
        });

        // The turn_ended row becomes a TurnStop.
        Assert.Contains(observations, o => o.Interpretation.Role == ObservationRole.TurnStop);
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

        // Metadata rows (mode, system, cost-state) do not create nodes.
        Assert.DoesNotContain(observations, o => o.HookEventName is "mode" or "cost-state" or "turn_duration");
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

        // The flattened <server>-<tool> name is never split on the hyphen.
        Assert.Equal("dotnet-dstrings-get_duplicated_strings", request.ToolName);

        Assert.Contains(observations, o =>
            o.Interpretation.Role == ObservationRole.AgentThought &&
            o.Interpretation.Evidence == InferenceEvidence.Opaque);
        Assert.Contains(observations, o => o.Interpretation.Role == ObservationRole.PermissionRequest);
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
        Assert.NotNull(assistant.Timestamp);
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
        List<HookObservation> observations = [];
        int lineNumber = 1;
        foreach (string raw in File.ReadAllLines(fixturePath))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

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
                agentId)));
            lineNumber++;
        }

        return observations;
    }

    private static TranscriptLine Line(string raw, HookProvider provider, HookSurface surface, string dialectId) =>
        new(raw, "C:/t.jsonl", 0, 1, 1, TranscriptFileRole.Main, provider, surface, dialectId, $"{provider}:{surface}:s1", "s1");

    private static string Fixture(string provider, string file) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", provider, file);
}
