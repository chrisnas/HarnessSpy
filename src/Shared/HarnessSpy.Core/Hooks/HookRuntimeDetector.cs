using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Hooks;

public interface IHookRuntimeDetector
{
    HookSurface Detect(
        ProviderProfile expected,
        JsonElement payload,
        IReadOnlyDictionary<string, string?> environment);

    bool IsAccepted(ProviderProfile expected, HookSurface detected);
}

public sealed class HookRuntimeDetector : IHookRuntimeDetector
{
    public HookSurface Detect(
        ProviderProfile expected,
        JsonElement payload,
        IReadOnlyDictionary<string, string?> environment)
    {
        if (Has(payload, "cursor_version") ||
            Has(payload, "conversation_id") && Has(payload, "generation_id") ||
            HasEnvironment(environment, "CURSOR_VERSION"))
        {
            return HookSurface.CursorIde;
        }

        if (HasEnvironment(environment, "CLAUDE_CODE_CHILD_SESSION") ||
            Has(payload, "prompt_id") ||
            Has(payload, "permission_mode") ||
            Has(payload, "effort"))
        {
            return HookSurface.ClaudeCode;
        }

        if (Has(payload, "sessionId") &&
            payload.TryGetProperty("timestamp", out JsonElement nativeTimestamp) &&
            nativeTimestamp.ValueKind == JsonValueKind.Number)
        {
            return HookSurface.CopilotCli;
        }

        if (Has(payload, "hook_event_name") &&
            Has(payload, "timestamp") &&
            (Has(payload, "session_id") ||
             Has(payload, "tool_use_id") ||
             Has(payload, "agent_id")))
        {
            return HookSurface.VsCodeAgentHooks;
        }

        return expected.Surface;
    }

    public bool IsAccepted(ProviderProfile expected, HookSurface detected)
    {
        return expected.Provider switch
        {
            HookProvider.Cursor => detected == HookSurface.CursorIde,
            HookProvider.ClaudeCode => detected == HookSurface.ClaudeCode,
            HookProvider.GitHubCopilot =>
                detected is HookSurface.CopilotCli or HookSurface.VsCodeAgentHooks,
            _ => false
        };
    }

    private static bool Has(JsonElement payload, string propertyName) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(propertyName, out _);

    private static bool HasEnvironment(
        IReadOnlyDictionary<string, string?> environment,
        string name) =>
        environment.TryGetValue(name, out string? value) &&
        !string.IsNullOrWhiteSpace(value) &&
        !StringComparer.Ordinal.Equals(value, "0") &&
        !StringComparer.OrdinalIgnoreCase.Equals(value, "false");
}

public static class HookEnvironment
{
    private static readonly string[] AllowedNames =
    [
        "CURSOR_VERSION",
        "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_SESSION_ID",
        "COPILOT_HOME",
        "HARNESS_SPY_HOST",
        "HARNESS_SPY_RUNTIME_ID"
    ];

    public static IReadOnlyDictionary<string, string?> Capture()
    {
        return AllowedNames.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.OrdinalIgnoreCase);
    }
}
