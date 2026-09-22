using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Runtimes;

// Produces one lossless display string from the provider's decoded JSON value.
// It deliberately performs no second escape pass: JsonElement.GetString()
// already decoded JSON, so unescaping again would corrupt paths and regexes.
internal sealed class SystemPromptTextNormalizer
{
    public SystemPromptContent? FromString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = TrimOuterBlankLines(value);
        return string.IsNullOrWhiteSpace(text)
            ? null
            : Create(text, partCount: 1);
    }

    public SystemPromptContent? FromValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => FromString(value.GetString()),
            JsonValueKind.Array => FromArray(value),
            _ => null
        };
    }

    public SystemPromptContent? FromArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string> parts = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                item.GetString() is not string raw)
            {
                continue;
            }

            string part = TrimOuterBlankLines(raw);
            if (!string.IsNullOrWhiteSpace(part))
            {
                parts.Add(part);
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        return Create(string.Join("\n\n", parts), parts.Count);
    }

    private SystemPromptContent Create(string text, int partCount)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        string hash = Convert.ToHexString(SHA256.HashData(utf8))
            .ToLowerInvariant();
        return new SystemPromptContent(text, hash, partCount);
    }

    private string TrimOuterBlankLines(string value)
    {
        string normalized = value.ReplaceLineEndings("\n");
        string[] lines = normalized.Split('\n');
        int first = 0;
        while (first < lines.Length &&
               string.IsNullOrWhiteSpace(lines[first]))
        {
            first++;
        }

        int last = lines.Length - 1;
        while (last >= first &&
               string.IsNullOrWhiteSpace(lines[last]))
        {
            last--;
        }

        return first > last
            ? string.Empty
            : string.Join("\n", lines[first..(last + 1)]);
    }
}
