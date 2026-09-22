using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;

namespace HarnessSpy.Core.Services;

// Produces the same fallback signature from hook and transcript tool payloads.
// Copilot canonicalizes the complete argument value. Cursor's hook and
// transcript schemas reshape the same call, so Cursor instead hashes stable
// semantic fields such as command, path, pattern, and glob.
public sealed class ToolCorrelationSignatureBuilder
{
    private static readonly string[] ArgumentContainers =
        ["arguments", "toolArgs", "tool_input", "input"];

    public string Build(HookObservation observation)
    {
        CanonicalToolKind kind =
            observation.Provider == HookProvider.Cursor &&
            observation.ToolKind != CanonicalToolKind.Unknown
                ? observation.ToolKind
                : ToolClassifier.Classify(observation.ToolName);
        string toolIdentity = observation.Provider == HookProvider.Cursor
            ? CursorToolIdentity(observation, kind)
            : kind.ToString();
        string toolName = observation.Provider == HookProvider.GitHubCopilot
            ? NormalizeString(observation.ToolName ?? string.Empty)
            : string.Empty;
        string arguments = observation.Provider == HookProvider.Cursor
            ? ReadCursorSemanticArguments(observation, kind) ??
                ReadCanonicalArguments(observation) ??
                string.Empty
            : ReadCanonicalArguments(observation) ?? string.Empty;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(arguments)));
        return $"{observation.ProviderScopedSessionId}|{toolIdentity}|{toolName}|{hash}";
    }

    private static string CursorToolIdentity(
        HookObservation observation,
        CanonicalToolKind kind)
    {
        JsonElement arguments =
            ReadArgumentValue(observation) ?? default;
        if (kind == CanonicalToolKind.Mcp)
        {
            string? server =
                observation.McpServerName ??
                ReadCursorValue(
                    observation,
                    arguments,
                    "namespace",
                    "server",
                    "serverName",
                    "server_name");
            string? tool =
                observation.McpToolName ??
                ReadCursorValue(
                    observation,
                    arguments,
                    "toolName",
                    "tool_name") ??
                McpToolName(observation.ToolName);
            return $"CursorMcp:{NormalizeMcpServer(server)}:" +
                NormalizeString(tool ?? string.Empty);
        }

        string? glob = ReadCursorValue(
            observation,
            arguments,
            "glob",
            "glob_pattern");
        string? pattern = ReadCursorValue(
            observation,
            arguments,
            "pattern");
        if (kind == CanonicalToolKind.FileSearch ||
            kind == CanonicalToolKind.TextSearch &&
            pattern is null &&
            glob is not null)
        {
            return "CursorFileSearch";
        }

        return kind.ToString();
    }

    private static string? ReadCursorSemanticArguments(
        HookObservation observation,
        CanonicalToolKind kind)
    {
        JsonElement arguments =
            ReadArgumentValue(observation) ?? default;
        string? path = ReadCursorValue(
            observation,
            arguments,
            "file_path",
            "path",
            "target_directory",
            "target_notebook");

        return kind switch
        {
            CanonicalToolKind.Shell => SemanticArguments(
                ("command", ReadCursorValue(
                    observation,
                    arguments,
                    "command"))),
            CanonicalToolKind.FileRead or
            CanonicalToolKind.FileWrite or
            CanonicalToolKind.FileEdit or
            CanonicalToolKind.FileDelete or
            CanonicalToolKind.Notebook => SemanticArguments(
                ("path", path)),
            CanonicalToolKind.TextSearch => SemanticArguments(
                ("path", path),
                ("pattern", ReadCursorValue(
                    observation,
                    arguments,
                    "pattern")),
                ("glob", ReadCursorValue(
                    observation,
                    arguments,
                    "glob",
                    "glob_pattern"))),
            CanonicalToolKind.FileSearch => SemanticArguments(
                ("path", path),
                ("glob", ReadCursorValue(
                    observation,
                    arguments,
                    "glob_pattern",
                    "glob",
                    "pattern"))),
            CanonicalToolKind.Web => SemanticArguments(
                ("query", ReadCursorValue(
                    observation,
                    arguments,
                    "query",
                    "search_term")),
                ("url", ReadCursorValue(
                    observation,
                    arguments,
                    "url"))),
            CanonicalToolKind.Mcp =>
                ReadCursorMcpArguments(observation),
            _ => null
        };
    }

    private static string? ReadCursorMcpArguments(
        HookObservation observation)
    {
        JsonElement arguments =
            ReadArgumentValue(observation) ?? default;
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty(
                "arguments",
                out JsonElement nestedArguments))
        {
            return CanonicalizeArgumentValue(nestedArguments);
        }

        return arguments.ValueKind is
            JsonValueKind.Undefined or JsonValueKind.Null
                ? null
                : CanonicalizeArgumentValue(arguments);
    }

    private static JsonElement? ReadArgumentValue(
        HookObservation observation)
    {
        JsonElement payload = observation.Payload;
        foreach (string container in ArgumentContainers)
        {
            if (payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty(container, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            try
            {
                using JsonDocument parsed =
                    JsonDocument.Parse(value.GetString() ?? string.Empty);
                if (parsed.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return parsed.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static string? ReadCursorValue(
        HookObservation observation,
        JsonElement arguments,
        params string[] names) =>
        RuntimeJson.String(arguments, names) ??
        RuntimeJson.String(observation.Payload, names);

    private static string? SemanticArguments(
        params (string Name, string? Value)[] values)
    {
        string[] present = values
            .Where(static item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item =>
                $"{item.Name}={JsonSerializer.Serialize(
                    NormalizeString(item.Value!))}")
            .ToArray();
        return present.Length == 0
            ? null
            : string.Join("|", present);
    }

    private static string? McpToolName(string? toolName)
    {
        if (toolName?.StartsWith(
                "MCP:",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            return toolName[4..];
        }

        return toolName is "CallDynamicTool" or "GetDynamicTools"
            ? null
            : toolName;
    }

    private static string NormalizeMcpServer(string? server)
    {
        if (server?.StartsWith(
                "user-",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            server = server[5..];
        }

        return NormalizeString(server ?? string.Empty);
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
