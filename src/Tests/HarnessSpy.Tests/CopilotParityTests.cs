using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;
using HarnessSpy.Wpf.ViewModels;

namespace HarnessSpy.Tests;

// Characterizes the two native Copilot hook dialects (CLI camelCase and VS Code
// Local PascalCase) so their exact identities and correlation capabilities are
// locked in.
public sealed class CopilotParityTests
{
    [Fact]
    public void CliFixtureKeepsExactConfiguredEventAndNativeToolNames()
    {
        using JsonDocument fixtures = ReadFixture("CopilotCli", "native-events.json");
        JsonElement[] events = fixtures.RootElement.EnumerateArray().ToArray();

        Assert.Equal(14, events.Length);
        foreach (JsonElement item in events)
        {
            string configured = item.GetProperty("event").GetString()!;
            HookObservation observation = ParseEnvelope(
                HookProvider.GitHubCopilot,
                HookSurface.CopilotCli,
                item.GetProperty("payload").GetRawText(),
                configured);

            // The configured event key is the native identity; nothing renamed.
            Assert.Equal(configured, observation.HookEventName);
            Assert.Equal("c1", observation.SessionId);
            Assert.Equal(CorrelationQuality.Derived, GetBaselineQuality(observation));
        }
    }

    [Fact]
    public void CliPreservesExactPowershellToolName()
    {
        HookObservation observation = ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":5,"cwd":"C:\\Repo","toolName":"powershell","toolArgs":{"command":"git status"}}""",
            "preToolUse");

        Assert.Equal("powershell", observation.ToolName);
        Assert.Equal(CanonicalToolKind.Shell, observation.ToolKind);
    }

    [Fact]
    public void CliSubagentsCorrelateHeuristicallyByName()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":1,"cwd":"C:\\Repo","prompt":"go"}""",
            "userPromptSubmitted"));
        HookObservation start = ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":2,"cwd":"C:\\Repo","agentName":"explore"}""",
            "subagentStart");
        viewModel.AddObservation(start);
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":3,"cwd":"C:\\Repo","agentId":"a1","agentType":"explore","agentName":"explore","response":"found"}""",
            "subagentStop"));

        // Start lacks the stop event's agentId, so correlation is by name and
        // must be marked heuristic rather than exact.
        Assert.Equal(CorrelationQuality.Heuristic, start.CorrelationQuality);

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel startNode = Assert.Single(
            turn.Children,
            child => child.Observation?.HookEventName == "subagentStart");
        Assert.Equal(
            "subagentStop",
            Assert.Single(startNode.Children).Observation!.HookEventName);
    }

    [Fact]
    public void VsCodeKeepsExactPascalCaseAndToolUseCorrelation()
    {
        using JsonDocument fixtures = ReadFixture("VSCode", "events.json");
        JsonElement[] events = fixtures.RootElement.EnumerateArray().ToArray();
        Assert.Equal(8, events.Length);

        MainWindowViewModel viewModel = new();
        foreach (JsonElement payload in events)
        {
            viewModel.AddObservation(ParseEnvelope(
                HookProvider.GitHubCopilot,
                HookSurface.VsCodeAgentHooks,
                payload.GetRawText()));
        }

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel pre = Assert.Single(
            turn.Children,
            child => child.Observation?.HookEventName == "PreToolUse");
        Assert.Equal("editFiles", pre.Observation!.ToolName);
        Assert.Equal(CorrelationQuality.Exact, pre.Observation.CorrelationQuality);
        Assert.Equal(
            "PostToolUse",
            Assert.Single(pre.Children).Observation!.HookEventName);
    }

    [Fact]
    public void CliSessionStartSortsAboveTurnEvenWhenItsTimestampIsLater()
    {
        // Copilot CLI emits sessionStart a couple of seconds after the first
        // prompt, so its native timestamp is later than the turn's; it must
        // still read as the opening node of the session.
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":1788075874476,"cwd":"C:\\Repo","prompt":"go"}""",
            "userPromptSubmitted"));
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":1788075877060,"cwd":"C:\\Repo","source":"new","initialPrompt":"go"}""",
            "sessionStart"));

        TreeNodeViewModel session = Assert.Single(Assert.Single(viewModel.Roots).Children);
        Assert.Equal("sessionStart", session.Children[0].Observation?.HookEventName);
    }

    [Fact]
    public void CliTransformedPromptNestsUnderSubmittedPrompt()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":1,"cwd":"C:\\Repo","prompt":"go"}""",
            "userPromptSubmitted"));
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":2,"cwd":"C:\\Repo","prompt":"go","transformedPrompt":"GO"}""",
            "userPromptTransformed"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel submitted = Assert.Single(
            turn.Children,
            child => child.Observation?.HookEventName == "userPromptSubmitted");
        Assert.Equal(
            "userPromptTransformed",
            Assert.Single(submitted.Children).Observation!.HookEventName);
    }

    [Fact]
    public void CliToolCompletionNestsUnderRequestByToolSignature()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":1,"cwd":"C:\\Repo","prompt":"go"}""",
            "userPromptSubmitted"));
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":2,"cwd":"C:\\Repo","toolName":"grep","toolArgs":{"pattern":"hook","path":"C:\\Repo"}}""",
            "preToolUse"));
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":3,"cwd":"C:\\Repo","toolName":"grep","toolArgs":{"pattern":"hook","path":"C:\\Repo"},"toolResult":{"resultType":"success"}}""",
            "postToolUse"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel pre = Assert.Single(
            turn.Children,
            child => child.Observation?.HookEventName == "preToolUse");
        Assert.Equal(
            "postToolUse",
            Assert.Single(pre.Children).Observation!.HookEventName);
    }

    [Fact]
    public void CliIdenticalToolCallsPairInArrivalOrder()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":1,"cwd":"C:\\Repo","prompt":"go"}""",
            "userPromptSubmitted"));
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":2,"cwd":"C:\\Repo","toolName":"glob","toolArgs":{"pattern":"**/*.cs"}}""",
            "preToolUse"));
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":3,"cwd":"C:\\Repo","toolName":"glob","toolArgs":{"pattern":"**/*.cs"},"toolResult":{"resultType":"success"}}""",
            "postToolUse"));
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":4,"cwd":"C:\\Repo","toolName":"glob","toolArgs":{"pattern":"**/*.cs"}}""",
            "preToolUse"));
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":5,"cwd":"C:\\Repo","toolName":"glob","toolArgs":{"pattern":"**/*.cs"},"toolResult":{"resultType":"success"}}""",
            "postToolUse"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        List<TreeNodeViewModel> preNodes = turn.Children
            .Where(child => child.Observation?.HookEventName == "preToolUse")
            .ToList();

        // Every request pairs with exactly one completion; none is left orphaned.
        Assert.Equal(2, preNodes.Count);
        Assert.All(preNodes, pre => Assert.Equal(
            "postToolUse",
            Assert.Single(pre.Children).Observation!.HookEventName));
    }

    [Fact]
    public void CliPermissionNotificationUsesPermissionTone()
    {
        HookObservation observation = ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"c1","timestamp":1,"cwd":"C:\\Repo","message":"Path permission needed","title":"Permission needed","notification_type":"permission_prompt"}""",
            "notification");

        Assert.Equal(ObservationTone.Permission, observation.Interpretation.Tone);
    }

    [Fact]
    public void CliUnknownToolIsFlaggedMcpByAllowlistWithoutPermission()
    {
        // Idea 3: with no permission event to learn from, a tool name that is
        // not a built-in is still recognised as MCP - only the server is unknown.
        HookObservation observation = ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"mcp-allowlist","timestamp":1,"cwd":"C:\\Repo","toolName":"dotnet-dstrings-get_duplicated_strings","toolArgs":{"dumpPath":"d.dmp"}}""",
            "preToolUse");

        Assert.Equal(CanonicalToolKind.Mcp, observation.ToolKind);
        Assert.Equal(ObservationTone.Mcp, observation.Interpretation.Tone);
        Assert.Null(observation.McpServerName);
    }

    [Theory]
    [InlineData("grep", CanonicalToolKind.TextSearch)]
    [InlineData("glob", CanonicalToolKind.FileSearch)]
    [InlineData("view", CanonicalToolKind.FileRead)]
    [InlineData("powershell", CanonicalToolKind.Shell)]
    public void CliBuiltInToolsAreNeverFlaggedAsMcp(string toolName, CanonicalToolKind expectedKind)
    {
        string payload =
            "{\"sessionId\":\"mcp-builtin\",\"timestamp\":1,\"cwd\":\"C:\\\\Repo\",\"toolName\":\"" +
            toolName +
            "\",\"toolArgs\":{}}";
        HookObservation observation = ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            payload,
            "preToolUse");

        Assert.Equal(expectedKind, observation.ToolKind);
        Assert.NotEqual(ObservationTone.Mcp, observation.Interpretation.Tone);
        Assert.Null(observation.McpServerName);
    }

    [Fact]
    public void CliPermissionRequestSlashFormIsFlaggedMcpAndKeepsPermissionTone()
    {
        // Idea 2: the "<server>/<tool>" permission form is unambiguously MCP.
        HookObservation observation = ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"mcp-permission","timestamp":1,"cwd":"C:\\Repo","toolName":"dotnet-dstrings/get_duplicated_strings","toolInput":{"dumpPath":"d.dmp"}}""",
            "permissionRequest");

        Assert.Equal(CanonicalToolKind.Mcp, observation.ToolKind);
        Assert.Equal("dotnet-dstrings", observation.McpServerName);
        Assert.Equal(ObservationTone.Permission, observation.Interpretation.Tone);
    }

    [Fact]
    public void CliPostToolUseRecoversServerFromEarlierPermission()
    {
        // Idea 4: the permission slash form teaches the session how to split the
        // flattened preToolUse/postToolUse name back into its server and tool.
        ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"mcp-learn","timestamp":1,"cwd":"C:\\Repo","toolName":"dotnet-dstrings/get_duplicated_strings","toolInput":{"dumpPath":"d.dmp"}}""",
            "permissionRequest");

        HookObservation post = ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"mcp-learn","timestamp":2,"cwd":"C:\\Repo","toolName":"dotnet-dstrings-get_duplicated_strings","toolArgs":{"dumpPath":"d.dmp"},"toolResult":{"resultType":"success"}}""",
            "postToolUse");

        Assert.Equal(CanonicalToolKind.Mcp, post.ToolKind);
        Assert.Equal(ObservationTone.Mcp, post.Interpretation.Tone);
        Assert.Equal("dotnet-dstrings", post.McpServerName);
    }

    [Fact]
    public void CliNotificationTeachesServerSplitForLaterCalls()
    {
        // Idea 4 via the "Use MCP tool: <server>/<tool>" notification message.
        ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"mcp-notify","timestamp":1,"cwd":"C:\\Repo","message":"Use MCP tool: dotnet-dstrings/get_duplicated_strings","title":"Permission needed","notification_type":"permission_prompt"}""",
            "notification");

        HookObservation post = ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"mcp-notify","timestamp":2,"cwd":"C:\\Repo","toolName":"dotnet-dstrings-get_duplicated_strings","toolArgs":{"dumpPath":"d.dmp"},"toolResult":{"resultType":"success"}}""",
            "postToolUse");

        Assert.Equal("dotnet-dstrings", post.McpServerName);
    }

    [Fact]
    public void CliMcpCallCountsAsMcpNotNativeToolInSummary()
    {
        // The request arrives before the permission (as observed in real traces),
        // yet the completion still pairs with it and the call is summarised as a
        // single MCP call rather than a native tool.
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"mcp-summary","timestamp":1,"cwd":"C:\\Repo","prompt":"go"}""",
            "userPromptSubmitted"));
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"mcp-summary","timestamp":2,"cwd":"C:\\Repo","toolName":"dotnet-dstrings-get_duplicated_strings","toolArgs":{"dumpPath":"d.dmp"}}""",
            "preToolUse"));
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"mcp-summary","timestamp":3,"cwd":"C:\\Repo","toolName":"dotnet-dstrings/get_duplicated_strings","toolInput":{"dumpPath":"d.dmp"}}""",
            "permissionRequest"));
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"mcp-summary","timestamp":4,"cwd":"C:\\Repo","toolName":"dotnet-dstrings-get_duplicated_strings","toolArgs":{"dumpPath":"d.dmp"},"toolResult":{"resultType":"success"}}""",
            "postToolUse"));

        NodeSummary summary = OnlyTurn(viewModel).NodeSummary!;
        Assert.Empty(summary.Tools);
        CountedDurationRow mcpCall = Assert.Single(summary.McpCalls);
        Assert.Equal("dotnet-dstrings-get_duplicated_strings", mcpCall.Name);
        Assert.Equal(1, mcpCall.Count);
    }

    private static CorrelationQuality GetBaselineQuality(HookObservation observation)
    {
        // Subagent events are explicitly heuristic on the CLI; every other CLI
        // event is derived because the CLI supplies no exact tool-use id.
        return observation.Interpretation.Role is
            ObservationRole.SubagentStart or ObservationRole.SubagentStop
            ? CorrelationQuality.Derived
            : observation.CorrelationQuality;
    }

    private static TreeNodeViewModel OnlyTurn(MainWindowViewModel viewModel)
    {
        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        return Assert.Single(session.Children, child => child.Kind == TreeNodeKind.Generation);
    }

    private static HookObservation ParseEnvelope(
        HookProvider provider,
        HookSurface surface,
        string payloadJson,
        string? configuredEventName = null)
    {
        using JsonDocument payloadDocument = JsonDocument.Parse(payloadJson);
        ObservationEnvelope envelope = new(
            ObservationEnvelope.CurrentIngressVersion,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            provider,
            surface,
            surface,
            ObservationSourceKind.Hook,
            configuredEventName,
            configuredEventName ??
                (payloadDocument.RootElement.TryGetProperty("hook_event_name", out JsonElement e)
                    ? e.GetString()
                    : null),
            "test",
            null,
            "valid",
            payloadDocument.RootElement.Clone());
        string line = JsonSerializer.Serialize(
            envelope,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(HookObservation.TryParse(line, out HookObservation? observation));
        return Assert.IsType<HookObservation>(observation);
    }

    private static JsonDocument ReadFixture(params string[] parts)
    {
        string path = parts.Aggregate(
            Path.Combine(AppContext.BaseDirectory, "Fixtures"),
            Path.Combine);
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
