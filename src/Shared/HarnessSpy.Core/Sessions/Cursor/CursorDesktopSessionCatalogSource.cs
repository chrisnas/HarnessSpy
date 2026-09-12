using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Cursor;

/// <summary>
/// Reads Cursor Desktop's internal conversation stores without mutating them.
/// The SQLite schema is capability-probed because it is not a public contract.
/// </summary>
public sealed class CursorDesktopSessionCatalogSource : ISessionCatalogSource
{
    private readonly SessionDiscoveryContext _context;
    private readonly CursorDesktopSqliteReader _reader;
    private readonly IReadOnlyList<string> _watchRoots;

    public CursorDesktopSessionCatalogSource()
        : this(new SessionDiscoveryContext())
    {
    }

    public CursorDesktopSessionCatalogSource(SessionDiscoveryContext context)
        : this(context, new WorkspaceNormalizer())
    {
    }

    public CursorDesktopSessionCatalogSource(
        SessionDiscoveryContext context,
        WorkspaceNormalizer workspaceNormalizer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workspaceNormalizer);

        _context = context;
        CursorFileSystemInspector fileSystem = new();
        CursorJsonReader json = new();
        CursorIdentityBuilder identity = new();
        string userDataRoot = Path.GetFullPath(Path.Combine(
            context.ApplicationData,
            "Cursor",
            "User"));
        string globalDatabasePath = Path.Combine(
            userDataRoot,
            "globalStorage",
            "state.vscdb");
        string workspaceStorageRoot = Path.Combine(
            userDataRoot,
            "workspaceStorage");
        _watchRoots =
        [
            Path.GetDirectoryName(globalDatabasePath)!,
            workspaceStorageRoot
        ];
        _reader = new CursorDesktopSqliteReader(
            context,
            workspaceNormalizer,
            fileSystem,
            json,
            identity,
            globalDatabasePath,
            workspaceStorageRoot);
    }

    public string Name => "Cursor Desktop";

    public HookProvider Provider => HookProvider.Cursor;

    public IReadOnlyList<string> WatchRoots => _watchRoots;

    public Task<SessionCatalogScanResult> ScanAsync(
        CancellationToken cancellationToken) =>
        ScanAsync(
            readBubbles: true,
            progress: null,
            cancellationToken);

    // When <paramref name="readBubbles"/> is false the per-message blobs are
    // skipped, producing a fast "skeleton" scan (composer id, title, workspace,
    // lifecycle) with the same catalog identities as the full scan, so the two
    // reconcile onto the same tree nodes.
    internal async Task<SessionCatalogScanResult> ScanAsync(
        bool readBubbles,
        IProgress<SessionCatalogEntry>? progress,
        CancellationToken cancellationToken)
    {
        CursorDiscoveryGuard guard = new(_context.Limits);
        CursorWarningCollector warnings = new();
        IReadOnlyList<SessionCatalogEntry> sessions = await _reader.ReadAsync(
            guard,
            warnings,
            cancellationToken,
            readBubbles,
            progress).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new SessionCatalogScanResult(
            sessions,
            guard.IsComplete,
            warnings.Snapshot());
    }
}
