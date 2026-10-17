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

    public string? Summary { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }

    public string? ClientName { get; init; }

    public string? HostType { get; init; }

    public bool? UserNamed { get; init; }

    public long? SummaryCount { get; init; }

    public long? ForkCount { get; init; }

    public bool? RemoteSteerable { get; init; }

    public string? MissionControlTaskId { get; init; }

    public string? MissionControlSessionId { get; init; }

    public string? MissionControlLastEventId { get; init; }

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
            RawSource: new SessionRawSourceReference(
                SessionRawSourceKind.File,
                file.FullName,
                SourceLastWriteTimeUtc: new DateTimeOffset(
                    DateTime.SpecifyKind(
                        file.LastWriteTimeUtc,
                        DateTimeKind.Utc)),
                SourceLength: snapshotLength));
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
        string? summary = Scalar(values, "summary");
        string? clientName = Scalar(values, "client_name", "clientName");
        string? hostType = Scalar(values, "host_type", "hostType");
        bool? userNamed = Boolean(values, "user_named", "userNamed");
        long? summaryCount = Integer(values, "summary_count", "summaryCount");
        long? forkCount = Integer(values, "fork_count", "forkCount");
        bool? remoteSteerable = Boolean(
            values,
            "remote_steerable",
            "remoteSteerable");
        string? missionControlTaskId = Scalar(
            values,
            "mc_task_id",
            "mcTaskId");
        string? missionControlSessionId = Scalar(
            values,
            "mc_session_id",
            "mcSessionId");
        string? missionControlLastEventId = Scalar(
            values,
            "mc_last_event_id",
            "mcLastEventId");
        object? createdAt = Value(values, "created_at", "createdAt", "creation_time");
        object? updatedAt = Value(values, "updated_at", "updatedAt", "update_time");

        Dictionary<string, string?> metadata = PreserveScalarMetadata(values);
        SetMetadata(metadata, "id", id);
        SetMetadata(metadata, "cwd", cwd);
        SetMetadata(metadata, "git_root", gitRoot);
        SetMetadata(metadata, "repository", repository);
        SetMetadata(metadata, "branch", branch);
        SetMetadata(metadata, "name", name);
        SetMetadata(metadata, "summary", summary);
        SetMetadata(metadata, "client_name", clientName);
        SetMetadata(metadata, "host_type", hostType);
        SetMetadata(metadata, "user_named", ScalarValue(userNamed));
        SetMetadata(metadata, "summary_count", ScalarValue(summaryCount));
        SetMetadata(metadata, "fork_count", ScalarValue(forkCount));
        SetMetadata(
            metadata,
            "remote_steerable",
            ScalarValue(remoteSteerable));
        SetMetadata(metadata, "mc_task_id", missionControlTaskId);
        SetMetadata(metadata, "mc_session_id", missionControlSessionId);
        SetMetadata(metadata, "mc_last_event_id", missionControlLastEventId);
        SetMetadata(metadata, "created_at", ScalarValue(createdAt));
        SetMetadata(metadata, "updated_at", ScalarValue(updatedAt));

        return new CopilotWorkspaceReadResult(
            new CopilotWorkspaceSnapshot
            {
                Id = id,
                Cwd = cwd,
                GitRoot = gitRoot,
                Repository = repository,
                Branch = branch,
                Name = name,
                Summary = summary,
                CreatedAtUtc = _timestampParser.Parse(createdAt),
                UpdatedAtUtc = _timestampParser.Parse(updatedAt),
                ClientName = clientName,
                HostType = hostType,
                UserNamed = userNamed,
                SummaryCount = summaryCount,
                ForkCount = forkCount,
                RemoteSteerable = remoteSteerable,
                MissionControlTaskId = missionControlTaskId,
                MissionControlSessionId = missionControlSessionId,
                MissionControlLastEventId = missionControlLastEventId,
                Provenance = provenance,
                File = binding,
                Metadata = metadata
            },
            isComplete,
            warnings);
    }

    private static Dictionary<string, string?> PreserveScalarMetadata(
        IReadOnlyDictionary<object, object?> values)
    {
        Dictionary<string, string?> metadata = new(StringComparer.Ordinal);
        foreach ((object key, object? value) in values)
        {
            string? name = Convert.ToString(key, CultureInfo.InvariantCulture);
            string? scalar = ScalarValue(value);
            if (string.IsNullOrWhiteSpace(name) || scalar is null)
            {
                continue;
            }

            metadata[name] = scalar;
            metadata[$"workspace.{name}"] = scalar;
        }

        return metadata;
    }

    private static void SetMetadata(
        IDictionary<string, string?> metadata,
        string name,
        string? value)
    {
        metadata[name] = value;
        metadata[$"workspace.{name}"] = value;
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

    private static bool? Boolean(
        IReadOnlyDictionary<object, object?> values,
        params string[] keys) =>
        bool.TryParse(Scalar(values, keys), out bool value)
            ? value
            : null;

    private static long? Integer(
        IReadOnlyDictionary<object, object?> values,
        params string[] keys) =>
        long.TryParse(
            Scalar(values, keys),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long value)
                ? value
                : null;

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
