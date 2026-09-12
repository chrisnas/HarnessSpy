using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HarnessSpy.Core.Sessions.Claude;

internal sealed class ClaudeJsonFileReadResult
{
    public ClaudeJsonFileReadResult(
        string? rawContent,
        JsonElement? json,
        bool isComplete,
        IReadOnlyList<string> warnings)
    {
        RawContent = rawContent;
        Json = json;
        IsComplete = isComplete;
        Warnings = warnings;
    }

    public string? RawContent { get; }

    public JsonElement? Json { get; }

    public bool IsComplete { get; }

    public IReadOnlyList<string> Warnings { get; }
}

internal sealed class ClaudeJsonFileReader
{
    private const int ReadBufferSize = 64 * 1024;

    private readonly SessionDiscoveryLimits _limits;
    private readonly UTF8Encoding _utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public ClaudeJsonFileReader(SessionDiscoveryLimits limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public async Task<ClaudeJsonFileReadResult> ReadAsync(
        string path,
        Func<bool> deadlineExceeded,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(deadlineExceeded);

        List<string> warnings = [];
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
            {
                warnings.Add($"Claude metadata file disappeared before it could be read: '{path}'.");
                return new ClaudeJsonFileReadResult(null, null, false, warnings);
            }
        }
        catch (Exception exception) when (IsRecoverableIoException(exception))
        {
            warnings.Add($"Could not inspect Claude metadata file '{path}': {exception.Message}");
            return new ClaudeJsonFileReadResult(null, null, false, warnings);
        }

        if (file.Length > _limits.MaximumFileBytes)
        {
            warnings.Add(
                $"Skipped Claude metadata file '{path}' because its {file.Length} byte size " +
                $"exceeds the configured {_limits.MaximumFileBytes} byte limit.");
            return new ClaudeJsonFileReadResult(null, null, false, warnings);
        }

        if (file.Length > int.MaxValue)
        {
            warnings.Add($"Skipped Claude metadata file '{path}' because it is too large to parse.");
            return new ClaudeJsonFileReadResult(null, null, false, warnings);
        }

        if (deadlineExceeded())
        {
            warnings.Add($"Claude session scan deadline reached before reading '{path}'.");
            return new ClaudeJsonFileReadResult(null, null, false, warnings);
        }

        try
        {
            int expectedLength = checked((int)file.Length);
            byte[] bytes = new byte[expectedLength];
            int bytesRead = 0;

            await using FileStream stream = new(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    BufferSize = ReadBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });

            while (bytesRead < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (deadlineExceeded())
                {
                    warnings.Add($"Claude session scan deadline reached while reading '{path}'.");
                    return new ClaudeJsonFileReadResult(null, null, false, warnings);
                }

                int count = await stream.ReadAsync(
                    bytes.AsMemory(bytesRead),
                    cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                bytesRead += count;
            }

            ReadOnlySpan<byte> content = bytes.AsSpan(0, bytesRead);
            if (content.Length >= 3 &&
                content[0] == 0xef &&
                content[1] == 0xbb &&
                content[2] == 0xbf)
            {
                content = content[3..];
            }

            string raw = _utf8.GetString(content);
            using JsonDocument document = JsonDocument.Parse(
                raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 128
                });

            return new ClaudeJsonFileReadResult(
                raw,
                document.RootElement.Clone(),
                bytesRead == expectedLength,
                warnings);
        }
        catch (DecoderFallbackException exception)
        {
            warnings.Add($"Claude metadata file '{path}' is not valid UTF-8: {exception.Message}");
        }
        catch (JsonException exception)
        {
            warnings.Add($"Claude metadata file '{path}' contains malformed JSON: {exception.Message}");
        }
        catch (Exception exception) when (IsRecoverableIoException(exception))
        {
            warnings.Add($"Could not read Claude metadata file '{path}': {exception.Message}");
        }

        return new ClaudeJsonFileReadResult(null, null, false, warnings);
    }

    private static bool IsRecoverableIoException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
        NotSupportedException or System.Security.SecurityException;
}

internal sealed class ClaudeSessionIndexEntry
{
    public required string SessionId { get; init; }

    public string? FullPath { get; init; }

    public string? FirstPrompt { get; init; }

    public string? Summary { get; init; }

    public long? MessageCount { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public DateTimeOffset? ModifiedAtUtc { get; init; }

    public DateTimeOffset? FileModifiedAtUtc { get; init; }

    public string? GitBranch { get; init; }

    public string? ProjectPath { get; init; }

    public bool IsSidechain { get; init; }

    public required string RawContent { get; init; }

    public IReadOnlyDictionary<string, string?> Metadata { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);
}

internal sealed class ClaudeSessionIndex
{
    public ClaudeSessionIndex(
        string path,
        string? version,
        string? originalPath,
        string rawContent,
        IReadOnlyList<ClaudeSessionIndexEntry> entries)
    {
        Path = path;
        Version = version;
        OriginalPath = originalPath;
        RawContent = rawContent;
        Entries = entries;
    }

    public string Path { get; }

    public string? Version { get; }

    public string? OriginalPath { get; }

    public string RawContent { get; }

    public IReadOnlyList<ClaudeSessionIndexEntry> Entries { get; }
}

internal sealed class ClaudeSessionIndexReadResult
{
    public ClaudeSessionIndexReadResult(
        ClaudeSessionIndex? index,
        bool isComplete,
        IReadOnlyList<string> warnings)
    {
        Index = index;
        IsComplete = isComplete;
        Warnings = warnings;
    }

    public ClaudeSessionIndex? Index { get; }

    public bool IsComplete { get; }

    public IReadOnlyList<string> Warnings { get; }
}

internal sealed class ClaudeSessionIndexReader
{
    private readonly ClaudeJsonFileReader _jsonReader;

    public ClaudeSessionIndexReader(SessionDiscoveryLimits limits)
    {
        _jsonReader = new ClaudeJsonFileReader(limits);
    }

    public async Task<ClaudeSessionIndexReadResult> ReadAsync(
        string path,
        Func<bool> deadlineExceeded,
        CancellationToken cancellationToken)
    {
        ClaudeJsonFileReadResult jsonResult = await _jsonReader.ReadAsync(
            path,
            deadlineExceeded,
            cancellationToken).ConfigureAwait(false);

        if (jsonResult.Json is not JsonElement parsedRoot ||
            jsonResult.RawContent is not string raw)
        {
            return new ClaudeSessionIndexReadResult(
                null,
                false,
                jsonResult.Warnings);
        }

        if (parsedRoot.ValueKind != JsonValueKind.Object)
        {
            List<string> warnings = [.. jsonResult.Warnings];
            warnings.Add($"Claude session index '{path}' must contain a JSON object.");
            return new ClaudeSessionIndexReadResult(null, false, warnings);
        }

        JsonElement root = parsedRoot;
        string? version = ScalarText(root, "version");
        string? originalPath = String(root, "originalPath", "original_path");
        List<ClaudeSessionIndexEntry> entries = [];
        List<string> parseWarnings = [.. jsonResult.Warnings];

        if (root.TryGetProperty("entries", out JsonElement entryArray))
        {
            if (entryArray.ValueKind != JsonValueKind.Array)
            {
                parseWarnings.Add(
                    $"Claude session index '{path}' has a non-array 'entries' property.");
            }
            else
            {
                int ordinal = 0;
                foreach (JsonElement element in entryArray.EnumerateArray())
                {
                    ordinal++;
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        parseWarnings.Add(
                            $"Skipped non-object entry {ordinal} in Claude session index '{path}'.");
                        continue;
                    }

                    string? sessionId = String(element, "sessionId", "session_id");
                    if (string.IsNullOrWhiteSpace(sessionId))
                    {
                        parseWarnings.Add(
                            $"Skipped entry {ordinal} without a session ID in Claude session index '{path}'.");
                        continue;
                    }

                    Dictionary<string, string?> metadata =
                        new(StringComparer.Ordinal);
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        metadata[property.Name] = MetadataText(property.Value);
                    }

                    entries.Add(new ClaudeSessionIndexEntry
                    {
                        SessionId = sessionId,
                        FullPath = String(element, "fullPath", "full_path"),
                        FirstPrompt = String(element, "firstPrompt", "first_prompt"),
                        Summary = String(element, "summary", "title"),
                        MessageCount = Long(element, "messageCount", "message_count"),
                        CreatedAtUtc = Timestamp(element, "created", "createdAt", "created_at"),
                        ModifiedAtUtc = Timestamp(element, "modified", "updatedAt", "updated_at"),
                        FileModifiedAtUtc = UnixTimestamp(
                            element,
                            "fileMtime",
                            "file_mtime",
                            "mtime"),
                        GitBranch = String(element, "gitBranch", "git_branch"),
                        ProjectPath = String(element, "projectPath", "project_path", "cwd"),
                        IsSidechain = Boolean(element, "isSidechain", "is_sidechain") ?? false,
                        RawContent = element.GetRawText(),
                        Metadata = metadata
                    });
                }
            }
        }

        ClaudeSessionIndex index = new(path, version, originalPath, raw, entries);
        return new ClaudeSessionIndexReadResult(
            index,
            jsonResult.IsComplete,
            parseWarnings);
    }

    private static string? String(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static string? ScalarText(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                continue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False =>
                    value.GetRawText(),
                _ => null
            };
        }

        return null;
    }

    private static long? Long(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out long number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(
                    value.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out number))
            {
                return number;
            }
        }

        return null;
    }

    private static bool? Boolean(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            if (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out bool result))
            {
                return result;
            }
        }

        return null;
    }

    private static DateTimeOffset? Timestamp(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    value.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset timestamp))
            {
                return timestamp;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out long number))
            {
                return FromUnixNumber(number);
            }
        }

        return null;
    }

    private static DateTimeOffset? UnixTimestamp(
        JsonElement element,
        params string[] names)
    {
        long? value = Long(element, names);
        return value is long number ? FromUnixNumber(number) : null;
    }

    private static DateTimeOffset? FromUnixNumber(long value)
    {
        try
        {
            return value <= -100_000_000_000 ||
                value >= 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? MetadataText(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            _ => value.GetRawText()
        };
}
