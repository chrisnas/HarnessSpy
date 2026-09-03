using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using System.Linq;
using HarnessSpy.Core.Providers;
using HarnessSpy.Core.Runtimes;

namespace HarnessSpy.Core.Models;

// A single native observation. The payload is stored untouched; the exact
// native event and tool names are the public identity. All provider-specific
// interpretation (scope, correlation, presentation, summary traits) is produced
// by the owning harness runtime engine and carried in Interpretation.
public sealed class HookObservation
{
    private HookObservation(
        Guid eventId,
        DateTimeOffset observedAtUtc,
        JsonElement payload,
        HookProvider provider,
        HookSurface surface,
        ObservationSourceKind sourceKind,
        string rawHookEventName,
        WorkspaceContext workspace,
        string displayJson,
        double? durationMs,
        string? sourceFilePath,
        ObservationInterpretation interpretation,
        int? spawningProcessId,
        string? spawningProcessName,
        ObservationProvenance? provenance = null)
    {
        EventId = eventId;
        ObservedAtUtc = observedAtUtc;
        NativeTimestampUtc = ReadNativeTimestamp(payload);
        IngestionOrdinal = System.Threading.Interlocked.Increment(ref _ingestionCounter);
        Payload = payload;
        Provider = provider;
        Surface = surface;
        SourceKind = sourceKind;
        RawHookEventName = rawHookEventName;
        Workspace = workspace;
        DisplayJson = displayJson;
        DurationMs = durationMs;
        SourceFilePath = sourceFilePath;
        Interpretation = interpretation;
        SpawningProcessId = spawningProcessId;
        SpawningProcessName = spawningProcessName;
        Provenance = provenance;
    }

    // Builds a transcript-sourced observation from an already-parsed native
    // fragment payload and its interpretation. The payload is stored untouched,
    // exactly like a hook payload; provenance records the file coordinates used
    // for deduplication and durable replay.
    public static HookObservation CreateTranscriptFragment(
        HookProvider provider,
        HookSurface surface,
        JsonElement fragmentPayload,
        string rawEventName,
        DateTimeOffset observedAtUtc,
        ObservationInterpretation interpretation,
        ObservationProvenance provenance)
    {
        JsonElement payload = fragmentPayload.Clone();
        WorkspaceContext workspace = ResolveWorkspace(payload, provider);
        string displayJson = FormatForDisplay(payload);
        double? durationMs = ReadDouble(payload, "duration_ms") ?? ReadDouble(payload, "duration");

        return new HookObservation(
            Guid.NewGuid(),
            observedAtUtc,
            payload,
            provider,
            surface,
            ObservationSourceKind.TranscriptFile,
            rawEventName,
            workspace,
            displayJson,
            durationMs,
            provenance.CapturedPath ?? provenance.NormalizedPath,
            interpretation,
            spawningProcessId: null,
            spawningProcessName: null,
            provenance);
    }

    private static long _ingestionCounter;

    public Guid EventId { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    // The provider's own timestamp when the payload carries one (Copilot CLI
    // epoch milliseconds, VS Code ISO-8601). Null when the payload has none.
    public DateTimeOffset? NativeTimestampUtc { get; }

    // Monotonic capture order, used as the deterministic tie-breaker when two
    // observations share an effective timestamp.
    public long IngestionOrdinal { get; }

    // Native timestamp when present, otherwise HarnessSpy capture time. Drives
    // deterministic chronological placement in the tree.
    public DateTimeOffset EffectiveTimestamp => NativeTimestampUtc ?? ObservedAtUtc;

    // The untouched provider payload. Never rewritten with aliases.
    public JsonElement Payload { get; }

    // Retained for callers that referenced the pre-refactor property name; the
    // native payload is the raw payload now that nothing is normalised.
    public JsonElement RawPayload => Payload;

    public HookProvider Provider { get; }

    public HookSurface Surface { get; }

    public ObservationSourceKind SourceKind { get; }

    // The exact event name as it arrived (hook_event_name or the configured
    // hook key). Kept distinct from the interpreted native identity.
    public string RawHookEventName { get; }

    // The exact native event/item name. This is the identity shown in the UI
    // and used for correlation; it is never renamed.
    public string HookEventName => Interpretation.NativeEventName;

    public ObservationInterpretation Interpretation { get; }

    public CanonicalEventKind EventKind => Interpretation.EventKind;

    public CanonicalToolKind ToolKind => Interpretation.ToolKind;

    public CorrelationQuality CorrelationQuality => Interpretation.CorrelationQuality;

    public string? SessionId => Interpretation.SessionId;

    public string ProviderScopedSessionId =>
        $"{Provider}:{Surface}:{SessionId ?? "unknown"}";

    // The turn/generation key: every observation a single turn produces shares
    // it. Null for session-scoped events and payloads that carry no turn id.
    public string? GenerationId => Interpretation.TurnId;

    public WorkspaceContext Workspace { get; }

    public string DisplayJson { get; }

    public double? DurationMs { get; }

    public string? SourceFilePath { get; }

    // Best-effort identity of the process that launched this hook invocation.
    // Both null for legacy captures and non-Windows hosts.
    public int? SpawningProcessId { get; }

    public string? SpawningProcessName { get; }

    // File coordinates for a transcript-sourced observation; null for hooks.
    public ObservationProvenance? Provenance { get; }

    public bool IsTranscriptSourced => SourceKind == ObservationSourceKind.TranscriptFile;

    // True for a transcript fragment that only enriches a canonical hook node.
    public bool IsEnrichmentOnly => Interpretation.EnrichmentOnly;

    public string? ToolUseId => Interpretation.ToolCallId;

    public IReadOnlyList<string> BatchToolCallIds => Interpretation.BatchToolCallIds;

    public string? SubagentId => Interpretation.SubagentId;

    public string? ToolName => Interpretation.ToolName;

    public string? PromptText => Interpretation.PromptText;

    public string? Text => Interpretation.AssistantText;

    public string? McpServerName => Interpretation.McpServerName;

    public string? Status => Interpretation.Status;

    public string? SubagentType => Interpretation.SubagentType;

    public string? Task => Interpretation.Task;

    public long? InputTokens => ReadLong(Payload, "input_tokens");

    public long? OutputTokens => ReadLong(Payload, "output_tokens");

    public long? CacheReadTokens => ReadLong(Payload, "cache_read_tokens");

    public long? CacheWriteTokens => ReadLong(Payload, "cache_write_tokens");

    public string? TargetFilePath => Interpretation.TargetFilePath;

    public IReadOnlyList<string> TargetFilePaths => Interpretation.TargetFilePaths.Count > 0
        ? Interpretation.TargetFilePaths
        : TargetFilePath is string path ? [path] : [];

    public string? SkillName => TryGetSkillName(TargetFilePath);

    public IReadOnlyList<string> SlashCommands => TryGetSlashCommands(PromptText);

    public IReadOnlyList<string> SkillMentions =>
        Interpretation.Role == ObservationRole.AgentThought
            ? TryGetSkillMentions(Text)
            : [];

    public bool HasTokenCounts =>
        InputTokens is not null ||
        OutputTokens is not null ||
        CacheReadTokens is not null ||
        CacheWriteTokens is not null;

    public bool IsMcpPrefixedTool => RuntimeJson.IsMcpPrefixed(ToolName);

    public IReadOnlyList<PayloadField> DetailFields => BuildDetailFields();

    private IReadOnlyList<PayloadField> BuildDetailFields()
    {
        List<PayloadField> fields = [];

        foreach (FieldSpec spec in Interpretation.DetailFieldSpecs)
        {
            ApplyFieldSpec(fields, spec);
        }

        AddDurationField(fields);
        AddSpawningProcessField(fields);
        return fields;
    }

    private void ApplyFieldSpec(List<PayloadField> fields, FieldSpec spec)
    {
        switch (spec.Kind)
        {
            case FieldSpecKind.Scalar:
                AddScalar(fields, spec.Names[0]);
                break;
            case FieldSpecKind.ScalarPreferred:
                AddScalarPreferred(fields, spec.Names);
                break;
            case FieldSpecKind.ObjectMembers:
                AddObjectMembers(fields, spec.Names[0]);
                break;
            case FieldSpecKind.JsonStringMembers:
                AddJsonStringMembers(fields, spec.Names[0]);
                break;
            case FieldSpecKind.ArrayMembers:
                AddArrayMembers(fields, spec.Names[0]);
                break;
            case FieldSpecKind.FlattenedArrayMembers:
                AddFlattenedArrayMembers(fields, spec.Names[0]);
                break;
            case FieldSpecKind.PreCompactSummary:
                AddPreCompactDetailFields(fields);
                break;
            case FieldSpecKind.AllTopLevel:
                AddAllTopLevel(fields);
                break;
        }
    }

    // Surfaces the elapsed time on any hook that reports it, always as the first
    // row, even when a preceding spec already listed it.
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

    // Surfaces the process that launched this hook invocation, when resolved.
    // Only inserted when at least one of id/name is known.
    private void AddSpawningProcessField(List<PayloadField> fields)
    {
        if (SpawningProcessId is null && SpawningProcessName is null)
        {
            return;
        }

        string value = (SpawningProcessName, SpawningProcessId) switch
        {
            (string name, int id) => $"{name} (pid {id})",
            (string name, null) => name,
            (null, int id) => $"pid {id}",
            _ => string.Empty
        };

        fields.Add(new PayloadField("spawning process", value));
    }

    private void AddScalar(List<PayloadField> fields, string propertyName)
    {
        if (Payload.ValueKind == JsonValueKind.Object &&
            Payload.TryGetProperty(propertyName, out JsonElement value))
        {
            fields.Add(new PayloadField(propertyName, FormatFieldValue(value)));
        }
    }

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
            if (fields.Any(field => StringComparer.Ordinal.Equals(field.Name, member.Name)))
            {
                continue;
            }

            fields.Add(new PayloadField(member.Name, FormatFieldValue(member.Value)));
        }
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

    private static string FormatPercent(double value) => $"{value:0.##}%";

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

    private static string NormalizeDisplayEscapes(string value)
    {
        return value.Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    public bool IsSessionLifecycle => Interpretation.Scope == ObservationScope.SessionLifecycle;

    public bool IsWorkspaceOpen => Interpretation.Scope == ObservationScope.Workspace;

    public bool IsTabHook => Interpretation.Scope == ObservationScope.Tab;

    public bool IsStop => Interpretation.Role == ObservationRole.TurnStop;

    public bool IsAbortedStop => Interpretation.IsAbortedStop;

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

    public static IReadOnlyList<string> TryGetSlashCommands(string? promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText))
        {
            return [];
        }

        string text = ExtractUserQuery(promptText);
        List<string>? commands = null;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '/')
            {
                continue;
            }

            if (i > 0 && !char.IsWhiteSpace(text[i - 1]))
            {
                continue;
            }

            int nameStart = i + 1;
            if (nameStart >= text.Length || !IsCommandStart(text[nameStart]))
            {
                continue;
            }

            int end = nameStart;
            while (end < text.Length && IsCommandChar(text[end]))
            {
                end++;
            }

            if (end < text.Length && !IsCommandTerminator(text[end]))
            {
                continue;
            }

            string command = text[i..end];
            commands ??= [];
            if (!commands.Contains(command, StringComparer.OrdinalIgnoreCase))
            {
                commands.Add(command);
            }
        }

        return commands ?? (IReadOnlyList<string>)[];
    }

    public static IReadOnlyList<string> TryGetSkillMentions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<string>? mentions = null;
        foreach (Match match in SkillMentionRegex.Matches(text))
        {
            string name = match.Groups[1].Value.ToLowerInvariant();
            mentions ??= [];
            if (!mentions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                mentions.Add(name);
            }
        }

        return mentions ?? (IReadOnlyList<string>)[];
    }

    private static readonly Regex SkillMentionRegex = new(
        @"(?<![\w-])([A-Za-z0-9]+(?:-[A-Za-z0-9]+)+)[*`_]*\s+skills?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string ExtractUserQuery(string text)
    {
        const string open = "<user_query>";
        const string close = "</user_query>";

        int start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return text;
        }

        start += open.Length;
        int end = text.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static bool IsCommandStart(char c) => char.IsLetterOrDigit(c);

    private static bool IsCommandChar(char c) =>
        char.IsLetterOrDigit(c) || c is '-' or '_';

    private static bool IsCommandTerminator(char c) =>
        char.IsWhiteSpace(c) || c is '.' or ',' or ';' or '!' or '?' or ')' or ']' or '}' or '"' or '\'';

    // Builds "{nativeEventName} · {detail} · {timing}", omitting a segment when
    // there is nothing relevant to show.
    public string OccurrenceHeader
    {
        get
        {
            string header = HookEventName;

            if (!string.IsNullOrEmpty(Interpretation.HeaderDetail))
            {
                header = $"{header} \u00b7 {Interpretation.HeaderDetail}";
            }

            string? timing = GetTimingSuffix();
            if (!string.IsNullOrEmpty(timing))
            {
                header = $"{header} \u00b7 {timing}";
            }

            return header;
        }
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

    // The clock time is meaningful for the first event of a session; every
    // other event reports its own duration or no timing at all.
    private string? GetTimingSuffix()
    {
        if (Interpretation.ShowClockTimestamp)
        {
            return ObservedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }

        return DurationMs is double ms ? FormatDuration(TimeSpan.FromMilliseconds(ms)) : null;
    }

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
                spawningProcessId: ReadInt(root, "spawningProcessId"),
                spawningProcessName: ReadString(root, "spawningProcessName"),
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
                    spawningProcessId: ReadInt(root, "spawningProcessId"),
                    spawningProcessName: ReadString(root, "spawningProcessName"),
                    out observation);
            }

            if (ReadString(root, "hook_event_name") is null &&
                !RuntimeJson.Has(root, "sessionId"))
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
                spawningProcessId: null,
                spawningProcessName: null,
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
        int? spawningProcessId,
        string? spawningProcessName,
        out HookObservation? observation)
    {
        observation = null;

        if (payloadElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonElement payload = payloadElement.Clone();
        string? payloadEventName = ReadString(payload, "hook_event_name");
        string rawEventName = payloadEventName ?? configuredEventName ?? "unknownHook";

        IHarnessRuntimeEngine engine = HarnessRuntimeRegistry.Resolve(provider, surface);
        ObservationContext context = new(
            payload,
            payloadEventName,
            configuredEventName,
            HarnessRuntimeRegistry.DialectFor(provider, surface),
            observedAtUtc);
        ObservationInterpretation interpretation = engine.Interpret(context);

        WorkspaceContext workspace = ResolveWorkspace(payload, provider);
        string displayJson = FormatForDisplay(payload);
        double? durationMs = ReadDouble(payload, "duration_ms") ?? ReadDouble(payload, "duration");

        observation = new HookObservation(
            eventId,
            observedAtUtc,
            payload,
            provider,
            surface,
            sourceKind,
            rawEventName,
            workspace,
            displayJson,
            durationMs,
            sourceFilePath,
            interpretation,
            spawningProcessId,
            spawningProcessName);

        return true;
    }

    // Cursor supplies workspace_roots directly. Other providers report a single
    // cwd, which becomes the workspace root so their sessions group under the
    // repository they run in.
    private static WorkspaceContext ResolveWorkspace(JsonElement payload, HookProvider provider)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("workspace_roots", out _))
        {
            return WorkspaceContext.FromPayload(payload);
        }

        string? cwd = ReadString(payload, "cwd");
        if (cwd is not null)
        {
            return WorkspaceContext.FromRoot(cwd);
        }

        return WorkspaceContext.FromPayload(payload);
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

    // Parses a native timestamp without rewriting the payload: Copilot CLI sends
    // epoch milliseconds as a number; VS Code sends ISO-8601 as a string.
    private static DateTimeOffset? ReadNativeTimestamp(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("timestamp", out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long epochMs))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(epochMs).ToUniversalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), out DateTimeOffset parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
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

    private static int? ReadInt(JsonElement root, string propertyName)
    {
        return ReadLong(root, propertyName) is long value && value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : null;
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

    // High-volume events (MessageDisplay, Elicitation) can carry very large
    // payloads; cap the rendered inspector text so the UI stays responsive while
    // still showing the great majority of the content.
    private const int MaxDisplayJsonLength = 200_000;

    private static string FormatForDisplay(JsonElement element)
    {
        var builder = new StringBuilder();
        WriteElement(builder, element, indentLevel: 0);
        if (builder.Length > MaxDisplayJsonLength)
        {
            return builder.ToString(0, MaxDisplayJsonLength) +
                "\n\u2026 (payload truncated for display)";
        }

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
