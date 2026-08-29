using System.IO;
using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Runtimes.Cursor;

// Interprets Cursor IDE hook events. Cursor's native event and tool names are
// already the vocabulary the tree understands, so this engine maps them onto
// the shared roles/traits verbatim while keeping the native identity.
internal sealed class CursorRuntimeEngine : HarnessRuntimeEngineBase
{
    public override string HarnessId => HarnessIds.Cursor;

    private static readonly string[] ReadOwnerTools = ["Read"];
    private static readonly string[] EditOwnerTools = ["Write", "StrReplace", "EditNotebook"];

    public override ObservationInterpretation Interpret(ObservationContext context)
    {
        JsonElement payload = context.Payload;
        string name =
            context.PayloadEventName ??
            context.ConfiguredEventName ??
            "unknownHook";

        string? sessionId =
            RuntimeJson.String(payload, "conversation_id") ??
            RuntimeJson.String(payload, "session_id");
        string? turnId = RuntimeJson.String(payload, "generation_id");
        string? toolName = RuntimeJson.String(payload, "tool_name");
        string? mcpServer = RuntimeJson.String(payload, "mcp_server_name");
        string? targetFilePath =
            RuntimeJson.String(payload, "file_path") ??
            RuntimeJson.NestedString(payload, "tool_input", "file_path", "path");

        var interpretation = new InterpretationBuilder(name)
        {
            SessionId = sessionId,
            TurnId = turnId,
            ToolName = toolName,
            McpServerName = mcpServer,
            TargetFilePath = targetFilePath,
            PromptText = RuntimeJson.String(payload, "prompt"),
            AssistantText = RuntimeJson.String(payload, "text"),
            Task = RuntimeJson.String(payload, "task"),
            Status = RuntimeJson.String(payload, "status"),
            SubagentId = RuntimeJson.String(payload, "subagent_id"),
            SubagentType = RuntimeJson.String(payload, "subagent_type"),
            ToolKind = ToolKind(toolName),
            HasTokenCounts =
                RuntimeJson.Long(payload, "input_tokens") is not null ||
                RuntimeJson.Long(payload, "output_tokens") is not null ||
                RuntimeJson.Long(payload, "cache_read_tokens") is not null ||
                RuntimeJson.Long(payload, "cache_write_tokens") is not null
        };

        switch (name)
        {
            case "workspaceOpen":
                interpretation.ScopeOverride = ObservationScope.Workspace;
                interpretation.Role = ObservationRole.WorkspaceOpen;
                interpretation.EventKind = CanonicalEventKind.WorkspaceOpened;
                interpretation.Fields(
                    Scalar("cursor_version"),
                    Scalar("user_email"),
                    Array("workspace_roots"));
                break;

            case "sessionStart":
                interpretation.ScopeOverride = ObservationScope.SessionLifecycle;
                interpretation.Role = ObservationRole.SessionStart;
                interpretation.EventKind = CanonicalEventKind.SessionStarted;
                interpretation.ShowClockTimestamp = true;
                interpretation.HeaderDetail = RuntimeJson.String(payload, "cursor_version") is string version
                    ? $"Cursor {version}"
                    : null;
                interpretation.Fields(Scalar("is_background_agent"));
                break;

            case "sessionEnd":
                interpretation.ScopeOverride = ObservationScope.SessionLifecycle;
                interpretation.Role = ObservationRole.SessionEnd;
                interpretation.EventKind = CanonicalEventKind.SessionEnded;
                interpretation.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "reason"),
                    RuntimeJson.String(payload, "final_status"),
                    RuntimeJson.String(payload, "error_message"));
                interpretation.Fields(Scalar("is_background_agent"));
                break;

            case "beforeSubmitPrompt":
                interpretation.Role = ObservationRole.PromptSubmitted;
                interpretation.EventKind = CanonicalEventKind.PromptSubmitted;
                interpretation.Direction = ObservationDirection.Input;
                interpretation.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "composer_mode"),
                    RuntimeJson.String(payload, "model"));
                interpretation.Fields(Scalar("prompt"), Array("attachments"));
                break;

            case "promptTransformed":
                interpretation.Role = ObservationRole.PromptTransformed;
                interpretation.EventKind = CanonicalEventKind.PromptTransformed;
                interpretation.Direction = ObservationDirection.Input;
                break;

            case "afterAgentThought":
                interpretation.Role = ObservationRole.AgentThought;
                interpretation.EventKind = CanonicalEventKind.AssistantThought;
                interpretation.Tone = ObservationTone.Thought;
                interpretation.HoverText = interpretation.AssistantText;
                interpretation.Fields(Scalar("text"));
                break;

            case "afterAgentResponse":
                interpretation.Role = ObservationRole.AgentResponse;
                interpretation.EventKind = CanonicalEventKind.AssistantMessage;
                interpretation.Direction = ObservationDirection.Output;
                interpretation.HeaderDetail = TokenSummary(payload);
                interpretation.HoverText = interpretation.AssistantText;
                interpretation.Fields(Scalar("text"));
                break;

            case "preToolUse":
                interpretation.Role = ObservationRole.ToolRequest;
                interpretation.EventKind = CanonicalEventKind.ToolRequested;
                interpretation.Direction = ObservationDirection.Input;
                interpretation.OpensToolCall = true;
                interpretation.ToolCallId = RuntimeJson.String(payload, "tool_use_id");
                interpretation.HeaderDetail = ToolUseDetail(payload);
                interpretation.Fields(ObjectMembers("tool_input"));
                break;

            case "postToolUse":
                interpretation.Role = ObservationRole.ToolSuccess;
                interpretation.EventKind = CanonicalEventKind.ToolSucceeded;
                interpretation.Direction = ObservationDirection.Output;
                interpretation.MatchStrategy = ToolCallMatchStrategy.ToolCallId;
                interpretation.ToolCallId = RuntimeJson.String(payload, "tool_use_id");
                interpretation.HeaderDetail = ToolUseDetail(payload);
                interpretation.Fields(JsonStringMembers("tool_output"));
                break;

            case "postToolUseFailure":
                interpretation.Role = ObservationRole.ToolFailure;
                interpretation.EventKind = CanonicalEventKind.ToolFailed;
                interpretation.Direction = ObservationDirection.Output;
                interpretation.Tone = ObservationTone.Failure;
                interpretation.CountsAsFailure = true;
                interpretation.MatchStrategy = ToolCallMatchStrategy.ToolCallId;
                interpretation.ToolCallId = RuntimeJson.String(payload, "tool_use_id");
                interpretation.HeaderDetail = FailureDetail(payload);
                interpretation.Fields(
                    ObjectMembers("tool_input"),
                    Scalar("is_interrupt"),
                    Scalar("failure_type"),
                    Scalar("error_message"));
                break;

            case "beforeShellExecution":
                interpretation.Role = ObservationRole.InnerExecutionStart;
                interpretation.Direction = ObservationDirection.Input;
                interpretation.MatchStrategy = ToolCallMatchStrategy.ExecutionEvidence;
                interpretation.InnerExecutionKind = "beforeShellExecution";
                interpretation.InnerExecutionOwnerTool = "Shell";
                interpretation.InnerCategory = InnerExecutionCategory.Shell;
                interpretation.HeaderDetail = RuntimeJson.String(payload, "command");
                interpretation.Fields(Scalar("command"), Scalar("cwd"), Scalar("sandbox"));
                break;

            case "afterShellExecution":
                interpretation.Role = ObservationRole.InnerExecutionEnd;
                interpretation.Direction = ObservationDirection.Output;
                interpretation.MatchStrategy = ToolCallMatchStrategy.ExecutionEvidence;
                interpretation.InnerExecutionKind = "beforeShellExecution";
                interpretation.InnerExecutionOwnerTool = "Shell";
                interpretation.InnerCategory = InnerExecutionCategory.Shell;
                interpretation.Fields(Scalar("command"), Scalar("output"), Scalar("sandbox"));
                break;

            case "beforeMCPExecution":
                interpretation.Role = ObservationRole.InnerExecutionStart;
                interpretation.Direction = ObservationDirection.Input;
                interpretation.Tone = ObservationTone.Mcp;
                interpretation.MatchStrategy = ToolCallMatchStrategy.ExecutionEvidence;
                interpretation.InnerExecutionKind = "beforeMCPExecution";
                interpretation.InnerExecutionOwnerTool = toolName is null ? null : $"MCP:{toolName}";
                interpretation.InnerCategory = InnerExecutionCategory.Mcp;
                interpretation.HeaderDetail = JoinNonEmpty(mcpServer, toolName);
                interpretation.Fields(
                    Scalar("mcp_server_name"),
                    Scalar("tool_name"),
                    Scalar("command"),
                    JsonStringMembers("tool_input"));
                break;

            case "afterMCPExecution":
                interpretation.Role = ObservationRole.InnerExecutionEnd;
                interpretation.Direction = ObservationDirection.Output;
                interpretation.Tone = ObservationTone.Mcp;
                interpretation.MatchStrategy = ToolCallMatchStrategy.ExecutionEvidence;
                interpretation.InnerExecutionKind = "beforeMCPExecution";
                interpretation.InnerExecutionOwnerTool = toolName is null ? null : $"MCP:{toolName}";
                interpretation.InnerCategory = InnerExecutionCategory.Mcp;
                interpretation.HeaderDetail = JoinNonEmpty(mcpServer, toolName);
                interpretation.Fields(
                    Scalar("mcp_server_name"),
                    Scalar("tool_name"),
                    Scalar("command"),
                    JsonStringMembers("tool_input"),
                    JsonStringMembers("result_json"));
                break;

            case "beforeReadFile":
                interpretation.Role = ObservationRole.FileAccess;
                interpretation.Direction = ObservationDirection.Input;
                interpretation.MatchStrategy = ToolCallMatchStrategy.FileTarget;
                interpretation.InnerExecutionKind = "beforeReadFile";
                interpretation.InnerCategory = InnerExecutionCategory.FileRead;
                interpretation.FileAccessOwnerTools = ReadOwnerTools;
                interpretation.HeaderDetail = FileName(payload);
                interpretation.Fields(Scalar("file_path"), Scalar("content"), Array("attachments"));
                break;

            case "afterFileEdit":
                interpretation.Role = ObservationRole.FileAccess;
                interpretation.Direction = ObservationDirection.Output;
                interpretation.MatchStrategy = ToolCallMatchStrategy.FileTarget;
                interpretation.InnerExecutionKind = "afterFileEdit";
                interpretation.InnerCategory = InnerExecutionCategory.FileEdit;
                interpretation.FileAccessOwnerTools = EditOwnerTools;
                interpretation.HeaderDetail = FileName(payload);
                interpretation.Fields(Scalar("file_path"), Array("edits"));
                break;

            case "beforeTabFileRead":
                interpretation.ScopeOverride = ObservationScope.Tab;
                interpretation.Direction = ObservationDirection.Input;
                interpretation.HeaderDetail = FileName(payload);
                interpretation.Fields(Scalar("content"));
                break;

            case "afterTabFileEdit":
                interpretation.ScopeOverride = ObservationScope.Tab;
                interpretation.Direction = ObservationDirection.Output;
                interpretation.HeaderDetail = FileName(payload);
                interpretation.Fields(FlattenedArray("edits"));
                break;

            case "subagentStart":
                interpretation.Role = ObservationRole.SubagentStart;
                interpretation.EventKind = CanonicalEventKind.SubagentStarted;
                interpretation.Direction = ObservationDirection.Input;
                interpretation.OpensSubagent = true;
                interpretation.Fields(SubagentFields());
                break;

            case "subagentStop":
                interpretation.Role = ObservationRole.SubagentStop;
                interpretation.EventKind = CanonicalEventKind.SubagentCompleted;
                interpretation.Direction = ObservationDirection.Output;
                interpretation.MatchStrategy = ToolCallMatchStrategy.Subagent;
                interpretation.Fields(SubagentFields());
                break;

            case "preCompact":
                interpretation.Role = ObservationRole.CompactionStart;
                interpretation.EventKind = CanonicalEventKind.CompactionStarted;
                interpretation.Direction = ObservationDirection.Input;
                interpretation.Tone = ObservationTone.Compaction;
                interpretation.HeaderDetail = PreCompactDetail(payload);
                interpretation.Fields(new FieldSpec(FieldSpecKind.PreCompactSummary));
                break;

            case "stop":
                interpretation.Role = ObservationRole.TurnStop;
                interpretation.EventKind = CanonicalEventKind.TurnCompleted;
                interpretation.Direction = ObservationDirection.Output;
                interpretation.Tone = ObservationTone.Stop;
                interpretation.IsAbortedStop =
                    StringComparer.OrdinalIgnoreCase.Equals(interpretation.Status, "aborted");
                interpretation.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "status"),
                    TokenSummary(payload));
                interpretation.Fields(Scalar("loop_count"));
                break;

            default:
                interpretation.Direction = DirectionFromName(name);
                break;
        }

        return interpretation.Build();
    }

    private static ObservationDirection DirectionFromName(string name)
    {
        if (name.StartsWith("before", StringComparison.Ordinal) ||
            name.StartsWith("pre", StringComparison.Ordinal))
        {
            return ObservationDirection.Input;
        }

        if (name.StartsWith("after", StringComparison.Ordinal) ||
            name.StartsWith("post", StringComparison.Ordinal))
        {
            return ObservationDirection.Output;
        }

        return ObservationDirection.None;
    }

    private static FieldSpec Scalar(string name) => new(FieldSpecKind.Scalar, name);

    private static FieldSpec Array(string name) => new(FieldSpecKind.ArrayMembers, name);

    private static FieldSpec FlattenedArray(string name) => new(FieldSpecKind.FlattenedArrayMembers, name);

    private static FieldSpec ObjectMembers(string name) => new(FieldSpecKind.ObjectMembers, name);

    private static FieldSpec JsonStringMembers(string name) => new(FieldSpecKind.JsonStringMembers, name);

    private static FieldSpec[] SubagentFields() =>
    [
        new(FieldSpecKind.Scalar, "subagent_type"),
        new(FieldSpecKind.Scalar, "task"),
        new(FieldSpecKind.ScalarPreferred, "model", "subagent_model"),
        new(FieldSpecKind.Scalar, "git_branch")
    ];

    private static string? ToolUseDetail(JsonElement payload)
    {
        string? toolName = RuntimeJson.String(payload, "tool_name");
        string? model = RuntimeJson.String(payload, "model");

        string? head = toolName;
        if (!string.IsNullOrEmpty(model))
        {
            head = string.IsNullOrEmpty(head) ? $"({model})" : $"{head} ({model})";
        }

        if (toolName is "Read" or "Write")
        {
            string? filePath = RuntimeJson.NestedString(payload, "tool_input", "file_path", "path");
            if (!string.IsNullOrEmpty(filePath))
            {
                head = JoinNonEmpty(head, filePath);
            }
        }

        return head;
    }

    private static string? TokenSummary(JsonElement payload)
    {
        List<string> parts = [];
        if (RuntimeJson.Long(payload, "input_tokens") is long input)
        {
            parts.Add($"in {HookObservation.FormatTokens(input)}");
        }

        if (RuntimeJson.Long(payload, "output_tokens") is long output)
        {
            parts.Add($"out {HookObservation.FormatTokens(output)}");
        }

        if (RuntimeJson.Long(payload, "cache_read_tokens") is long cacheRead)
        {
            parts.Add($"cache r {HookObservation.FormatTokens(cacheRead)}");
        }

        if (RuntimeJson.Long(payload, "cache_write_tokens") is long cacheWrite)
        {
            parts.Add($"cache w {HookObservation.FormatTokens(cacheWrite)}");
        }

        return parts.Count == 0 ? null : string.Join(" \u00b7 ", parts);
    }

    private static string? FailureDetail(JsonElement payload)
    {
        string? errorMessage =
            RuntimeJson.String(payload, "error_message") ??
            RuntimeJson.String(payload, "error");
        return JoinNonEmpty(
            RuntimeJson.String(payload, "tool_name"),
            RuntimeJson.String(payload, "failure_type"),
            FailurePreview(errorMessage));
    }

    private static string? FailurePreview(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
        {
            return null;
        }

        string normalized = errorMessage.ReplaceLineEndings("\n");
        int newlineIndex = normalized.IndexOf('\n');
        return newlineIndex < 0 ? normalized : normalized[..newlineIndex].TrimEnd() + "...";
    }

    private static string? PreCompactDetail(JsonElement payload)
    {
        List<string> parts = [];
        if (RuntimeJson.String(payload, "trigger") is string trigger)
        {
            parts.Add(trigger);
        }

        if (RuntimeJson.Double(payload, "context_usage_percent") is double usage)
        {
            parts.Add($"{usage:0.##}%");
        }

        if (RuntimeJson.Long(payload, "context_window_size") is long windowSize)
        {
            parts.Add(HookObservation.FormatTokens(windowSize));
        }

        long? messageCount = RuntimeJson.Long(payload, "message_count");
        long? messagesToCompact = RuntimeJson.Long(payload, "messages_to_compact");
        if (messageCount is not null || messagesToCompact is not null)
        {
            parts.Add($"{messageCount?.ToString() ?? "?"}/{messagesToCompact?.ToString() ?? "?"}");
        }

        return parts.Count == 0 ? null : string.Join(" \u00b7 ", parts);
    }

    private static string? FileName(JsonElement payload)
    {
        string? filePath = RuntimeJson.String(payload, "file_path");
        return filePath is null ? null : Path.GetFileName(filePath);
    }

    private static string? JoinNonEmpty(params string?[] parts)
    {
        IEnumerable<string> nonEmpty = parts.Where(part => !string.IsNullOrEmpty(part))!;
        string joined = string.Join(" \u00b7 ", nonEmpty);
        return joined.Length == 0 ? null : joined;
    }
}
