using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Runtimes.Copilot;

// Interprets the VS Code Local agent-hooks Preview dialect: PascalCase event
// keys, snake_case fields, exact tool_use_id and agent_id. Native tool names
// (e.g. editFiles) are preserved. Turns are derived from prompt/stop because
// this dialect exposes no turn id.
internal sealed class VsCodeLocalRuntimeEngine : HarnessRuntimeEngineBase
{
    public override string HarnessId => HarnessIds.GitHubCopilot;

    public override ObservationInterpretation Interpret(ObservationContext context)
    {
        JsonElement payload = context.Payload;
        string name =
            context.PayloadEventName ??
            context.ConfiguredEventName ??
            "unknownHook";

        string? toolName = RuntimeJson.String(payload, "tool_name");
        IReadOnlyList<string> targetFilePaths = RuntimeJson.NestedStringArray(payload, "tool_input", "files");
        string? targetFilePath =
            RuntimeJson.String(payload, "file_path") ??
            RuntimeJson.NestedString(payload, "tool_input", "file_path", "path") ??
            (targetFilePaths.Count > 0 ? targetFilePaths[0] : null);

        var b = new InterpretationBuilder(name)
        {
            SessionId = RuntimeJson.String(payload, "session_id"),
            ToolName = toolName,
            TargetFilePath = targetFilePath,
            TargetFilePaths = targetFilePaths,
            PromptText = RuntimeJson.String(payload, "prompt"),
            SubagentId = RuntimeJson.String(payload, "agent_id"),
            SubagentType = RuntimeJson.String(payload, "agent_type"),
            ToolKind = ToolKind(toolName),
            CorrelationQuality = CorrelationQuality.Exact,
            ParticipatesInDerivedTurns = true
        };

        switch (name)
        {
            case "SessionStart":
                b.ScopeOverride = ObservationScope.SessionLifecycle;
                b.Role = ObservationRole.SessionStart;
                b.EventKind = CanonicalEventKind.SessionStarted;
                b.ShowClockTimestamp = true;
                b.ParticipatesInDerivedTurns = false;
                b.HeaderDetail = RuntimeJson.String(payload, "source");
                b.Fields(S("source"), S("model"), S("agent_type"));
                break;

            case "UserPromptSubmit":
                b.Role = ObservationRole.PromptSubmitted;
                b.EventKind = CanonicalEventKind.PromptSubmitted;
                b.Direction = ObservationDirection.Input;
                b.StartsDerivedTurn = true;
                b.HeaderDetail = Preview(b.PromptText);
                b.Fields(S("prompt"));
                break;

            case "PreToolUse":
                b.Role = ObservationRole.ToolRequest;
                b.EventKind = CanonicalEventKind.ToolRequested;
                b.Direction = ObservationDirection.Input;
                b.OpensToolCall = true;
                b.ToolCallId = RuntimeJson.String(payload, "tool_use_id");
                b.HeaderDetail = ToolDetail(toolName, targetFilePath);
                b.Fields(S("tool_name"), S("tool_use_id"), O("tool_input"));
                break;

            case "PostToolUse":
                b.Role = ObservationRole.ToolSuccess;
                b.EventKind = CanonicalEventKind.ToolSucceeded;
                b.Direction = ObservationDirection.Output;
                b.MatchStrategy = ToolCallMatchStrategy.ToolCallId;
                b.ToolCallId = RuntimeJson.String(payload, "tool_use_id");
                b.HeaderDetail = ToolDetail(toolName, targetFilePath);
                b.Fields(S("tool_name"), S("tool_use_id"), O("tool_input"), J("tool_response"));
                break;

            case "PreCompact":
                b.Role = ObservationRole.CompactionStart;
                b.EventKind = CanonicalEventKind.CompactionStarted;
                b.Direction = ObservationDirection.Input;
                b.Tone = ObservationTone.Compaction;
                b.HeaderDetail = RuntimeJson.String(payload, "trigger");
                b.Fields(S("trigger"), S("custom_instructions"));
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
                b.HeaderDetail = JoinNonEmpty(b.SubagentType, Short(b.SubagentId));
                b.Fields(S("agent_id"), S("agent_type"), S("stop_hook_active"));
                break;

            case "Stop":
                b.Role = ObservationRole.TurnStop;
                b.EventKind = CanonicalEventKind.TurnCompleted;
                b.Direction = ObservationDirection.Output;
                b.Tone = ObservationTone.Stop;
                b.Fields(S("stop_hook_active"));
                break;

            default:
                b.Role = ObservationRole.Generic;
                b.Fields(FieldSpec.AllTopLevel);
                break;
        }

        return b.Build();
    }

    private static FieldSpec S(string name) => new(FieldSpecKind.Scalar, name);

    private static FieldSpec O(string name) => new(FieldSpecKind.ObjectMembers, name);

    private static FieldSpec J(string name) => new(FieldSpecKind.JsonStringMembers, name);

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

    private static string? Short(string? id) =>
        string.IsNullOrEmpty(id) ? null : id.Length <= 8 ? id : id[..8];

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
