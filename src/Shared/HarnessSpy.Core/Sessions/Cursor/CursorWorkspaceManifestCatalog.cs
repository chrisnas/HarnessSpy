using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Cursor;

internal sealed record CursorKnownWorkspaceScanResult(
    IReadOnlyList<WorkspaceContext> Workspaces,
    bool IsComplete,
    IReadOnlyList<string> Warnings);

// Reads every workspace manifest independently from its state.vscdb. Cursor
// can retain a useful workspace.json after the per-workspace database has been
// removed, and those paths are still valuable inference candidates.
internal sealed class CursorWorkspaceManifestCatalog
{
    private readonly SessionDiscoveryContext _context;
    private readonly WorkspaceNormalizer _normalizer;
    private readonly CursorFileSystemInspector _fileSystem;
    private readonly CursorJsonReader _json;
    private readonly string _workspaceStorageRoot;

    public CursorWorkspaceManifestCatalog(
        SessionDiscoveryContext context,
        WorkspaceNormalizer normalizer)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _normalizer = normalizer ??
            throw new ArgumentNullException(nameof(normalizer));
        _fileSystem = new CursorFileSystemInspector();
        _json = new CursorJsonReader();
        _workspaceStorageRoot = Path.GetFullPath(Path.Combine(
            context.ApplicationData,
            "Cursor",
            "User",
            "workspaceStorage"));
    }

    public async Task<CursorKnownWorkspaceScanResult> ScanAsync(
        CancellationToken cancellationToken)
    {
        CursorDiscoveryGuard guard = new(_context.Limits);
        CursorWarningCollector warnings = new();
        if (!Directory.Exists(_workspaceStorageRoot))
        {
            return new CursorKnownWorkspaceScanResult([], true, []);
        }

        if (!_fileSystem.IsReadableDirectory(_workspaceStorageRoot) ||
            !guard.TryVisitDirectory(_workspaceStorageRoot, warnings))
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Skipped unsafe Cursor workspace storage " +
                $"'{_workspaceStorageRoot}'.");
            return new CursorKnownWorkspaceScanResult(
                [],
                guard.IsComplete,
                warnings.Snapshot());
        }

        IReadOnlyList<DirectoryInfo> directories;
        try
        {
            directories = new DirectoryInfo(_workspaceStorageRoot)
                .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                .Where(directory => !directory.Attributes.HasFlag(
                    FileAttributes.ReparsePoint))
                .OrderBy(
                    directory => directory.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (_fileSystem.IsRecoverable(exception))
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Could not enumerate Cursor workspace manifests in " +
                $"'{_workspaceStorageRoot}': {exception.Message}");
            return new CursorKnownWorkspaceScanResult(
                [],
                false,
                warnings.Snapshot());
        }

        Dictionary<string, WorkspaceContext> workspaces =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (DirectoryInfo directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!guard.TryVisitDirectory(directory.FullName, warnings))
            {
                break;
            }

            string path = Path.Combine(directory.FullName, "workspace.json");
            if (!File.Exists(path) ||
                !_fileSystem.IsReadableRegularFile(path))
            {
                continue;
            }

            if (!guard.TryVisitFile(path, warnings))
            {
                break;
            }

            WorkspaceContext? workspace = await ReadWorkspaceAsync(
                path,
                guard,
                warnings,
                cancellationToken).ConfigureAwait(false);
            if (workspace?.Kind == WorkspaceContextKind.Normal &&
                workspace.DisplayRoots.Count == 1)
            {
                workspaces[workspace.Key] = workspace;
            }
        }

        return new CursorKnownWorkspaceScanResult(
            workspaces.Values
                .OrderBy(
                    static workspace => workspace.DisplayName,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            guard.IsComplete,
            warnings.Snapshot());
    }

    private async Task<WorkspaceContext?> ReadWorkspaceAsync(
        string path,
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        CancellationToken cancellationToken)
    {
        if (!_fileSystem.TryGetFileInfo(
            path,
            out long length,
            out _,
            out string? inspectError))
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Could not inspect Cursor workspace manifest '{path}': " +
                inspectError);
            return null;
        }

        long maximumBytes = Math.Min(
            Math.Max(0, _context.Limits.MaximumFileBytes),
            Math.Max(1, _context.Limits.MaximumLineBytes));
        if (length > maximumBytes)
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Skipped Cursor workspace manifest '{path}' because its " +
                $"{length} bytes exceed the {maximumBytes}-byte limit.");
            return null;
        }

        try
        {
            string raw = await File.ReadAllTextAsync(
                path,
                cancellationToken).ConfigureAwait(false);
            if (!_json.TryParse(
                    raw,
                    out JsonDocument? document,
                    out string? error) ||
                document is null)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Skipped malformed Cursor workspace manifest '{path}': " +
                    error);
                return null;
            }

            using (document)
            {
                string? workspacePath = _json.String(
                    document.RootElement,
                    "folder",
                    "workspace",
                    "workspacePath");
                return string.IsNullOrWhiteSpace(workspacePath)
                    ? null
                    : _normalizer.FromPath(workspacePath);
            }
        }
        catch (Exception exception) when (_fileSystem.IsRecoverable(exception))
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Could not read Cursor workspace manifest '{path}': " +
                exception.Message);
            return null;
        }
    }
}
