using System.Globalization;
using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Claude;

internal sealed record ClaudeSessionCacheEntry
{
    public required string SessionId { get; init; }

    public string? FilePath { get; init; }

    public string? ProjectName { get; init; }

    public string? Summary { get; init; }

    public string? FirstUserContent { get; init; }

    public string? LastUserContent { get; init; }

    public string? EntryPoint { get; init; }

    public DateTimeOffset? FirstMessageAtUtc { get; init; }

    public DateTimeOffset? LastMessageAtUtc { get; init; }

    public long? MessageCount { get; init; }

    public long? SidechainCount { get; init; }

    public bool IsSubagent { get; init; }

    public IReadOnlyDictionary<string, string?> Metadata { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);
}

internal sealed record ClaudeSessionCache(
    string Path,
    string? Version,
    IReadOnlyList<ClaudeSessionCacheEntry> Entries);

internal sealed record ClaudeSessionCacheParseResult(
    ClaudeSessionCache? Cache,
    IReadOnlyList<string> Warnings);

internal sealed class ClaudeSessionCacheParser
{
    public ClaudeSessionCacheParseResult Parse(
        string path,
        JsonElement root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        List<string> warnings = [];
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("entries", out JsonElement entries) ||
            entries.ValueKind != JsonValueKind.Object)
        {
            warnings.Add(
                $"Claude session cache '{path}' does not contain an object 'entries' property.");
            return new ClaudeSessionCacheParseResult(null, warnings);
        }

        List<ClaudeSessionCacheEntry> parsed = [];
        foreach (JsonProperty property in entries.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object ||
                !property.Value.TryGetProperty(
                    "session",
                    out JsonElement session) ||
                session.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? sessionId = String(
                session,
                "actual_session_id",
                "actualSessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                warnings.Add(
                    $"Skipped Claude session-cache entry '{property.Name}' without " +
                    "an actual session id.");
                continue;
            }

            Dictionary<string, string?> metadata =
                new(StringComparer.Ordinal);
            foreach (JsonProperty item in session.EnumerateObject())
            {
                metadata[item.Name] = ScalarText(item.Value);
            }
            foreach (JsonProperty item in property.Value.EnumerateObject())
            {
                if (!item.NameEquals("session"))
                {
                    metadata[$"entry.{item.Name}"] =
                        ScalarText(item.Value);
                }
            }

            string? filePath =
                String(session, "file_path", "filePath") ??
                property.Name;
            parsed.Add(new ClaudeSessionCacheEntry
            {
                SessionId = sessionId,
                FilePath = filePath,
                ProjectName = String(
                    session,
                    "project_name",
                    "projectName"),
                Summary = String(session, "summary"),
                FirstUserContent = String(
                    property.Value,
                    "first_user_content",
                    "firstUserContent"),
                LastUserContent = String(
                    property.Value,
                    "last_user_content",
                    "lastUserContent"),
                EntryPoint = String(session, "entrypoint"),
                FirstMessageAtUtc = Timestamp(
                    session,
                    "first_message_time",
                    "firstMessageTime"),
                LastMessageAtUtc = Timestamp(
                    session,
                    "last_message_time",
                    "lastMessageTime"),
                MessageCount = Integer(
                    session,
                    "message_count",
                    "messageCount"),
                SidechainCount = Integer(
                    property.Value,
                    "sidechain_count",
                    "sidechainCount"),
                IsSubagent = IsSubagentPath(filePath),
                Metadata = metadata
            });
        }

        return new ClaudeSessionCacheParseResult(
            new ClaudeSessionCache(
                path,
                ScalarText(root, "version"),
                parsed),
            warnings);
    }

    private static bool IsSubagentPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment.Equals(
                "subagents",
                StringComparison.OrdinalIgnoreCase));

    private static string? String(
        JsonElement element,
        params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static long? Integer(
        JsonElement element,
        params string[] names)
    {
        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out long number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(
                    value.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out number))
            {
                return number;
            }
        }

        return null;
    }

    private static DateTimeOffset? Timestamp(
        JsonElement element,
        params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    value.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal |
                    DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset timestamp))
            {
                return timestamp;
            }
        }

        return null;
    }

    private static string? ScalarText(
        JsonElement element,
        string name) =>
        element.TryGetProperty(name, out JsonElement value)
            ? ScalarText(value)
            : null;

    private static string? ScalarText(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or
            JsonValueKind.True or
            JsonValueKind.False => value.GetRawText(),
            _ => null
        };
}

internal sealed record ClaudeSessionHistoryEntry(
    string SessionId,
    string? ProjectPath,
    string? Display,
    DateTimeOffset? TimestampUtc,
    SessionSourceProvenance Provenance);

internal sealed class ClaudeSessionHistoryEntryParser
{
    private readonly ClaudeJsonAccessor _json = new();

    public ClaudeSessionHistoryEntry? Parse(ClaudeTranscriptRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        string? sessionId = _json.String(
            row.Json,
            "sessionId",
            "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        SessionSourceProvenance provenance = new(
            SessionSourceKind.ClaudeHistoryJsonl,
            row.Path,
            "jsonl",
            LineNumber: row.LineNumber,
            ByteOffset: row.ByteOffset,
            RecordId: sessionId,
            RawSource: new SessionRawSourceReference(
                SessionRawSourceKind.JsonLine,
                row.Path,
                row.ByteOffset,
                row.ByteLength,
                SourceLastWriteTimeUtc: row.SourceLastWriteTimeUtc,
                SourceLength: row.SourceLength));
        return new ClaudeSessionHistoryEntry(
            sessionId,
            _json.String(row.Json, "project", "projectPath"),
            _json.String(row.Json, "display"),
            _json.Timestamp(row.Json, "timestamp"),
            provenance);
    }
}
