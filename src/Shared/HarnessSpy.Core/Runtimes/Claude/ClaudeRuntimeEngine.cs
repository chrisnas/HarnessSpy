using System.IO;
using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Runtimes.Claude;

// Interprets Claude Code hook events using their exact native PascalCase names
// and native tool names (Bash, Read, Edit, Agent, mcp__server__tool, ...). No
// event or tool is renamed into Cursor's vocabulary.
internal sealed class ClaudeRuntimeEngine : HarnessRuntimeEngineBase
{
    public override string HarnessId => HarnessIds.ClaudeCode;

    public override ObservationInterpretation Interpret(ObservationContext context)
    {
        JsonElement payload = context.Payload;
        string name =
            context.PayloadEventName ??
            context.ConfiguredEventName ??
            "unknownHook";

        string? toolName = RuntimeJson.String(payload, "tool_name");
        string? mcpServer = McpServer(toolName);
        string? targetFilePath =
            RuntimeJson.String(payload, "file_path") ??
            RuntimeJson.NestedString(payload, "tool_input", "file_path", "path");

        var b = new InterpretationBuilder(name)
        {
            SessionId = RuntimeJson.String(payload, "session_id"),
            TurnId = RuntimeJson.String(payload, "prompt_id"),
            ToolName = toolName,
            McpServerName = mcpServer,
            TargetFilePath = targetFilePath,
            PromptText = RuntimeJson.String(payload, "prompt"),
            AssistantText = RuntimeJson.String(payload, "last_assistant_message"),
            Status = RuntimeJson.String(payload, "status"),
            SubagentId = RuntimeJson.String(payload, "agent_id"),
            SubagentType = RuntimeJson.String(payload, "agent_type"),
            ToolKind = ToolKind(toolName),
            CorrelationQuality = CorrelationQuality.Exact,
            HasTokenCounts = false
        };

        switch (name)
        {
            case "SessionStart":
                b.ScopeOverride = ObservationScope.SessionLifecycle;
                b.Role = ObservationRole.SessionStart;
                b.EventKind = CanonicalEventKind.SessionStarted;
                b.ShowClockTimestamp = true;
                b.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "source"),
                    RuntimeJson.String(payload, "model"),
                    RuntimeJson.String(payload, "session_title"));
                b.Fields(S("source"), S("model"), S("agent_type"), S("session_title"), FieldSpec.AllTopLevel);
                break;

            case "Setup":
                b.Role = ObservationRole.Generic;
                b.HeaderDetail = RuntimeJson.String(payload, "trigger");
                b.Fields(S("trigger"));
                break;

            case "InstructionsLoaded":
                b.Role = ObservationRole.InstructionsLoaded;
                b.HeaderDetail = JoinNonEmpty(
                    FileName(payload),
                    RuntimeJson.String(payload, "memory_type"),
                    RuntimeJson.String(payload, "load_reason"));
                b.Fields(
                    S("file_path"),
                    S("memory_type"),
                    S("load_reason"),
                    S("globs"),
                    S("trigger_file_path"),
                    S("parent_file_path"));
                break;

            case "SessionEnd":
                b.ScopeOverride = ObservationScope.SessionLifecycle;
                b.Role = ObservationRole.SessionEnd;
                b.EventKind = CanonicalEventKind.SessionEnded;
                b.HeaderDetail = RuntimeJson.String(payload, "reason");
                b.Fields(S("reason"));
                break;

            case "UserPromptSubmit":
                b.Role = ObservationRole.PromptSubmitted;
                b.EventKind = CanonicalEventKind.PromptSubmitted;
                b.Direction = ObservationDirection.Input;
                b.HeaderDetail = Preview(b.PromptText);
                b.Fields(S("prompt"), S("session_title"));
                break;

            case "UserPromptExpansion":
                b.Role = ObservationRole.PromptTransformed;
                b.EventKind = CanonicalEventKind.PromptTransformed;
                b.Direction = ObservationDirection.Input;
                b.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "command_name"),
                    RuntimeJson.String(payload, "command_args"));
                b.Fields(S("expansion_type"), S("command_name"), S("command_args"), S("command_source"), S("prompt"));
                break;

            case "MessageDisplay":
                b.Role = ObservationRole.Message;
                b.HeaderDetail = Preview(RuntimeJson.String(payload, "delta"));
                b.Fields(S("turn_id"), S("message_id"), S("index"), S("final"), S("delta"));
                break;

            case "PreToolUse":
                b.Role = ObservationRole.ToolRequest;
                b.EventKind = CanonicalEventKind.ToolRequested;
                b.Direction = ObservationDirection.Input;
                b.OpensToolCall = true;
                b.ToolCallId = RuntimeJson.String(payload, "tool_use_id");
                b.HeaderDetail = ToolDetail(toolName, targetFilePath);
                if (mcpServer is not null)
                {
                    b.Tone = ObservationTone.Mcp;
                }

                b.Fields(S("tool_name"), S("tool_use_id"), O("tool_input"));
                break;

            case "PermissionRequest":
                b.Role = ObservationRole.PermissionRequest;
                b.EventKind = CanonicalEventKind.PermissionRequested;
                b.Direction = ObservationDirection.Input;
                b.HeaderDetail = ToolDetail(toolName, targetFilePath);
                b.Fields(S("tool_name"), O("tool_input"), Arr("permission_suggestions"));
                break;

            case "PostToolUse":
                b.Role = ObservationRole.ToolSuccess;
                b.EventKind = CanonicalEventKind.ToolSucceeded;
                b.Direction = ObservationDirection.Output;
                b.MatchStrategy = ToolCallMatchStrategy.ToolCallId;
                b.ToolCallId = RuntimeJson.String(payload, "tool_use_id");
                b.HeaderDetail = ToolDetail(toolName, targetFilePath);
                if (mcpServer is not null)
                {
                    b.Tone = ObservationTone.Mcp;
                }

                b.Fields(S("tool_name"), S("tool_use_id"), O("tool_input"), J("tool_response"), S("duration_ms"));
                break;

            case "PostToolUseFailure":
                b.Role = ObservationRole.ToolFailure;
                b.EventKind = CanonicalEventKind.ToolFailed;
                b.Direction = ObservationDirection.Output;
                b.Tone = ObservationTone.Failure;
                b.CountsAsFailure = true;
                b.MatchStrategy = ToolCallMatchStrategy.ToolCallId;
                b.ToolCallId = RuntimeJson.String(payload, "tool_use_id");
                b.HeaderDetail = JoinNonEmpty(toolName, Preview(RuntimeJson.String(payload, "error")));
                b.Fields(S("tool_name"), S("tool_use_id"), O("tool_input"), S("error"), S("is_interrupt"), S("duration_ms"));
                break;

            case "PostToolBatch":
            {
                b.Role = ObservationRole.ToolBatch;
                b.Direction = ObservationDirection.Output;
                b.MatchStrategy = ToolCallMatchStrategy.ToolCallId;
                IReadOnlyList<string> batchIds = BatchToolCallIds(payload);
                b.BatchToolCallIds = batchIds;
                b.ToolCallId = batchIds.Count == 1 ? batchIds[0] : null;
                b.HeaderDetail = BatchDetail(payload);
                if (IsAllMcp(payload))
                {
                    b.Tone = ObservationTone.Mcp;
                }

                b.Fields(Arr("tool_calls"));
                break;
            }

            case "PermissionDenied":
                b.Role = ObservationRole.PermissionDenied;
                b.EventKind = CanonicalEventKind.PermissionDenied;
                b.Direction = ObservationDirection.Output;
                b.Tone = ObservationTone.Failure;
                b.MatchStrategy = ToolCallMatchStrategy.ToolCallId;
                b.ToolCallId = RuntimeJson.String(payload, "tool_use_id");
                b.HeaderDetail = JoinNonEmpty(toolName, Preview(RuntimeJson.String(payload, "reason")));
                b.Fields(S("tool_name"), S("tool_use_id"), O("tool_input"), S("reason"));
                break;

            case "Notification":
                b.Role = ObservationRole.Notification;
                b.EventKind = CanonicalEventKind.Notification;
                b.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "notification_type"),
                    Preview(RuntimeJson.String(payload, "message")));
                b.Fields(S("notification_type"), S("title"), S("message"));
                break;

            case "SubagentStart":
                b.Role = ObservationRole.SubagentStart;
                b.EventKind = CanonicalEventKind.SubagentStarted;
                b.Direction = ObservationDirection.Input;
                b.OpensSubagent = true;
                b.HeaderDetail = JoinNonEmpty(b.SubagentType, Short(b.SubagentId));
                b.Fields(S("agent_id"), S("agent_type"));
                break;

            case "SubagentStop":
                b.Role = ObservationRole.SubagentStop;
                b.EventKind = CanonicalEventKind.SubagentCompleted;
                b.Direction = ObservationDirection.Output;
                b.MatchStrategy = ToolCallMatchStrategy.Subagent;
                b.HoverText = b.AssistantText;
                b.HeaderDetail = JoinNonEmpty(b.SubagentType, Preview(b.AssistantText));
                b.Fields(
                    S("stop_hook_active"),
                    S("agent_id"),
                    S("agent_type"),
                    S("agent_transcript_path"),
                    S("last_assistant_message"),
                    Arr("background_tasks"),
                    Arr("session_crons"));
                break;

            case "TaskCreated":
                b.Role = ObservationRole.TaskCreated;
                b.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "task_subject"),
                    RuntimeJson.String(payload, "teammate_name"));
                b.Fields(S("task_id"), S("task_subject"), S("task_description"), S("teammate_name"), S("team_name"));
                break;

            case "TaskCompleted":
                b.Role = ObservationRole.TaskCompleted;
                b.Direction = ObservationDirection.Output;
                b.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "task_subject"),
                    RuntimeJson.String(payload, "teammate_name"));
                b.Fields(S("task_id"), S("task_subject"), S("task_description"), S("teammate_name"), S("team_name"));
                break;

            case "Stop":
                b.Role = ObservationRole.TurnStop;
                b.EventKind = CanonicalEventKind.TurnCompleted;
                b.Direction = ObservationDirection.Output;
                b.Tone = ObservationTone.Stop;
                b.HoverText = b.AssistantText;
                b.HeaderDetail = JoinNonEmpty(Preview(b.AssistantText), BackgroundCounts(payload));
                b.Fields(S("stop_hook_active"), S("last_assistant_message"), Arr("background_tasks"), Arr("session_crons"));
                break;

            case "StopFailure":
                b.Role = ObservationRole.RuntimeError;
                b.EventKind = CanonicalEventKind.RuntimeError;
                b.Direction = ObservationDirection.Output;
                b.Tone = ObservationTone.Failure;
                b.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "error"),
                    Preview(RuntimeJson.String(payload, "error_details")));
                b.Fields(S("error"), S("error_details"), S("last_assistant_message"));
                break;

            case "TeammateIdle":
                b.Role = ObservationRole.Generic;
                b.HeaderDetail = RuntimeJson.String(payload, "teammate_name");
                b.Fields(S("teammate_name"), S("team_name"));
                break;

            case "ConfigChange":
                b.Role = ObservationRole.ConfigChange;
                b.HeaderDetail = JoinNonEmpty(RuntimeJson.String(payload, "source"), FileName(payload));
                b.Fields(S("source"), S("file_path"));
                break;

            case "CwdChanged":
                b.Role = ObservationRole.WorkingDirectoryChange;
                b.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "old_cwd"),
                    RuntimeJson.String(payload, "new_cwd"));
                b.Fields(S("old_cwd"), S("new_cwd"));
                break;

            case "DirectoryAdded":
                b.Role = ObservationRole.DirectoryChange;
                b.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "directory"),
                    RuntimeJson.String(payload, "source"));
                b.Fields(S("directory"), S("source"));
                break;

            case "FileChanged":
                b.Role = ObservationRole.Generic;
                b.HeaderDetail = JoinNonEmpty(RuntimeJson.String(payload, "event"), FileName(payload));
                b.Fields(S("file_path"), S("event"));
                break;

            case "WorktreeCreate":
                b.Role = ObservationRole.Generic;
                b.HeaderDetail = RuntimeJson.String(payload, "name");
                b.Fields(S("name"));
                break;

            case "WorktreeRemove":
                b.Role = ObservationRole.Generic;
                b.HeaderDetail = RuntimeJson.String(payload, "worktree_path");
                b.Fields(S("worktree_path"));
                break;

            case "PreCompact":
                b.Role = ObservationRole.CompactionStart;
                b.EventKind = CanonicalEventKind.CompactionStarted;
                b.Direction = ObservationDirection.Input;
                b.Tone = ObservationTone.Compaction;
                b.HeaderDetail = RuntimeJson.String(payload, "trigger");
                b.Fields(S("trigger"), S("custom_instructions"));
                break;

            case "PostCompact":
                b.Role = ObservationRole.CompactionEnd;
                b.EventKind = CanonicalEventKind.CompactionCompleted;
                b.Direction = ObservationDirection.Output;
                b.Tone = ObservationTone.Compaction;
                b.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "trigger"),
                    Preview(RuntimeJson.String(payload, "compact_summary")));
                b.Fields(S("trigger"), S("compact_summary"));
                break;

            case "Elicitation":
                b.Role = ObservationRole.Generic;
                b.Direction = ObservationDirection.Input;
                b.HeaderDetail = JoinNonEmpty(mcpServerName(payload), RuntimeJson.String(payload, "mode"), Preview(RuntimeJson.String(payload, "message")));
                b.Fields(S("mcp_server_name"), S("message"), S("mode"), S("url"), S("elicitation_id"), S("requested_schema"));
                break;

            case "ElicitationResult":
                b.Role = ObservationRole.Generic;
                b.Direction = ObservationDirection.Output;
                b.HeaderDetail = JoinNonEmpty(mcpServerName(payload), RuntimeJson.String(payload, "action"));
                b.Fields(S("mcp_server_name"), S("action"), S("mode"), S("elicitation_id"), S("content"));
                break;

            case "PreModelSwitch":
                b.Role = ObservationRole.ModelSwitchStart;
                b.Direction = ObservationDirection.Input;
                b.Fields(FieldSpec.AllTopLevel);
                break;

            case "PostModelSwitch":
                b.Role = ObservationRole.ModelSwitchEnd;
                b.Direction = ObservationDirection.Output;
                b.Fields(FieldSpec.AllTopLevel);
                break;

            default:
                b.Role = ObservationRole.Generic;
                b.Fields(FieldSpec.AllTopLevel);
                break;
        }

        return b.Build();
    }

    private static string? mcpServerName(JsonElement payload) =>
        RuntimeJson.String(payload, "mcp_server_name");

    private static FieldSpec S(string name) => new(FieldSpecKind.Scalar, name);

    private static FieldSpec O(string name) => new(FieldSpecKind.ObjectMembers, name);

    private static FieldSpec J(string name) => new(FieldSpecKind.JsonStringMembers, name);

    private static FieldSpec Arr(string name) => new(FieldSpecKind.ArrayMembers, name);

    private static string? McpServer(string? toolName)
    {
        if (toolName is null || !toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return null;
        }

        string remainder = toolName[5..];
        int separator = remainder.IndexOf("__", StringComparison.Ordinal);
        return separator > 0 ? remainder[..separator] : null;
    }

    // A batch reads as an MCP execution only when every bundled call is one -
    // a batch mixing MCP and native tools has no single tone to show.
    private static bool IsAllMcp(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("tool_calls", out JsonElement calls) ||
            calls.ValueKind != JsonValueKind.Array ||
            calls.GetArrayLength() == 0)
        {
            return false;
        }

        foreach (JsonElement call in calls.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object ||
                McpServer(RuntimeJson.String(call, "tool_name")) is null)
            {
                return false;
            }
        }

        return true;
    }

    private static string? ToolDetail(string? toolName, string? targetFilePath)
    {
        if (toolName is null)
        {
            return null;
        }

        return string.IsNullOrEmpty(targetFilePath)
            ? toolName
            : $"{toolName} \u00b7 {targetFilePath}";
    }

    private static string? BatchDetail(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("tool_calls", out JsonElement calls) &&
            calls.ValueKind == JsonValueKind.Array)
        {
            int count = calls.GetArrayLength();
            return count == 1 ? "1 call" : $"{count} calls";
        }

        return null;
    }

    // A batch of exactly one call correlates unambiguously to its PreToolUse
    // by tool_use_id, the same way PostToolUse does. A batch bundling several
    // calls has no single owning pre, so the projection groups every matching
    // pre under a synthetic batch node instead of guessing a single parent.
    private static IReadOnlyList<string> BatchToolCallIds(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("tool_calls", out JsonElement calls) ||
            calls.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<string> ids = [];
        foreach (JsonElement call in calls.EnumerateArray())
        {
            if (call.ValueKind == JsonValueKind.Object &&
                RuntimeJson.String(call, "tool_use_id") is string id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static string? BackgroundCounts(JsonElement payload)
    {
        int tasks = ArrayLength(payload, "background_tasks");
        int crons = ArrayLength(payload, "session_crons");
        List<string> parts = [];
        if (tasks > 0)
        {
            parts.Add(tasks == 1 ? "1 background task" : $"{tasks} background tasks");
        }

        if (crons > 0)
        {
            parts.Add(crons == 1 ? "1 cron" : $"{crons} crons");
        }

        return parts.Count == 0 ? null : string.Join(" \u00b7 ", parts);
    }

    private static int ArrayLength(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static string? FileName(JsonElement payload)
    {
        string? filePath = RuntimeJson.String(payload, "file_path");
        return filePath is null ? null : Path.GetFileName(filePath);
    }

    private static string? Short(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        return id.Length <= 8 ? id : id[..8];
    }

    private static string? Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string oneLine = text.ReplaceLineEndings(" ").Trim();
        const int maxLength = 60;
        return oneLine.Length <= maxLength ? oneLine : oneLine[..maxLength].TrimEnd() + "\u2026";
    }

    private static string? JoinNonEmpty(params string?[] parts)
    {
        IEnumerable<string> nonEmpty = parts.Where(part => !string.IsNullOrEmpty(part))!;
        string joined = string.Join(" \u00b7 ", nonEmpty);
        return joined.Length == 0 ? null : joined;
    }
}
