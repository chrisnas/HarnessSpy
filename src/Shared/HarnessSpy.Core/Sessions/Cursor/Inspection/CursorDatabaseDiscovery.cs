using System.Text.Json;

namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public sealed class CursorDatabaseDiscovery
{
    private const long MaximumManifestBytes = 1024 * 1024;

    private readonly string _userDataRoot;
    private readonly string _globalDatabasePath;
    private readonly string _workspaceStorageRoot;
    private readonly WorkspaceNormalizer _workspaceNormalizer = new();

    public CursorDatabaseDiscovery()
        : this(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData))
    {
    }

    public CursorDatabaseDiscovery(string applicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationData);

        _userDataRoot = Path.GetFullPath(Path.Combine(
            applicationData,
            "Cursor",
            "User"));
        _globalDatabasePath = Path.Combine(
            _userDataRoot,
            "globalStorage",
            "state.vscdb");
        _workspaceStorageRoot = Path.Combine(
            _userDataRoot,
            "workspaceStorage");
    }

    public string GlobalDatabasePath => _globalDatabasePath;

    public string WorkspaceStorageRoot => _workspaceStorageRoot;

    public CursorDatabaseDescriptor CreateCustomDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        return new CursorDatabaseDescriptor(
            databasePath,
            CursorDatabaseKind.Custom);
    }

    public async Task<IReadOnlyList<CursorDatabaseDescriptor>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<CursorDatabaseDescriptor> databases = [];

        string globalStorage = Path.GetDirectoryName(_globalDatabasePath)!;
        if (IsReadableDirectory(globalStorage) &&
            IsReadableRegularFile(_globalDatabasePath))
        {
            databases.Add(new CursorDatabaseDescriptor(
                _globalDatabasePath,
                CursorDatabaseKind.Global));
        }

        if (!IsReadableDirectory(_workspaceStorageRoot))
        {
            return databases;
        }

        IReadOnlyList<DirectoryInfo> workspaceDirectories =
            EnumerateWorkspaceDirectories();
        foreach (DirectoryInfo directory in workspaceDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsReadableDirectory(directory.FullName))
            {
                continue;
            }

            string databasePath = Path.Combine(
                directory.FullName,
                "state.vscdb");
            if (!IsReadableRegularFile(databasePath))
            {
                continue;
            }

            (string? manifestPath, string? workspacePath) =
                await ReadWorkspaceManifestAsync(
                    directory.FullName,
                    cancellationToken).ConfigureAwait(false);
            databases.Add(new CursorDatabaseDescriptor(
                databasePath,
                CursorDatabaseKind.Workspace,
                directory.Name,
                manifestPath,
                workspacePath));
        }

        return databases;
    }

    public async Task<IReadOnlyList<CursorWorkspaceEvidence>>
        DiscoverWorkspaceEvidenceAsync(
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsReadableDirectory(_workspaceStorageRoot))
        {
            return [];
        }

        List<CursorWorkspaceEvidence> workspaces = [];
        foreach (DirectoryInfo directory in EnumerateWorkspaceDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsReadableDirectory(directory.FullName))
            {
                continue;
            }

            (string? manifestPath, string? workspacePath) =
                await ReadWorkspaceManifestAsync(
                    directory.FullName,
                    cancellationToken).ConfigureAwait(false);
            if (manifestPath is null)
            {
                continue;
            }

            string databasePath = Path.Combine(
                directory.FullName,
                "state.vscdb");
            workspaces.Add(new CursorWorkspaceEvidence(
                directory.Name,
                manifestPath,
                workspacePath,
                IsReadableRegularFile(databasePath)
                    ? databasePath
                    : null));
        }

        return workspaces;
    }

    private IReadOnlyList<DirectoryInfo> EnumerateWorkspaceDirectories()
    {
        try
        {
            return new DirectoryInfo(_workspaceStorageRoot)
                .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                .Where(directory =>
                    !directory.Attributes.HasFlag(
                        FileAttributes.ReparsePoint))
                .OrderBy(
                    directory => directory.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return [];
        }
    }

    private async Task<(string? ManifestPath, string? WorkspacePath)>
        ReadWorkspaceManifestAsync(
            string workspaceDirectory,
            CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(
            workspaceDirectory,
            "workspace.json");
        if (!IsReadableRegularFile(manifestPath))
        {
            return (null, null);
        }

        try
        {
            FileInfo manifest = new(manifestPath);
            if (manifest.Length > MaximumManifestBytes)
            {
                return (manifestPath, null);
            }

            await using FileStream stream = OpenRead(manifestPath);
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return (
                manifestPath,
                WorkspacePath(document.RootElement));
        }
        catch (Exception exception) when (IsRecoverable(exception) ||
            exception is JsonException)
        {
            return (manifestPath, null);
        }
    }

    private string? WorkspacePath(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string propertyName in new[]
                 {
                     "folder", "workspace", "workspacePath"
                 })
        {
            if (!root.TryGetProperty(
                    propertyName,
                    out JsonElement value) ||
                value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? path = value.GetString();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            return _workspaceNormalizer
                .FromPath(path)
                .DisplayRoots
                .FirstOrDefault() ??
                path;
        }

        return null;
    }

    private bool IsReadableRegularFile(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            using FileStream stream = OpenRead(path);
            return stream.CanRead;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    private bool IsReadableDirectory(string path)
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

    private FileStream OpenRead(string path) =>
        new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous |
                    FileOptions.SequentialScan
            });

    private bool IsRecoverable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
        NotSupportedException or System.Security.SecurityException;
}
