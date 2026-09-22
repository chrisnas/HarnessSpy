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
    private readonly ClaudeTranscriptSemantics _semantics = new();

    public override string DialectId => DialectIds.ClaudeTranscript;

    protected override IReadOnlyList<HookObservation> ParseRow(TranscriptLine line, JsonElement row)
    {
        string? type = RuntimeJson.String(row, "type");
        return type switch
        {
            "assistant" => AssistantRow(line, row),
            "user" => UserRow(line, row),
            "system" => SystemRow(line, row),
            "cost-state" => CostState(line, row),
            "attachment" => Attachment(line, row),
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

        if (!row.TryGetProperty("message", out JsonElement message))
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
        string sourceRecordId =
            RuntimeJson.String(message, "id") ??
            uuid ??
            $"{line.NormalizedPath}:{line.LineNumber}";
        IReadOnlyList<UsageMeasurement> usage =
            _semantics.ReadAssistantUsage(message, sourceRecordId);
        if (!message.TryGetProperty("content", out JsonElement content))
        {
            return usage.Count == 0
                ? []
                : [UsageOnly(line, row, promptId, uuid, parentUuid, model, usage)];
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            string text = content.GetString() ?? string.Empty;
            return [AssistantText(
                line,
                row,
                text,
                0,
                promptId,
                uuid,
                parentUuid,
                model,
                usage)];
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return usage.Count == 0
                ? []
                : [UsageOnly(line, row, promptId, uuid, parentUuid, model, usage)];
        }

        List<HookObservation> observations = [];
        int index = 0;
        bool usageAssigned = false;
        foreach (JsonElement block in content.EnumerateArray())
        {
            string? blockType = RuntimeJson.String(block, "type");
            IReadOnlyList<UsageMeasurement> blockUsage = usageAssigned ? [] : usage;
            HookObservation? observation = blockType switch
            {
                "thinking" or "redacted_thinking" =>
                    Thinking(line, row, message, block, index, promptId, uuid, parentUuid, model, blockUsage),
                "text" =>
                    AssistantText(
                        line,
                        row,
                        RuntimeJson.String(block, "text") ?? string.Empty,
                        index,
                        promptId,
                        uuid,
                        parentUuid,
                        model,
                        blockUsage),
                "tool_use" =>
                    ToolUse(line, row, message, block, index, promptId, uuid, parentUuid, model, blockUsage),
                _ => null
            };

            if (observation is not null)
            {
                observations.Add(observation);
                usageAssigned = true;
            }

            index++;
        }

        if (!usageAssigned && usage.Count > 0)
        {
            observations.Add(UsageOnly(
                line,
                row,
                promptId,
                uuid,
                parentUuid,
                model,
                usage));
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
        JsonElement row,
        JsonElement message,
        JsonElement block,
        int index,
        string? promptId,
        string? uuid,
        string? parentUuid,
        string? model,
        IReadOnlyList<UsageMeasurement> usage)
    {
        string? blockType = RuntimeJson.String(block, "type");
        string? thinkingText = RuntimeJson.String(block, "thinking", "text");
        string? signature = RuntimeJson.String(block, "signature");
        bool opaque = _semantics.IsOpaqueThinking(blockType, block);

        var builder = new InterpretationBuilder(blockType ?? "thinking")
        {
            SessionId = line.NativeSessionId,
            TurnId = promptId,
            SubagentId = AgentId(line, row),
            SubagentType = RuntimeJson.String(row, "attributionAgent", "agentType", "agent_type"),
            Role = ObservationRole.AgentThought,
            EventKind = CanonicalEventKind.AssistantThought,
            Tone = ObservationTone.Thought,
            AssistantText = opaque ? null : thinkingText,
            HoverText = opaque ? "Thinking is opaque/redacted by the provider." : thinkingText,
            HeaderDetail = opaque ? OpaqueHeader(signature, usage) : Preview(thinkingText),
            Evidence = opaque ? InferenceEvidence.Opaque : InferenceEvidence.Observed,
            UsageMeasurements = usage,
            Model = model,
            AssistantStepId = RuntimeJson.String(message, "id") ?? uuid
        };

        JsonObject payload = new()
        {
            ["type"] = blockType ?? "thinking",
            ["signature_present"] = !string.IsNullOrEmpty(signature),
            ["signature_length"] = signature?.Length ?? 0,
            ["model"] = model
        };

        return Emit(line, payload, builder.Build(), line.Provenance(
            index, TranscriptCompleteness.Complete, uuid, parentUuid, promptId));
    }

    private HookObservation AssistantText(
        TranscriptLine line,
        JsonElement row,
        string text,
        int index,
        string? promptId,
        string? uuid,
        string? parentUuid,
        string? model,
        IReadOnlyList<UsageMeasurement> usage)
    {
        var builder = new InterpretationBuilder("text")
        {
            SessionId = line.NativeSessionId,
            TurnId = promptId,
            SubagentId = AgentId(line, row),
            Role = ObservationRole.AgentResponse,
            EventKind = CanonicalEventKind.AssistantMessage,
            // Standalone assistant text is not a reply to a preceding request
            // node, so it carries no directional arrow.
            Direction = ObservationDirection.None,
            AssistantText = text,
            HoverText = text,
            HeaderDetail = Preview(text),
            Evidence = InferenceEvidence.Observed,
            UsageMeasurements = usage,
            Model = model,
            AssistantStepId = uuid,
            EnrichmentOnly = true,
            MetadataOnly = true,
            ExcludeFromSummary = true
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
        JsonElement row,
        JsonElement message,
        JsonElement block,
        int index,
        string? promptId,
        string? uuid,
        string? parentUuid,
        string? model,
        IReadOnlyList<UsageMeasurement> usage)
    {
        string toolName = RuntimeJson.String(block, "name") ?? "tool_use";
        string? toolCallId = RuntimeJson.String(block, "id");
        JsonElement input = block.TryGetProperty("input", out JsonElement toolInput)
            ? toolInput
            : default;
        ClaudeMcpIdentity mcp = _semantics.ReadMcpIdentity(
            toolName,
            row,
            message,
            block);

        var builder = new InterpretationBuilder(toolName)
        {
            SessionId = line.NativeSessionId,
            TurnId = promptId,
            SubagentId = AgentId(line, row),
            SubagentType = RuntimeJson.String(row, "attributionAgent", "agentType", "agent_type"),
            ToolName = toolName,
            ToolCallId = toolCallId,
            McpServerName = mcp.ServerName,
            McpToolName = mcp.ToolName,
            Role = ObservationRole.ToolRequest,
            EventKind = CanonicalEventKind.ToolRequested,
            // No directional arrow: a transcript tool request is not one side of
            // a request/response pair the way a hook Pre/PostToolUse is.
            Direction = ObservationDirection.None,
            ToolKind = mcp.IsMcp ? CanonicalToolKind.Mcp : ToolKind(toolName),
            TargetFilePath = ToolInputValue(block, "file_path", "path"),
            TargetFilePaths = input.ValueKind == JsonValueKind.Undefined
                ? []
                : _semantics.ReadTargetPaths(input),
            HeaderDetail = ToolInputPreview(block),
            Evidence = InferenceEvidence.Observed,
            UsageMeasurements = usage,
            Model = model,
            AssistantStepId = RuntimeJson.String(message, "id") ?? uuid,
            Skill = _semantics.ReadSkill(
                toolName,
                input,
                row,
                message,
                block,
                line.NormalizedPath),
            EnrichmentOnly = true,
            ExcludeFromSummary = true
        };

        if (mcp.IsMcp || RuntimeJson.IsMcpPrefixed(toolName))
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
        bool isError = _semantics.IsToolFailure(row, block);
        string status = _semantics.ToolResultStatus(row, block, isError);

        var builder = new InterpretationBuilder("tool_result")
        {
            SessionId = line.NativeSessionId,
            TurnId = promptId,
            SubagentId = AgentId(line, row),
            SubagentType = RuntimeJson.String(row, "attributionAgent", "agentType", "agent_type"),
            ToolCallId = toolCallId,
            Role = isError ? ObservationRole.ToolFailure : ObservationRole.ToolSuccess,
            EventKind = isError ? CanonicalEventKind.ToolFailed : CanonicalEventKind.ToolSucceeded,
            // No directional arrow; it nests under its tool's PreToolUse node.
            Direction = ObservationDirection.None,
            HeaderDetail = ToolResultPreview(row),
            Status = status,
            MatchStrategy = ToolCallMatchStrategy.ToolCallId,
            Evidence = InferenceEvidence.Observed,
            CountsAsFailure = isError,
            TargetFilePaths = _semantics.ReadTargetPaths(block, row),
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

    private IReadOnlyList<HookObservation> SystemRow(
        TranscriptLine line,
        JsonElement row)
    {
        if (RuntimeJson.String(row, "subtype") != "turn_duration")
        {
            return [];
        }

        string? promptId = RuntimeJson.String(row, "promptId") ?? line.TurnHint;
        double? duration = RuntimeJson.Double(row, "durationMs", "duration_ms");
        string source = RuntimeJson.String(row, "uuid") ??
            $"turn-duration:{line.NormalizedPath}:{line.LineNumber}";
        IReadOnlyList<UsageMeasurement> measurements = duration is double durationMs
            ? [new UsageMeasurement(
                "turn_duration",
                (long)Math.Round(durationMs),
                "ms",
                UsageScope.Turn,
                UsageBehavior.Delta,
                source)]
            : [];

        var builder = new InterpretationBuilder("turn_duration")
        {
            SessionId = RuntimeJson.String(row, "sessionId", "session_id") ?? line.NativeSessionId,
            TurnId = promptId,
            SubagentId = AgentId(line, row),
            Role = ObservationRole.Message,
            HeaderDetail = duration is double milliseconds
                ? HookObservation.FormatDuration(TimeSpan.FromMilliseconds(milliseconds))
                : null,
            Evidence = InferenceEvidence.Observed,
            UsageMeasurements = measurements,
            EnrichmentOnly = true,
            MetadataOnly = true,
            ExcludeFromSummary = true
        };

        JsonObject payload = CloneToObject(row);
        if (duration is double value)
        {
            payload["duration_ms"] = value;
        }

        return [Emit(
            line,
            payload,
            builder.Build(),
            line.Provenance(0, TranscriptCompleteness.Complete, source, turnId: promptId))];
    }

    private IReadOnlyList<HookObservation> CostState(
        TranscriptLine line,
        JsonElement row)
    {
        string source = RuntimeJson.String(row, "uuid") ??
            $"cost-state:{line.NormalizedPath}:{line.LineNumber}";
        IReadOnlyList<UsageMeasurement> usage = _semantics.ReadCostState(row, source);
        if (usage.Count == 0)
        {
            return [];
        }

        var builder = new InterpretationBuilder("cost-state")
        {
            SessionId = RuntimeJson.String(row, "sessionId", "session_id") ?? line.NativeSessionId,
            Role = ObservationRole.Message,
            Status = "latest-snapshot",
            Evidence = InferenceEvidence.Observed,
            UsageMeasurements = usage,
            EnrichmentOnly = true,
            MetadataOnly = true,
            ExcludeFromSummary = true
        };

        return [Emit(
            line,
            CloneToObject(row),
            builder.Build(),
            line.Provenance(0, TranscriptCompleteness.Complete, source))];
    }

    private IReadOnlyList<HookObservation> Attachment(
        TranscriptLine line,
        JsonElement row)
    {
        if (!row.TryGetProperty("attachment", out JsonElement attachment) ||
            attachment.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        string? attachmentType = RuntimeJson.String(attachment, "type");
        if (attachmentType == "prompt_snapshot")
        {
            return SystemPromptSnapshot(line, row, attachment);
        }

        if (attachmentType is not ("skill_listing" or "skill_activated"))
        {
            return [];
        }

        SkillEvidenceStage stage = attachmentType == "skill_activated"
            ? SkillEvidenceStage.Invoked
            : SkillEvidenceStage.Available;
        string? promptId = RuntimeJson.String(row, "promptId") ?? line.TurnHint;
        string? recordId = RuntimeJson.String(row, "uuid");
        List<HookObservation> observations = [];
        int index = 0;
        foreach (string skill in _semantics.ReadSkillNames(attachment))
        {
            var builder = new InterpretationBuilder(attachmentType)
            {
                SessionId = RuntimeJson.String(row, "sessionId", "session_id") ?? line.NativeSessionId,
                TurnId = promptId,
                SubagentId = AgentId(line, row),
                Role = ObservationRole.Message,
                AssistantText = skill,
                HeaderDetail = skill,
                Evidence = InferenceEvidence.Observed,
                Skill = new SkillEvidence(
                    skill,
                    stage,
                    InferenceEvidence.Observed,
                    line.NormalizedPath),
                EnrichmentOnly = true,
                MetadataOnly = true,
                ExcludeFromSummary = true
            };
            JsonObject payload = new()
            {
                ["type"] = attachmentType,
                ["skill"] = skill,
                ["stage"] = stage.ToString()
            };
            observations.Add(Emit(
                line,
                payload,
                builder.Build(),
                line.Provenance(
                    index++,
                    TranscriptCompleteness.Complete,
                    recordId,
                    turnId: promptId)));
        }

        return observations;
    }

    private IReadOnlyList<HookObservation> SystemPromptSnapshot(
        TranscriptLine line,
        JsonElement row,
        JsonElement attachment)
    {
        SystemPromptContent? systemPrompt =
            _semantics.ReadSystemPrompt(attachment);
        if (systemPrompt is null)
        {
            return [];
        }

        string? promptId = RuntimeJson.String(row, "promptId") ?? line.TurnHint;
        string? recordId = RuntimeJson.String(row, "uuid");
        string? parentRecordId = RuntimeJson.String(row, "parentUuid");
        var builder = new InterpretationBuilder("prompt_snapshot")
        {
            SessionId = RuntimeJson.String(row, "sessionId", "session_id") ??
                line.NativeSessionId,
            TurnId = promptId,
            SubagentId = AgentId(line, row),
            Role = ObservationRole.SystemPrompt,
            EventKind = CanonicalEventKind.SystemPromptSnapshot,
            Direction = ObservationDirection.Input,
            AssistantText = systemPrompt.Text,
            SystemPrompt = systemPrompt,
            HeaderDetail = $"System prompt \u00b7 {PromptSummary(systemPrompt)}",
            Evidence = InferenceEvidence.Observed,
            ExcludeFromSummary = true
        };
        if (builder.SubagentId is null)
        {
            builder.ScopeOverride = ObservationScope.Session;
        }

        JsonObject payload = new()
        {
            ["type"] = "prompt_snapshot",
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
                    recordId,
                    parentRecordId,
                    promptId))
        ];
    }

    private HookObservation UsageOnly(
        TranscriptLine line,
        JsonElement row,
        string? promptId,
        string? uuid,
        string? parentUuid,
        string? model,
        IReadOnlyList<UsageMeasurement> usage)
    {
        var builder = new InterpretationBuilder("assistant.usage")
        {
            SessionId = line.NativeSessionId,
            TurnId = promptId,
            SubagentId = AgentId(line, row),
            Role = ObservationRole.Message,
            Model = model,
            AssistantStepId = uuid,
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
        return Emit(
            line,
            payload,
            builder.Build(),
            line.Provenance(
                0,
                TranscriptCompleteness.Complete,
                uuid,
                parentUuid,
                promptId));
    }

    private static string? AgentId(TranscriptLine line, JsonElement row) =>
        line.AgentId ?? RuntimeJson.String(row, "agentId", "agent_id");

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

    private static string PromptSummary(SystemPromptContent prompt)
    {
        string parts = prompt.PartCount == 1
            ? "1 part"
            : $"{prompt.PartCount} parts";
        return $"{parts} \u00b7 {prompt.CharacterCount:N0} chars";
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
