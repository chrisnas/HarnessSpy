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
