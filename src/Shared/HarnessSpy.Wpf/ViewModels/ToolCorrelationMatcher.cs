using System.IO;
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

    // Copilot CLI has no tool-use id, so a completion is matched to its request
    // by identical canonical toolArgs. Returns false when either side has no
    // toolArgs so callers can fall back to arrival order.
    public static bool CopilotToolArgsEqual(
        HookObservation left,
        HookObservation right)
    {
        string? leftArgs = ReadCanonicalObject(left.Payload, "toolArgs");
        string? rightArgs = ReadCanonicalObject(right.Payload, "toolArgs");
        return leftArgs is not null &&
            rightArgs is not null &&
            StringComparer.Ordinal.Equals(leftArgs, rightArgs);
    }

    private static string? ReadCanonicalObject(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return CanonicalizeToolInput(value);
    }

    // Copilot CLI has no tool-use id, and its permission prompt / permission
    // notification spell the same call differently from the request
    // ("<server>/<tool>" vs "<server>-<tool>", "edit" vs "apply_patch") and carry
    // their input in a different shape ("toolInput"/message vs "toolArgs"). So a
    // prompt is matched to its request name-agnostically, by the strongest signal
    // they share: target file name, then shell command, then canonical arguments.
    // Returns a positive score on agreement and NoMatch when they share no signal
    // or disagree, so SelectUniqueBest can pick a single owner (or none).
    public static int ScoreCopilotCallReference(
        HookObservation candidatePre,
        HookObservation reference)
    {
        CopilotSignature pre = ReadCopilotSignature(candidatePre);
        CopilotSignature other = ReadCopilotSignature(reference);

        if (pre.FileName is not null && other.FileName is not null)
        {
            return StringComparer.OrdinalIgnoreCase.Equals(pre.FileName, other.FileName)
                ? 100
                : NoMatch;
        }

        if (pre.Command is not null && other.Command is not null)
        {
            return StringComparer.Ordinal.Equals(
                NormalizeCommand(pre.Command),
                NormalizeCommand(other.Command))
                ? 100
                : NoMatch;
        }

        if (pre.Args is not null && other.Args is not null)
        {
            return StringComparer.Ordinal.Equals(pre.Args, other.Args) ? 100 : NoMatch;
        }

        return NoMatch;
    }

    private readonly record struct CopilotSignature(string? FileName, string? Command, string? Args);

    private static readonly string[] CopilotArgKeys = ["toolArgs", "toolInput", "tool_input"];

    private static readonly string[] PatchFileVerbs =
        ["Update File:", "Add File:", "Delete File:", "Move to:"];

    private static readonly string[] CommandMessagePrefixes =
        ["Run command:", "Execute command:"];

    private static readonly string[] PathMessagePrefixes =
    [
        "Path permission needed:", "Edit file:", "Read file:", "View file:",
        "Create file:", "Write file:", "Write to file:", "Delete file:"
    ];

    private static CopilotSignature ReadCopilotSignature(HookObservation observation)
    {
        JsonElement payload = observation.Payload;

        string? filePath = ReadCopilotFilePath(payload);
        string? command = ReadCopilotArgString(payload, "command");
        string? args = ReadCopilotArgsObject(payload);

        // A permission-prompt notification carries no structured input, only a
        // message such as "Run command: <cmd>", "Edit file: <path>", or
        // "Path permission needed: <path>".
        if (filePath is null && command is null && args is null)
        {
            (string? messageFile, string? messageCommand) =
                ParseNotificationMessage(ReadString(payload, "message"));
            filePath = messageFile;
            command = messageCommand;
        }

        string? fileName = string.IsNullOrEmpty(filePath)
            ? null
            : Path.GetFileName(filePath.Replace('/', '\\'));
        return new CopilotSignature(fileName, command, args);
    }

    // Copilot input arrives under "toolArgs" (request/completion) or "toolInput"
    // (permission), either as an object or a JSON-encoded string.
    private static bool TryReadCopilotArgs(JsonElement payload, out JsonElement args)
    {
        args = default;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (string key in CopilotArgKeys)
        {
            if (!payload.TryGetProperty(key, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                args = value;
                return true;
            }

            if (value.ValueKind == JsonValueKind.String &&
                TryParseJsonContainer(value.GetString(), out JsonDocument? nested) &&
                nested is not null)
            {
                using (nested)
                {
                    args = nested.RootElement.Clone();
                    return true;
                }
            }
        }

        return false;
    }

    private static string? ReadCopilotArgString(JsonElement payload, string name) =>
        TryReadCopilotArgs(payload, out JsonElement args) && args.ValueKind == JsonValueKind.Object
            ? ReadString(args, name)
            : null;

    private static string? ReadCopilotArgsObject(JsonElement payload) =>
        TryReadCopilotArgs(payload, out JsonElement args) && args.ValueKind == JsonValueKind.Object
            ? Canonicalize(args)
            : null;

    private static string? ReadCopilotFilePath(JsonElement payload)
    {
        string? structured =
            ReadCopilotArgString(payload, "file_path") ??
            ReadCopilotArgString(payload, "path");
        if (structured is not null)
        {
            return structured;
        }

        // apply_patch delivers its edit as a raw patch string whose header names
        // the file (e.g. "*** Update File: relative/path").
        if (payload.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in CopilotArgKeys)
            {
                if (payload.TryGetProperty(key, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String &&
                    ExtractPatchFilePath(value.GetString()) is string patchFile)
                {
                    return patchFile;
                }
            }
        }

        return null;
    }

    private static string? ExtractPatchFilePath(string? patch)
    {
        if (string.IsNullOrEmpty(patch))
        {
            return null;
        }

        foreach (string verb in PatchFileVerbs)
        {
            int verbIndex = patch.IndexOf(verb, StringComparison.Ordinal);
            if (verbIndex < 0)
            {
                continue;
            }

            int start = verbIndex + verb.Length;
            int newline = patch.IndexOf('\n', start);
            string path = (newline < 0 ? patch[start..] : patch[start..newline]).Trim();
            if (path.Length > 0)
            {
                return path;
            }
        }

        return null;
    }

    // Extracts the referenced command or file from a permission-prompt
    // notification message. Classification is by prefix, because a "Run command:"
    // value routinely embeds a Windows path that would otherwise look like a file.
    private static (string? File, string? Command) ParseNotificationMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return (null, null);
        }

        foreach (string prefix in CommandMessagePrefixes)
        {
            if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string command = message[prefix.Length..].Trim();
                return (null, command.Length == 0 ? null : command);
            }
        }

        foreach (string prefix in PathMessagePrefixes)
        {
            if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = message[prefix.Length..].Trim();
                int comma = rest.IndexOf(',');
                string first = (comma >= 0 ? rest[..comma] : rest).Trim();
                return (first.Length == 0 ? null : first, null);
            }
        }

        return (null, null);
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
