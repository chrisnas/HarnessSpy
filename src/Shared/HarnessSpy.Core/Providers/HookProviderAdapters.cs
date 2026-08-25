using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Providers;

public sealed record ProviderNormalization(
    HookProvider Provider,
    HookSurface Surface,
    string RawEventName,
    string CanonicalEventName,
    CanonicalEventKind EventKind,
    CanonicalToolKind ToolKind,
    CorrelationQuality CorrelationQuality,
    JsonElement RawPayload,
    JsonElement CanonicalPayload);

public interface IProviderEventAdapter
{
    HookProvider Provider { get; }

    ProviderNormalization Normalize(
        JsonElement rawPayload,
        HookSurface surface,
        string? configuredEventName);
}

public interface IHookProviderAdapter : IProviderEventAdapter;

public static class ProviderAdapterRegistry
{
    private static readonly CursorHookProviderAdapter Cursor = new();
    private static readonly ClaudeHookProviderAdapter Claude = new();
    private static readonly CopilotHookProviderAdapter Copilot = new();

    public static IHookProviderAdapter Get(HookProvider provider) => provider switch
    {
        HookProvider.ClaudeCode => Claude,
        HookProvider.GitHubCopilot => Copilot,
        _ => Cursor
    };

    public static (HookProvider Provider, HookSurface Surface) DetectLegacy(JsonElement payload)
    {
        if (Has(payload, "cursor_version") || Has(payload, "conversation_id"))
        {
            return (HookProvider.Cursor, HookSurface.CursorIde);
        }

        if (Has(payload, "sessionId"))
        {
            return (HookProvider.GitHubCopilot, HookSurface.CopilotCli);
        }

        string? eventName = ReadString(payload, "hook_event_name");
        if (Has(payload, "prompt_id") ||
            Has(payload, "permission_mode") ||
            eventName is "SessionEnd" or "PostToolUseFailure" or "PostCompact")
        {
            return (HookProvider.ClaudeCode, HookSurface.ClaudeCode);
        }

        if (eventName is not null &&
            char.IsUpper(eventName[0]) &&
            Has(payload, "timestamp"))
        {
            return (HookProvider.GitHubCopilot, HookSurface.VsCodeAgentHooks);
        }

        return (HookProvider.Cursor, HookSurface.CursorIde);
    }

    private static bool Has(JsonElement payload, string propertyName) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(propertyName, out _);

    private static string? ReadString(JsonElement payload, string propertyName) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

internal abstract class HookProviderAdapterBase : IHookProviderAdapter
{
    public abstract HookProvider Provider { get; }

    public abstract ProviderNormalization Normalize(
        JsonElement rawPayload,
        HookSurface surface,
        string? configuredEventName);

    protected static JsonObject ToObject(JsonElement rawPayload) =>
        JsonNode.Parse(rawPayload.GetRawText()) as JsonObject ?? new JsonObject();

    protected static JsonElement ToElement(JsonObject payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload.ToJsonString());
        return document.RootElement.Clone();
    }

    protected static string? String(JsonElement payload, params string[] names)
    {
        foreach (string name in names)
        {
            if (payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    protected static JsonNode? Node(JsonElement payload, params string[] names)
    {
        foreach (string name in names)
        {
            if (payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty(name, out JsonElement value))
            {
                return JsonNode.Parse(value.GetRawText());
            }
        }

        return null;
    }

    protected static void SetIfMissing(JsonObject target, string name, JsonNode? value)
    {
        if (!target.ContainsKey(name) && value is not null)
        {
            target[name] = value;
        }
    }

    protected static CanonicalEventKind EventKind(string canonicalName) => canonicalName switch
    {
        "workspaceOpen" => CanonicalEventKind.WorkspaceOpened,
        "sessionStart" => CanonicalEventKind.SessionStarted,
        "sessionEnd" => CanonicalEventKind.SessionEnded,
        "beforeSubmitPrompt" => CanonicalEventKind.PromptSubmitted,
        "promptTransformed" => CanonicalEventKind.PromptTransformed,
        "afterAgentResponse" => CanonicalEventKind.AssistantMessage,
        "afterAgentThought" => CanonicalEventKind.AssistantThought,
        "preToolUse" => CanonicalEventKind.ToolRequested,
        "postToolUse" => CanonicalEventKind.ToolSucceeded,
        "postToolUseFailure" => CanonicalEventKind.ToolFailed,
        "permissionRequest" => CanonicalEventKind.PermissionRequested,
        "permissionDenied" => CanonicalEventKind.PermissionDenied,
        "subagentStart" => CanonicalEventKind.SubagentStarted,
        "subagentStop" => CanonicalEventKind.SubagentCompleted,
        "preCompact" => CanonicalEventKind.CompactionStarted,
        "postCompact" => CanonicalEventKind.CompactionCompleted,
        "stop" => CanonicalEventKind.TurnCompleted,
        "stopFailure" or "errorOccurred" => CanonicalEventKind.RuntimeError,
        "notification" => CanonicalEventKind.Notification,
        _ => CanonicalEventKind.ProviderSpecific
    };

    protected static CanonicalToolKind ToolKind(string? nativeToolName)
    {
        if (string.IsNullOrWhiteSpace(nativeToolName))
        {
            return CanonicalToolKind.Unknown;
        }

        if (nativeToolName.StartsWith("MCP:", StringComparison.Ordinal) ||
            nativeToolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return CanonicalToolKind.Mcp;
        }

        return nativeToolName.ToLowerInvariant() switch
        {
            "shell" or "bash" or "powershell" => CanonicalToolKind.Shell,
            "read" or "view" => CanonicalToolKind.FileRead,
            "write" or "create" => CanonicalToolKind.FileWrite,
            "edit" or "strreplace" or "str_replace_editor" or "apply_patch" =>
                CanonicalToolKind.FileEdit,
            "delete" => CanonicalToolKind.FileDelete,
            "grep" or "rg" => CanonicalToolKind.TextSearch,
            "glob" => CanonicalToolKind.FileSearch,
            "editnotebook" or "notebookedit" => CanonicalToolKind.Notebook,
            "task" or "agent" => CanonicalToolKind.Agent,
            "webfetch" or "websearch" or "web_fetch" or "web_search" =>
                CanonicalToolKind.Web,
            "askuserquestion" or "ask_user" => CanonicalToolKind.UserInteraction,
            "todowrite" or "update_todo" => CanonicalToolKind.Task,
            _ => CanonicalToolKind.Unknown
        };
    }
}

internal sealed class CursorHookProviderAdapter : HookProviderAdapterBase
{
    public override HookProvider Provider => HookProvider.Cursor;

    public override ProviderNormalization Normalize(
        JsonElement rawPayload,
        HookSurface surface,
        string? configuredEventName)
    {
        string rawEvent =
            String(rawPayload, "hook_event_name") ??
            configuredEventName ??
            "unknownHook";
        string? toolName = String(rawPayload, "tool_name");
        return new ProviderNormalization(
            Provider,
            surface,
            rawEvent,
            rawEvent,
            EventKind(rawEvent),
            ToolKind(toolName),
            CorrelationQuality.Exact,
            rawPayload.Clone(),
            rawPayload.Clone());
    }
}

internal sealed class ClaudeHookProviderAdapter : HookProviderAdapterBase
{
    private static readonly IReadOnlyDictionary<string, string> Events =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SessionStart"] = "sessionStart",
            ["SessionEnd"] = "sessionEnd",
            ["UserPromptSubmit"] = "beforeSubmitPrompt",
            ["UserPromptExpansion"] = "promptTransformed",
            ["PreToolUse"] = "preToolUse",
            ["PostToolUse"] = "postToolUse",
            ["PostToolUseFailure"] = "postToolUseFailure",
            ["PermissionRequest"] = "permissionRequest",
            ["PermissionDenied"] = "permissionDenied",
            ["SubagentStart"] = "subagentStart",
            ["SubagentStop"] = "subagentStop",
            ["PreCompact"] = "preCompact",
            ["PostCompact"] = "postCompact",
            ["Stop"] = "stop",
            ["StopFailure"] = "stopFailure",
            ["Notification"] = "notification"
        };

    public override HookProvider Provider => HookProvider.ClaudeCode;

    public override ProviderNormalization Normalize(
        JsonElement rawPayload,
        HookSurface surface,
        string? configuredEventName)
    {
        string rawEvent =
            String(rawPayload, "hook_event_name") ??
            configuredEventName ??
            "unknownHook";
        string canonicalEvent = Events.GetValueOrDefault(rawEvent, rawEvent);
        JsonObject canonical = ToObject(rawPayload);
        canonical["hook_event_name"] = canonicalEvent;

        SetIfMissing(canonical, "conversation_id",
            JsonValue.Create(String(rawPayload, "session_id")));
        SetIfMissing(canonical, "generation_id",
            JsonValue.Create(String(rawPayload, "prompt_id")));
        SetIfMissing(canonical, "subagent_id",
            JsonValue.Create(String(rawPayload, "agent_id")));
        SetIfMissing(canonical, "subagent_type",
            JsonValue.Create(String(rawPayload, "agent_type")));
        SetIfMissing(canonical, "error_message",
            JsonValue.Create(String(rawPayload, "error")));
        SetIfMissing(canonical, "text",
            JsonValue.Create(String(rawPayload, "last_assistant_message")));

        string? cwd = String(rawPayload, "cwd");
        if (!canonical.ContainsKey("workspace_roots") && cwd is not null)
        {
            canonical["workspace_roots"] = new JsonArray(cwd);
        }

        string? nativeToolName = String(rawPayload, "tool_name");
        string? canonicalToolName = MapClaudeToolName(nativeToolName);
        if (canonicalToolName is not null)
        {
            canonical["tool_name"] = canonicalToolName;
        }

        if (rawPayload.TryGetProperty("tool_response", out JsonElement response))
        {
            canonical["tool_output"] = response.ValueKind == JsonValueKind.String
                ? response.GetString()
                : response.GetRawText();
        }

        if (nativeToolName?.StartsWith("mcp__", StringComparison.Ordinal) == true)
        {
            string remainder = nativeToolName[5..];
            int separator = remainder.IndexOf("__", StringComparison.Ordinal);
            if (separator > 0)
            {
                canonical["mcp_server_name"] = remainder[..separator];
            }
        }

        return new ProviderNormalization(
            Provider,
            surface,
            rawEvent,
            canonicalEvent,
            EventKind(canonicalEvent),
            ToolKind(nativeToolName),
            CorrelationQuality.Exact,
            rawPayload.Clone(),
            ToElement(canonical));
    }

    private static string? MapClaudeToolName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        if (name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            string remainder = name[5..];
            int separator = remainder.IndexOf("__", StringComparison.Ordinal);
            return $"MCP:{(separator >= 0 ? remainder[(separator + 2)..] : remainder)}";
        }

        return name switch
        {
            "Bash" or "PowerShell" => "Shell",
            "Edit" => "Write",
            "Agent" => "Task",
            _ => name
        };
    }
}

internal sealed class CopilotHookProviderAdapter : HookProviderAdapterBase
{
    private static readonly IReadOnlyDictionary<string, string> Events =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sessionStart"] = "sessionStart",
            ["SessionStart"] = "sessionStart",
            ["sessionEnd"] = "sessionEnd",
            ["SessionEnd"] = "sessionEnd",
            ["userPromptSubmitted"] = "beforeSubmitPrompt",
            ["UserPromptSubmit"] = "beforeSubmitPrompt",
            ["userPromptTransformed"] = "promptTransformed",
            ["preToolUse"] = "preToolUse",
            ["PreToolUse"] = "preToolUse",
            ["postToolUse"] = "postToolUse",
            ["PostToolUse"] = "postToolUse",
            ["postToolUseFailure"] = "postToolUseFailure",
            ["PostToolUseFailure"] = "postToolUseFailure",
            ["permissionRequest"] = "permissionRequest",
            ["PermissionRequest"] = "permissionRequest",
            ["subagentStart"] = "subagentStart",
            ["SubagentStart"] = "subagentStart",
            ["subagentStop"] = "subagentStop",
            ["SubagentStop"] = "subagentStop",
            ["preCompact"] = "preCompact",
            ["PreCompact"] = "preCompact",
            ["agentStop"] = "stop",
            ["Stop"] = "stop",
            ["errorOccurred"] = "errorOccurred",
            ["ErrorOccurred"] = "errorOccurred",
            ["notification"] = "notification",
            ["Notification"] = "notification"
        };

    public override HookProvider Provider => HookProvider.GitHubCopilot;

    public override ProviderNormalization Normalize(
        JsonElement rawPayload,
        HookSurface surface,
        string? configuredEventName)
    {
        string rawEvent =
            String(rawPayload, "hook_event_name") ??
            configuredEventName ??
            "unknownHook";
        string canonicalEvent = Events.GetValueOrDefault(rawEvent, rawEvent);
        JsonObject canonical = ToObject(rawPayload);
        canonical["hook_event_name"] = canonicalEvent;

        SetIfMissing(canonical, "conversation_id",
            JsonValue.Create(String(rawPayload, "sessionId", "session_id")));
        SetIfMissing(canonical, "session_id",
            JsonValue.Create(String(rawPayload, "sessionId", "session_id")));
        SetIfMissing(canonical, "generation_id",
            JsonValue.Create(String(rawPayload, "interactionId", "interaction_id")));
        SetIfMissing(canonical, "subagent_id",
            JsonValue.Create(String(rawPayload, "agentId", "agent_id")));
        SetIfMissing(canonical, "subagent_type",
            JsonValue.Create(String(rawPayload, "agentType", "agent_type", "agentName")));
        SetIfMissing(canonical, "error_message",
            JsonValue.Create(ReadCopilotError(rawPayload)));
        SetIfMissing(canonical, "text",
            JsonValue.Create(String(rawPayload, "response", "last_assistant_message")));

        string? cwd = String(rawPayload, "cwd");
        if (!canonical.ContainsKey("workspace_roots") && cwd is not null)
        {
            canonical["workspace_roots"] = new JsonArray(cwd);
        }

        string? nativeToolName = String(rawPayload, "toolName", "tool_name");
        string? mappedToolName = MapCopilotToolName(nativeToolName);
        if (mappedToolName is not null)
        {
            canonical["tool_name"] = mappedToolName;
        }

        JsonNode? toolInput = Node(rawPayload, "toolArgs", "tool_input");
        if (toolInput is JsonValue jsonString &&
            jsonString.TryGetValue(out string? encoded) &&
            !string.IsNullOrWhiteSpace(encoded))
        {
            try
            {
                toolInput = JsonNode.Parse(encoded);
            }
            catch (JsonException)
            {
            }
        }

        SetIfMissing(canonical, "tool_input", toolInput);

        JsonNode? result = Node(rawPayload, "toolResult", "tool_response");
        if (result is not null)
        {
            canonical["tool_output"] = result.ToJsonString();
        }

        CorrelationQuality correlation =
            String(rawPayload, "tool_use_id", "interactionId", "interaction_id") is not null
                ? CorrelationQuality.Exact
                : CorrelationQuality.Derived;

        return new ProviderNormalization(
            Provider,
            surface,
            rawEvent,
            canonicalEvent,
            EventKind(canonicalEvent),
            ToolKind(nativeToolName),
            correlation,
            rawPayload.Clone(),
            ToElement(canonical));
    }

    private static string? ReadCopilotError(JsonElement rawPayload)
    {
        if (String(rawPayload, "error") is string direct)
        {
            return direct;
        }

        if (rawPayload.TryGetProperty("error", out JsonElement error) &&
            error.ValueKind == JsonValueKind.Object)
        {
            return String(error, "message");
        }

        return null;
    }

    private static string? MapCopilotToolName(string? name) => name?.ToLowerInvariant() switch
    {
        "bash" or "powershell" => "Shell",
        "view" => "Read",
        "create" => "Write",
        "edit" or "str_replace_editor" or "apply_patch" => "Write",
        "grep" or "rg" => "Grep",
        "glob" => "Glob",
        "task" => "Task",
        _ => name
    };
}
