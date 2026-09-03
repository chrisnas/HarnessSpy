using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;
using HarnessSpy.Core.Sources;

namespace HarnessSpy.Core.Runtimes.Copilot;

// Parses Copilot CLI session-state events.jsonl (schema v1, verified against
// Copilot 1.0.81/1.0.82). Every row is {type,data,id,timestamp,parentId}.
// MCP metadata is authoritative here (mcpServerName/mcpToolName/toolCallId and
// permission kind "mcp"); reasoning is opaque. Unknown event types are ignored
// as nodes but still durably captured by the coordinator.
internal sealed class CopilotCliTranscriptDialectParser : TranscriptDialectParserBase
{
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

        return type switch
        {
            "assistant.message" => AssistantMessage(line, data, id, parentId),
            "tool.execution_start" => [ToolExecution(line, data, id, parentId, start: true)],
            "tool.execution_complete" => [ToolExecution(line, data, id, parentId, start: false)],
            "permission.requested" => [Permission(line, data, id, parentId, requested: true)],
            "permission.completed" => [Permission(line, data, id, parentId, requested: false)],
            _ => []
        };
    }

    private IReadOnlyList<HookObservation> AssistantMessage(
        TranscriptLine line,
        JsonElement data,
        string? id,
        string? parentId)
    {
        List<HookObservation> observations = [];
        int index = 0;

        if (RuntimeJson.String(data, "reasoningOpaque") is not null ||
            RuntimeJson.String(data, "encryptedContent") is not null)
        {
            observations.Add(OpaqueReasoning(line, index++, id, parentId));
        }

        if (data.TryGetProperty("toolRequests", out JsonElement toolRequests) &&
            toolRequests.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement request in toolRequests.EnumerateArray())
            {
                observations.Add(ToolRequest(line, request, index++, id, parentId));
            }
        }

        if (RuntimeJson.String(data, "content") is string content &&
            RuntimeJson.String(data, "phase") is "final_answer")
        {
            observations.Add(FinalAnswer(line, content, index++, id, parentId));
        }

        return observations;
    }

    private HookObservation OpaqueReasoning(TranscriptLine line, int index, string? id, string? parentId)
    {
        var builder = new InterpretationBuilder("assistant.reasoning")
        {
            SessionId = line.NativeSessionId,
            Role = ObservationRole.AgentThought,
            EventKind = CanonicalEventKind.AssistantThought,
            Tone = ObservationTone.Thought,
            HoverText = "Reasoning is opaque/encrypted by the provider.",
            HeaderDetail = "opaque reasoning",
            Evidence = InferenceEvidence.Opaque
        };

        JsonObject payload = new() { ["type"] = "assistant.reasoning", ["opaque"] = true };
        return Emit(line, payload, builder.Build(), line.Provenance(
            index, TranscriptCompleteness.Complete, id, parentId));
    }

    private HookObservation ToolRequest(
        TranscriptLine line,
        JsonElement request,
        int index,
        string? id,
        string? parentId)
    {
        string toolName = RuntimeJson.String(request, "name") ?? "tool";
        string? toolCallId = RuntimeJson.String(request, "toolCallId");
        string? mcpServer = RuntimeJson.String(request, "mcpServerName");

        var builder = new InterpretationBuilder(toolName)
        {
            SessionId = line.NativeSessionId,
            ToolName = toolName,
            ToolCallId = toolCallId,
            McpServerName = mcpServer,
            Role = ObservationRole.ToolRequest,
            EventKind = CanonicalEventKind.ToolRequested,
            Direction = ObservationDirection.None,
            ToolKind = mcpServer is null ? ToolKind(toolName) : CanonicalToolKind.Mcp,
            Tone = mcpServer is null ? ObservationTone.Normal : ObservationTone.Mcp,
            HeaderDetail = mcpServer is null
                ? toolName
                : $"{mcpServer}/{RuntimeJson.String(request, "mcpToolName") ?? toolName}",
            Evidence = InferenceEvidence.Observed,
            EnrichmentOnly = true,
            ExcludeFromSummary = true
        };

        JsonObject payload = CloneToObject(request);
        return Emit(line, payload, builder.Build(), line.Provenance(
            index, TranscriptCompleteness.Complete, id, parentId, toolCallId: toolCallId));
    }

    private HookObservation ToolExecution(
        TranscriptLine line,
        JsonElement data,
        string? id,
        string? parentId,
        bool start)
    {
        string? toolCallId = RuntimeJson.String(data, "toolCallId");
        string? toolName = RuntimeJson.String(data, "name");
        string? mcpServer = RuntimeJson.String(data, "mcpServerName");

        var builder = new InterpretationBuilder(start ? "tool.execution_start" : "tool.execution_complete")
        {
            SessionId = line.NativeSessionId,
            ToolName = toolName,
            ToolCallId = toolCallId,
            McpServerName = mcpServer,
            Role = start ? ObservationRole.InnerExecutionStart : ObservationRole.InnerExecutionEnd,
            Direction = start ? ObservationDirection.Input : ObservationDirection.Output,
            ToolKind = mcpServer is null ? ToolKind(toolName) : CanonicalToolKind.Mcp,
            Tone = mcpServer is null ? ObservationTone.Normal : ObservationTone.Mcp,
            Evidence = InferenceEvidence.Observed,
            EnrichmentOnly = true,
            ExcludeFromSummary = true
        };

        JsonObject payload = CloneToObject(data);
        return Emit(line, payload, builder.Build(), line.Provenance(
            0, TranscriptCompleteness.Complete, id, parentId, toolCallId: toolCallId));
    }

    private HookObservation Permission(
        TranscriptLine line,
        JsonElement data,
        string? id,
        string? parentId,
        bool requested)
    {
        string? kind = requested
            ? RuntimeJson.NestedString(data, "permissionRequest", "kind")
            : RuntimeJson.String(data, "kind");
        string? server = RuntimeJson.NestedString(data, "permissionRequest", "serverName");
        bool isMcp = string.Equals(kind, "mcp", StringComparison.OrdinalIgnoreCase) || IsMcpApproval(data);

        var builder = new InterpretationBuilder(requested ? "permission.requested" : "permission.completed")
        {
            SessionId = line.NativeSessionId,
            Role = requested ? ObservationRole.PermissionRequest : ObservationRole.Generic,
            EventKind = requested ? CanonicalEventKind.PermissionRequested : CanonicalEventKind.ProviderSpecific,
            Direction = requested ? ObservationDirection.Input : ObservationDirection.Output,
            McpServerName = isMcp ? server : null,
            Tone = isMcp ? ObservationTone.Mcp : ObservationTone.Permission,
            HeaderDetail = JoinNonEmpty(kind, server),
            Evidence = InferenceEvidence.Observed,
            EnrichmentOnly = true,
            ExcludeFromSummary = true
        };

        JsonObject payload = CloneToObject(data);
        return Emit(line, payload, builder.Build(), line.Provenance(
            0, TranscriptCompleteness.Complete, id, parentId));
    }

    private HookObservation FinalAnswer(
        TranscriptLine line,
        string content,
        int index,
        string? id,
        string? parentId)
    {
        var builder = new InterpretationBuilder("assistant.message")
        {
            SessionId = line.NativeSessionId,
            Role = ObservationRole.AgentResponse,
            EventKind = CanonicalEventKind.AssistantMessage,
            Direction = ObservationDirection.None,
            AssistantText = content,
            HoverText = content,
            HeaderDetail = Preview(content),
            Evidence = InferenceEvidence.Observed
        };

        JsonObject payload = new() { ["type"] = "assistant.message", ["content"] = content };
        return Emit(line, payload, builder.Build(), line.Provenance(
            index, TranscriptCompleteness.Complete, id, parentId));
    }

    private static bool IsMcpApproval(JsonElement data) =>
        data.TryGetProperty("result", out JsonElement result) &&
        result.TryGetProperty("approval", out JsonElement approval) &&
        string.Equals(RuntimeJson.String(approval, "kind"), "mcp", StringComparison.OrdinalIgnoreCase);

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
}
