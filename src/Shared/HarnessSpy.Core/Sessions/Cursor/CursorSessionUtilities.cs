using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Cursor;

internal sealed class CursorWarningCollector
{
    private const int MaximumWarnings = 200;

    private readonly object _gate = new();
    private readonly List<string> _warnings = [];
    private int _suppressedCount;

    public void Add(string warning)
    {
        lock (_gate)
        {
            if (_warnings.Count < MaximumWarnings)
            {
                _warnings.Add(warning);
            }
            else
            {
                _suppressedCount++;
            }
        }
    }

    public void AddRange(IEnumerable<string> warnings)
    {
        foreach (string warning in warnings)
        {
            Add(warning);
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            if (_suppressedCount == 0)
            {
                return _warnings.ToArray();
            }

            return
            [
                .. _warnings,
                $"{_suppressedCount} additional Cursor discovery warnings were suppressed."
            ];
        }
    }
}

internal sealed class CursorDiscoveryGuard
{
    private readonly SessionDiscoveryLimits _limits;
    private readonly TimeSpan _maximumDuration;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _directories;
    private int _files;

    public CursorDiscoveryGuard(SessionDiscoveryLimits limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _maximumDuration = limits.EffectiveMaximumDuration;
    }

    public bool IsComplete { get; private set; } = true;

    public bool IsExhausted { get; private set; }

    public bool DeadlineExceeded =>
        _maximumDuration != Timeout.InfiniteTimeSpan &&
        (_maximumDuration <= TimeSpan.Zero ||
            _stopwatch.Elapsed >= _maximumDuration);

    public bool TryVisitDirectory(string path, CursorWarningCollector warnings)
    {
        if (DeadlineExceeded)
        {
            if (!IsExhausted)
            {
                warnings.Add($"Cursor discovery time limit was reached at '{path}'.");
            }

            MarkExhausted();
            return false;
        }

        if (_directories >= Math.Max(0, _limits.MaximumDirectories))
        {
            if (!IsExhausted)
            {
                warnings.Add(
                    $"Cursor discovery directory limit " +
                    $"{_limits.MaximumDirectories} was reached.");
            }

            MarkExhausted();
            return false;
        }

        _directories++;
        return true;
    }

    public bool TryVisitFile(string path, CursorWarningCollector warnings)
    {
        if (DeadlineExceeded)
        {
            if (!IsExhausted)
            {
                warnings.Add($"Cursor discovery time limit was reached at '{path}'.");
            }

            MarkExhausted();
            return false;
        }

        if (_files >= Math.Max(0, _limits.MaximumFiles))
        {
            if (!IsExhausted)
            {
                warnings.Add(
                    $"Cursor discovery file limit {_limits.MaximumFiles} was reached.");
            }

            MarkExhausted();
            return false;
        }

        _files++;
        return true;
    }

    public void MarkIncomplete() => IsComplete = false;

    public void MarkExhausted()
    {
        IsComplete = false;
        IsExhausted = true;
    }
}

internal sealed class CursorFileSystemInspector
{
    public bool IsReadableRegularFile(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.Directory) &&
                !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    public bool IsReadableDirectory(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) &&
                !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    public bool TryGetFileInfo(
        string path,
        out long length,
        out DateTimeOffset lastWriteTimeUtc,
        out string? error)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || !IsReadableRegularFile(path))
            {
                length = 0;
                lastWriteTimeUtc = default;
                error = "the file is absent, not regular, or is a symbolic link";
                return false;
            }

            length = file.Length;
            lastWriteTimeUtc = file.LastWriteTimeUtc;
            error = null;
            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            length = 0;
            lastWriteTimeUtc = default;
            error = exception.Message;
            return false;
        }
    }

    public bool IsRecoverable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
        NotSupportedException or System.Security.SecurityException;
}

internal sealed class CursorIdentityBuilder
{
    public string CatalogId(string nativeSessionId, string projectKey, bool scoped)
    {
        if (!scoped)
        {
            return $"cursor:{nativeSessionId}";
        }

        return $"cursor:{nativeSessionId}:project:{StableHash(projectKey, 12)}";
    }

    public string EventId(
        string sourcePath,
        long byteOffset,
        int fragmentIndex,
        string nativeName) =>
        $"cursor-event:{StableHash(sourcePath, 16)}:{byteOffset}:{fragmentIndex}:" +
        StableHash(nativeName, 8);

    public string TranscriptTurnId(
        string nativeSessionId,
        string? agentId,
        int number)
    {
        string agent = string.IsNullOrWhiteSpace(agentId)
            ? string.Empty
            : $":agent:{EscapeSegment(agentId)}";
        return $"cursor:{EscapeSegment(nativeSessionId)}{agent}:turn:{number}";
    }

    public string AssistantStepId(string sourcePath, long byteOffset) =>
        $"cursor-step:{StableHash(sourcePath, 16)}:{byteOffset}";

    private string StableHash(string value, int characters)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        string hash = Convert.ToHexStringLower(bytes);
        return hash[..Math.Min(characters, hash.Length)];
    }

    private string EscapeSegment(string value) =>
        Uri.EscapeDataString(value).Replace(":", "%3A", StringComparison.Ordinal);
}

internal sealed class CursorJsonReader
{
    private readonly JsonDocumentOptions _documentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 128
    };

    public bool TryParse(
        string raw,
        out JsonDocument? document,
        out string? error)
    {
        try
        {
            document = JsonDocument.Parse(raw, _documentOptions);
            error = null;
            return true;
        }
        catch (JsonException firstException)
        {
            string sanitized = SanitizeControlCharacters(raw);
            if (string.Equals(sanitized, raw, StringComparison.Ordinal))
            {
                document = null;
                error = firstException.Message;
                return false;
            }

            try
            {
                document = JsonDocument.Parse(sanitized, _documentOptions);
                error = null;
                return true;
            }
            catch (JsonException secondException)
            {
                document = null;
                error = secondException.Message;
                return false;
            }
        }
    }

    public bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(
                    property.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public string? String(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetProperty(element, name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                string? text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    public bool? Boolean(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetProperty(element, name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out int number))
            {
                return number != 0;
            }

            if (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out bool parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public int? Integer(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetProperty(element, name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out int number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(
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

    public double? Double(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetProperty(element, name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out double number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(
                    value.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out number))
            {
                return number;
            }
        }

        return null;
    }

    public DateTimeOffset? Timestamp(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetProperty(element, name, out JsonElement value))
            {
                continue;
            }

            DateTimeOffset? parsed = Timestamp(value);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    public DateTimeOffset? Timestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal |
                DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset date))
        {
            return date;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double numeric) ||
            !double.IsFinite(numeric) ||
            numeric <= 0)
        {
            return null;
        }

        try
        {
            double milliseconds = numeric switch
            {
                > 100_000_000_000_000 => numeric / 1000d,
                < 100_000_000_000 => numeric * 1000d,
                _ => numeric
            };

            return DateTimeOffset.FromUnixTimeMilliseconds(
                checked((long)milliseconds));
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    public string? JsonText(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetProperty(element, name, out JsonElement value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            return value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.GetRawText();
        }

        return null;
    }

    public string NormalizeUserPrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        const string openTag = "<user_query>";
        const string closeTag = "</user_query>";
        List<string> chunks = [];
        int cursor = 0;

        while (cursor < text.Length)
        {
            int relativeStart = text.IndexOf(
                openTag,
                cursor,
                StringComparison.OrdinalIgnoreCase);
            if (relativeStart < 0)
            {
                break;
            }

            int contentStart = relativeStart + openTag.Length;
            int contentEnd = text.IndexOf(
                closeTag,
                contentStart,
                StringComparison.OrdinalIgnoreCase);
            if (contentEnd < 0)
            {
                string remainder = text[contentStart..].Trim();
                if (remainder.Length > 0)
                {
                    chunks.Add(remainder);
                }

                break;
            }

            string chunk = text[contentStart..contentEnd].Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            cursor = contentEnd + closeTag.Length;
        }

        return chunks.Count == 0 ? text.Trim() : string.Join("\n", chunks);
    }

    public string Preview(string? text, int maximumCharacters = 120)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string oneLine = string.Join(
            " ",
            text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
        return oneLine.Length <= maximumCharacters
            ? oneLine
            : oneLine[..maximumCharacters].TrimEnd() + "\u2026";
    }

    public IReadOnlyList<string> TargetPaths(JsonElement value)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        CollectTargetPaths(value, paths, depth: 0);
        return paths.Take(64).ToArray();
    }

    public IReadOnlyList<string> WorkspacePaths(JsonElement value)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        CollectWorkspacePaths(value, paths, depth: 0);
        return paths.Take(16).ToArray();
    }

    public bool IsValidIdentity(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= 512 &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Contains('\0');

    private void CollectTargetPaths(
        JsonElement value,
        HashSet<string> paths,
        int depth)
    {
        if (depth > 12 || paths.Count >= 64)
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                string key = property.Name.ToLowerInvariant();
                if (key is "path" or "file_path" or "filepath" or
                    "target_file" or "targetfile" or "notebook_path" or
                    "working_directory" or "workingdirectory" or "cwd")
                {
                    AddStringValues(property.Value, paths);
                }
                else if (key is "paths" or "files" or "target_paths" or
                    "targetpaths")
                {
                    AddStringValues(property.Value, paths);
                }

                CollectTargetPaths(property.Value, paths, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in value.EnumerateArray())
            {
                CollectTargetPaths(child, paths, depth + 1);
            }
        }
    }

    private void CollectWorkspacePaths(
        JsonElement value,
        HashSet<string> paths,
        int depth)
    {
        if (depth > 12 || paths.Count >= 16)
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                string key = property.Name.ToLowerInvariant();
                if (key is "cwd" or "workspace" or "workspace_root" or
                    "workspaceroot" or "workspace_path" or "workspacepath" or
                    "project_path" or "projectpath" or "working_directory" or
                    "workingdirectory" or "fspath")
                {
                    AddStringValues(property.Value, paths);
                }
                else if (key is "workspace_roots" or "workspaceroots")
                {
                    AddStringValues(property.Value, paths);
                }

                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    AddLabeledWorkspacePath(property.Value.GetString(), paths);
                }

                CollectWorkspacePaths(property.Value, paths, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in value.EnumerateArray())
            {
                CollectWorkspacePaths(child, paths, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            AddLabeledWorkspacePath(value.GetString(), paths);
        }
    }

    private void AddStringValues(JsonElement value, HashSet<string> values)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            AddCandidate(value.GetString(), values);
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                AddCandidate(item.GetString(), values);
            }
        }
    }

    private void AddLabeledWorkspacePath(
        string? value,
        HashSet<string> paths)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string[] labels =
        [
            "Workspace Path:",
            "Workspace:",
            "\"cwd\":"
        ];
        foreach (string label in labels)
        {
            int index = value.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            int start = index + label.Length;
            int end = value.IndexOfAny(['\r', '\n', '"', '<'], start);
            string candidate = (end < 0 ? value[start..] : value[start..end])
                .Trim()
                .Trim('"');
            AddCandidate(candidate, paths);
        }
    }

    private void AddCandidate(string? candidate, HashSet<string> values)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 32_768)
        {
            return;
        }

        values.Add(candidate.Trim());
    }

    private string SanitizeControlCharacters(string value)
    {
        StringBuilder? builder = null;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (!char.IsControl(character) ||
                character is '\r' or '\n' or '\t')
            {
                builder?.Append(character);
                continue;
            }

            builder ??= new StringBuilder(value.Length).Append(value, 0, index);
            builder.Append(' ');
        }

        return builder?.ToString() ?? value;
    }
}

internal sealed class CursorWorkspaceResolver
{
    private readonly WorkspaceNormalizer _normalizer;
    private readonly CursorJsonReader _json;

    public CursorWorkspaceResolver(
        WorkspaceNormalizer normalizer,
        CursorJsonReader json)
    {
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _json = json ?? throw new ArgumentNullException(nameof(json));
    }

    public WorkspaceContext ResolveTranscriptWorkspace(
        string projectKey,
        IEnumerable<JsonElement> rows)
    {
        string? decoded = _normalizer.DecodeCursorProjectName(projectKey) ??
            DecodeCurrentProjectKey(projectKey);
        if (!string.IsNullOrWhiteSpace(decoded))
        {
            return _normalizer.FromPath(decoded);
        }

        IEnumerable<string> candidates = rows
            .SelectMany(_json.WorkspacePaths)
            .Where(IsPlausibleWorkspacePath);
        return _normalizer.FromRoots(candidates);
    }

    public WorkspaceContext ResolveDesktopWorkspace(
        string? path,
        bool isEmptyWindow = false)
    {
        if (isEmptyWindow)
        {
            return WorkspaceContext.NoWorkspace;
        }

        return _normalizer.FromPath(path);
    }

    private string? DecodeCurrentProjectKey(string encoded)
    {
        if (encoded.Length < 3 ||
            !char.IsAsciiLetter(encoded[0]) ||
            encoded[1] != '-')
        {
            return null;
        }

        string remainder = encoded[2..].TrimStart('-');
        if (remainder.Length == 0)
        {
            return null;
        }

        string root = $"{encoded[0]}:{Path.DirectorySeparatorChar}";
        return DecodeExistingSegments(root, remainder, depth: 0);
    }

    private string? DecodeExistingSegments(
        string current,
        string remainder,
        int depth)
    {
        if (depth > 24)
        {
            return null;
        }

        List<int> boundaries = [];
        for (int index = 0; index < remainder.Length; index++)
        {
            if (remainder[index] == '-')
            {
                boundaries.Add(index);
            }
        }

        boundaries.Add(remainder.Length);
        foreach (int boundary in boundaries.OrderDescending())
        {
            string segment = remainder[..boundary];
            if (segment.Length == 0)
            {
                continue;
            }

            string candidate = Path.Combine(current, segment);
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            if (boundary == remainder.Length)
            {
                return Path.GetFullPath(candidate);
            }

            string? nested = DecodeExistingSegments(
                candidate,
                remainder[(boundary + 1)..],
                depth + 1);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private bool IsPlausibleWorkspacePath(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.IsFile)
        {
            return true;
        }

        return Path.IsPathRooted(value);
    }
}
