using System.Text;
using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Wpf.ViewModels;

internal static class ToolCorrelationMatcher
{
    public const int NoMatch = -1;

    public static int ScoreGenericToolCall(
        HookObservation preObservation,
        HookObservation postObservation)
    {
        int score = 0;
        return CompareToolInput(preObservation, postObservation, weight: 100, ref score)
            ? score
            : NoMatch;
    }

    public static int ScoreShellExecution(
        HookObservation candidate,
        HookObservation observation)
    {
        int score = 0;

        if (!CompareString(
                ReadShellCommand(candidate),
                ReadShellCommand(observation),
                NormalizeCommand,
                StringComparer.Ordinal,
                weight: 100,
                ref score) ||
            !CompareString(
                ReadWorkingDirectory(candidate),
                ReadWorkingDirectory(observation),
                NormalizePath,
                StringComparer.OrdinalIgnoreCase,
                weight: 20,
                ref score) ||
            !CompareBoolean(
                ReadSandbox(candidate),
                ReadSandbox(observation),
                weight: 5,
                ref score))
        {
            return NoMatch;
        }

        return score;
    }

    // Matches a file-access inner hook (beforeReadFile / afterFileEdit) to its
    // owning preToolUse by the target file path they share.
    public static int ScoreFileTargetExecution(
        HookObservation candidate,
        HookObservation observation)
    {
        int score = 0;

        if (!CompareString(
                ReadTargetFilePath(candidate),
                ReadTargetFilePath(observation),
                NormalizePath,
                StringComparer.OrdinalIgnoreCase,
                weight: 100,
                ref score))
        {
            return NoMatch;
        }

        return score;
    }

    public static int ScoreMcpExecution(
        HookObservation candidate,
        HookObservation observation)
    {
        int score = 0;

        if (!CompareString(
                NormalizeMcpToolName(candidate.ToolName),
                NormalizeMcpToolName(observation.ToolName),
                static value => value,
                StringComparer.Ordinal,
                weight: 20,
                ref score) ||
            !CompareString(
                candidate.McpServerName,
                observation.McpServerName,
                static value => value,
                StringComparer.Ordinal,
                weight: 40,
                ref score) ||
            !CompareString(
                ReadString(candidate.Payload, "url"),
                ReadString(observation.Payload, "url"),
                static value => value,
                StringComparer.Ordinal,
                weight: 30,
                ref score) ||
            !CompareString(
                ReadString(candidate.Payload, "command"),
                ReadString(observation.Payload, "command"),
                NormalizeCommand,
                StringComparer.Ordinal,
                weight: 20,
                ref score) ||
            !CompareToolInput(candidate, observation, weight: 100, ref score))
        {
            return NoMatch;
        }

        return score;
    }

    private static bool CompareToolInput(
        HookObservation left,
        HookObservation right,
        int weight,
        ref int score)
    {
        string? leftInput = ReadCanonicalToolInput(left);
        string? rightInput = ReadCanonicalToolInput(right);
        return CompareString(
            leftInput,
            rightInput,
            static value => value,
            StringComparer.Ordinal,
            weight,
            ref score);
    }

    private static bool CompareString(
        string? left,
        string? right,
        Func<string, string> normalize,
        StringComparer comparer,
        int weight,
        ref int score)
    {
        if (left is null || right is null)
        {
            return true;
        }

        if (!comparer.Equals(normalize(left), normalize(right)))
        {
            return false;
        }

        score += weight;
        return true;
    }

    private static bool CompareBoolean(
        bool? left,
        bool? right,
        int weight,
        ref int score)
    {
        if (left is null || right is null)
        {
            return true;
        }

        if (left != right)
        {
            return false;
        }

        score += weight;
        return true;
    }

    // beforeReadFile/afterFileEdit carry file_path at the payload root; the
    // owning preToolUse carries it inside tool_input as file_path (Read/Write),
    // path (StrReplace), or target_notebook (EditNotebook).
    private static string? ReadTargetFilePath(HookObservation observation) =>
        ReadString(observation.Payload, "file_path") ??
        ReadToolInputString(observation.Payload, "file_path") ??
        ReadToolInputString(observation.Payload, "path") ??
        ReadToolInputString(observation.Payload, "target_notebook");

    private static string? ReadShellCommand(HookObservation observation) =>
        ReadString(observation.Payload, "command") ??
        ReadToolInputString(observation.Payload, "command");

    private static string? ReadWorkingDirectory(HookObservation observation) =>
        ReadToolInputString(observation.Payload, "working_directory") ??
        ReadToolInputString(observation.Payload, "cwd") ??
        ReadString(observation.Payload, "cwd");

    private static bool? ReadSandbox(HookObservation observation) =>
        ReadBoolean(observation.Payload, "sandbox") ??
        ReadToolInputBoolean(observation.Payload, "sandbox");

    private static string? NormalizeMcpToolName(string? toolName)
    {
        if (toolName?.StartsWith("MCP:", StringComparison.Ordinal) == true)
        {
            return toolName[4..];
        }

        return toolName;
    }

    private static string NormalizeCommand(string command) =>
        command.ReplaceLineEndings("\n").TrimEnd('\n');

    private static string NormalizePath(string path) =>
        path.Trim().Replace('/', '\\').TrimEnd('\\');

    private static string? ReadCanonicalToolInput(HookObservation observation)
    {
        if (observation.Payload.ValueKind != JsonValueKind.Object ||
            !observation.Payload.TryGetProperty("tool_input", out JsonElement input))
        {
            return null;
        }

        return CanonicalizeToolInput(input);
    }

    private static string CanonicalizeToolInput(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String &&
            TryParseJsonContainer(input.GetString(), out JsonDocument? nested) &&
            nested is not null)
        {
            using (nested)
            {
                return Canonicalize(nested.RootElement);
            }
        }

        return Canonicalize(input);
    }

    private static bool TryParseJsonContainer(
        string? value,
        out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = value.AsSpan().TrimStart();
        if (trimmed.IsEmpty || trimmed[0] is not ('{' or '['))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            document?.Dispose();
            document = null;
            return false;
        }
    }

    private static string Canonicalize(JsonElement value)
    {
        StringBuilder builder = new();
        AppendCanonicalJson(builder, value);
        return builder.ToString();
    }

    private static void AppendCanonicalJson(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                builder.Append('{');
                bool first = true;
                foreach (JsonProperty property in value
                    .EnumerateObject()
                    .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(JsonSerializer.Serialize(property.Name));
                    builder.Append(':');
                    AppendCanonicalJson(builder, property.Value);
                }

                builder.Append('}');
                break;
            }

            case JsonValueKind.Array:
            {
                builder.Append('[');
                bool first = true;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    AppendCanonicalJson(builder, item);
                }

                builder.Append(']');
                break;
            }

            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(value.GetString()));
                break;

            case JsonValueKind.Number:
                builder.Append(value.GetRawText());
                break;

            case JsonValueKind.True:
                builder.Append("true");
                break;

            case JsonValueKind.False:
                builder.Append("false");
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                builder.Append("null");
                break;
        }
    }

    private static string? ReadToolInputString(
        JsonElement payload,
        string propertyName)
    {
        if (!TryReadToolInput(payload, out JsonElement toolInput) ||
            toolInput.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(toolInput, propertyName);
    }

    private static bool? ReadToolInputBoolean(
        JsonElement payload,
        string propertyName)
    {
        if (!TryReadToolInput(payload, out JsonElement toolInput) ||
            toolInput.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadBoolean(toolInput, propertyName);
    }

    private static bool TryReadToolInput(
        JsonElement payload,
        out JsonElement toolInput)
    {
        toolInput = default;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("tool_input", out JsonElement value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            toolInput = value;
            return true;
        }

        if (value.ValueKind != JsonValueKind.String ||
            !TryParseJsonContainer(value.GetString(), out JsonDocument? nested) ||
            nested is null)
        {
            return false;
        }

        using (nested)
        {
            toolInput = nested.RootElement.Clone();
            return true;
        }
    }

    private static string? ReadString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool? ReadBoolean(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        return value.GetBoolean();
    }
}
