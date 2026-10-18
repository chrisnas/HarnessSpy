using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;

namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public sealed class CursorTranscriptEvidenceDiscovery
{
    private readonly SessionDiscoveryContext _context;
    private readonly WorkspaceNormalizer _workspaceNormalizer = new();
    private readonly string _projectsRoot;

    public CursorTranscriptEvidenceDiscovery(SessionDiscoveryContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _projectsRoot = Path.GetFullPath(Path.Combine(
            context.UserProfile,
            ".cursor",
            "projects"));
    }

    public Task<IReadOnlyList<SessionCatalogEntry>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_projectsRoot))
        {
            return Task.FromResult<IReadOnlyList<SessionCatalogEntry>>([]);
        }

        int remainingDirectories = Math.Max(
            0,
            _context.Limits.MaximumDirectories);
        int remainingFiles = Math.Max(0, _context.Limits.MaximumFiles);
        Dictionary<string, SessionCatalogEntry> sessions =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (DirectoryInfo project in Directories(_projectsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remainingDirectories-- <= 0)
            {
                break;
            }

            string transcripts = Path.Combine(
                project.FullName,
                "agent-transcripts");
            if (!ReadableDirectory(transcripts))
            {
                continue;
            }

            foreach (FileInfo file in JsonLines(transcripts))
            {
                if (remainingFiles-- <= 0)
                {
                    break;
                }

                Add(
                    sessions,
                    project.Name,
                    Path.GetFileNameWithoutExtension(file.Name),
                    file);
            }

            foreach (DirectoryInfo sessionDirectory in Directories(
                         transcripts))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (remainingDirectories-- <= 0 ||
                    remainingFiles <= 0)
                {
                    break;
                }

                FileInfo? main = JsonLines(sessionDirectory.FullName)
                    .FirstOrDefault(file => string.Equals(
                        Path.GetFileNameWithoutExtension(file.Name),
                        sessionDirectory.Name,
                        StringComparison.OrdinalIgnoreCase));
                if (main is null)
                {
                    continue;
                }

                remainingFiles--;
                Add(
                    sessions,
                    project.Name,
                    sessionDirectory.Name,
                    main);
            }
        }

        return Task.FromResult<IReadOnlyList<SessionCatalogEntry>>(
            sessions.Values.ToArray());
    }

    private void Add(
        Dictionary<string, SessionCatalogEntry> sessions,
        string projectKey,
        string nativeSessionId,
        FileInfo file)
    {
        if (string.IsNullOrWhiteSpace(nativeSessionId) ||
            !ReadableFile(file))
        {
            return;
        }

        string identity = projectKey + "\0" + nativeSessionId;
        WorkspaceContext workspace = Workspace(projectKey);
        sessions[identity] = new SessionCatalogEntry
        {
            CatalogSessionId =
                $"cursor:{nativeSessionId}:project:{projectKey}",
            NativeSessionId = nativeSessionId,
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Workspace = workspace,
            Title = nativeSessionId,
            LastActivityAtUtc = file.LastWriteTimeUtc,
            Files =
            [
                new SessionFileBinding(
                    file.FullName,
                    SessionSourceKind.CursorTranscriptJsonl,
                    DialectIds.CursorTranscript,
                    TranscriptFileRole.Main,
                    file.LastWriteTimeUtc,
                    file.Length)
            ]
        };
    }

    private WorkspaceContext Workspace(string projectKey)
    {
        string? decoded = _workspaceNormalizer.DecodeCursorProjectName(
            projectKey);
        return decoded is null
            ? WorkspaceContext.Unknown
            : _workspaceNormalizer.FromPath(decoded);
    }

    private IReadOnlyList<DirectoryInfo> Directories(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                .Where(directory =>
                    !directory.Attributes.HasFlag(
                        FileAttributes.ReparsePoint))
                .OrderBy(
                    directory => directory.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (Recoverable(exception))
        {
            return [];
        }
    }

    private IReadOnlyList<FileInfo> JsonLines(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly)
                .Where(file =>
                    !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderBy(
                    file => file.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (Recoverable(exception))
        {
            return [];
        }
    }

    private bool ReadableDirectory(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) &&
                !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (Recoverable(exception))
        {
            return false;
        }
    }

    private bool ReadableFile(FileInfo file)
    {
        try
        {
            return file.Exists &&
                !file.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (Recoverable(exception))
        {
            return false;
        }
    }

    private bool Recoverable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
        NotSupportedException or System.Security.SecurityException;
}
