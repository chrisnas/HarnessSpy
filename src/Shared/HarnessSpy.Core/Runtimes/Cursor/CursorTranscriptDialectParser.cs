using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;
using HarnessSpy.Core.Sources;

namespace HarnessSpy.Core.Runtimes.Cursor;

// Parses Cursor's sparse agent-transcript JSONL (verified against Cursor
// 3.7.27). Rows are role:"user", role:"assistant" (one step, possibly several
// content blocks), or type:"turn_ended". There are no timestamps, ids, tokens,
// or thinking blocks, so tools/thoughts are heuristic evidence that the
// reconciler matches to authoritative hooks. Native tool names are preserved.
internal sealed class CursorTranscriptDialectParser : TranscriptDialectParserBase
{
    public override string DialectId => DialectIds.CursorTranscript;

    protected override IReadOnlyList<HookObservation> ParseRow(TranscriptLine line, JsonElement row)
    {
        if (RuntimeJson.String(row, "type") == "turn_ended")
        {
            return [TurnEnded(line, row)];
        }

        string? role = RuntimeJson.String(row, "role");
        if (role == "user")
        {
            return [UserPrompt(line, row)];
        }

        if (role == "assistant")
        {
            return AssistantStep(line, row);
        }

        return [];
    }

    private HookObservation UserPrompt(TranscriptLine line, JsonElement row)
    {
        string? prompt = FirstText(row);
        var builder = new InterpretationBuilder("user")
        {
            SessionId = line.NativeSessionId,
            Role = ObservationRole.PromptSubmitted,
            EventKind = CanonicalEventKind.PromptSubmitted,
            Direction = ObservationDirection.Input,
            PromptText = prompt,
            Evidence = InferenceEvidence.Corroborated,
            EnrichmentOnly = true,
            ExcludeFromSummary = true
        };

        if (TryReadAttachedSkill(prompt) is string skill)
        {
            builder.Skill = new SkillEvidence(
                skill,
                SkillEvidenceStage.Attached,
                InferenceEvidence.Observed);
        }

        JsonObject payload = new()
        {
            ["role"] = "user",
            ["prompt"] = prompt
        };

        return Emit(line, payload, builder.Build(), line.Provenance(0, TranscriptCompleteness.Complete));
    }

    private IReadOnlyList<HookObservation> AssistantStep(TranscriptLine line, JsonElement row)
    {
        if (!row.TryGetProperty("message", out JsonElement message) ||
            !message.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        JsonElement[] blocks = [.. content.EnumerateArray()];
        bool hasTool = blocks.Any(block => RuntimeJson.String(block, "type") == "tool_use");

        List<HookObservation> observations = [];
        for (int index = 0; index < blocks.Length; index++)
        {
            JsonElement block = blocks[index];
            string? type = RuntimeJson.String(block, "type");
            if (type == "text")
            {
                observations.Add(TextBlock(line, block, index, thought: hasTool));
            }
            else if (type == "tool_use")
            {
                observations.Add(ToolBlock(line, block, index));
            }
        }

        return observations;
    }

    private HookObservation TextBlock(TranscriptLine line, JsonElement block, int index, bool thought)
    {
        string? text = RuntimeJson.String(block, "text");
        var builder = new InterpretationBuilder(thought ? "text" : "text")
        {
            SessionId = line.NativeSessionId,
            AssistantText = text,
            HoverText = text,
            Role = thought ? ObservationRole.AgentThought : ObservationRole.AgentResponse,
            EventKind = thought ? CanonicalEventKind.AssistantThought : CanonicalEventKind.AssistantMessage,
            Direction = ObservationDirection.None,
            Tone = thought ? ObservationTone.Thought : ObservationTone.Normal,
            HeaderDetail = Preview(text),
            Evidence = InferenceEvidence.Heuristic,
            EnrichmentOnly = true,
            ExcludeFromSummary = true
        };

        JsonObject payload = new()
        {
            ["type"] = "text",
            ["text"] = text
        };

        return Emit(line, payload, builder.Build(), line.Provenance(index, TranscriptCompleteness.Complete));
    }

    private HookObservation ToolBlock(TranscriptLine line, JsonElement block, int index)
    {
        string toolName = RuntimeJson.String(block, "name") ?? "tool_use";
        bool isDiscovery = toolName == "GetDynamicTools";
        bool isDynamicCall = toolName == "CallDynamicTool";

        var builder = new InterpretationBuilder(toolName)
        {
            SessionId = line.NativeSessionId,
            ToolName = toolName,
            Role = ObservationRole.ToolRequest,
            EventKind = CanonicalEventKind.ToolRequested,
            Direction = ObservationDirection.None,
            ToolKind = ToolKind(toolName),
            TargetFilePath = ToolInputValue(block, "file_path", "path"),
            HeaderDetail = ToolInputPreview(block) ?? toolName,
            Evidence = InferenceEvidence.Heuristic
        };

        if (isDynamicCall)
        {
            builder.Tone = ObservationTone.Mcp;
            builder.ToolKind = CanonicalToolKind.Mcp;
        }
        else if (isDiscovery)
        {
            builder.Tone = ObservationTone.Mcp;
        }

        JsonObject payload = CloneToObject(block);

        return Emit(line, payload, builder.Build(), line.Provenance(index, TranscriptCompleteness.Complete));
    }

    private HookObservation TurnEnded(TranscriptLine line, JsonElement row)
    {
        string? status = RuntimeJson.String(row, "status");
        bool aborted = string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
        var builder = new InterpretationBuilder("turn_ended")
        {
            SessionId = line.NativeSessionId,
            Role = ObservationRole.TurnStop,
            EventKind = CanonicalEventKind.TurnCompleted,
            Direction = ObservationDirection.Output,
            Tone = ObservationTone.Stop,
            Status = status,
            IsAbortedStop = aborted,
            HeaderDetail = status,
            Evidence = InferenceEvidence.Corroborated,
            EnrichmentOnly = true,
            ExcludeFromSummary = true
        };

        JsonObject payload = CloneToObject(row);
        return Emit(line, payload, builder.Build(), line.Provenance(0, TranscriptCompleteness.Complete));
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

    private static string? ToolInputPreview(JsonElement block)
    {
        string? value = ToolInputValue(
            block,
            "command",
            "file_path",
            "path",
            "pattern",
            "glob_pattern",
            "query",
            "url");
        return Preview(value);
    }

    private static string? FirstText(JsonElement row)
    {
        if (!row.TryGetProperty("message", out JsonElement message) ||
            !message.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement block in content.EnumerateArray())
        {
            if (RuntimeJson.String(block, "type") == "text")
            {
                return RuntimeJson.String(block, "text");
            }
        }

        return null;
    }

    // Extracts a manually attached skill name from the injected
    // <manually_attached_skills> block without trusting arbitrary prompt XML.
    private static string? TryReadAttachedSkill(string? prompt)
    {
        if (string.IsNullOrEmpty(prompt))
        {
            return null;
        }

        const string open = "<manually_attached_skills>";
        int start = prompt.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        int nameIndex = prompt.IndexOf("name:", start, StringComparison.OrdinalIgnoreCase);
        if (nameIndex < 0)
        {
            return null;
        }

        int valueStart = nameIndex + "name:".Length;
        int end = prompt.IndexOfAny(['\n', '\r'], valueStart);
        string value = end < 0 ? prompt[valueStart..] : prompt[valueStart..end];
        value = value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
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
