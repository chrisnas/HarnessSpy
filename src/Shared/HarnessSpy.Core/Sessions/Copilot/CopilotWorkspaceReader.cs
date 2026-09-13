using System.Globalization;
using HarnessSpy.Core.Models;
using YamlDotNet.Serialization;

namespace HarnessSpy.Core.Sessions.Copilot;

internal sealed class CopilotWorkspaceSnapshot
{
    public static CopilotWorkspaceSnapshot Empty { get; } = new();

    public string? Id { get; init; }

    public string? Cwd { get; init; }

    public string? GitRoot { get; init; }

    public string? Repository { get; init; }

    public string? Branch { get; init; }

    public string? Name { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }

    public string? ClientName { get; init; }

    public string? HostType { get; init; }

    public SessionSourceProvenance? Provenance { get; init; }

    public SessionFileBinding? File { get; init; }

    public IReadOnlyDictionary<string, string?> Metadata { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);
}

internal sealed class CopilotWorkspaceReadResult
{
    public CopilotWorkspaceReadResult(
        CopilotWorkspaceSnapshot snapshot,
        bool isComplete,
        IReadOnlyList<string> warnings)
    {
        Snapshot = snapshot;
        IsComplete = isComplete;
        Warnings = warnings;
    }

    public CopilotWorkspaceSnapshot Snapshot { get; }

    public bool IsComplete { get; }

    public IReadOnlyList<string> Warnings { get; }
}

internal sealed class CopilotWorkspaceReader
{
    private readonly SessionDiscoveryContext _context;
    private readonly CopilotFileSystemGuard _fileSystemGuard;
    private readonly CopilotTimestampParser _timestampParser;
    private readonly CopilotUtf8Decoder _decoder;
    private readonly IDeserializer _deserializer;

    public CopilotWorkspaceReader(
        SessionDiscoveryContext context,
        CopilotFileSystemGuard fileSystemGuard)
    {
        _context = context;
        _fileSystemGuard = fileSystemGuard;
        _timestampParser = new CopilotTimestampParser();
        _decoder = new CopilotUtf8Decoder();
        _deserializer = new DeserializerBuilder().Build();
    }

    public async Task<CopilotWorkspaceReadResult> ReadAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new CopilotWorkspaceReadResult(
                CopilotWorkspaceSnapshot.Empty,
                true,
                []);
        }

        FileInfo file = new(path);
        if (!_fileSystemGuard.IsSafeFile(file, out string? reason))
        {
            return new CopilotWorkspaceReadResult(
                CopilotWorkspaceSnapshot.Empty,
                false,
                [$"Skipped Copilot workspace metadata '{path}': {reason}"]);
        }

        long configuredFileLimit = Math.Max(0, _context.Limits.MaximumFileBytes);
        long workspaceLimit = Math.Min(configuredFileLimit, int.MaxValue);
        long snapshotLength = file.Length;
        if (snapshotLength > workspaceLimit || snapshotLength > int.MaxValue)
        {
            return new CopilotWorkspaceReadResult(
                CopilotWorkspaceSnapshot.Empty,
                false,
                [
                    $"Skipped Copilot workspace metadata '{path}' because its " +
                    $"{snapshotLength} bytes exceed the {workspaceLimit}-byte limit."
                ]);
        }

        byte[] bytes = new byte[(int)snapshotLength];
        int totalRead = 0;
        await using (FileStream stream = new(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (!_fileSystemGuard.IsSafeFile(file, out string? postOpenReason))
            {
                return new CopilotWorkspaceReadResult(
                    CopilotWorkspaceSnapshot.Empty,
                    false,
                    [
                        $"Skipped Copilot workspace metadata '{path}' after it " +
                        $"changed while being opened: {postOpenReason}"
                    ]);
            }

            while (totalRead < bytes.Length)
            {
                int count = await stream.ReadAsync(
                    bytes.AsMemory(totalRead),
                    cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                totalRead += count;
            }
        }

        List<string> warnings = [];
        bool isComplete = totalRead == bytes.Length;
        if (!isComplete)
        {
            warnings.Add(
                $"Copilot workspace metadata '{path}' changed while it was read; " +
                "the available snapshot was used.");
        }

        ReadOnlySpan<byte> snapshotBytes = bytes.AsSpan(0, totalRead);
        if (HasOversizedLine(snapshotBytes, _context.Limits.MaximumLineBytes))
        {
            return new CopilotWorkspaceReadResult(
                CopilotWorkspaceSnapshot.Empty,
                false,
                [
                    $"Skipped Copilot workspace metadata '{path}' because it " +
                    $"contains a line larger than " +
                    $"{_context.Limits.MaximumLineBytes} bytes."
                ]);
        }

        string rawContent = _decoder.Decode(
            snapshotBytes,
            out bool hadInvalidEncoding);
        if (hadInvalidEncoding)
        {
            warnings.Add(
                $"Copilot workspace metadata '{path}' contains invalid UTF-8; " +
                "replacement characters were used.");
            isComplete = false;
        }

        SessionSourceProvenance provenance = new(
            SessionSourceKind.CopilotWorkspaceYaml,
            file.FullName,
            "yaml",
            rawContent);
        SessionFileBinding binding = new(
            file.FullName,
            SessionSourceKind.CopilotWorkspaceYaml,
            DialectIds.CopilotCliTranscript,
            TranscriptFileRole.Main,
            new DateTimeOffset(
                DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc)),
            snapshotLength);

        Dictionary<object, object?> values;
        try
        {
            values = _deserializer.Deserialize<Dictionary<object, object?>>(rawContent) ??
                new Dictionary<object, object?>();
        }
        catch (Exception exception) when (
            exception is YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            warnings.Add(
                $"Could not parse Copilot workspace metadata '{path}': " +
                exception.Message);
            return new CopilotWorkspaceReadResult(
                new CopilotWorkspaceSnapshot
                {
                    Provenance = provenance,
                    File = binding
                },
                false,
                warnings);
        }

        string? id = Scalar(values, "id", "session_id", "sessionId");
        string? cwd = Scalar(values, "cwd", "working_directory", "workingDirectory");
        string? gitRoot = Scalar(values, "git_root", "gitRoot");
        string? repository = Scalar(values, "repository", "repository_name", "repositoryName");
        string? branch = Scalar(values, "branch", "git_branch", "gitBranch");
        string? name = Scalar(values, "name", "title");
        string? clientName = Scalar(values, "client_name", "clientName");
        string? hostType = Scalar(values, "host_type", "hostType");
        object? createdAt = Value(values, "created_at", "createdAt", "creation_time");
        object? updatedAt = Value(values, "updated_at", "updatedAt", "update_time");

        Dictionary<string, string?> metadata =
            new(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["cwd"] = cwd,
                ["git_root"] = gitRoot,
                ["repository"] = repository,
                ["branch"] = branch,
                ["name"] = name,
                ["client_name"] = clientName,
                ["host_type"] = hostType,
                ["created_at"] = ScalarValue(createdAt),
                ["updated_at"] = ScalarValue(updatedAt),
                ["workspace.id"] = id,
                ["workspace.cwd"] = cwd,
                ["workspace.git_root"] = gitRoot,
                ["workspace.repository"] = repository,
                ["workspace.branch"] = branch,
                ["workspace.name"] = name,
                ["workspace.client_name"] = clientName,
                ["workspace.host_type"] = hostType,
                ["workspace.created_at"] = ScalarValue(createdAt),
                ["workspace.updated_at"] = ScalarValue(updatedAt)
            };

        return new CopilotWorkspaceReadResult(
            new CopilotWorkspaceSnapshot
            {
                Id = id,
                Cwd = cwd,
                GitRoot = gitRoot,
                Repository = repository,
                Branch = branch,
                Name = name,
                CreatedAtUtc = _timestampParser.Parse(createdAt),
                UpdatedAtUtc = _timestampParser.Parse(updatedAt),
                ClientName = clientName,
                HostType = hostType,
                Provenance = provenance,
                File = binding,
                Metadata = metadata
            },
            isComplete,
            warnings);
    }

    private static bool HasOversizedLine(
        ReadOnlySpan<byte> bytes,
        int maximumLineBytes)
    {
        int limit = Math.Max(0, maximumLineBytes);
        int currentLength = 0;
        foreach (byte value in bytes)
        {
            if (value == (byte)'\n')
            {
                currentLength = 0;
                continue;
            }

            currentLength++;
            if (currentLength > limit)
            {
                return true;
            }
        }

        return false;
    }

    private static object? Value(
        IReadOnlyDictionary<object, object?> values,
        params string[] keys)
    {
        foreach ((object key, object? value) in values)
        {
            string keyText = Convert.ToString(
                key,
                CultureInfo.InvariantCulture) ?? string.Empty;
            if (keys.Any(candidate => keyText.Equals(
                candidate,
                StringComparison.OrdinalIgnoreCase)))
            {
                return value;
            }
        }

        return null;
    }

    private static string? Scalar(
        IReadOnlyDictionary<object, object?> values,
        params string[] keys)
    {
        object? value = Value(values, keys);
        if (value is IReadOnlyDictionary<object, object?> readOnlyNested)
        {
            return Scalar(readOnlyNested, "full_name", "fullName", "name", "slug");
        }

        if (value is IDictionary<object, object?> nested)
        {
            return Scalar(
                new Dictionary<object, object?>(nested),
                "full_name",
                "fullName",
                "name",
                "slug");
        }

        return ScalarValue(value);
    }

    private static string? ScalarValue(object? value)
    {
        if (value is null ||
            value is System.Collections.IDictionary ||
            value is System.Collections.IEnumerable and not string)
        {
            return null;
        }

        string? text = value switch
        {
            IFormattable formattable =>
                formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
