using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Runtimes.Copilot;

// Interprets Copilot CLI hook payloads. Copilot CLI omits an event
// discriminator, so the configured event key is the native identity. Native
// camelCase tool names (bash, powershell, view, create, edit, task) are kept
// exactly. Turns are derived from userPromptSubmitted..agentStop and labelled
// as derived, and correlation quality is Derived because the CLI supplies no
// exact tool-use id.
internal sealed class CopilotCliRuntimeEngine : HarnessRuntimeEngineBase
{
    // MCP calls arrive flattened as "<server>-<tool>" with no marker, so a
    // per-session classifier learns their identity from the permission events.
    private readonly CopilotMcpToolClassifier _mcp = new();

    public override string HarnessId => HarnessIds.GitHubCopilot;

    public override ObservationInterpretation Interpret(ObservationContext context)
    {
        JsonElement payload = context.Payload;

        // The CLI's configured event key is authoritative; the payload event
        // name is only a fallback (VS Code-compatible dialect sends one).
        string name =
            context.ConfiguredEventName ??
            context.PayloadEventName ??
            "unknownHook";

        string? sessionId = RuntimeJson.String(payload, "sessionId", "session_id");
        string? toolName = RuntimeJson.String(payload, "toolName", "tool_name");
        string? targetFilePath = RuntimeJson.ToolInputString(payload, "toolArgs", "path", "file_path")
            ?? RuntimeJson.ToolInputString(payload, "tool_input", "path", "file_path");

        var b = new InterpretationBuilder(name)
        {
            SessionId = sessionId,
            ToolName = toolName,
            TargetFilePath = targetFilePath,
            PromptText = RuntimeJson.String(payload, "prompt", "initialPrompt"),
            AssistantText = RuntimeJson.String(payload, "response"),
            Status = RuntimeJson.String(payload, "stopReason"),
            ToolKind = ToolKind(toolName),
            CorrelationQuality = CorrelationQuality.Derived,
            ParticipatesInDerivedTurns = true
        };

        b.TranscriptReferences = TranscriptReferences(payload);

        switch (name)
        {
            case "sessionStart":
                _mcp.ResetSession(sessionId);
                b.ScopeOverride = ObservationScope.SessionLifecycle;
                b.Role = ObservationRole.SessionStart;
                b.EventKind = CanonicalEventKind.SessionStarted;
                b.ShowClockTimestamp = true;
                b.ParticipatesInDerivedTurns = false;
                b.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "source"),
                    Preview(RuntimeJson.String(payload, "initialPrompt")));
                b.Fields(S("source"), S("initialPrompt"));
                break;

            case "sessionEnd":
                _mcp.ResetSession(sessionId);
                b.ScopeOverride = ObservationScope.SessionLifecycle;
                b.Role = ObservationRole.SessionEnd;
                b.EventKind = CanonicalEventKind.SessionEnded;
                b.ParticipatesInDerivedTurns = false;
                b.HeaderDetail = RuntimeJson.String(payload, "reason");
                b.Fields(S("reason"));
                break;

            case "userPromptSubmitted":
                b.Role = ObservationRole.PromptSubmitted;
                b.EventKind = CanonicalEventKind.PromptSubmitted;
                b.Direction = ObservationDirection.Input;
                b.StartsDerivedTurn = true;
                b.HeaderDetail = Preview(b.PromptText);
                b.Fields(S("prompt"));
                break;

            case "userPromptTransformed":
                b.Role = ObservationRole.PromptTransformed;
                b.EventKind = CanonicalEventKind.PromptTransformed;
                b.Direction = ObservationDirection.Input;
                b.HeaderDetail = Preview(RuntimeJson.String(payload, "transformedPrompt"));
                b.Fields(S("prompt"), S("transformedPrompt"));
                break;

            case "preToolUse":
                b.Role = ObservationRole.ToolRequest;
                b.EventKind = CanonicalEventKind.ToolRequested;
                b.Direction = ObservationDirection.Input;
                b.OpensToolCall = true;
                ApplyMcp(b, _mcp.ClassifyFlatName(sessionId, toolName));
                b.HeaderDetail = ToolDetail(toolName, targetFilePath);
                b.Fields(S("toolName"), J("toolArgs"));
                break;

            case "postToolUse":
                b.Role = ObservationRole.ToolSuccess;
                b.EventKind = CanonicalEventKind.ToolSucceeded;
                b.Direction = ObservationDirection.Output;
                // The CLI carries no tool-use id, so the completion is paired to
                // its request by tool name and canonical toolArgs.
                b.MatchStrategy = ToolCallMatchStrategy.ToolSignature;
                ApplyMcp(b, _mcp.ClassifyFlatName(sessionId, toolName));
                b.HeaderDetail = ToolDetail(toolName, targetFilePath);
                b.Fields(S("toolName"), J("toolArgs"), J("toolResult"));
                break;

            case "postToolUseFailure":
                b.Role = ObservationRole.ToolFailure;
                b.EventKind = CanonicalEventKind.ToolFailed;
                b.Direction = ObservationDirection.Output;
                b.Tone = ObservationTone.Failure;
                b.CountsAsFailure = true;
                b.MatchStrategy = ToolCallMatchStrategy.ToolSignature;
                // Keep the failure tone; only flag the MCP kind/server.
                ApplyMcp(b, _mcp.ClassifyFlatName(sessionId, toolName), applyMcpTone: false);
                b.HeaderDetail = JoinNonEmpty(toolName, Preview(RuntimeJson.String(payload, "error")));
                b.Fields(S("toolName"), J("toolArgs"), S("error"));
                break;

            case "permissionRequest":
                b.Role = ObservationRole.PermissionRequest;
                b.EventKind = CanonicalEventKind.PermissionRequested;
                b.Direction = ObservationDirection.Input;
                b.Tone = ObservationTone.Permission;
                // The "<server>/<tool>" form here teaches the session its MCP
                // split; keep the permission tone rather than the MCP tone.
                ApplyMcp(b, _mcp.RegisterFromSlashName(sessionId, toolName), applyMcpTone: false);
                b.HeaderDetail = ToolDetail(toolName, targetFilePath);
                b.Fields(FieldSpec.AllTopLevel);
                break;

            case "notification":
                b.Role = ObservationRole.Notification;
                b.EventKind = CanonicalEventKind.Notification;
                // A permission prompt is a user-blocking approval request, so it
                // is highlighted like the other permission events.
                if (RuntimeJson.String(payload, "notification_type") == "permission_prompt")
                {
                    b.Tone = ObservationTone.Permission;
                }

                // "Use MCP tool: <server>/<tool>" also teaches the session split.
                ApplyMcp(b, _mcp.RegisterFromNotification(sessionId, RuntimeJson.String(payload, "message")), applyMcpTone: false);
                b.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "notification_type"),
                    Preview(RuntimeJson.String(payload, "message")));
                b.Fields(S("notification_type"), S("title"), S("message"));
                break;

            case "agentStop":
                b.Role = ObservationRole.TurnStop;
                b.EventKind = CanonicalEventKind.TurnCompleted;
                b.Direction = ObservationDirection.Output;
                b.Tone = ObservationTone.Stop;
                b.HeaderDetail = RuntimeJson.String(payload, "stopReason");
                b.Fields(S("transcriptPath"), S("stopReason"), S("stop_hook_active"));
                break;

            case "subagentStart":
                b.Role = ObservationRole.SubagentStart;
                b.EventKind = CanonicalEventKind.SubagentStarted;
                b.Direction = ObservationDirection.Input;
                b.OpensSubagent = true;
                // CLI start lacks the stop event's agentId; correlate by name.
                b.SubagentId = RuntimeJson.String(payload, "agentName");
                b.SubagentType = RuntimeJson.String(payload, "agentName", "agentDisplayName");
                b.CorrelationQuality = CorrelationQuality.Heuristic;
                b.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "agentName"),
                    RuntimeJson.String(payload, "agentDisplayName"));
                b.Fields(S("transcriptPath"), S("agentName"), S("agentDisplayName"), S("agentDescription"));
                break;

            case "subagentStop":
                b.Role = ObservationRole.SubagentStop;
                b.EventKind = CanonicalEventKind.SubagentCompleted;
                b.Direction = ObservationDirection.Output;
                b.MatchStrategy = ToolCallMatchStrategy.Subagent;
                b.SubagentId = RuntimeJson.String(payload, "agentName", "agentId");
                b.SubagentType = RuntimeJson.String(payload, "agentType", "agentName");
                b.CorrelationQuality = CorrelationQuality.Heuristic;
                b.HoverText = b.AssistantText;
                b.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "agentType", "agentName"),
                    Preview(b.AssistantText));
                b.Fields(
                    S("transcriptPath"),
                    S("agentId"),
                    S("agentType"),
                    S("agentName"),
                    S("agentDisplayName"),
                    S("response"),
                    S("stopReason"));
                break;

            case "errorOccurred":
                b.Role = ObservationRole.RuntimeError;
                b.EventKind = CanonicalEventKind.RuntimeError;
                b.Tone = ObservationTone.Failure;
                b.HeaderDetail = JoinNonEmpty(
                    RuntimeJson.String(payload, "errorContext"),
                    Preview(ErrorMessage(payload)));
                b.Fields(J("error"), S("errorContext"), S("recoverable"));
                break;

            case "preCompact":
                b.Role = ObservationRole.CompactionStart;
                b.EventKind = CanonicalEventKind.CompactionStarted;
                b.Direction = ObservationDirection.Input;
                b.Tone = ObservationTone.Compaction;
                b.HeaderDetail = RuntimeJson.String(payload, "trigger");
                b.Fields(S("transcriptPath"), S("trigger"), S("customInstructions"));
                break;

            default:
                b.Role = ObservationRole.Generic;
                b.Fields(FieldSpec.AllTopLevel);
                break;
        }

        return b.Build();
    }

    // Copilot CLI exposes the session transcript on agentStop (and, when
    // present, sessionEnd) via transcriptPath at
    // %USERPROFILE%\.copilot\session-state\<sessionId>\events.jsonl.
    private static IReadOnlyList<TranscriptReference> TranscriptReferences(JsonElement payload)
    {
        if (RuntimeJson.String(payload, "transcriptPath") is not string path)
        {
            return [];
        }

        return [new TranscriptReference(
            string.Empty,
            path,
            TranscriptFileRole.Main,
            DialectIds.CopilotCliTranscript)];
    }

    // Marks an interpretation as an MCP call. The server name is only attached
    // once it has been learned; the MCP tone is suppressed for events (failure,
    // permission) whose own tone must win.
    private static void ApplyMcp(
        InterpretationBuilder b,
        CopilotMcpIdentity? identity,
        bool applyMcpTone = true)
    {
        if (identity is null)
        {
            return;
        }

        b.ToolKind = CanonicalToolKind.Mcp;

        if (identity.Value.ServerName is string server)
        {
            b.McpServerName = server;
        }

        if (applyMcpTone)
        {
            b.Tone = ObservationTone.Mcp;
        }
    }

    private static FieldSpec S(string name) => new(FieldSpecKind.Scalar, name);

    private static FieldSpec J(string name) => new(FieldSpecKind.JsonStringMembers, name);

    private static string? ErrorMessage(JsonElement payload)
    {
        if (RuntimeJson.String(payload, "error") is string direct)
        {
            return direct;
        }

        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("error", out JsonElement error) &&
            error.ValueKind == JsonValueKind.Object)
        {
            return RuntimeJson.String(error, "message");
        }

        return null;
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
