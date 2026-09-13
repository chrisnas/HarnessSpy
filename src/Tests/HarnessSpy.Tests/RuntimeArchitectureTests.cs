using System.Text.Json;
using HarnessSpy.Core.Hooks;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;
using HarnessSpy.Core.Runtimes.Claude;
using HarnessSpy.Core.Runtimes.Copilot;
using HarnessSpy.Wpf.ViewModels;

namespace HarnessSpy.Tests;

public sealed class RuntimeArchitectureTests
{
    [Fact]
    public void ClaudeCatalogCoversDocumentedAndModelSwitchEvents()
    {
        Assert.Equal(31, ClaudeHookCatalog.DocumentedEvents.Count);
        Assert.Equal(2, ClaudeHookCatalog.ModelSwitchEvents.Count);
        Assert.Contains("PreModelSwitch", ClaudeHookCatalog.ModelSwitchEvents);
        Assert.Contains("PostModelSwitch", ClaudeHookCatalog.ModelSwitchEvents);
    }

    [Fact]
    public void ClaudeSafeProfileHas28EventsWithoutWorktreeCreate()
    {
        Assert.Equal(28, ClaudeHookCatalog.SafeProfileEvents.Count);
        Assert.DoesNotContain("WorktreeCreate", ClaudeHookCatalog.SafeProfileEvents);
        Assert.Contains("PreModelSwitch", ClaudeHookCatalog.SafeProfileEvents);
        Assert.Contains("WorktreeRemove", ClaudeHookCatalog.SafeProfileEvents);
    }

    [Fact]
    public void ClaudeFullProfileHas32EventsAndAddsSensitiveOnes()
    {
        Assert.Equal(32, ClaudeHookCatalog.FullProfileEvents.Count);
        Assert.DoesNotContain("WorktreeCreate", ClaudeHookCatalog.FullProfileEvents);
        foreach (string extra in ClaudeHookCatalog.FullProfileOnlyEvents)
        {
            Assert.Contains(extra, ClaudeHookCatalog.FullProfileEvents);
        }

        Assert.Contains("MessageDisplay", ClaudeHookCatalog.FullProfileEvents);
        Assert.Contains("Elicitation", ClaudeHookCatalog.FullProfileEvents);
    }

    [Fact]
    public void CommittedClaudeExampleFilesMatchProfiles()
    {
        string safe = File.ReadAllText(ConfigPath("Claude", "settings.example.json"));
        string full = File.ReadAllText(ConfigPath("Claude", "settings.full.example.json"));

        Assert.Equal(
            ClaudeHookCatalog.SafeProfileEvents.OrderBy(x => x, StringComparer.Ordinal),
            ClaudeSettingsGenerator.ReadRegisteredEvents(safe).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(
            ClaudeHookCatalog.FullProfileEvents.OrderBy(x => x, StringComparer.Ordinal),
            ClaudeSettingsGenerator.ReadRegisteredEvents(full).OrderBy(x => x, StringComparer.Ordinal));

        // The checked-in files never ship the author's absolute path.
        Assert.Contains(ClaudeSettingsGenerator.ExecutablePlaceholder, safe, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\dev\\research", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void CopilotCliCatalogHas14EventsAndVsCodeHas8()
    {
        Assert.Equal(14, CopilotHookCatalog.CliV1Events.Count);
        Assert.Equal(8, CopilotHookCatalog.VsCodeLocalEvents.Count);
    }

    [Fact]
    public void CommittedCopilotExampleMatchesCliCatalog()
    {
        string cli = File.ReadAllText(ConfigPath("Copilot", "harness-spy.example.json"));
        Assert.Equal(
            CopilotHookCatalog.CliV1Events.OrderBy(x => x, StringComparer.Ordinal),
            CopilotSettingsGenerator.ReadRegisteredEvents(cli).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Contains(CopilotSettingsGenerator.ExecutablePlaceholder, cli, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\dev\\research", cli, StringComparison.Ordinal);
    }

    [Fact]
    public void CommittedCopilotVsCodeExampleMatchesVsCodeCatalog()
    {
        string vscode = File.ReadAllText(ConfigPath("Copilot", "vscode-hooks.example.json"));
        Assert.Equal(
            CopilotHookCatalog.VsCodeLocalEvents.OrderBy(x => x, StringComparer.Ordinal),
            CopilotSettingsGenerator.ReadRegisteredEvents(vscode).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Contains(CopilotSettingsGenerator.ExecutablePlaceholder, vscode, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\dev\\research", vscode, StringComparison.Ordinal);

        // The committed example must match what the generator produces today.
        Assert.Equal(
            CopilotSettingsGenerator.GenerateVsCode(CopilotSettingsGenerator.ExecutablePlaceholder)
                .ReplaceLineEndings("\n"),
            vscode.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void GenerateVsCodeUsesVsCodeSchemaAndRuntimeId()
    {
        string json = CopilotSettingsGenerator.GenerateVsCode(
            CopilotSettingsGenerator.ExecutablePlaceholder);

        // Exactly the eight PascalCase VS Code events, nothing renamed.
        Assert.Equal(
            CopilotHookCatalog.VsCodeLocalEvents.OrderBy(x => x, StringComparer.Ordinal),
            CopilotSettingsGenerator.ReadRegisteredEvents(json).OrderBy(x => x, StringComparer.Ordinal));

        // VS Code command schema: the cross-platform "command" field and
        // "timeout" (seconds), never the CLI's "powershell"/"timeoutSec".
        Assert.Contains("\"command\"", json, StringComparison.Ordinal);
        Assert.Contains("\"timeout\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("powershell", json, StringComparison.Ordinal);
        Assert.DoesNotContain("timeoutSec", json, StringComparison.Ordinal);
        Assert.DoesNotContain("copilot-cli-camel", json, StringComparison.Ordinal);

        // VS Code runs the "command" value through PowerShell on Windows, so it
        // must use the call operator + single-quoted path ("& '<exe>' ..."). A
        // bare quoted path ("\"<exe>\" ...") is parsed as a string literal and
        // never executes, so the hook silently never fires.
        Assert.Contains(
            "& '" + CopilotSettingsGenerator.ExecutablePlaceholder + "'",
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\\\"" + CopilotSettingsGenerator.ExecutablePlaceholder,
            json,
            StringComparison.Ordinal);

        // The authoritative routing key that sends these to the VS Code engine.
        Assert.Contains("vscode-agent-hooks", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"HARNESS_SPY_RUNTIME_ID\": \"github-copilot\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void VsCodeProfileRuntimeIdRoutesToVsCodeEngine()
    {
        // The generated env id is what the detector uses (authoritative), so a
        // VS Code profile must resolve to the VS Code Local surface under the
        // shared Copilot provider profile.
        HookRuntimeDetector detector = new();
        using JsonDocument payload = JsonDocument.Parse(
            """{"hook_event_name":"PreToolUse","session_id":"s1","tool_use_id":"t1"}""");

        HookSurface detected = detector.Detect(
            ProviderProfile.Copilot,
            payload.RootElement,
            new Dictionary<string, string?> { ["HARNESS_SPY_RUNTIME_ID"] = "vscode-agent-hooks" });

        Assert.Equal(HookSurface.VsCodeAgentHooks, detected);
        Assert.True(detector.IsAccepted(ProviderProfile.Copilot, detected));
    }

    [Fact]
    public void LateArrivingEventIsReinsertedInChronologicalOrder()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Cursor(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"prompt":"go"}""",
            "2026-08-20T12:00:01Z"));
        // A later event arrives first...
        viewModel.AddObservation(Cursor(
            """{"hook_event_name":"preToolUse","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"late"}""",
            "2026-08-20T12:00:05Z"));
        // ...then an earlier one, which must be reinserted before it.
        viewModel.AddObservation(Cursor(
            """{"hook_event_name":"preToolUse","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"],"tool_name":"Grep","tool_use_id":"early"}""",
            "2026-08-20T12:00:02Z"));

        TreeNodeViewModel turn = Assert.Single(
            Assert.Single(Assert.Single(viewModel.Roots).Children).Children,
            child => child.Kind == TreeNodeKind.Generation);
        Assert.Equal(3, turn.Children.Count);
        Assert.Equal("Grep", turn.Children[1].Observation!.ToolName);
        Assert.Equal("Read", turn.Children[2].Observation!.ToolName);
    }

    [Fact]
    public void UnknownProviderEventStillAppearsWithNativeName()
    {
        HookObservation observation = ParseEnvelope(
            HookProvider.Unknown,
            HookSurface.Unknown,
            """{"hook_event_name":"someFutureHook","session_id":"s1","cwd":"C:\\Repo"}""");

        Assert.Equal("someFutureHook", observation.HookEventName);
        Assert.Equal(ObservationRole.Generic, observation.Interpretation.Role);

        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(observation);
        TreeNodeViewModel session = Assert.Single(Assert.Single(viewModel.Roots).Children);
        Assert.Contains(
            session.Children,
            child => child.Observation?.HookEventName == "someFutureHook");
    }

    [Fact]
    public void UnknownCursorEventFallsBackToGenericTurnNode()
    {
        HookObservation observation = Cursor(
            """{"hook_event_name":"aBrandNewCursorHook","conversation_id":"c1","generation_id":"g1","workspace_roots":["C:\\Repo"]}""");
        Assert.Equal("aBrandNewCursorHook", observation.HookEventName);
        Assert.Equal(ObservationRole.Generic, observation.Interpretation.Role);
        Assert.Equal(ObservationScope.Turn, observation.Interpretation.Scope);
    }

    [Fact]
    public void RuntimeIdEnvironmentIsAuthoritativeOverPayloadShape()
    {
        // A Copilot-looking payload, but the generated runtime id says Claude.
        using JsonDocument document = JsonDocument.Parse(
            """{"sessionId":"s1","timestamp":123}""");
        HookRuntimeDetector detector = new();

        HookSurface detected = detector.Detect(
            ProviderProfile.Claude,
            document.RootElement,
            new Dictionary<string, string?> { ["HARNESS_SPY_RUNTIME_ID"] = "claude-code" });

        Assert.Equal(HookSurface.ClaudeCode, detected);
    }

    [Fact]
    public void LegacyRawSessionScopedClaudeEventIsDetectedByCatalog()
    {
        // A session-scoped Claude event with no prompt_id/permission_mode is
        // detected via session_id + catalog membership, and keeps its native
        // PascalCase name.
        Assert.True(HookObservation.TryParseRawPayload(
            """{"hook_event_name":"SessionEnd","session_id":"s1","cwd":"C:\\Repo","reason":"other"}""",
            DateTimeOffset.UtcNow,
            "file.json",
            out HookObservation? observation));
        Assert.NotNull(observation);
        Assert.Equal(HookProvider.ClaudeCode, observation!.Provider);
        Assert.Equal("SessionEnd", observation.HookEventName);
    }

    [Fact]
    public void PascalCaseClaudePayloadIsNotMisreadAsVsCode()
    {
        // Claude PreToolUse (no string timestamp) must not be classified as VS
        // Code even though both use the PascalCase name PreToolUse.
        Assert.True(HookObservation.TryParseRawPayload(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_use_id":"t1"}""",
            DateTimeOffset.UtcNow,
            "file.json",
            out HookObservation? observation));
        Assert.NotNull(observation);
        Assert.Equal(HookProvider.ClaudeCode, observation!.Provider);
        Assert.Equal(HookSurface.ClaudeCode, observation.Surface);
        Assert.Equal("Bash", observation.ToolName);
    }

    private static HookObservation Cursor(string payloadJson, string observedAtUtc = "2026-08-20T12:00:00Z")
    {
        string envelope = $$"""
            {
              "ingressVersion": 1,
              "eventId": "{{Guid.NewGuid()}}",
              "observedAtUtc": "{{observedAtUtc}}",
              "payload": {{payloadJson}}
            }
            """;
        Assert.True(HookObservation.TryParse(envelope, out HookObservation? observation));
        return Assert.IsType<HookObservation>(observation);
    }

    private static HookObservation ParseEnvelope(
        HookProvider provider,
        HookSurface surface,
        string payloadJson)
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
            null,
            payloadDocument.RootElement.TryGetProperty("hook_event_name", out JsonElement e)
                ? e.GetString()
                : null,
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

    private static string ConfigPath(string provider, string file) =>
        Path.Combine(SolutionRoot(), "Config", provider, file);

    private static string SolutionRoot() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
