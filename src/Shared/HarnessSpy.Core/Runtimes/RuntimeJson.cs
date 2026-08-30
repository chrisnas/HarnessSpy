using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarnessSpy.Core.Runtimes;

// Shared, provider-neutral payload readers used by every runtime engine. These
// never mutate the payload; they only read native fields as evidence.
internal static class RuntimeJson
{
    public static string? String(JsonElement payload, params string[] names)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (payload.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    public static bool Has(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out _);

    public static double? Double(JsonElement payload, params string[] names)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (payload.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out double result))
            {
                return result;
            }
        }

        return null;
    }

    public static long? Long(JsonElement payload, params string[] names)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (payload.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out long result))
            {
                return result;
            }
        }

        return null;
    }

    // Reads a string from a nested object member, e.g. tool_input.file_path.
    public static string? NestedString(JsonElement payload, string container, params string[] names)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(container, out JsonElement inner) ||
            inner.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return String(inner, names);
    }

    // Reads tool arguments that may be an object or a JSON-encoded string.
    public static string? ToolInputString(JsonElement payload, string container, params string[] names)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(container, out JsonElement inner))
        {
            return null;
        }

        if (inner.ValueKind == JsonValueKind.Object)
        {
            return String(inner, names);
        }

        if (inner.ValueKind == JsonValueKind.String)
        {
            string? raw = inner.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                using JsonDocument parsed = JsonDocument.Parse(raw);
                if (parsed.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return String(parsed.RootElement, names);
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    // Reads a string array from a nested object member, e.g. tool_input.files.
    public static IReadOnlyList<string> NestedStringArray(JsonElement payload, string container, string arrayName)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(container, out JsonElement inner) ||
            inner.ValueKind != JsonValueKind.Object ||
            !inner.TryGetProperty(arrayName, out JsonElement array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<string> values = [];
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is string value &&
                !string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    public static bool IsMcpPrefixed(string? toolName) =>
        toolName is not null &&
        (toolName.StartsWith("MCP:", StringComparison.Ordinal) ||
         toolName.StartsWith("mcp__", StringComparison.Ordinal));
}
