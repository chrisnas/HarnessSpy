using System.Text;
using System.Text.Json;

namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public sealed class CursorRecordClassifier
{
    private readonly CursorBubbleKeyParser _bubbleKeyParser;
    private readonly CursorJsonReader _json = new();

    public CursorRecordClassifier(CursorBubbleKeyParser? bubbleKeyParser = null)
    {
        _bubbleKeyParser = bubbleKeyParser ?? new CursorBubbleKeyParser();
    }

    public CursorRecordClassification Classify(
        string tableName,
        IReadOnlyDictionary<string, object?> cells,
        IReadOnlySet<string>? knownComposerIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(cells);

        string? key = Text(Cell(cells, "key"));
        string? raw = Text(Cell(cells, "value"));
        string? payloadComposerId = JsonString(
            raw,
            "composerId",
            "composer_id",
            "id");

        if (string.Equals(
            tableName,
            "composerHeaders",
            StringComparison.OrdinalIgnoreCase))
        {
            string? composerId =
                Text(Cell(cells, "composerId")) ?? payloadComposerId;
            return new CursorRecordClassification(
                CursorRecordKind.ComposerHeader,
                key,
                composerId,
                null,
                raw);
        }

        if (string.Equals(
            tableName,
            "ItemTable",
            StringComparison.OrdinalIgnoreCase))
        {
            CursorRecordKind kind = key switch
            {
                "composer.composerHeaders" =>
                    CursorRecordKind.ComposerHeaders,
                "composer.composerData" =>
                    CursorRecordKind.ComposerData,
                _ => CursorRecordKind.Unknown
            };
            return new CursorRecordClassification(
                kind,
                key,
                payloadComposerId,
                null,
                raw);
        }

        if (!string.Equals(
            tableName,
            "cursorDiskKV",
            StringComparison.OrdinalIgnoreCase))
        {
            return new CursorRecordClassification(
                CursorRecordKind.Unknown,
                key,
                payloadComposerId,
                null,
                raw);
        }

        if (key?.StartsWith(
                "composerData:",
                StringComparison.Ordinal) == true)
        {
            string composerId = key["composerData:".Length..];
            return new CursorRecordClassification(
                CursorRecordKind.ComposerData,
                key,
                composerId,
                null,
                raw);
        }

        string? payloadBubbleId = JsonString(
            raw,
            "bubbleId",
            "bubble_id",
            "id");
        if (_bubbleKeyParser.TryParse(
            key,
            knownComposerIds ?? new HashSet<string>(StringComparer.Ordinal),
            payloadBubbleId,
            out string bubbleComposerId,
            out string bubbleId))
        {
            return new CursorRecordClassification(
                CursorRecordKind.Bubble,
                key,
                bubbleComposerId,
                bubbleId,
                raw);
        }

        return new CursorRecordClassification(
            CursorRecordKind.Unknown,
            key,
            payloadComposerId,
            payloadBubbleId,
            raw);
    }

    private object? Cell(
        IReadOnlyDictionary<string, object?> cells,
        string name)
    {
        foreach ((string key, object? value) in cells)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private string? JsonString(string? raw, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            !_json.TryParse(raw, out JsonDocument? document, out _) ||
            document is null)
        {
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            string? direct = _json.String(root, names);
            if (direct is not null)
            {
                return direct;
            }

            foreach (string containerName in new[]
                     {
                         "allComposers", "composerHeaders", "headers"
                     })
            {
                if (_json.TryGetProperty(
                        root,
                        containerName,
                        out JsonElement items) &&
                    items.ValueKind == JsonValueKind.Array)
                {
                    JsonElement first = items
                        .EnumerateArray()
                        .FirstOrDefault(item =>
                            item.ValueKind == JsonValueKind.Object);
                    if (first.ValueKind == JsonValueKind.Object)
                    {
                        return _json.String(first, names);
                    }
                }
            }

            return null;
        }
    }

    private string? Text(object? value) =>
        value switch
        {
            null or DBNull => null,
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => Convert.ToString(value)
        };
}
