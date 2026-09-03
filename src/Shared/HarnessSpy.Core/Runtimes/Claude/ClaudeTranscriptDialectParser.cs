using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;
using HarnessSpy.Core.Sources;

namespace HarnessSpy.Core.Runtimes.Claude;

// Parses Claude Code transcript JSONL (verified against Claude Code 2.1.251).
// Records are type-tagged; assistant/user carry the content blocks. Thinking is
// opaque (empty text + signature) with the token count in usage; tool_use/
// tool_result correlate exactly by id. Metadata rows (mode, system, cost-state,
// attachments, file-history, ...) are durably captured by the coordinator but
// are not turned into noisy tree nodes here.
internal sealed class ClaudeTranscriptDialectParser : TranscriptDialectParserBase
{
    public override string DialectId => DialectIds.ClaudeTranscript;

    protected override IReadOnlyList<HookObservation> ParseRow(TranscriptLine line, JsonElement row)
    {
        string? type = RuntimeJson.String(row, "type");
        return type switch
        {
            "assistant" => AssistantRow(line, row),
            "user" => UserRow(line, row),
            _ => []
        };
    }

    private IReadOnlyList<HookObservation> AssistantRow(TranscriptLine line, JsonElement row)
    {
        // Each Claude row carries its own session id, so transcript fragments
        // adopt it directly. This keeps them under the same session node as the
        // hooks even when the durable manifest predates native-session capture.
        line = line with
        {
            NativeSessionId = RuntimeJson.String(row, "sessionId", "session_id") ?? line.NativeSessionId
        };

        if (!row.TryGetProperty("message", out JsonElement message) ||
            !message.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // Assistant rows carry no promptId; the ingestion loop supplies the
        // last user turn id as a hint so thinking/text/tool_use land in the
        // correct turn instead of at session scope.
        string? promptId = RuntimeJson.String(row, "promptId") ?? line.TurnHint;
        string? uuid = RuntimeJson.String(row, "uuid");
        string? parentUuid = RuntimeJson.String(row, "parentUuid");
        string? model = RuntimeJson.String(message, "model");
        IReadOnlyList<UsageMeasurement> usage = ReadUsage(message, uuid);

        List<HookObservation> observations = [];
        int index = 0;
        foreach (JsonElement block in content.EnumerateArray())
        {
            string? blockType = RuntimeJson.String(block, "type");
            HookObservation? observation = blockType switch
            {
                "thinking" => Thinking(line, block, index, promptId, uuid, parentUuid, model, usage),
                "text" => AssistantText(line, block, index, promptId, uuid, parentUuid),
                "tool_use" => ToolUse(line, block, index, promptId, uuid, parentUuid),
                _ => null
            };

            if (observation is not null)
            {
                observations.Add(observation);
            }

            index++;
        }

        return observations;
    }

    private IReadOnlyList<HookObservation> UserRow(TranscriptLine line, JsonElement row)
    {
        line = line with
        {
            NativeSessionId = RuntimeJson.String(row, "sessionId", "session_id") ?? line.NativeSessionId
        };

        if (!row.TryGetProperty("message", out JsonElement message) ||
            !message.TryGetProperty("content", out JsonElement content))
        {
            return [];
        }

        string? promptId = RuntimeJson.String(row, "promptId") ?? line.TurnHint;
        string? uuid = RuntimeJson.String(row, "uuid");

        // tool_result rows enrich the matching PostToolUse by tool_use_id.
        if (content.ValueKind == JsonValueKind.Array)
        {
            List<HookObservation> results = [];
            int index = 0;
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (RuntimeJson.String(block, "type") == "tool_result")
                {
                    results.Add(ToolResult(line, block, row, index, promptId, uuid));
                }

                index++;
            }

            if (results.Count > 0)
            {
                return results;
            }
        }

        return [];
    }

    private HookObservation Thinking(
        TranscriptLine line,
        JsonElement block,
        int index,
        string? promptId,
        string? uuid,
        string? parentUuid,
        string? model,
        IReadOnlyList<UsageMeasurement> usage)
    {
        string? thinkingText = RuntimeJson.String(block, "thinking");
        string? signature = RuntimeJson.String(block, "signature");
        bool opaque = string.IsNullOrEmpty(thinkingText) && !string.IsNullOrEmpty(signature);

        var builder = new InterpretationBuilder("thinking")
        {
            SessionId = line.NativeSessionId,
            TurnId = promptId,
            Role = ObservationRole.AgentThought,
            EventKind = CanonicalEventKind.AssistantThought,
            Tone = ObservationTone.Thought,
            AssistantText = opaque ? null : thinkingText,
            HoverText = opaque ? "Thinking is opaque/redacted by the provider." : thinkingText,
            HeaderDetail = opaque ? OpaqueHeader(signature, usage) : Preview(thinkingText),
            Evidence = opaque ? InferenceEvidence.Opaque : InferenceEvidence.Observed,
            UsageMeasurements = usage
        };

        JsonObject payload = new()
        {
            ["type"] = "thinking",
            ["signature_present"] = !string.IsNullOrEmpty(signature),
            ["signature_length"] = signature?.Length ?? 0,
            ["model"] = model
        };

        return Emit(line, payload, builder.Build(), line.Provenance(
            index, TranscriptCompleteness.Complete, uuid, parentUuid, promptId));
    }

    private HookObservation AssistantText(
        TranscriptLine line,
        JsonElement block,
        int index,
        string? promptId,
        string? uuid,
        string? parentUuid)
    {
        string? text = RuntimeJson.String(block, "text");
        var builder = new InterpretationBuilder("text")
        {
            SessionId = line.NativeSessionId,
            TurnId = promptId,
            Role = ObservationRole.AgentResponse,
            EventKind = CanonicalEventKind.AssistantMessage,
            // Standalone assistant text is not a reply to a preceding request
            // node, so it carries no directional arrow.
            Direction = ObservationDirection.None,
            AssistantText = text,
            HoverText = text,
            HeaderDetail = Preview(text),
            Evidence = InferenceEvidence.Observed
        };

        JsonObject payload = new()
        {
            ["type"] = "text",
            ["text"] = text
        };

        return Emit(line, payload, builder.Build(), line.Provenance(
            index, TranscriptCompleteness.Complete, uuid, parentUuid, promptId));
    }

    private HookObservation ToolUse(
        TranscriptLine line,
        JsonElement block,
        int index,
        string? promptId,
        string? uuid,
        string? parentUuid)
    {
        string toolName = RuntimeJson.String(block, "name") ?? "tool_use";
        string? toolCallId = RuntimeJson.String(block, "id");

        var builder = new InterpretationBuilder(toolName)
        {
            SessionId = line.NativeSessionId,
            TurnId = promptId,
            ToolName = toolName,
            ToolCallId = toolCallId,
            Role = ObservationRole.ToolRequest,
            EventKind = CanonicalEventKind.ToolRequested,
            // No directional arrow: a transcript tool request is not one side of
            // a request/response pair the way a hook Pre/PostToolUse is.
            Direction = ObservationDirection.None,
            ToolKind = ToolKind(toolName),
            TargetFilePath = ToolInputValue(block, "file_path", "path"),
            HeaderDetail = ToolInputPreview(block),
            Evidence = InferenceEvidence.Observed,
            EnrichmentOnly = true,
            ExcludeFromSummary = true
        };

        if (RuntimeJson.IsMcpPrefixed(toolName))
        {
            builder.Tone = ObservationTone.Mcp;
        }

        JsonObject payload = CloneToObject(block);

        return Emit(line, payload, builder.Build(), line.Provenance(
            index, TranscriptCompleteness.Complete, uuid, parentUuid, promptId, toolCallId: toolCallId));
    }

    private HookObservation ToolResult(
        TranscriptLine line,
        JsonElement block,
        JsonElement row,
        int index,
        string? promptId,
        string? uuid)
    {
        string? toolCallId = RuntimeJson.String(block, "tool_use_id");
        bool isError = row.TryGetProperty("toolUseResult", out JsonElement result) &&
            result.TryGetProperty("interrupted", out JsonElement interrupted) &&
            interrupted.ValueKind == JsonValueKind.True;

        var builder = new InterpretationBuilder("tool_result")
        {
            SessionId = line.NativeSessionId,
            TurnId = promptId,
            ToolCallId = toolCallId,
            Role = isError ? ObservationRole.ToolFailure : ObservationRole.ToolSuccess,
            EventKind = isError ? CanonicalEventKind.ToolFailed : CanonicalEventKind.ToolSucceeded,
            // No directional arrow; it nests under its tool's PreToolUse node.
            Direction = ObservationDirection.None,
            HeaderDetail = ToolResultPreview(row),
            MatchStrategy = ToolCallMatchStrategy.ToolCallId,
            Evidence = InferenceEvidence.Observed,
            EnrichmentOnly = true,
            ExcludeFromSummary = true
        };

        JsonObject payload = CloneToObject(block);
        if (row.TryGetProperty("toolUseResult", out JsonElement structured))
        {
            payload["toolUseResult"] = JsonNode.Parse(structured.GetRawText());
        }

        return Emit(line, payload, builder.Build(), line.Provenance(
            index, TranscriptCompleteness.Complete, uuid, turnId: promptId, toolCallId: toolCallId));
    }

    // Reads Claude usage as typed measurements: input/cache are cumulative
    // snapshots; output is per model step; thinking tokens are output detail.
    private static IReadOnlyList<UsageMeasurement> ReadUsage(JsonElement message, string? recordId)
    {
        if (!message.TryGetProperty("usage", out JsonElement usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        string source = recordId ?? "unknown";
        List<UsageMeasurement> measurements = [];

        AddMeasurement(measurements, usage, "input_tokens", "tokens", UsageBehavior.CumulativeSnapshot, source);
        AddMeasurement(measurements, usage, "output_tokens", "tokens", UsageBehavior.Delta, source);
        AddMeasurement(measurements, usage, "cache_read_input_tokens", "tokens", UsageBehavior.CumulativeSnapshot, source);
        AddMeasurement(measurements, usage, "cache_creation_input_tokens", "tokens", UsageBehavior.CumulativeSnapshot, source);

        if (usage.TryGetProperty("output_tokens_details", out JsonElement details) &&
            details.ValueKind == JsonValueKind.Object)
        {
            AddMeasurement(measurements, details, "thinking_tokens", "tokens", UsageBehavior.Delta, source);
        }

        return measurements;
    }

    private static void AddMeasurement(
        List<UsageMeasurement> measurements,
        JsonElement container,
        string name,
        string unit,
        UsageBehavior behavior,
        string source)
    {
        if (RuntimeJson.Long(container, name) is long value)
        {
            measurements.Add(new UsageMeasurement(name, value, unit, UsageScope.Turn, behavior, source));
        }
    }

    // A short preview of the tool result so the nested result node is readable
    // (stdout, else the tool_result content string, else interrupted state).
    private static string? ToolResultPreview(JsonElement row)
    {
        if (row.TryGetProperty("toolUseResult", out JsonElement result) &&
            result.ValueKind == JsonValueKind.Object)
        {
            if (RuntimeJson.String(result, "stdout") is string stdout)
            {
                return Preview(stdout);
            }

            if (result.TryGetProperty("interrupted", out JsonElement interrupted) &&
                interrupted.ValueKind == JsonValueKind.True)
            {
                return "interrupted";
            }
        }

        return null;
    }

    private static string? ToolInputValue(JsonElement block, params string[] names)
    {
        if (!block.TryGetProperty("input", out JsonElement input) ||
            input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return RuntimeJson.String(input, names);
    }

    // A short preview of the tool's primary argument (command, path, pattern, …)
    // so the node text shows what the tool did, not just its name.
    private static string? ToolInputPreview(JsonElement block)
    {
        string? value = ToolInputValue(
            block,
            "command",
            "file_path",
            "path",
            "pattern",
            "query",
            "url",
            "description");
        return Preview(value);
    }

    private static string? OpaqueHeader(string? signature, IReadOnlyList<UsageMeasurement> usage)
    {
        long? thinkingTokens = usage
            .Where(measurement => measurement.Name == "thinking_tokens")
            .Select(measurement => (long?)measurement.Value)
            .FirstOrDefault();

        string signaturePart = signature is null ? "no signature" : $"signature {signature.Length} chars";
        return thinkingTokens is long tokens
            ? $"opaque \u00b7 {signaturePart} \u00b7 {HookObservation.FormatTokens(tokens)} thinking tokens"
            : $"opaque \u00b7 {signaturePart}";
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
