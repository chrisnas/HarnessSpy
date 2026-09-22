using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;
using HarnessSpy.Core.Sessions.Copilot;
using HarnessSpy.Core.Sources;
using TranscriptMcpIdentity = HarnessSpy.Core.Sessions.Copilot.CopilotMcpIdentity;

namespace HarnessSpy.Core.Runtimes.Copilot;

// Parses Copilot CLI session-state events.jsonl (schema v1, verified against
// Copilot 1.0.81/1.0.82/1.0.86). Every row is {type,data,id,timestamp,parentId}.
// MCP metadata is authoritative here (mcpServerName/mcpToolName/toolCallId and
// permission kind "mcp"); reasoning is opaque. Unknown event types are ignored
// as nodes but still durably captured by the coordinator.
internal sealed class CopilotCliTranscriptDialectParser : TranscriptDialectParserBase
{
    private readonly CopilotJsonValueReader _json = new();
    private readonly CopilotToolSemantics _toolSemantics;
    private readonly CopilotExecutionSemantics _executionSemantics;
    private readonly CopilotUsageExtractor _usageExtractor;
    private readonly SystemPromptTextNormalizer _systemPromptNormalizer = new();

    public CopilotCliTranscriptDialectParser()
    {
        _toolSemantics = new CopilotToolSemantics(_json);
        _executionSemantics = new CopilotExecutionSemantics(_json);
        _usageExtractor = new CopilotUsageExtractor(_json);
    }

    public override string DialectId => DialectIds.CopilotCliTranscript;

    protected override IReadOnlyList<HookObservation> ParseRow(TranscriptLine line, JsonElement row)
    {
        string? type = RuntimeJson.String(row, "type");
        if (type is null || !row.TryGetProperty("data", out JsonElement data))
        {
            return [];
        }

        string? id = RuntimeJson.String(row, "id");
        string? parentId = RuntimeJson.String(row, "parentId");
        RowIdentity identity = new(
            Identifier(data, "turnId", "turn_id"),
            Identifier(data, "interactionId", "interaction_id"),
            Identifier(row, "agentId", "agent_id") ??
                Identifier(data, "agentId", "agent_id"));

        return type switch
        {
            "system.message" => SystemMessage(line, data, id, parentId, identity),
            "assistant.message" => AssistantMessage(line, data, id, parentId, identity),
            "assistant.reasoning" => [AssistantReasoning(line, data, id, parentId, identity)],
            "tool.execution_start" => [ToolExecution(line, data, id, parentId, identity, start: true)],
            "tool.execution_complete" => [ToolExecution(line, data, id, parentId, identity, start: false)],
            "permission.requested" => [Permission(line, data, id, parentId, identity, requested: true)],
            "permission.completed" => [Permission(line, data, id, parentId, identity, requested: false)],
            "permission.denied" => [Permission(line, data, id, parentId, identity, requested: false, denied: true)],
            "session.usage_checkpoint" => [SessionUsage(line, type, data, id, parentId, identity, final: false)],
            "session.shutdown" => [SessionUsage(line, type, data, id, parentId, identity, final: true)],
            "session.model_change" or "model_change" or
            "session.auto_mode_resolved" or "auto_mode_resolved" or
            "session.mode_changed" or "session.mode_change" or
            "session.context_changed" or "session.context_change" or
            "session.permissions_changed" =>
                [SessionMetadata(line, type, data, id, parentId, identity)],
            _ => []
        };
    }

    private IReadOnlyList<HookObservation> SystemMessage(
        TranscriptLine line,
        JsonElement data,
        string? id,
        string? parentId,
        RowIdentity identity)
    {
        if (!string.Equals(
                RuntimeJson.String(data, "role"),
                "system",
                StringComparison.Ordinal))
        {
            return [];
        }

        SystemPromptContent? systemPrompt = _systemPromptNormalizer.FromString(
            RuntimeJson.String(data, "content"));
        if (systemPrompt is null)
        {
            return [];
        }

        var builder = new InterpretationBuilder("system.message")
        {
            SessionId = line.NativeSessionId,
            TurnId = line.TurnHint,
            SubagentId = identity.AgentId,
            Role = ObservationRole.SystemPrompt,
            EventKind = CanonicalEventKind.SystemPromptSnapshot,
            Direction = ObservationDirection.Input,
            AssistantText = systemPrompt.Text,
            SystemPrompt = systemPrompt,
            HeaderDetail = $"System prompt \u00b7 {PromptSummary(systemPrompt)}",
            Evidence = InferenceEvidence.Observed,
            ExcludeFromSummary = true
        };
        if (identity.AgentId is null)
        {
            builder.ScopeOverride = ObservationScope.Session;
        }

        JsonObject payload = new()
        {
            ["type"] = "system.message",
            ["content_hash"] = systemPrompt.ContentHash,
            ["part_count"] = systemPrompt.PartCount
        };
        builder.Fields(
            new FieldSpec(FieldSpecKind.Scalar, "content_hash"),
            new FieldSpec(FieldSpecKind.Scalar, "part_count"));

        return
        [
            Emit(
                line,
                payload,
                builder.Build(),
                line.Provenance(
                    0,
                    TranscriptCompleteness.Complete,
                    id,
                    parentId,
                    identity.NativeTurnId,
                    identity.InteractionId))
        ];
    }

    private IReadOnlyList<HookObservation> AssistantMessage(
        TranscriptLine line,
        JsonElement data,
        string? id,
        string? parentId,
        RowIdentity identity)
    {
        List<HookObservation> observations = [];
        int index = 0;
        string sourceRecordId = id ?? $"{line.NormalizedPath}:{line.LineNumber}";
        string? assistantStepId =
            RuntimeJson.String(data, "messageId", "message_id") ??
            id;
        string? model = RuntimeJson.String(data, "model", "selectedModel", "currentModel");
        IReadOnlyList<UsageMeasurement> usage =
            _usageExtractor.ExtractForEvent("assistant.message", data, sourceRecordId);
        bool usageAssigned = false;

        if (RuntimeJson.String(data, "reasoningOpaque") is not null ||
            RuntimeJson.String(data, "encryptedContent") is not null)
        {
            observations.Add(OpaqueReasoning(
                line,
                index++,
                id,
                parentId,
                identity,
                assistantStepId,
                model,
                usage));
            usageAssigned = true;
        }

        if (RuntimeJson.String(data, "reasoningText") is string reasoningText)
        {
            observations.Add(ReadableReasoning(
                line,
                reasoningText,
                index++,
                id,
                parentId,
                identity,
                assistantStepId,
                model,
                usageAssigned ? [] : usage));
            usageAssigned = true;
        }

        if (data.TryGetProperty("toolRequests", out JsonElement toolRequests) &&
            toolRequests.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement request in toolRequests.EnumerateArray())
            {
                observations.Add(ToolRequest(
                    line,
                    request,
                    index++,
                    id,
                    parentId,
                    identity,
                    assistantStepId,
                    model,
                    usageAssigned ? [] : usage));
                usageAssigned = true;
            }
        }

        if (RuntimeJson.String(data, "content") is string content &&
            RuntimeJson.String(data, "phase") is "final_answer")
        {
            observations.Add(FinalAnswer(
                line,
                content,
                index++,
                id,
                parentId,
                identity,
                assistantStepId,
                model,
                usageAssigned ? [] : usage));
            usageAssigned = true;
        }

        if (!usageAssigned && usage.Count > 0)
        {
            observations.Add(UsageOnly(
                line,
                index,
                id,
                parentId,
                identity,
                assistantStepId,
                model,
                usage));
        }

        return observations;
    }

    private HookObservation OpaqueReasoning(
        TranscriptLine line,
        int index,
        string? id,
        string? parentId,
        RowIdentity identity,
        string? assistantStepId,
        string? model,
        IReadOnlyList<UsageMeasurement> usage)
    {
        var builder = new InterpretationBuilder("assistant.reasoning")
        {
            SessionId = line.NativeSessionId,
            TurnId = line.TurnHint,
            SubagentId = identity.AgentId,
            Role = ObservationRole.AgentThought,
            EventKind = CanonicalEventKind.AssistantThought,
            Tone = ObservationTone.Thought,
            HoverText = "Reasoning is opaque/encrypted by the provider.",
            HeaderDetail = "opaque reasoning",
            Evidence = InferenceEvidence.Opaque,
            AssistantStepId = assistantStepId,
            Model = model,
            UsageMeasurements = usage
        };

        JsonObject payload = new() { ["type"] = "assistant.reasoning", ["opaque"] = true };
        return Emit(line, payload, builder.Build(), line.Provenance(
            index,
            TranscriptCompleteness.Complete,
            id,
            parentId,
            identity.NativeTurnId,
            identity.InteractionId,
            assistantStepId: assistantStepId));
    }

    private HookObservation AssistantReasoning(
        TranscriptLine line,
        JsonElement data,
        string? id,
        string? parentId,
        RowIdentity identity)
    {
        string sourceRecordId = id ?? $"{line.NormalizedPath}:{line.LineNumber}";
        IReadOnlyList<UsageMeasurement> usage =
            _usageExtractor.ExtractForEvent("assistant.reasoning", data, sourceRecordId);
        string? assistantStepId =
            RuntimeJson.String(data, "reasoningId", "messageId", "message_id") ??
            id;
        string? model = RuntimeJson.String(data, "model", "selectedModel", "currentModel");
        string? content = RuntimeJson.String(data, "content", "text", "reasoningText");
        return content is string readable
            ? ReadableReasoning(
                line,
                readable,
                0,
                id,
                parentId,
                identity,
                assistantStepId,
                model,
                usage)
            : OpaqueReasoning(
                line,
                0,
                id,
                parentId,
                identity,
                assistantStepId,
                model,
                usage);
    }

    private HookObservation ReadableReasoning(
        TranscriptLine line,
        string content,
        int index,
        string? id,
        string? parentId,
        RowIdentity identity,
        string? assistantStepId,
        string? model,
        IReadOnlyList<UsageMeasurement> usage)
    {
        var builder = new InterpretationBuilder("assistant.reasoning")
        {
            SessionId = line.NativeSessionId,
            TurnId = line.TurnHint,
            SubagentId = identity.AgentId,
            Role = ObservationRole.AgentThought,
            EventKind = CanonicalEventKind.AssistantThought,
            Tone = ObservationTone.Thought,
            AssistantText = content,
            HoverText = content,
            HeaderDetail = Preview(content),
            Evidence = InferenceEvidence.Observed,
            AssistantStepId = assistantStepId,
            Model = model,
            UsageMeasurements = usage
        };

        JsonObject payload = new()
        {
            ["type"] = "assistant.reasoning",
            ["content"] = content,
            ["model"] = model
        };
        return Emit(line, payload, builder.Build(), line.Provenance(
            index,
            TranscriptCompleteness.Complete,
            id,
            parentId,
            identity.NativeTurnId,
            identity.InteractionId,
            assistantStepId: assistantStepId));
    }

    private HookObservation ToolRequest(
        TranscriptLine line,
        JsonElement request,
        int index,
        string? id,
        string? parentId,
        RowIdentity identity,
        string? assistantStepId,
        string? model,
        IReadOnlyList<UsageMeasurement> usage)
    {
        string toolName = RuntimeJson.String(request, "name") ?? "tool";
        string? toolCallId = RuntimeJson.String(request, "toolCallId");
        TranscriptMcpIdentity mcp = _toolSemantics.ReadMcpIdentity(request);
        IReadOnlyList<string> targetPaths = _toolSemantics.TargetPaths(request);
        string? targetFilePath = targetPaths.FirstOrDefault();

        var builder = new InterpretationBuilder(toolName)
        {
            SessionId = line.NativeSessionId,
            TurnId = line.TurnHint,
            SubagentId = identity.AgentId,
            ToolName = toolName,
            ToolCallId = toolCallId,
            McpServerName = mcp.ServerName,
            McpToolName = mcp.ToolName,
            TargetFilePath = targetFilePath,
            TargetFilePaths = targetPaths,
            Role = ObservationRole.ToolRequest,
            EventKind = CanonicalEventKind.ToolRequested,
            Direction = ObservationDirection.None,
            ToolKind = _toolSemantics.Classify(toolName, mcp),
            Tone = mcp.IsMcp ? ObservationTone.Mcp : ObservationTone.Normal,
            HeaderDetail = !mcp.IsMcp
                ? toolName
                : $"{mcp.ServerName}/{mcp.ToolName ?? toolName}",
            Evidence = InferenceEvidence.Observed,
            AssistantStepId = assistantStepId,
            Model = model,
            UsageMeasurements = usage,
            EnrichmentOnly = true,
            ExcludeFromSummary = true
        };

        JsonObject payload = CloneToObject(request);
        return Emit(line, payload, builder.Build(), line.Provenance(
            index,
            TranscriptCompleteness.Complete,
            id,
            parentId,
            identity.NativeTurnId,
            identity.InteractionId,
            toolCallId,
            assistantStepId: assistantStepId));
    }

    private HookObservation ToolExecution(
        TranscriptLine line,
        JsonElement data,
        string? id,
        string? parentId,
        RowIdentity identity,
        bool start)
    {
        string nativeType = start ? "tool.execution_start" : "tool.execution_complete";
        string sourceRecordId = id ?? $"{line.NormalizedPath}:{line.LineNumber}";
        string? toolCallId = _executionSemantics.ReadToolCallId(data);
        string? toolName = RuntimeJson.String(data, "toolName", "name");
        TranscriptMcpIdentity mcp = _toolSemantics.ReadMcpIdentity(data);
        bool aborted = !start && _executionSemantics.IsAborted(data);
        bool failure = !start && (_executionSemantics.IsFailure(data) || aborted);
        string? status = _executionSemantics.ReadStatus(data) ??
            (aborted ? "aborted" : null);
        string? resultText = _executionSemantics.ReadResultText(data);
        double? duration = _executionSemantics.ReadDuration(data);
        IReadOnlyList<string> targetPaths = _toolSemantics.TargetPaths(data);

        var builder = new InterpretationBuilder(nativeType)
        {
            SessionId = line.NativeSessionId,
            TurnId = line.TurnHint,
            SubagentId = identity.AgentId,
            ToolName = toolName,
            ToolCallId = toolCallId,
            McpServerName = mcp.ServerName,
            McpToolName = mcp.ToolName,
            Role = start
                ? ObservationRole.InnerExecutionStart
                : failure
                    ? ObservationRole.ToolFailure
                    : ObservationRole.ToolSuccess,
            EventKind = start
                ? CanonicalEventKind.ProviderSpecific
                : failure
                    ? CanonicalEventKind.ToolFailed
                    : CanonicalEventKind.ToolSucceeded,
            Direction = start ? ObservationDirection.Input : ObservationDirection.Output,
            ToolKind = _toolSemantics.Classify(toolName, mcp),
            Tone = failure
                ? ObservationTone.Failure
                : mcp.IsMcp
                    ? ObservationTone.Mcp
                    : ObservationTone.Normal,
            HeaderDetail = JoinNonEmpty(
                toolName,
                mcp.ServerName,
                mcp.ToolName,
                Preview(resultText)),
            Status = status,
            TargetFilePath = targetPaths.FirstOrDefault(),
            TargetFilePaths = targetPaths,
            CountsAsFailure = failure,
            Model = RuntimeJson.String(data, "model", "selectedModel", "currentModel"),
            UsageMeasurements = _usageExtractor.ExtractForEvent(
                nativeType,
                data,
                sourceRecordId),
            Evidence = InferenceEvidence.Observed,
            EnrichmentOnly = true,
            ExcludeFromSummary = true
        };

        JsonObject payload = CloneToObject(data);
        if (duration is double durationMs)
        {
            payload["duration_ms"] = durationMs;
        }
        return Emit(line, payload, builder.Build(), line.Provenance(
            0,
            TranscriptCompleteness.Complete,
            id,
            parentId,
            identity.NativeTurnId,
            identity.InteractionId,
            toolCallId));
    }

    private HookObservation Permission(
        TranscriptLine line,
        JsonElement data,
        string? id,
        string? parentId,
        RowIdentity identity,
        bool requested,
        bool denied = false)
    {
        string? kind = requested
            ? RuntimeJson.NestedString(data, "permissionRequest", "kind")
            : RuntimeJson.String(data, "kind");
        string? toolCallId = _executionSemantics.ReadToolCallId(data);
        TranscriptMcpIdentity mcp = _toolSemantics.ReadMcpIdentity(data);
        denied |= _executionSemantics.IsDenied(
            requested ? "permission.requested" : "permission.completed",
            data);
        string? status = _executionSemantics.ReadStatus(data);
        IReadOnlyList<string> targetPaths = _toolSemantics.TargetPaths(data);

        string nativeType = requested
            ? "permission.requested"
            : denied
                ? "permission.denied"
                : "permission.completed";
        var builder = new InterpretationBuilder(nativeType)
        {
            SessionId = line.NativeSessionId,
            TurnId = line.TurnHint,
            SubagentId = identity.AgentId,
            ToolCallId = toolCallId,
            Role = requested
                ? ObservationRole.PermissionRequest
                : denied
                    ? ObservationRole.PermissionDenied
                    : ObservationRole.Generic,
            EventKind = requested
                ? CanonicalEventKind.PermissionRequested
                : denied
                    ? CanonicalEventKind.PermissionDenied
                    : CanonicalEventKind.ProviderSpecific,
            Direction = requested ? ObservationDirection.Input : ObservationDirection.Output,
            McpServerName = mcp.ServerName,
            McpToolName = mcp.ToolName,
            ToolKind = mcp.IsMcp ? CanonicalToolKind.Mcp : CanonicalToolKind.Unknown,
            Tone = denied ? ObservationTone.Failure : ObservationTone.Permission,
            HeaderDetail = JoinNonEmpty(kind, mcp.ServerName, mcp.ToolName, status),
            Status = status,
            TargetFilePath = targetPaths.FirstOrDefault(),
            TargetFilePaths = targetPaths,
            CountsAsFailure = denied,
            Evidence = InferenceEvidence.Observed,
            EnrichmentOnly = true,
            ExcludeFromSummary = true
        };

        JsonObject payload = CloneToObject(data);
        return Emit(line, payload, builder.Build(), line.Provenance(
            0,
            TranscriptCompleteness.Complete,
            id,
            parentId,
            identity.NativeTurnId,
            identity.InteractionId,
            toolCallId));
    }

    private HookObservation FinalAnswer(
        TranscriptLine line,
        string content,
        int index,
        string? id,
        string? parentId,
        RowIdentity identity,
        string? assistantStepId,
        string? model,
        IReadOnlyList<UsageMeasurement> usage)
    {
        var builder = new InterpretationBuilder("assistant.message")
        {
            SessionId = line.NativeSessionId,
            TurnId = line.TurnHint,
            SubagentId = identity.AgentId,
            Role = ObservationRole.AgentResponse,
            EventKind = CanonicalEventKind.AssistantMessage,
            Direction = ObservationDirection.None,
            AssistantText = content,
            HoverText = content,
            HeaderDetail = Preview(content),
            Evidence = InferenceEvidence.Observed,
            AssistantStepId = assistantStepId,
            Model = model,
            UsageMeasurements = usage
        };

        JsonObject payload = new() { ["type"] = "assistant.message", ["content"] = content };
        return Emit(line, payload, builder.Build(), line.Provenance(
            index,
            TranscriptCompleteness.Complete,
            id,
            parentId,
            identity.NativeTurnId,
            identity.InteractionId,
            assistantStepId: assistantStepId));
    }

    private HookObservation UsageOnly(
        TranscriptLine line,
        int index,
        string? id,
        string? parentId,
        RowIdentity identity,
        string? assistantStepId,
        string? model,
        IReadOnlyList<UsageMeasurement> usage)
    {
        var builder = new InterpretationBuilder("assistant.usage")
        {
            SessionId = line.NativeSessionId,
            TurnId = line.TurnHint,
            SubagentId = identity.AgentId,
            Role = ObservationRole.Message,
            Model = model,
            AssistantStepId = assistantStepId,
            UsageMeasurements = usage,
            Evidence = InferenceEvidence.Observed,
            EnrichmentOnly = true,
            MetadataOnly = true,
            ExcludeFromSummary = true
        };
        JsonObject payload = new()
        {
            ["type"] = "assistant.usage",
            ["model"] = model
        };
        return Emit(line, payload, builder.Build(), line.Provenance(
            index,
            TranscriptCompleteness.Complete,
            id,
            parentId,
            identity.NativeTurnId,
            identity.InteractionId,
            assistantStepId: assistantStepId));
    }

    private HookObservation SessionUsage(
        TranscriptLine line,
        string nativeType,
        JsonElement data,
        string? id,
        string? parentId,
        RowIdentity identity,
        bool final)
    {
        string sourceRecordId = id ?? $"{line.NormalizedPath}:{line.LineNumber}";
        IReadOnlyList<UsageMeasurement> usage =
            _usageExtractor.ExtractSessionAggregate(
                data,
                sourceRecordId,
                final
                    ? UsageBehavior.FinalSnapshot
                    : UsageBehavior.CumulativeSnapshot);
        var builder = new InterpretationBuilder(nativeType)
        {
            SessionId = line.NativeSessionId,
            Role = ObservationRole.Message,
            Model = RuntimeJson.String(data, "model", "selectedModel", "currentModel"),
            Status = _executionSemantics.ReadStatus(data),
            HeaderDetail = final ? "final usage" : "usage checkpoint",
            UsageMeasurements = usage,
            Evidence = InferenceEvidence.Observed,
            EnrichmentOnly = true,
            MetadataOnly = true,
            ExcludeFromSummary = true
        };

        return Emit(line, CloneToObject(data), builder.Build(), line.Provenance(
            0,
            TranscriptCompleteness.Complete,
            id,
            parentId,
            identity.NativeTurnId,
            identity.InteractionId));
    }

    private HookObservation SessionMetadata(
        TranscriptLine line,
        string nativeType,
        JsonElement data,
        string? id,
        string? parentId,
        RowIdentity identity)
    {
        string? model = RuntimeJson.String(
            data,
            "model",
            "selectedModel",
            "currentModel",
            "resolvedModel",
            "newModel",
            "chosenModel");
        string? status = RuntimeJson.String(
            data,
            "mode",
            "newMode",
            "selectedMode",
            "permissionMode",
            "allowAllPermissionMode",
            "status",
            "reason",
            "source");
        var builder = new InterpretationBuilder(nativeType)
        {
            SessionId = line.NativeSessionId,
            Role = ObservationRole.Message,
            Model = model,
            Status = status,
            HeaderDetail = JoinNonEmpty(model, status),
            Evidence = InferenceEvidence.Observed,
            EnrichmentOnly = true,
            MetadataOnly = true,
            ExcludeFromSummary = true
        };
        return Emit(line, CloneToObject(data), builder.Build(), line.Provenance(
            0,
            TranscriptCompleteness.Complete,
            id,
            parentId,
            identity.NativeTurnId,
            identity.InteractionId));
    }

    private static string? Identifier(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetRawText();
            }
        }

        return null;
    }

    private static string? JoinNonEmpty(params string?[] parts)
    {
        IEnumerable<string> nonEmpty = parts.Where(part => !string.IsNullOrEmpty(part))!;
        string joined = string.Join(" \u00b7 ", nonEmpty);
        return joined.Length == 0 ? null : joined;
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

    private static string PromptSummary(SystemPromptContent prompt)
    {
        string parts = prompt.PartCount == 1
            ? "1 part"
            : $"{prompt.PartCount} parts";
        return $"{parts} \u00b7 {prompt.CharacterCount:N0} chars";
    }

    private readonly record struct RowIdentity(
        string? NativeTurnId,
        string? InteractionId,
        string? AgentId);
}
