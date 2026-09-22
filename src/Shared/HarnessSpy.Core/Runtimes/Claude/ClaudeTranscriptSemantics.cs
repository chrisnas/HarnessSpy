using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Runtimes.Claude;

// Shared, stateless interpretation of Claude transcript values. Both the live
// enrichment parser and the passive catalog can use these rules without sharing
// their different turn/session projection models.
internal sealed class ClaudeTranscriptSemantics
{
    private readonly SystemPromptTextNormalizer _systemPromptNormalizer = new();

    public SystemPromptContent? ReadSystemPrompt(JsonElement attachment)
    {
        if (attachment.ValueKind != JsonValueKind.Object ||
            !string.Equals(
                RuntimeJson.String(attachment, "type"),
                "prompt_snapshot",
                StringComparison.Ordinal) ||
            !attachment.TryGetProperty(
                "systemPrompt",
                out JsonElement systemPrompt))
        {
            return null;
        }

        return _systemPromptNormalizer.FromValue(systemPrompt);
    }

    public IReadOnlyList<UsageMeasurement> ReadAssistantUsage(
        JsonElement message,
        string sourceRecordId)
    {
        if (!message.TryGetProperty("usage", out JsonElement usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<UsageMeasurement> measurements = [];
        AddUsage(measurements, usage, "input_tokens", UsageScope.Turn, UsageBehavior.CumulativeSnapshot, sourceRecordId);
        AddUsage(measurements, usage, "cache_read_input_tokens", UsageScope.Turn, UsageBehavior.CumulativeSnapshot, sourceRecordId);
        AddUsage(measurements, usage, "cache_creation_input_tokens", UsageScope.Turn, UsageBehavior.CumulativeSnapshot, sourceRecordId);
        AddUsage(measurements, usage, "output_tokens", UsageScope.Turn, UsageBehavior.Delta, sourceRecordId);

        if (usage.TryGetProperty("output_tokens_details", out JsonElement outputDetails) &&
            outputDetails.ValueKind == JsonValueKind.Object)
        {
            AddUsage(measurements, outputDetails, "thinking_tokens", UsageScope.Turn, UsageBehavior.Delta, sourceRecordId);
        }

        if (usage.TryGetProperty("server_tool_use", out JsonElement serverToolUse) &&
            serverToolUse.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in serverToolUse.EnumerateObject())
            {
                AddUsage(
                    measurements,
                    serverToolUse,
                    property.Name,
                    UsageScope.Turn,
                    UsageBehavior.Delta,
                    sourceRecordId,
                    "requests");
            }
        }

        return measurements;
    }

    public IReadOnlyList<UsageMeasurement> ReadCostState(
        JsonElement row,
        string sourceRecordId)
    {
        List<UsageMeasurement> measurements = [];
        AddUsage(measurements, row, "totalAPIDuration", UsageScope.Session, UsageBehavior.FinalSnapshot, sourceRecordId, "ms");
        AddUsage(measurements, row, "totalAPIDurationWithoutRetries", UsageScope.Session, UsageBehavior.FinalSnapshot, sourceRecordId, "ms");
        AddUsage(measurements, row, "totalToolDuration", UsageScope.Session, UsageBehavior.FinalSnapshot, sourceRecordId, "ms");
        AddUsage(measurements, row, "totalDuration", UsageScope.Session, UsageBehavior.FinalSnapshot, sourceRecordId, "ms");
        AddUsage(measurements, row, "totalLinesAdded", UsageScope.Session, UsageBehavior.FinalSnapshot, sourceRecordId, "lines");
        AddUsage(measurements, row, "totalLinesRemoved", UsageScope.Session, UsageBehavior.FinalSnapshot, sourceRecordId, "lines");

        double? cost = RuntimeJson.Double(row, "totalCostUSD", "total_cost_usd");
        if (cost is double dollars)
        {
            double microDollars = Math.Round(
                dollars * 1_000_000d,
                MidpointRounding.AwayFromZero);
            if (double.IsFinite(microDollars) &&
                microDollars is >= long.MinValue and <= long.MaxValue)
            {
                measurements.Add(new UsageMeasurement(
                    "total_cost_usd",
                    (long)microDollars,
                    "micro-usd",
                    UsageScope.Session,
                    UsageBehavior.FinalSnapshot,
                    sourceRecordId));
            }
        }

        if (row.TryGetProperty("modelUsage", out JsonElement modelUsage) &&
            modelUsage.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty model in modelUsage.EnumerateObject())
            {
                if (model.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string prefix = $"{model.Name}.";
                AddUsage(measurements, model.Value, "inputTokens", UsageScope.Session, UsageBehavior.FinalSnapshot, sourceRecordId, namePrefix: prefix);
                AddUsage(measurements, model.Value, "outputTokens", UsageScope.Session, UsageBehavior.FinalSnapshot, sourceRecordId, namePrefix: prefix);
                AddUsage(measurements, model.Value, "cacheReadInputTokens", UsageScope.Session, UsageBehavior.FinalSnapshot, sourceRecordId, namePrefix: prefix);
                AddUsage(measurements, model.Value, "cacheCreationInputTokens", UsageScope.Session, UsageBehavior.FinalSnapshot, sourceRecordId, namePrefix: prefix);
                AddUsage(measurements, model.Value, "webSearchRequests", UsageScope.Session, UsageBehavior.FinalSnapshot, sourceRecordId, "requests", prefix);
            }
        }

        return measurements;
    }

    public bool IsOpaqueThinking(string? blockType, JsonElement block)
    {
        if (string.Equals(blockType, "redacted_thinking", StringComparison.Ordinal))
        {
            return true;
        }

        return string.IsNullOrEmpty(RuntimeJson.String(block, "thinking", "text")) &&
            !string.IsNullOrEmpty(RuntimeJson.String(block, "signature"));
    }

    public bool IsToolFailure(JsonElement row, JsonElement block)
    {
        if (Boolean(block, "is_error", "isError") == true ||
            Boolean(row, "is_error", "isError") == true ||
            Boolean(row, "interrupted") == true ||
            RuntimeJson.String(row, "toolDenialKind") is not null)
        {
            return true;
        }

        if (!row.TryGetProperty("toolUseResult", out JsonElement result))
        {
            return false;
        }

        if (result.ValueKind == JsonValueKind.String)
        {
            return result.GetString()?.StartsWith(
                "Error:",
                StringComparison.OrdinalIgnoreCase) == true;
        }

        return result.ValueKind == JsonValueKind.Object &&
            (Boolean(result, "interrupted", "is_error", "isError") == true ||
             Boolean(result, "success") == false ||
             (result.TryGetProperty("error", out JsonElement error) &&
              error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined));
    }

    public string ToolResultStatus(JsonElement row, JsonElement block, bool isFailure)
    {
        if (RuntimeJson.String(row, "toolDenialKind") is not null)
        {
            return "denied";
        }

        bool interrupted =
            Boolean(block, "interrupted") == true ||
            Boolean(row, "interrupted") == true ||
            (row.TryGetProperty("toolUseResult", out JsonElement result) &&
             result.ValueKind == JsonValueKind.Object &&
             Boolean(result, "interrupted") == true);
        return interrupted ? "interrupted" : isFailure ? "failure" : "success";
    }

    public ClaudeMcpIdentity ReadMcpIdentity(
        string? nativeToolName,
        JsonElement row,
        JsonElement message,
        JsonElement block)
    {
        string? server =
            AttributionName(block, "attributionMcpServer") ??
            AttributionName(message, "attributionMcpServer") ??
            AttributionName(row, "attributionMcpServer");
        string? tool =
            AttributionName(block, "attributionMcpTool") ??
            AttributionName(message, "attributionMcpTool") ??
            AttributionName(row, "attributionMcpTool");

        if (nativeToolName is not null &&
            nativeToolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            string remainder = nativeToolName[5..];
            int separator = remainder.IndexOf("__", StringComparison.Ordinal);
            if (separator > 0)
            {
                server ??= remainder[..separator];
                tool ??= remainder[(separator + 2)..];
            }
        }

        return new ClaudeMcpIdentity(server, tool);
    }

    public SkillEvidence? ReadSkill(
        string nativeToolName,
        JsonElement input,
        JsonElement row,
        JsonElement message,
        JsonElement block,
        string sourcePath)
    {
        if (string.Equals(nativeToolName, "Skill", StringComparison.OrdinalIgnoreCase) &&
            input.ValueKind == JsonValueKind.Object &&
            RuntimeJson.String(input, "skill", "skillName", "skill_name") is string invoked)
        {
            return new SkillEvidence(
                invoked.Trim(),
                SkillEvidenceStage.Invoked,
                InferenceEvidence.Observed,
                sourcePath);
        }

        string? name =
            AttributionName(block, "attributionSkill") ??
            AttributionName(message, "attributionSkill") ??
            AttributionName(row, "attributionSkill") ??
            AttributionName(block, "skill") ??
            AttributionName(row, "skill");
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new SkillEvidence(
                name.Trim(),
                SkillEvidenceStage.Invoked,
                InferenceEvidence.Observed,
                sourcePath);
    }

    public IReadOnlyList<string> ReadSkillNames(JsonElement attachment)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string propertyName in new[] { "names", "skills", "skillNames" })
        {
            if (!attachment.TryGetProperty(propertyName, out JsonElement value) ||
                value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement item in value.EnumerateArray())
            {
                string? name = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object => RuntimeJson.String(item, "name", "skillName"),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name.Trim());
                }
            }
        }

        string? content = RuntimeJson.String(attachment, "content");
        if (!string.IsNullOrWhiteSpace(content))
        {
            foreach (string line in content.Split(
                         ['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith("- ", StringComparison.Ordinal))
                {
                    continue;
                }

                string name = line[2..];
                int colon = name.IndexOf(':');
                if (colon >= 0)
                {
                    name = name[..colon];
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name.Trim());
                }
            }
        }

        return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<string> ReadTargetPaths(params JsonElement[] containers)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement container in containers)
        {
            CollectTargetPaths(container, paths, depth: 0);
        }

        return paths.ToArray();
    }

    private static void AddUsage(
        ICollection<UsageMeasurement> measurements,
        JsonElement container,
        string nativeName,
        UsageScope scope,
        UsageBehavior behavior,
        string sourceRecordId,
        string unit = "tokens",
        string namePrefix = "")
    {
        if (RuntimeJson.Long(container, nativeName) is long value)
        {
            measurements.Add(new UsageMeasurement(
                namePrefix + nativeName,
                value,
                unit,
                scope,
                behavior,
                sourceRecordId));
        }
    }

    private static string? AttributionName(JsonElement container, string propertyName)
    {
        if (container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => RuntimeJson.String(value, "name", "skillName", "toolName", "serverName"),
            _ => null
        };
    }

    private static void CollectTargetPaths(
        JsonElement element,
        ISet<string> paths,
        int depth)
    {
        if (depth > 4)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                CollectTargetPaths(item, paths, depth + 1);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String &&
                IsPathProperty(property.Name) &&
                property.Value.GetString() is string path &&
                !string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path);
            }
            else
            {
                CollectTargetPaths(property.Value, paths, depth + 1);
            }
        }
    }

    private static bool IsPathProperty(string name) =>
        name.Equals("path", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("file_path", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("filePath", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("targetPath", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("planFilePath", StringComparison.OrdinalIgnoreCase);

    private static bool? Boolean(JsonElement container, params string[] names)
    {
        if (container.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (!container.TryGetProperty(name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }

        return null;
    }
}

internal sealed record ClaudeMcpIdentity(
    string? ServerName,
    string? ToolName)
{
    public bool IsMcp =>
        !string.IsNullOrWhiteSpace(ServerName) ||
        !string.IsNullOrWhiteSpace(ToolName);
}
