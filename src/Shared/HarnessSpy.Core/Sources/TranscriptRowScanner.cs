using System.Text.Json;

namespace HarnessSpy.Core.Sources;

// Cheap pre-scan of a raw transcript row for the two fields the ingestion loop
// needs before handing the row to a dialect parser: the row's own timestamp
// (for chronological placement) and its turn id when present. Claude only
// stamps the turn id (promptId) on user rows, so the ingestion loop carries the
// last-seen value forward to the assistant rows that follow it.
public static class TranscriptRowScanner
{
    public readonly record struct RowMeta(
        DateTimeOffset? Timestamp,
        string? TurnId,
        string? InteractionId,
        string? RecordType,
        string? Role);

    public static RowMeta Read(string raw)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            JsonElement? data = ReadData(root);
            return new RowMeta(
                ReadTimestamp(root),
                ReadIdentifier(root, "promptId", "prompt_id", "turnId") ??
                    ReadIdentifier(data, "promptId", "prompt_id", "turnId"),
                ReadIdentifier(root, "interactionId", "interaction_id") ??
                    ReadIdentifier(data, "interactionId", "interaction_id"),
                ReadIdentifier(root, "type"),
                ReadIdentifier(root, "role"));
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), out DateTimeOffset iso))
        {
            return iso.ToUniversalTime();
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

        return null;
    }

    private static JsonElement? ReadData(JsonElement root)
    {
        if (root.TryGetProperty("data", out JsonElement data) &&
            data.ValueKind == JsonValueKind.Object)
        {
            return data;
        }

        return null;
    }

    private static string? ReadIdentifier(JsonElement? element, params string[] names)
    {
        if (element is not JsonElement valueContainer ||
            valueContainer.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (!valueContainer.TryGetProperty(name, out JsonElement value))
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
}
