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
        // Explicit, generated configuration metadata is authoritative for
        // short-lived hook collectors; payload heuristics are only a fallback.
        if (RuntimeIdSurface(environment) is HookSurface declared)
        {
            return declared;
        }

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

        // Copilot CLI camelCase: session id plus a numeric epoch timestamp.
        if (Has(payload, "sessionId") &&
            payload.TryGetProperty("timestamp", out JsonElement nativeTimestamp) &&
            nativeTimestamp.ValueKind == JsonValueKind.Number)
        {
            return HookSurface.CopilotCli;
        }

        // Actual VS Code Local observations are only inferred from payload shape
        // as a legacy fallback, never solely from PascalCase + snake_case, since
        // Copilot CLI can emit that same VS Code-compatible dialect. An ISO
        // string timestamp plus VS Code ids is the documented host evidence.
        if (Has(payload, "hook_event_name") &&
            payload.TryGetProperty("timestamp", out JsonElement isoTimestamp) &&
            isoTimestamp.ValueKind == JsonValueKind.String &&
            (Has(payload, "session_id") ||
             Has(payload, "tool_use_id") ||
             Has(payload, "agent_id")))
        {
            return HookSurface.VsCodeAgentHooks;
        }

        return expected.Surface;
    }

    // Maps the generated HARNESS_SPY_RUNTIME_ID to a surface. This is set by the
    // installer-generated hook configuration and is authoritative.
    private static HookSurface? RuntimeIdSurface(
        IReadOnlyDictionary<string, string?> environment)
    {
        if (!environment.TryGetValue("HARNESS_SPY_RUNTIME_ID", out string? runtimeId) ||
            string.IsNullOrWhiteSpace(runtimeId))
        {
            return null;
        }

        return runtimeId switch
        {
            "cursor" => HookSurface.CursorIde,
            "claude-code" => HookSurface.ClaudeCode,
            "github-copilot" or "copilot-cli" => HookSurface.CopilotCli,
            "vscode-agent-hooks" => HookSurface.VsCodeAgentHooks,
            _ => null
        };
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
    // Explicit allowlist so arbitrary environment variables are never captured.
    private static readonly string[] AllowedNames =
    [
        "CURSOR_VERSION",
        "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_REMOTE",
        "CLAUDE_CODE_BRIDGE_SESSION_ID",
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
