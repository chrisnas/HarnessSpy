using System.Text;
using System.Text.Json;
using HarnessSpy.Agent.Abstractions;
using HarnessSpy.Core.Hooks;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;
using HarnessSpy.Wpf.ViewModels;

namespace HarnessSpy.Tests;

public sealed class ProviderAdapterTests
{
    [Fact]
    public void EveryDefaultClaudeFixtureNormalizes()
    {
        using JsonDocument fixtures = ReadFixture("ClaudeCode", "default-events.json");
        JsonElement[] events = fixtures.RootElement.EnumerateArray().ToArray();

        Assert.Equal(19, events.Length);
        Assert.All(events, payload =>
        {
            HookObservation observation = ParseEnvelope(
                HookProvider.ClaudeCode,
                HookSurface.ClaudeCode,
                payload.GetRawText());
            Assert.Equal(HookProvider.ClaudeCode, observation.Provider);
            Assert.NotEqual("unknownHook", observation.HookEventName);
        });
    }

    [Fact]
    public void EveryCopilotCliFixtureUsesConfiguredEvent()
    {
        using JsonDocument fixtures = ReadFixture("CopilotCli", "native-events.json");
        JsonElement[] events = fixtures.RootElement.EnumerateArray().ToArray();

        Assert.Equal(14, events.Length);
        Assert.All(events, item =>
        {
            HookObservation observation = ParseEnvelope(
                HookProvider.GitHubCopilot,
                HookSurface.CopilotCli,
                item.GetProperty("payload").GetRawText(),
                item.GetProperty("event").GetString());
            Assert.Equal(HookProvider.GitHubCopilot, observation.Provider);
            Assert.Equal("c1", observation.SessionId);
        });
    }

    [Fact]
    public void EveryVsCodeFixtureNormalizes()
    {
        using JsonDocument fixtures = ReadFixture("VSCode", "events.json");
        JsonElement[] events = fixtures.RootElement.EnumerateArray().ToArray();

        Assert.Equal(8, events.Length);
        Assert.All(events, payload =>
        {
            HookObservation observation = ParseEnvelope(
                HookProvider.GitHubCopilot,
                HookSurface.VsCodeAgentHooks,
                payload.GetRawText());
            Assert.Equal(HookSurface.VsCodeAgentHooks, observation.Surface);
            Assert.NotEqual("unknownHook", observation.HookEventName);
        });
    }

    [Fact]
    public void ClaudeToolPayloadMapsToCanonicalCursorVocabulary()
    {
        HookObservation observation = ParseEnvelope(
            HookProvider.ClaudeCode,
            HookSurface.ClaudeCode,
            """
            {
              "hook_event_name": "PreToolUse",
              "session_id": "claude-session",
              "prompt_id": "prompt-1",
              "cwd": "C:\\Repo",
              "tool_name": "Bash",
              "tool_input": { "command": "dotnet test" },
              "tool_use_id": "tool-1"
            }
            """);

        Assert.Equal("PreToolUse", observation.RawHookEventName);
        Assert.Equal("preToolUse", observation.HookEventName);
        Assert.Equal("claude-session", observation.SessionId);
        Assert.Equal("prompt-1", observation.GenerationId);
        Assert.Equal("Shell", observation.ToolName);
        Assert.Equal(CanonicalToolKind.Shell, observation.ToolKind);
        Assert.Equal(HookProvider.ClaudeCode, observation.Provider);
        Assert.Equal("C:\\Repo", observation.Workspace.DisplayName);
        Assert.Contains("\"tool_name\": \"Bash\"", observation.DisplayJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeStopUsesLastAssistantMessage()
    {
        HookObservation observation = ParseEnvelope(
            HookProvider.ClaudeCode,
            HookSurface.ClaudeCode,
            """
            {
              "hook_event_name": "Stop",
              "session_id": "claude-session",
              "prompt_id": "prompt-1",
              "cwd": "C:\\Repo",
              "last_assistant_message": "Done."
            }
            """);

        Assert.Equal("stop", observation.HookEventName);
        Assert.Equal("Done.", observation.Text);
        Assert.Equal(CanonicalEventKind.TurnCompleted, observation.EventKind);
    }

    [Fact]
    public void CopilotNativePayloadUsesConfiguredEventAndCamelCaseFields()
    {
        HookObservation observation = ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """
            {
              "sessionId": "copilot-session",
              "timestamp": 1787414401000,
              "cwd": "C:\\Repo",
              "toolName": "powershell",
              "toolArgs": "{\"command\":\"git status\"}"
            }
            """,
            configuredEventName: "preToolUse");

        Assert.Equal("preToolUse", observation.RawHookEventName);
        Assert.Equal("preToolUse", observation.HookEventName);
        Assert.Equal("copilot-session", observation.SessionId);
        Assert.Equal("Shell", observation.ToolName);
        Assert.Equal(CorrelationQuality.Derived, observation.CorrelationQuality);
        Assert.Equal("git status", observation.Payload
            .GetProperty("tool_input")
            .GetProperty("command")
            .GetString());
    }

    [Fact]
    public void VsCodePayloadRetainsExactToolUseCorrelation()
    {
        HookObservation observation = ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.VsCodeAgentHooks,
            """
            {
              "timestamp": "2026-08-22T14:00:01Z",
              "cwd": "C:\\Repo",
              "session_id": "vs-session",
              "hook_event_name": "PreToolUse",
              "tool_name": "editFiles",
              "tool_input": { "files": ["src/App.cs"] },
              "tool_use_id": "tool-1"
            }
            """);

        Assert.Equal("preToolUse", observation.HookEventName);
        Assert.Equal("tool-1", observation.ToolUseId);
        Assert.Equal(CorrelationQuality.Exact, observation.CorrelationQuality);
        Assert.Equal(HookSurface.VsCodeAgentHooks, observation.Surface);
    }

    [Fact]
    public void CopilotCliEventsReceiveDerivedTurnGrouping()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"s1","timestamp":1,"cwd":"C:\\Repo","prompt":"hello"}""",
            "userPromptSubmitted"));
        viewModel.AddObservation(ParseEnvelope(
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            """{"sessionId":"s1","timestamp":2,"cwd":"C:\\Repo","toolName":"view","toolArgs":{"path":"README.md"}}""",
            "preToolUse"));

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel turn = Assert.Single(session.Children);
        Assert.Equal(TreeNodeKind.Generation, turn.Kind);
        Assert.Equal(2, turn.Children.Count);
    }

    [Fact]
    public void RuntimeDetectorRejectsCursorInvocationOfClaudeHook()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"hook_event_name":"preToolUse","cursor_version":"2.0","conversation_id":"c1"}""");
        HookRuntimeDetector detector = new();

        HookSurface detected = detector.Detect(
            ProviderProfile.Claude,
            document.RootElement,
            new Dictionary<string, string?>());

        Assert.Equal(HookSurface.CursorIde, detected);
        Assert.False(detector.IsAccepted(ProviderProfile.Claude, detected));
    }

    [Fact]
    public async Task ClaudeForwarderIsSilentAndAlwaysSucceeds()
    {
        RecordingSink sink = new();
        HookForwarder forwarder = new(
            sink,
            new HookProcessOptions(ProviderProfile.Claude),
            environment: new Dictionary<string, string?>
            {
                ["CLAUDE_CODE_CHILD_SESSION"] = "1"
            });
        StringWriter output = new();

        int exitCode = await forwarder.RunAsync(
            [],
            new StringReader(
                """{"hook_event_name":"SessionStart","session_id":"s1","cwd":"C:\\Repo"}"""),
            output);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Single(sink.Forwarded);
    }

    [Fact]
    public async Task ObservationBufferPreservesQueuedItems()
    {
        ObservationBuffer<int> buffer = new(capacity: 2);
        await buffer.WriteAsync(1, CancellationToken.None);
        await buffer.WriteAsync(2, CancellationToken.None);

        Assert.True(buffer.TryRead(out int first));
        Assert.True(buffer.TryRead(out int second));
        Assert.Equal(1, first);
        Assert.Equal(2, second);
    }

    [Fact]
    public async Task UnavailableAgentProviderExposesStableCapabilityBoundary()
    {
        await using UnavailableAgentProvider provider = new(HookProvider.Cursor);
        AgentProviderHealth health = await provider.GetHealthAsync(CancellationToken.None);

        Assert.False(provider.Capabilities.IsAvailable);
        Assert.False(health.IsHealthy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(
            new AgentCreateRequest(
                "C:\\Repo",
                "auto",
                AgentRuntimeKind.Local,
                "invocation-1"),
            CancellationToken.None));
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
            configuredEventName ?? ReadEvent(payloadDocument.RootElement),
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

    private static string? ReadEvent(JsonElement payload) =>
        payload.TryGetProperty("hook_event_name", out JsonElement eventName)
            ? eventName.GetString()
            : null;

    private sealed class RecordingSink : IHookPayloadSink
    {
        public List<string> Forwarded { get; } = [];

        public Task ForwardAsync(
            ReadOnlyMemory<byte> payloadLine,
            CancellationToken cancellationToken)
        {
            Forwarded.Add(Encoding.UTF8.GetString(payloadLine.Span));
            return Task.CompletedTask;
        }
    }
}
