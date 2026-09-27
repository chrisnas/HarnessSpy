using System.Text.Json;

namespace HarnessSpy.Wpf.ViewModels;

internal sealed class CommandTextExtractor
{
    private const int MaximumLength = 120;

    public string? Extract(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string name in new[]
        {
            "toolArgs",
            "tool_input",
            "arguments",
            "args"
        })
        {
            if (payload.TryGetProperty(name, out JsonElement arguments))
            {
                return ExtractArguments(arguments);
            }
        }

        return null;
    }

    public string? Extract(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(arguments);
            return ExtractArguments(document.RootElement);
        }
        catch (JsonException)
        {
            return Preview(arguments);
        }
    }

    private static string? ExtractArguments(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (string name in new[] { "command", "cmd", "script" })
            {
                if (arguments.TryGetProperty(name, out JsonElement command) &&
                    command.ValueKind == JsonValueKind.String)
                {
                    return Preview(command.GetString());
                }
            }

            return Preview(arguments.GetRawText());
        }

        return arguments.ValueKind == JsonValueKind.String
            ? Preview(arguments.GetString())
            : Preview(arguments.GetRawText());
    }

    private static string? Preview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string oneLine = value.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= MaximumLength
            ? oneLine
            : oneLine[..MaximumLength].TrimEnd() + "\u2026";
    }
}
