using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;

namespace HarnessSpy.Core.Services;

// Produces the same fallback signature from hook and transcript tool payloads.
// Providers use different container names (toolArgs, arguments, tool_input,
// input), so the complete argument value is canonicalized and hashed rather
// than relying on one path/command field or an ambiguous FIFO-only key.
public sealed class ToolCorrelationSignatureBuilder
{
    private static readonly string[] ArgumentContainers =
        ["arguments", "toolArgs", "tool_input", "input"];

    public string Build(HookObservation observation)
    {
        CanonicalToolKind kind = ToolClassifier.Classify(observation.ToolName);
        string toolName = observation.Provider == HookProvider.GitHubCopilot
            ? NormalizeString(observation.ToolName ?? string.Empty)
            : string.Empty;
        string arguments = ReadCanonicalArguments(observation) ?? string.Empty;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(arguments)));
        return $"{observation.ProviderScopedSessionId}|{kind}|{toolName}|{hash}";
    }

    private static string? ReadCanonicalArguments(HookObservation observation)
    {
        JsonElement payload = observation.Payload;
        foreach (string container in ArgumentContainers)
        {
            if (payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty(container, out JsonElement value))
            {
                continue;
            }

            return CanonicalizeArgumentValue(value);
        }

        if (observation.TargetFilePath is string targetFilePath)
        {
            return NormalizeString(targetFilePath);
        }

        string? direct =
            RuntimeJson.String(payload, "command", "path", "file_path", "pattern", "skill");
        return direct is null ? null : NormalizeString(direct);
    }

    private static string CanonicalizeArgumentValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return Canonicalize(value);
        }

        string raw = value.GetString() ?? string.Empty;
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(raw);
            return Canonicalize(parsed.RootElement);
        }
        catch (JsonException)
        {
            return NormalizeString(raw);
        }
    }

    private static string Canonicalize(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(
                ",",
                value.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property =>
                        $"{JsonSerializer.Serialize(property.Name)}:{Canonicalize(property.Value)}")) + "}",
            JsonValueKind.Array => "[" + string.Join(
                ",",
                value.EnumerateArray().Select(Canonicalize)) + "]",
            JsonValueKind.String => JsonSerializer.Serialize(
                NormalizeString(value.GetString() ?? string.Empty)),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => "null",
            _ => value.GetRawText()
        };

    private static string NormalizeString(string value) =>
        value.ReplaceLineEndings("\n")
            .Replace('/', '\\')
            .Trim()
            .ToUpperInvariant();
}
