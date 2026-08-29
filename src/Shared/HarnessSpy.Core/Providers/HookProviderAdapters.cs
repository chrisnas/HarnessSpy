using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes.Claude;

namespace HarnessSpy.Core.Providers;

// Legacy raw-payload provider/surface detection used only when replaying a
// captured payload that has no HarnessSpy envelope. New captures carry explicit
// provider/surface metadata and go straight to the runtime engines; this
// heuristic exists so old raw files keep loading. Native names are never
// rewritten here.
public static class ProviderAdapterRegistry
{
    public static (HookProvider Provider, HookSurface Surface) DetectLegacy(JsonElement payload)
    {
        if (Has(payload, "cursor_version") || Has(payload, "conversation_id"))
        {
            return (HookProvider.Cursor, HookSurface.CursorIde);
        }

        // Copilot CLI camelCase: sessionId plus a numeric epoch timestamp.
        if (Has(payload, "sessionId") && HasNumberProperty(payload, "timestamp"))
        {
            return (HookProvider.GitHubCopilot, HookSurface.CopilotCli);
        }

        string? eventName = ReadString(payload, "hook_event_name");

        // Actual VS Code Local: PascalCase name with an ISO string timestamp and
        // VS Code ids. Detected before Claude because they share PascalCase
        // names, and only from a string timestamp so a Claude payload (no
        // timestamp) is never misread as VS Code.
        if (eventName is not null &&
            HasStringProperty(payload, "timestamp") &&
            (Has(payload, "session_id") || Has(payload, "tool_use_id") || Has(payload, "agent_id")))
        {
            return (HookProvider.GitHubCopilot, HookSurface.VsCodeAgentHooks);
        }

        // Raw Claude payloads: strong evidence (transcript_path, prompt_id,
        // permission_mode, effort) or a session_id paired with a documented
        // Claude catalog event name.
        if (Has(payload, "transcript_path") ||
            Has(payload, "prompt_id") ||
            Has(payload, "permission_mode") ||
            Has(payload, "effort") ||
            (Has(payload, "session_id") &&
             eventName is not null &&
             ClaudeHookCatalog.DocumentedEvents.Contains(eventName)))
        {
            return (HookProvider.ClaudeCode, HookSurface.ClaudeCode);
        }

        if (Has(payload, "sessionId"))
        {
            return (HookProvider.GitHubCopilot, HookSurface.CopilotCli);
        }

        return (HookProvider.Cursor, HookSurface.CursorIde);
    }

    private static bool Has(JsonElement payload, string propertyName) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(propertyName, out _);

    private static bool HasNumberProperty(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number;

    private static bool HasStringProperty(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String;

    private static string? ReadString(JsonElement payload, string propertyName) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
