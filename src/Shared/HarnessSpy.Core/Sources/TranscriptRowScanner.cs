using System.Text.Json;

namespace HarnessSpy.Core.Sources;

// Cheap pre-scan of a raw transcript row for the two fields the ingestion loop
// needs before handing the row to a dialect parser: the row's own timestamp
// (for chronological placement) and its turn id when present. Claude only
// stamps the turn id (promptId) on user rows, so the ingestion loop carries the
// last-seen value forward to the assistant rows that follow it.
public static class TranscriptRowScanner
{
    public readonly record struct RowMeta(DateTimeOffset? Timestamp, string? TurnId);

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

            return new RowMeta(ReadTimestamp(root), ReadTurnId(root));
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

    private static string? ReadTurnId(JsonElement root)
    {
        foreach (string name in new[] { "promptId", "prompt_id", "turnId" })
        {
            if (root.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }
}
