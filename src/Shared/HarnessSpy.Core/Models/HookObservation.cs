using System.Text;
using System.Text.Json;
using System.IO;
using System.Linq;
using HarnessSpy.Core.Providers;

namespace HarnessSpy.Core.Models;

public sealed class HookObservation
{
    private HookObservation(
        Guid eventId,
        DateTimeOffset observedAtUtc,
        JsonElement payload,
        JsonElement rawPayload,
        HookProvider provider,
        HookSurface surface,
        ObservationSourceKind sourceKind,
        string rawHookEventName,
        string hookEventName,
        CanonicalEventKind eventKind,
        CanonicalToolKind toolKind,
        CorrelationQuality correlationQuality,
        string? sessionId,
        string? generationId,
        WorkspaceContext workspace,
        string displayJson,
        double? durationMs,
        string? sourceFilePath)
    {
        EventId = eventId;
        ObservedAtUtc = observedAtUtc;
        Payload = payload;
        RawPayload = rawPayload;
        Provider = provider;
        Surface = surface;
        SourceKind = sourceKind;
        RawHookEventName = rawHookEventName;
        HookEventName = hookEventName;
        EventKind = eventKind;
        ToolKind = toolKind;
        CorrelationQuality = correlationQuality;
        SessionId = sessionId;
        GenerationId = generationId;
        Workspace = workspace;
        DisplayJson = displayJson;
        DurationMs = durationMs;
        SourceFilePath = sourceFilePath;
    }

    public Guid EventId { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public JsonElement Payload { get; }

    public JsonElement RawPayload { get; }

    public HookProvider Provider { get; }

    public HookSurface Surface { get; }

    public ObservationSourceKind SourceKind { get; }

    public string RawHookEventName { get; }

    public string HookEventName { get; }

    public CanonicalEventKind EventKind { get; }

    public CanonicalToolKind ToolKind { get; }

    public CorrelationQuality CorrelationQuality { get; }

    public string? SessionId { get; }

    public string ProviderScopedSessionId =>
        $"{Provider}:{Surface}:{SessionId ?? "unknown"}";

    // Identifies a single agent turn within a conversation: every hook event a
    // prompt produces shares this id, which lets the viewer group a turn's
    // events together. Null for session-scoped events (sessionStart/End) and
    // any payload that does not carry one.
    public string? GenerationId { get; }

    public WorkspaceContext Workspace { get; }

    public string DisplayJson { get; }

    public double? DurationMs { get; }

    public string? SourceFilePath { get; }

    // Correlation key shared by preToolUse, postToolUse, and postToolUseFailure
    // for the same tool invocation. Used to nest post under pre.
    public string? ToolUseId => ReadString(Payload, "tool_use_id");

    // Correlation key shared by subagentStart and subagentStop for the same
    // subagent invocation.
    public string? SubagentId => ReadString(Payload, "subagent_id");

    // The tool being invoked (e.g. "Shell", "Read", "Write", "Grep").
    public string? ToolName => ReadString(Payload, "tool_name");

    // The user prompt that opened a turn; only present on beforeSubmitPrompt.
    public string? PromptText => ReadString(Payload, "prompt");

    // Aggregated assistant text (afterAgentResponse) or thinking text
    // (afterAgentThought); surfaced as a hover tooltip in the tree.
    public string? Text => ReadString(Payload, "text");

    public string? McpServerName => ReadString(Payload, "mcp_server_name");

    public string? Status => ReadString(Payload, "status");

    public string? SubagentType => ReadString(Payload, "subagent_type");

    public string? Task => ReadString(Payload, "task");

    public long? InputTokens => ReadLong(Payload, "input_tokens");

    public long? OutputTokens => ReadLong(Payload, "output_tokens");

    public long? CacheReadTokens => ReadLong(Payload, "cache_read_tokens");

    public long? CacheWriteTokens => ReadLong(Payload, "cache_write_tokens");

    // File path from beforeReadFile/afterFileEdit, or from a Read/Write tool_input.
    public string? TargetFilePath =>
        ReadString(Payload, "file_path") ?? ReadToolInputString("file_path") ?? ReadToolInputString("path");

    public string? SkillName => TryGetSkillName(TargetFilePath);

    public bool HasTokenCounts =>
        InputTokens is not null ||
        OutputTokens is not null ||
        CacheReadTokens is not null ||
        CacheWriteTokens is not null;

    public bool IsMcpPrefixedTool =>
        ToolName is not null &&
        ToolName.StartsWith("MCP:", StringComparison.Ordinal);

    // The most relevant payload fields for the selected hook, shown as a
    // name/value table above the raw payload in the inspector pane.
    public IReadOnlyList<PayloadField> DetailFields => BuildDetailFields();

    private IReadOnlyList<PayloadField> BuildDetailFields()
    {
        List<PayloadField> fields = [];

        switch (HookEventName)
        {
            case "sessionStart":
            case "sessionEnd":
                AddScalar(fields, "is_background_agent");
                break;

            case "beforeSubmitPrompt":
                AddScalar(fields, "prompt");
                AddArrayMembers(fields, "attachments");
                break;

            case "afterAgentThought":
            case "afterAgentResponse":
                AddScalar(fields, "text");
                break;

            case "preToolUse":
                AddObjectMembers(fields, "tool_input");
                break;

            case "postToolUse":
                AddJsonStringMembers(fields, "tool_output");
                break;

            case "postToolUseFailure":
                AddObjectMembers(fields, "tool_input");
                AddScalar(fields, "is_interrupt");
                AddScalar(fields, "failure_type");
                AddScalar(fields, "error_message");
                break;

            case "beforeShellExecution":
                AddScalar(fields, "command");
                AddScalar(fields, "cwd");
                AddScalar(fields, "sandbox");
                break;

            case "afterShellExecution":
                AddScalar(fields, "command");
                AddScalar(fields, "output");
                AddScalar(fields, "sandbox");
                break;

            case "beforeMCPExecution":
                AddScalar(fields, "mcp_server_name");
                AddScalar(fields, "tool_name");
                AddScalar(fields, "command");
                AddJsonStringMembers(fields, "tool_input");
                break;

            case "afterMCPExecution":
                AddScalar(fields, "mcp_server_name");
                AddScalar(fields, "tool_name");
                AddScalar(fields, "command");
                AddJsonStringMembers(fields, "tool_input");
                AddJsonStringMembers(fields, "result_json");
                break;

            case "beforeReadFile":
                AddScalar(fields, "file_path");
                AddScalar(fields, "content");
                AddArrayMembers(fields, "attachments");
                break;

            case "beforeTabFileRead":
                AddScalar(fields, "content");
                break;

            case "afterFileEdit":
                AddScalar(fields, "file_path");
                AddArrayMembers(fields, "edits");
                break;

            case "afterTabFileEdit":
                AddFlattenedArrayMembers(fields, "edits");
                break;

            case "workspaceOpen":
                AddScalar(fields, "cursor_version");
                AddScalar(fields, "user_email");
                AddArrayMembers(fields, "workspace_roots");
                break;

            case "subagentStart":
            case "subagentStop":
                AddScalar(fields, "subagent_type");
                AddScalar(fields, "task");
                AddScalarPreferred(fields, "model", "subagent_model");
                AddScalar(fields, "git_branch");
                break;

            case "preCompact":
                AddPreCompactDetailFields(fields);
                break;

            case "stop":
                AddScalar(fields, "loop_count");
                break;

            default:
                AddAllTopLevel(fields);
                break;
        }

        AddDurationField(fields);

        return fields;
    }

    // Surfaces the elapsed time on any hook that reports it, always as the first
    // row - even when a preceding case (e.g. preCompact) already listed it.
    private void AddDurationField(List<PayloadField> fields)
    {
        fields.RemoveAll(field => field.Name is "duration" or "duration_ms");

        if (Payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (string name in new[] { "duration_ms", "duration" })
        {
            if (Payload.TryGetProperty(name, out JsonElement value))
            {
                fields.Insert(0, new PayloadField(name, FormatFieldValue(value)));
                return;
            }
        }
    }

    private void AddScalar(List<PayloadField> fields, string propertyName)
    {
        if (Payload.ValueKind == JsonValueKind.Object &&
            Payload.TryGetProperty(propertyName, out JsonElement value))
        {
            fields.Add(new PayloadField(propertyName, FormatFieldValue(value)));
        }
    }

    // Adds the first of the candidate names that is present, keeping the label
    // aligned with the actual payload shape (e.g. model vs subagent_model).
    private void AddScalarPreferred(List<PayloadField> fields, params string[] propertyNames)
    {
        if (Payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (string propertyName in propertyNames)
        {
            if (Payload.TryGetProperty(propertyName, out JsonElement value))
            {
                fields.Add(new PayloadField(propertyName, FormatFieldValue(value)));
                return;
            }
        }
    }

    // Flattens an object's members into one row per member (e.g. tool_input).
    private void AddObjectMembers(List<PayloadField> fields, string propertyName)
    {
        if (Payload.ValueKind == JsonValueKind.Object &&
            Payload.TryGetProperty(propertyName, out JsonElement container) &&
            container.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty member in container.EnumerateObject())
            {
                fields.Add(new PayloadField(member.Name, FormatFieldValue(member.Value)));
            }
        }
    }

    // tool_output (and similar) arrives as a JSON-encoded string, so its inner
    // paths carry doubled backslashes. Parsing it yields one row per subfield
    // (e.g. tool_output.file_path, tool_output.success) with clean values.
    private void AddJsonStringMembers(List<PayloadField> fields, string propertyName)
    {
        if (Payload.ValueKind != JsonValueKind.Object ||
            !Payload.TryGetProperty(propertyName, out JsonElement value))
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string raw = value.GetString() ?? string.Empty;
            if (!TryAddJsonObjectMembers(fields, propertyName, raw))
            {
                fields.Add(new PayloadField(propertyName, NormalizeDisplayEscapes(raw)));
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty member in value.EnumerateObject())
            {
                fields.Add(new PayloadField($"{propertyName}.{member.Name}", FormatFieldValue(member.Value)));
            }

            return;
        }

        fields.Add(new PayloadField(propertyName, FormatFieldValue(value)));
    }

    private static bool TryAddJsonObjectMembers(List<PayloadField> fields, string propertyName, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (JsonProperty member in document.RootElement.EnumerateObject())
            {
                fields.Add(new PayloadField($"{propertyName}.{member.Name}", FormatFieldValue(member.Value)));
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Flattens an array of objects into indexed rows (e.g. attachments, edits),
    // noting an empty collection so the reader knows the field was present.
    private void AddArrayMembers(List<PayloadField> fields, string propertyName)
    {
        if (Payload.ValueKind != JsonValueKind.Object ||
            !Payload.TryGetProperty(propertyName, out JsonElement container) ||
            container.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        int index = 0;
        foreach (JsonElement item in container.EnumerateArray())
        {
            string prefix = $"{propertyName}[{index}]";
            if (item.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty member in item.EnumerateObject())
                {
                    fields.Add(new PayloadField($"{prefix}.{member.Name}", FormatFieldValue(member.Value)));
                }
            }
            else
            {
                fields.Add(new PayloadField(prefix, FormatFieldValue(item)));
            }

            index++;
        }

        if (index == 0)
        {
            fields.Add(new PayloadField(propertyName, "(none)"));
        }
    }

    // Same as AddArrayMembers, but object values (e.g. afterTabFileEdit range)
    // become one row per nested field instead of a single JSON blob.
    private void AddFlattenedArrayMembers(List<PayloadField> fields, string propertyName)
    {
        if (Payload.ValueKind != JsonValueKind.Object ||
            !Payload.TryGetProperty(propertyName, out JsonElement container) ||
            container.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        int index = 0;
        foreach (JsonElement item in container.EnumerateArray())
        {
            AddFlattenedValue(fields, $"{propertyName}[{index}]", item);
            index++;
        }

        if (index == 0)
        {
            fields.Add(new PayloadField(propertyName, "(none)"));
        }
    }

    private static void AddFlattenedValue(List<PayloadField> fields, string prefix, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty member in value.EnumerateObject())
            {
                AddFlattenedValue(fields, $"{prefix}.{member.Name}", member.Value);
            }

            return;
        }

        fields.Add(new PayloadField(prefix, FormatFieldValue(value)));
    }

    private void AddAllTopLevel(List<PayloadField> fields)
    {
        if (Payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty member in Payload.EnumerateObject())
        {
            fields.Add(new PayloadField(member.Name, FormatFieldValue(member.Value)));
        }
    }

    private static string FormatFieldValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => NormalizeDisplayEscapes(element.GetString() ?? string.Empty),
            JsonValueKind.Null => "null",
            JsonValueKind.Object or JsonValueKind.Array => FormatForDisplay(element),
            _ => element.GetRawText()
        };
    }

    // JSON-encoded MCP/tool payloads often carry doubled backslashes in string
    // values; collapse them so paths read naturally in the inspector table.
    private static string NormalizeDisplayEscapes(string value)
    {
        return value.Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    // sessionStart/sessionEnd bracket the whole conversation, so they belong at
    // the session level even though Cursor may stamp them with a generation_id.
    public bool IsSessionLifecycle =>
        StringComparer.Ordinal.Equals(HookEventName, "sessionStart") ||
        StringComparer.Ordinal.Equals(HookEventName, "sessionEnd");

    public bool IsWorkspaceOpen => StringComparer.Ordinal.Equals(HookEventName, "workspaceOpen");

    public bool IsTabHook =>
        StringComparer.Ordinal.Equals(HookEventName, "beforeTabFileRead") ||
        StringComparer.Ordinal.Equals(HookEventName, "afterTabFileEdit");

    public bool IsStop => StringComparer.Ordinal.Equals(HookEventName, "stop");

    public bool IsAbortedStop =>
        IsStop &&
        StringComparer.OrdinalIgnoreCase.Equals(Status, "aborted");

    // Skill usage is not a dedicated hook: the agent reads SKILL.md, and the
    // parent folder name is the skill id (e.g. .../dotnet-threads-analysis/SKILL.md).
    public static string? TryGetSkillName(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) ||
            !filePath.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? directory = Path.GetDirectoryName(filePath.Replace('/', Path.DirectorySeparatorChar));
        string? name = Path.GetFileName(directory);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    // Builds "{hookEventName} · {detail} · {timing}", omitting either segment
    // when there is nothing relevant to show for that event.
    public string OccurrenceHeader
    {
        get
        {
            string header = HookEventName;

            string? detail = GetDetail();
            if (!string.IsNullOrEmpty(detail))
            {
                header = $"{header} · {detail}";
            }

            string? timing = GetTimingSuffix();
            if (!string.IsNullOrEmpty(timing))
            {
                header = $"{header} · {timing}";
            }

            return header;
        }
    }

    // Surfaces the most useful field(s) for each hook type so the tree header
    // reads meaningfully without opening the raw payload pane.
    private string? GetDetail()
    {
        switch (HookEventName)
        {
            case "sessionStart":
                string? version = ReadString(Payload, "cursor_version");
                return version is null ? null : $"Cursor {version}";

            case "preToolUse":
            case "postToolUse":
                return GetToolUseDetail();

            case "beforeSubmitPrompt":
                return JoinNonEmpty(ReadString(Payload, "composer_mode"), ReadString(Payload, "model"));

            case "beforeReadFile":
            case "afterFileEdit":
            case "beforeTabFileRead":
            case "afterTabFileEdit":
                string? filePath = ReadString(Payload, "file_path");
                return filePath is null ? null : Path.GetFileName(filePath);

            case "beforeShellExecution":
                return ReadString(Payload, "command");

            case "beforeMCPExecution":
            case "afterMCPExecution":
                return GetMcpExecutionDetail();

            case "afterAgentResponse":
                return GetTokenSummary();

            case "stop":
                return JoinNonEmpty(ReadString(Payload, "status"), GetTokenSummary());

            case "preCompact":
                return GetPreCompactDetail();

            case "sessionEnd":
                return JoinNonEmpty(
                    ReadString(Payload, "reason"),
                    ReadString(Payload, "final_status"),
                    ReadString(Payload, "error_message"));

            case "postToolUseFailure":
                return GetFailureDetail();

            default:
                return null;
        }
    }

    // "{tool_name} ({model})", plus the target file for Read/Write tools whose
    // file path lives inside tool_input.
    private string? GetToolUseDetail()
    {
        string? toolName = ReadString(Payload, "tool_name");
        string? model = ReadString(Payload, "model");

        string? head = toolName;
        if (!string.IsNullOrEmpty(model))
        {
            head = string.IsNullOrEmpty(head) ? $"({model})" : $"{head} ({model})";
        }

        if (toolName is "Read" or "Write")
        {
            string? filePath = ReadToolInputString("file_path") ?? ReadToolInputString("path");
            if (!string.IsNullOrEmpty(filePath))
            {
                head = JoinNonEmpty(head, filePath);
            }
        }

        return head;
    }

    // Compact per-turn token accounting shared by afterAgentResponse and stop.
    private string? GetTokenSummary()
    {
        List<string> parts = [];

        if (ReadLong(Payload, "input_tokens") is long input)
        {
            parts.Add($"in {FormatTokens(input)}");
        }

        if (ReadLong(Payload, "output_tokens") is long output)
        {
            parts.Add($"out {FormatTokens(output)}");
        }

        if (ReadLong(Payload, "cache_read_tokens") is long cacheRead)
        {
            parts.Add($"cache r {FormatTokens(cacheRead)}");
        }

        if (ReadLong(Payload, "cache_write_tokens") is long cacheWrite)
        {
            parts.Add($"cache w {FormatTokens(cacheWrite)}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private void AddPreCompactDetailFields(List<PayloadField> fields)
    {
        AddScalar(fields, "is_first_compaction");
        AddScalar(fields, "trigger");

        if (ReadDouble(Payload, "context_usage_percent") is double usage)
        {
            fields.Add(new PayloadField("context usage percent", FormatPercent(usage)));
        }

        if (ReadLong(Payload, "context_window_size") is long windowSize)
        {
            fields.Add(new PayloadField("window size", FormatTokens(windowSize)));
        }

        long? messageCount = ReadLong(Payload, "message_count");
        long? messagesToCompact = ReadLong(Payload, "messages_to_compact");
        if (messageCount is not null || messagesToCompact is not null)
        {
            fields.Add(new PayloadField(
                "messages count/to compact",
                $"{messageCount?.ToString() ?? "?"}/{messagesToCompact?.ToString() ?? "?"}"));
        }
    }

    private string? GetPreCompactDetail()
    {
        List<string> parts = [];

        if (ReadString(Payload, "trigger") is string trigger)
        {
            parts.Add(trigger);
        }

        if (ReadDouble(Payload, "context_usage_percent") is double usage)
        {
            parts.Add(FormatPercent(usage));
        }

        if (ReadLong(Payload, "context_window_size") is long windowSize)
        {
            parts.Add(FormatTokens(windowSize));
        }

        long? messageCount = ReadLong(Payload, "message_count");
        long? messagesToCompact = ReadLong(Payload, "messages_to_compact");
        if (messageCount is not null || messagesToCompact is not null)
        {
            parts.Add($"{messageCount?.ToString() ?? "?"}/{messagesToCompact?.ToString() ?? "?"}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string FormatPercent(double value) => $"{value:0.##}%";

    private string? GetFailureDetail()
    {
        string? errorMessage = ReadString(Payload, "error_message") ?? ReadString(Payload, "error");
        return JoinNonEmpty(
            ReadString(Payload, "tool_name"),
            ReadString(Payload, "failure_type"),
            FormatFailureErrorPreview(errorMessage));
    }

    private static string? FormatFailureErrorPreview(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
        {
            return null;
        }

        string normalized = errorMessage.ReplaceLineEndings("\n");
        int newlineIndex = normalized.IndexOf('\n');
        if (newlineIndex < 0)
        {
            return normalized;
        }

        return normalized[..newlineIndex].TrimEnd() + "...";
    }

    private string? GetMcpExecutionDetail()
    {
        return JoinNonEmpty(ReadString(Payload, "mcp_server_name"), ReadString(Payload, "tool_name"));
    }

    private string? ReadToolInputString(string propertyName)
    {
        if (Payload.ValueKind == JsonValueKind.Object &&
            Payload.TryGetProperty("tool_input", out JsonElement toolInput) &&
            toolInput.ValueKind == JsonValueKind.Object)
        {
            return ReadString(toolInput, propertyName);
        }

        return null;
    }

    public static string FormatTokens(long value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.##}M";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000d:0.##}k";
        }

        return value.ToString();
    }

    // The clock time is only meaningful for sessionStart (the first event of
    // a session); every other event either reports its own duration or gets
    // no timing suffix at all.
    private string? GetTimingSuffix()
    {
        if (StringComparer.Ordinal.Equals(HookEventName, "sessionStart"))
        {
            return ObservedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }

        return DurationMs is double ms ? FormatDuration(TimeSpan.FromMilliseconds(ms)) : null;
    }

    // Formats a duration as its non-zero leading units down to milliseconds,
    // e.g. "456ms", "4s 000ms", "1m 05s 000ms", "1h 01m 01s 500ms". Leading
    // hour/minute/second units are dropped while they are zero; milliseconds
    // are always shown.
    public static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        long totalMs = (long)Math.Round(elapsed.TotalMilliseconds);
        long milliseconds = totalMs % 1000;
        long totalSeconds = totalMs / 1000;
        long seconds = totalSeconds % 60;
        long totalMinutes = totalSeconds / 60;
        long minutes = totalMinutes % 60;
        long hours = totalMinutes / 60;

        if (hours > 0)
        {
            return $"{hours}h {minutes:00}m {seconds:00}s {milliseconds:000}ms";
        }

        if (minutes > 0)
        {
            return $"{minutes}m {seconds:00}s {milliseconds:000}ms";
        }

        if (seconds > 0)
        {
            return $"{seconds}s {milliseconds:000}ms";
        }

        return $"{milliseconds}ms";
    }

    private static string? JoinNonEmpty(params string?[] parts)
    {
        IEnumerable<string> nonEmpty = parts.Where(part => !string.IsNullOrEmpty(part))!;
        string joined = string.Join(" · ", nonEmpty);
        return joined.Length == 0 ? null : joined;
    }

    public static bool TryParse(string line, out HookObservation? observation)
    {
        observation = null;

        try
        {
            using JsonDocument envelope = JsonDocument.Parse(line);
            JsonElement root = envelope.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("payload", out JsonElement payloadElement))
            {
                return false;
            }

            HookProvider provider = ReadEnum(root, "provider", HookProvider.Cursor);
            HookSurface surface = ReadEnum(
                root,
                "detectedSurface",
                provider switch
                {
                    HookProvider.ClaudeCode => HookSurface.ClaudeCode,
                    HookProvider.GitHubCopilot => HookSurface.CopilotCli,
                    _ => HookSurface.CursorIde
                });
            ObservationSourceKind sourceKind = ReadEnum(
                root,
                "sourceKind",
                ObservationSourceKind.Hook);

            return TryCreateObservation(
                payloadElement,
                ReadEventId(root),
                ReadObservedAtUtc(root),
                sourceFilePath: ReadString(root, "sourceFilePath"),
                provider,
                surface,
                sourceKind,
                configuredEventName: ReadString(root, "configuredEventName"),
                out observation);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static bool TryParseRawPayload(
        string json,
        DateTimeOffset observedAtUtc,
        string sourceFilePath,
        out HookObservation? observation)
    {
        observation = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("payload", out JsonElement envelopePayload))
            {
                HookProvider envelopeProvider = ReadEnum(root, "provider", HookProvider.Cursor);
                HookSurface envelopeSurface = ReadEnum(
                    root,
                    "detectedSurface",
                    envelopeProvider == HookProvider.ClaudeCode
                        ? HookSurface.ClaudeCode
                        : envelopeProvider == HookProvider.GitHubCopilot
                            ? HookSurface.CopilotCli
                            : HookSurface.CursorIde);
                return TryCreateObservation(
                    envelopePayload,
                    ReadEventId(root),
                    ReadObservedAtUtc(root),
                    sourceFilePath,
                    envelopeProvider,
                    envelopeSurface,
                    ReadEnum(root, "sourceKind", ObservationSourceKind.Replay),
                    ReadString(root, "configuredEventName"),
                    out observation);
            }

            if (ReadString(root, "hook_event_name") is null)
            {
                return false;
            }

            (HookProvider provider, HookSurface surface) =
                ProviderAdapterRegistry.DetectLegacy(root);
            return TryCreateObservation(
                root,
                Guid.NewGuid(),
                observedAtUtc.ToUniversalTime(),
                sourceFilePath,
                provider,
                surface,
                ObservationSourceKind.Replay,
                configuredEventName: null,
                out observation);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryCreateObservation(
        JsonElement payloadElement,
        Guid eventId,
        DateTimeOffset observedAtUtc,
        string? sourceFilePath,
        HookProvider provider,
        HookSurface surface,
        ObservationSourceKind sourceKind,
        string? configuredEventName,
        out HookObservation? observation)
    {
        observation = null;

        if (payloadElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        ProviderNormalization normalized = ProviderAdapterRegistry
            .Get(provider)
            .Normalize(payloadElement, surface, configuredEventName);
        JsonElement payload = normalized.CanonicalPayload;
        string hookEventName = normalized.CanonicalEventName;
        string? sessionId = ReadString(payload, "conversation_id") ?? ReadString(payload, "session_id");
        string? generationId = ReadString(payload, "generation_id");
        WorkspaceContext workspace = WorkspaceContext.FromPayload(payload);
        string displayJson = FormatForDisplay(normalized.RawPayload);
        double? durationMs = ReadDouble(payload, "duration_ms") ?? ReadDouble(payload, "duration");

        observation = new HookObservation(
            eventId,
            observedAtUtc,
            payload,
            normalized.RawPayload,
            normalized.Provider,
            normalized.Surface,
            sourceKind,
            normalized.RawEventName,
            hookEventName,
            normalized.EventKind,
            normalized.ToolKind,
            normalized.CorrelationQuality,
            sessionId,
            generationId,
            workspace,
            displayJson,
            durationMs,
            sourceFilePath);

        return true;
    }

    private static Guid ReadEventId(JsonElement root)
    {
        if (root.TryGetProperty("eventId", out JsonElement eventIdElement) &&
            eventIdElement.ValueKind == JsonValueKind.String &&
            Guid.TryParse(eventIdElement.GetString(), out Guid eventId))
        {
            return eventId;
        }

        return Guid.Empty;
    }

    private static DateTimeOffset ReadObservedAtUtc(JsonElement root)
    {
        if (root.TryGetProperty("observedAtUtc", out JsonElement observedElement) &&
            observedElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(observedElement.GetString(), out DateTimeOffset observedAtUtc))
        {
            return observedAtUtc.ToUniversalTime();
        }

        return DateTimeOffset.UtcNow;
    }

    private static TEnum ReadEnum<TEnum>(
        JsonElement root,
        string propertyName,
        TEnum fallback)
        where TEnum : struct, Enum
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int number) &&
            Enum.IsDefined(typeof(TEnum), number))
        {
            return (TEnum)Enum.ToObject(typeof(TEnum), number);
        }

        if (value.ValueKind == JsonValueKind.String &&
            Enum.TryParse(value.GetString(), ignoreCase: true, out TEnum parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static double? ReadDouble(JsonElement root, string propertyName)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out double result))
        {
            return result;
        }

        return null;
    }

    private static long? ReadLong(JsonElement root, string propertyName)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out long result))
        {
            return result;
        }

        return null;
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // Re-serializing a JsonElement with System.Text.Json always JSON-escapes
    // backslashes and quotes inside strings (that's required for the result to
    // be valid JSON). For a human-facing inspector that back-and-forth escaping
    // just adds noise - e.g. a Windows path turns into "C:\\dev\\..." - so this
    // walks the element tree itself and writes string values verbatim, only
    // escaping the handful of control characters that would otherwise garble
    // the indentation.
    private static string FormatForDisplay(JsonElement element)
    {
        var builder = new StringBuilder();
        WriteElement(builder, element, indentLevel: 0);
        return builder.ToString();
    }

    private static void WriteElement(StringBuilder builder, JsonElement element, int indentLevel)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(builder, element, indentLevel);
                break;
            case JsonValueKind.Array:
                WriteArray(builder, element, indentLevel);
                break;
            case JsonValueKind.String:
                WriteRawString(builder, element.GetString());
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                builder.Append(element.GetRawText());
                break;
            default:
                break;
        }
    }

    private static void WriteObject(StringBuilder builder, JsonElement element, int indentLevel)
    {
        using IEnumerator<JsonProperty> properties = element.EnumerateObject().GetEnumerator();
        if (!properties.MoveNext())
        {
            builder.Append("{}");
            return;
        }

        builder.Append('{').Append('\n');
        int childIndent = indentLevel + 1;
        bool hasNext;
        do
        {
            JsonProperty property = properties.Current;
            AppendIndent(builder, childIndent);
            WriteRawString(builder, property.Name);
            builder.Append(": ");
            WriteElement(builder, property.Value, childIndent);

            hasNext = properties.MoveNext();
            if (hasNext)
            {
                builder.Append(',');
            }

            builder.Append('\n');
        }
        while (hasNext);

        AppendIndent(builder, indentLevel);
        builder.Append('}');
    }

    private static void WriteArray(StringBuilder builder, JsonElement element, int indentLevel)
    {
        using JsonElement.ArrayEnumerator items = element.EnumerateArray().GetEnumerator();
        if (!items.MoveNext())
        {
            builder.Append("[]");
            return;
        }

        builder.Append('[').Append('\n');
        int childIndent = indentLevel + 1;
        bool hasNext;
        do
        {
            JsonElement item = items.Current;
            AppendIndent(builder, childIndent);
            WriteElement(builder, item, childIndent);

            hasNext = items.MoveNext();
            if (hasNext)
            {
                builder.Append(',');
            }

            builder.Append('\n');
        }
        while (hasNext);

        AppendIndent(builder, indentLevel);
        builder.Append(']');
    }

    // Only escapes what would otherwise be invisible or break the layout
    // (control characters); backslashes and quotes inside the value are
    // written as-is so paths and quoted shell commands read naturally.
    private static void WriteRawString(StringBuilder builder, string? value)
    {
        builder.Append('"');

        if (!string.IsNullOrEmpty(value))
        {
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }
        }

        builder.Append('"');
    }

    private static void AppendIndent(StringBuilder builder, int indentLevel)
    {
        builder.Append(' ', indentLevel * 2);
    }
}

// A single name/value row shown in the inspector's field table.
public sealed record PayloadField(string Name, string Value);
