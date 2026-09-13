using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HarnessSpy.Core.Models;
using Microsoft.Data.Sqlite;

namespace HarnessSpy.Core.Sessions.Cursor;

internal sealed class CursorDesktopSqliteReader
{
    private const string DesktopDialect = "cursor-desktop-sqlite";
    private const string ComposerHeadersKey = "composer.composerHeaders";
    private const string ComposerDataKey = "composer.composerData";

    private readonly SessionDiscoveryContext _context;
    private readonly WorkspaceNormalizer _normalizer;
    private readonly CursorFileSystemInspector _fileSystem;
    private readonly CursorJsonReader _json;
    private readonly CursorWorkspaceResolver _workspaceResolver;
    private readonly CursorDesktopBubbleProjector _bubbleProjector;
    private readonly string _globalDatabasePath;
    private readonly string _workspaceStorageRoot;

    private sealed record CursorComposerHydrationInput(
        string ComposerId,
        CursorSqliteJsonRecord Record,
        JsonElement Composer,
        int DataPriority);

    private sealed record CursorRawBubble(
        string Key,
        string BubbleId,
        string RawContent);

    private sealed record CursorBubbleWorkItem(
        CursorComposerHydrationInput Input,
        IReadOnlyList<CursorRawBubble> Bubbles);

    private sealed record CursorHydrationResult(
        CursorComposerHydrationInput Input,
        CursorBubbleProjection Projection);

    public CursorDesktopSqliteReader(
        SessionDiscoveryContext context,
        WorkspaceNormalizer normalizer,
        CursorFileSystemInspector fileSystem,
        CursorJsonReader json,
        CursorIdentityBuilder identity,
        string globalDatabasePath,
        string workspaceStorageRoot)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _json = json ?? throw new ArgumentNullException(nameof(json));
        _workspaceResolver = new CursorWorkspaceResolver(_normalizer, _json);
        _bubbleProjector = new CursorDesktopBubbleProjector(_json, identity);
        _globalDatabasePath = globalDatabasePath;
        _workspaceStorageRoot = workspaceStorageRoot;
    }

    public async Task<IReadOnlyList<SessionCatalogEntry>> ReadAsync(
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        CancellationToken cancellationToken,
        bool readBubbles = true,
        IProgress<SessionCatalogEntry>? progress = null)
    {
        IReadOnlyList<CursorDesktopDatabase> databases =
            await DiscoverDatabasesAsync(
                guard,
                warnings,
                cancellationToken).ConfigureAwait(false);
        Dictionary<string, CursorWorkspaceManifest> manifests = databases
            .Where(database => database.Manifest is not null)
            .Select(database => database.Manifest!)
            .GroupBy(
                manifest => manifest.WorkspaceId,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CursorDesktopSessionBuilder> builders =
            new(StringComparer.Ordinal);
        Dictionary<string, CursorComposerRelationship> relationships =
            new(StringComparer.Ordinal);
        HashSet<string> selectedComposerIds = new(StringComparer.Ordinal);

        foreach (CursorDesktopDatabase database in databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (guard.DeadlineExceeded)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    "Cursor Desktop database discovery stopped after reaching its time limit.");
                break;
            }

            await ReadDatabaseAsync(
                database,
                manifests,
                builders,
                relationships,
                selectedComposerIds,
                readBubbles,
                progress,
                guard,
                warnings,
                cancellationToken).ConfigureAwait(false);
        }

        foreach ((string composerId, CursorComposerRelationship relationship)
                 in relationships)
        {
            if (builders.TryGetValue(
                composerId,
                out CursorDesktopSessionBuilder? builder))
            {
                builder.MarkRelationship(relationship);
            }
        }

        foreach (string selectedComposerId in selectedComposerIds)
        {
            if (builders.TryGetValue(
                selectedComposerId,
                out CursorDesktopSessionBuilder? builder))
            {
                builder.MarkSelected();
            }
        }

        HashSet<string> attachedChildren = new(StringComparer.Ordinal);
        foreach (CursorDesktopSessionBuilder child in builders.Values
            .Where(builder => builder.IsBackgroundChild)
            .OrderByDescending(builder => RelationshipDepth(
                builder.ComposerId,
                relationships))
            .ToArray())
        {
            CursorComposerRelationship relationship = child.Relationship!;
            if (HasRelationshipCycle(child.ComposerId, relationships))
            {
                warnings.Add(
                    $"Cursor composer relationship cycle includes '{child.ComposerId}'; " +
                    "the composer was retained as a recoverable catalog entry.");
                guard.MarkIncomplete();
                continue;
            }

            if (builders.TryGetValue(
                relationship.ParentComposerId,
                out CursorDesktopSessionBuilder? parent))
            {
                parent.AttachBackgroundChild(child);
                attachedChildren.Add(child.ComposerId);
            }
            else
            {
                warnings.Add(
                    $"Cursor subagent composer '{child.ComposerId}' references missing " +
                    $"parent '{relationship.ParentComposerId}'; it was retained separately.");
            }
        }

        return builders.Values
            .Where(builder => !attachedChildren.Contains(builder.ComposerId))
            // Cursor's global store keeps a record for every composer ever
            // opened; drop the empty "new chat" shells (no messages) here so
            // they never enter the merge/projection pipeline. Emptiness is
            // judged from the header, so the headers-only skeleton pass and the
            // full pass agree and nothing flashes in or out.
            .Where(builder => builder.HasContent)
            .Select(builder => builder.Build())
            .OrderBy(
                session => session.Workspace.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(session => session.LastActivityAtUtc)
            .ThenBy(session => session.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<CursorDesktopDatabase>> DiscoverDatabasesAsync(
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        CancellationToken cancellationToken)
    {
        List<CursorDesktopDatabase> databases = [];
        if (File.Exists(_globalDatabasePath))
        {
            if (_fileSystem.IsReadableRegularFile(_globalDatabasePath) &&
                guard.TryVisitFile(_globalDatabasePath, warnings))
            {
                databases.Add(new CursorDesktopDatabase(
                    Path.GetFullPath(_globalDatabasePath),
                    IsGlobal: true,
                    Manifest: null));
            }
            else
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Skipped unsafe Cursor global database '{_globalDatabasePath}'.");
            }
        }

        if (!Directory.Exists(_workspaceStorageRoot))
        {
            return databases;
        }

        if (!_fileSystem.IsReadableDirectory(_workspaceStorageRoot) ||
            !guard.TryVisitDirectory(_workspaceStorageRoot, warnings))
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Skipped unsafe Cursor workspace storage '{_workspaceStorageRoot}'.");
            return databases;
        }

        IReadOnlyList<DirectoryInfo> workspaceDirectories;
        try
        {
            workspaceDirectories = new DirectoryInfo(_workspaceStorageRoot)
                .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                .Where(directory => !directory.Attributes.HasFlag(
                    FileAttributes.ReparsePoint))
                .OrderBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (_fileSystem.IsRecoverable(exception))
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Could not enumerate Cursor workspace storage " +
                $"'{_workspaceStorageRoot}': {exception.Message}");
            return databases;
        }

        foreach (DirectoryInfo workspaceDirectory in workspaceDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!guard.TryVisitDirectory(workspaceDirectory.FullName, warnings))
            {
                break;
            }

            string databasePath = Path.Combine(
                workspaceDirectory.FullName,
                "state.vscdb");
            if (!File.Exists(databasePath) ||
                !_fileSystem.IsReadableRegularFile(databasePath))
            {
                continue;
            }

            if (!guard.TryVisitFile(databasePath, warnings))
            {
                break;
            }

            CursorWorkspaceManifest? manifest = await ReadManifestAsync(
                workspaceDirectory,
                databasePath,
                guard,
                warnings,
                cancellationToken).ConfigureAwait(false);
            databases.Add(new CursorDesktopDatabase(
                Path.GetFullPath(databasePath),
                IsGlobal: false,
                manifest));
        }

        return databases
            .OrderBy(database => database.IsGlobal)
            .ThenBy(database => database.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<CursorWorkspaceManifest?> ReadManifestAsync(
        DirectoryInfo workspaceDirectory,
        string databasePath,
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(
            workspaceDirectory.FullName,
            "workspace.json");
        if (!File.Exists(manifestPath))
        {
            return new CursorWorkspaceManifest(
                workspaceDirectory.Name,
                workspaceDirectory.FullName,
                databasePath,
                WorkspaceContext.Unknown,
                null);
        }

        if (!_fileSystem.TryGetFileInfo(
            manifestPath,
            out long length,
            out _,
            out string? inspectError))
        {
            warnings.Add(
                $"Could not inspect Cursor workspace manifest '{manifestPath}': " +
                inspectError);
            guard.MarkIncomplete();
            return null;
        }

        if (!guard.TryVisitFile(manifestPath, warnings))
        {
            return null;
        }

        long maximumBytes = Math.Min(
            Math.Max(0, _context.Limits.MaximumFileBytes),
            Math.Max(1, _context.Limits.MaximumLineBytes));
        if (length > maximumBytes)
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Skipped Cursor workspace manifest '{manifestPath}' because its " +
                $"{length} bytes exceed the {maximumBytes}-byte limit.");
            return null;
        }

        try
        {
            string raw = await File.ReadAllTextAsync(
                manifestPath,
                cancellationToken).ConfigureAwait(false);
            if (!_json.TryParse(raw, out JsonDocument? document, out string? error) ||
                document is null)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Skipped malformed Cursor workspace manifest '{manifestPath}': " +
                    error);
                return null;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                string? workspacePath = _json.String(
                    root,
                    "folder",
                    "workspace",
                    "workspacePath");
                WorkspaceContext workspace = ResolveManifestWorkspace(workspacePath);
                SessionSourceProvenance provenance = new(
                    SessionSourceKind.CursorDesktopSqlite,
                    manifestPath,
                    "cursor-workspace-manifest-json",
                    raw,
                    RecordId: workspaceDirectory.Name);
                return new CursorWorkspaceManifest(
                    workspaceDirectory.Name,
                    workspaceDirectory.FullName,
                    databasePath,
                    workspace,
                    provenance);
            }
        }
        catch (Exception exception) when (_fileSystem.IsRecoverable(exception))
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Could not read Cursor workspace manifest '{manifestPath}': " +
                exception.Message);
            return null;
        }
    }

    private async Task ReadDatabaseAsync(
        CursorDesktopDatabase database,
        IReadOnlyDictionary<string, CursorWorkspaceManifest> manifests,
        Dictionary<string, CursorDesktopSessionBuilder> builders,
        Dictionary<string, CursorComposerRelationship> relationships,
        HashSet<string> selectedComposerIds,
        bool readBubbles,
        IProgress<SessionCatalogEntry>? progress,
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        CancellationToken cancellationToken)
    {
        if (!_fileSystem.TryGetFileInfo(
            database.Path,
            out long databaseLength,
            out DateTimeOffset databaseLastWrite,
            out string? inspectError))
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Could not inspect Cursor database '{database.Path}': {inspectError}");
            return;
        }

        SqliteConnectionStringBuilder connectionString = new()
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 2
        };

        try
        {
            await using SqliteConnection connection = new(
                connectionString.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            bool hasDiskKv = await TableExistsAsync(
                connection,
                "cursorDiskKV",
                cancellationToken).ConfigureAwait(false);
            bool hasComposerHeaders = await TableExistsAsync(
                connection,
                "composerHeaders",
                cancellationToken).ConfigureAwait(false);
            bool hasItemTable = await TableExistsAsync(
                connection,
                "ItemTable",
                cancellationToken).ConfigureAwait(false);

            if (hasDiskKv && !await HasKeyValueColumnsAsync(
                connection,
                "cursorDiskKV",
                cancellationToken).ConfigureAwait(false))
            {
                hasDiskKv = false;
                guard.MarkIncomplete();
                warnings.Add(
                    $"Cursor cursorDiskKV table in '{database.Path}' does not expose " +
                    "the required key/value columns.");
            }

            if (hasItemTable && !await HasKeyValueColumnsAsync(
                connection,
                "ItemTable",
                cancellationToken).ConfigureAwait(false))
            {
                hasItemTable = false;
                guard.MarkIncomplete();
                warnings.Add(
                    $"Cursor ItemTable in '{database.Path}' does not expose the " +
                    "required key/value columns.");
            }

            if (!hasDiskKv && !hasComposerHeaders && !hasItemTable)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Cursor database '{database.Path}' contains none of the supported " +
                    "cursorDiskKV, composerHeaders, or ItemTable capabilities.");
                return;
            }

            Dictionary<string, CursorComposerHydrationInput> hydrationInputs =
                new(StringComparer.Ordinal);

            if (hasItemTable)
            {
                await ReadItemTableAsync(
                    connection,
                    database,
                    databaseLength,
                    databaseLastWrite,
                    manifests,
                    builders,
                    relationships,
                    selectedComposerIds,
                    hydrationInputs,
                    progress,
                    guard,
                    warnings,
                    cancellationToken).ConfigureAwait(false);
            }

            if (hasComposerHeaders)
            {
                await ReadComposerHeadersTableAsync(
                    connection,
                    database,
                    databaseLength,
                    databaseLastWrite,
                    manifests,
                    builders,
                    relationships,
                    progress,
                    guard,
                    warnings,
                    cancellationToken).ConfigureAwait(false);
            }

            if (hasDiskKv)
            {
                await ReadCursorDiskComposerDataAsync(
                    connection,
                    database,
                    databaseLength,
                    databaseLastWrite,
                    manifests,
                    builders,
                    relationships,
                    hydrationInputs,
                    progress,
                    guard,
                    warnings,
                    cancellationToken).ConfigureAwait(false);
            }

            if (readBubbles &&
                hasDiskKv &&
                hydrationInputs.Count > 0)
            {
                await HydrateComposersAsync(
                    connection,
                    database,
                    databaseLength,
                    databaseLastWrite,
                    manifests,
                    builders,
                    relationships,
                    hydrationInputs,
                    progress,
                    guard,
                    warnings,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SqliteException exception)
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Could not read Cursor database '{database.Path}': " +
                exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Cursor database '{database.Path}' returned invalid data: " +
                exception.Message);
        }
        catch (Exception exception) when (_fileSystem.IsRecoverable(exception))
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Could not access Cursor database '{database.Path}': " +
                exception.Message);
        }
    }

    private async Task ReadCursorDiskComposerDataAsync(
        SqliteConnection connection,
        CursorDesktopDatabase database,
        long databaseLength,
        DateTimeOffset databaseLastWrite,
        IReadOnlyDictionary<string, CursorWorkspaceManifest> manifests,
        Dictionary<string, CursorDesktopSessionBuilder> builders,
        Dictionary<string, CursorComposerRelationship> relationships,
        Dictionary<string, CursorComposerHydrationInput> hydrationInputs,
        IProgress<SessionCatalogEntry>? progress,
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CursorSqliteJsonRecord> records =
            await ReadBoundedKeyValuesAsync(
                connection,
                "cursorDiskKV",
                "key LIKE 'composerData:%'",
                [],
                Math.Max(0, _context.Limits.MaximumFiles),
                guard,
                warnings,
                cancellationToken).ConfigureAwait(false);

        await ProcessCursorDiskRecordsAsync(
            database,
            databaseLength,
            databaseLastWrite,
            manifests,
            builders,
            relationships,
            hydrationInputs,
            records,
            progress,
            guard,
            warnings,
            cancellationToken).ConfigureAwait(false);
    }

    private Task ProcessCursorDiskRecordsAsync(
        CursorDesktopDatabase database,
        long databaseLength,
        DateTimeOffset databaseLastWrite,
        IReadOnlyDictionary<string, CursorWorkspaceManifest> manifests,
        Dictionary<string, CursorDesktopSessionBuilder> builders,
        Dictionary<string, CursorComposerRelationship> relationships,
        Dictionary<string, CursorComposerHydrationInput> hydrationInputs,
        IReadOnlyList<CursorSqliteJsonRecord> records,
        IProgress<SessionCatalogEntry>? progress,
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        CancellationToken cancellationToken)
    {
        foreach (CursorSqliteJsonRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (guard.DeadlineExceeded)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Cursor composer scan time limit was reached in '{database.Path}'.");
                break;
            }

            string keyComposerId = record.Key["composerData:".Length..];
            ProcessComposerPayload(
                database,
                databaseLength,
                databaseLastWrite,
                manifests,
                builders,
                relationships,
                hydrationInputs,
                record,
                keyComposerId,
                progress,
                guard,
                warnings,
                cancellationToken);
        }

        return Task.CompletedTask;
    }

    private async Task ReadItemTableAsync(
        SqliteConnection connection,
        CursorDesktopDatabase database,
        long databaseLength,
        DateTimeOffset databaseLastWrite,
        IReadOnlyDictionary<string, CursorWorkspaceManifest> manifests,
        Dictionary<string, CursorDesktopSessionBuilder> builders,
        Dictionary<string, CursorComposerRelationship> relationships,
        HashSet<string> selectedComposerIds,
        Dictionary<string, CursorComposerHydrationInput> hydrationInputs,
        IProgress<SessionCatalogEntry>? progress,
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        CancellationToken cancellationToken)
    {
        List<SqliteParameter> parameters =
        [
            new("$headers", ComposerHeadersKey),
            new("$data", ComposerDataKey)
        ];
        IReadOnlyList<CursorSqliteJsonRecord> records =
            await ReadBoundedKeyValuesAsync(
                connection,
                "ItemTable",
                "key = $headers OR key = $data",
                parameters,
                maximumRows: 3,
                guard,
                warnings,
                cancellationToken).ConfigureAwait(false);

        foreach (CursorSqliteJsonRecord record in records.OrderBy(
                     record => string.Equals(
                         record.Key,
                         ComposerHeadersKey,
                         StringComparison.Ordinal)
                         ? 0
                         : 1))
        {
            if (string.Equals(
                record.Key,
                ComposerHeadersKey,
                StringComparison.Ordinal))
            {
                ProcessHeaderPayload(
                    database,
                    databaseLength,
                    databaseLastWrite,
                    manifests,
                    builders,
                    relationships,
                    record,
                    progress,
                    guard,
                    warnings);
                continue;
            }

            CollectSelectedComposerIds(record.Json, selectedComposerIds);
            ProcessComposerPayload(
                database,
                databaseLength,
                databaseLastWrite,
                manifests,
                builders,
                relationships,
                hydrationInputs,
                record,
                keyComposerId: null,
                progress,
                guard,
                warnings,
                cancellationToken);
        }
    }

    private async Task ReadComposerHeadersTableAsync(
        SqliteConnection connection,
        CursorDesktopDatabase database,
        long databaseLength,
        DateTimeOffset databaseLastWrite,
        IReadOnlyDictionary<string, CursorWorkspaceManifest> manifests,
        Dictionary<string, CursorDesktopSessionBuilder> builders,
        Dictionary<string, CursorComposerRelationship> relationships,
        IProgress<SessionCatalogEntry>? progress,
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        CancellationToken cancellationToken)
    {
        HashSet<string> columns = await ReadColumnsAsync(
            connection,
            "composerHeaders",
            cancellationToken).ConfigureAwait(false);
        string? valueColumn = columns.FirstOrDefault(column =>
            string.Equals(column, "value", StringComparison.OrdinalIgnoreCase));
        if (valueColumn is null)
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Cursor composerHeaders table in '{database.Path}' has no value column.");
            return;
        }

        string? idColumn = columns.FirstOrDefault(column =>
            string.Equals(column, "composerId", StringComparison.OrdinalIgnoreCase));
        int maximumRows = Math.Max(0, _context.Limits.MaximumFiles);
        long maximumValueBytes = MaximumValueBytes();
        string idExpression = idColumn is null
            ? "NULL"
            : QuoteIdentifier(idColumn);
        string valueIdentifier = QuoteIdentifier(valueColumn);
        string sql =
            $"SELECT {idExpression}, length(CAST({valueIdentifier} AS BLOB)), " +
            $"CASE WHEN length(CAST({valueIdentifier} AS BLOB)) <= $maximumBytes " +
            $"THEN {valueIdentifier} ELSE NULL END FROM \"composerHeaders\" " +
            "LIMIT $maximumRows";

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 2;
        command.Parameters.AddWithValue("$maximumBytes", maximumValueBytes);
        command.Parameters.AddWithValue(
            "$maximumRows",
            MaximumRowsWithSentinel(maximumRows));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);

        int count = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (guard.DeadlineExceeded)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Cursor composerHeaders scan time limit was reached in " +
                    $"'{database.Path}'.");
                break;
            }

            if (count++ >= maximumRows)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Cursor composerHeaders row limit {maximumRows} was reached in " +
                    $"'{database.Path}'.");
                break;
            }

            string? composerId = reader.IsDBNull(0)
                ? null
                : Convert.ToString(reader.GetValue(0));
            long valueBytes = reader.IsDBNull(1)
                ? -1
                : reader.GetInt64(1);
            if (reader.IsDBNull(2) ||
                valueBytes < 0 ||
                valueBytes > maximumValueBytes)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Skipped oversized Cursor composer header '{composerId}' in " +
                    $"'{database.Path}'.");
                continue;
            }

            string? raw = ValueText(reader.GetValue(2));
            if (string.IsNullOrWhiteSpace(raw))
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Skipped empty Cursor composer header '{composerId}' in " +
                    $"'{database.Path}'.");
                continue;
            }

            if (!_json.TryParse(raw, out JsonDocument? document, out string? error) ||
                document is null)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Skipped malformed Cursor composer header '{composerId}' in " +
                    $"'{database.Path}': {error}");
                continue;
            }

            using (document)
            {
                CursorSqliteJsonRecord record = new(
                    $"composerHeaders:{composerId}",
                    raw,
                    document.RootElement.Clone());
                ProcessHeaderPayload(
                    database,
                    databaseLength,
                    databaseLastWrite,
                    manifests,
                    builders,
                    relationships,
                    record,
                    progress,
                    guard,
                    warnings,
                    composerId);
            }
        }
    }

    private void ProcessComposerPayload(
        CursorDesktopDatabase database,
        long databaseLength,
        DateTimeOffset databaseLastWrite,
        IReadOnlyDictionary<string, CursorWorkspaceManifest> manifests,
        Dictionary<string, CursorDesktopSessionBuilder> builders,
        Dictionary<string, CursorComposerRelationship> relationships,
        Dictionary<string, CursorComposerHydrationInput> hydrationInputs,
        CursorSqliteJsonRecord record,
        string? keyComposerId,
        IProgress<SessionCatalogEntry>? progress,
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<JsonElement> composers = ComposerObjects(record.Json);
        if (composers.Count == 0 &&
            keyComposerId is not null &&
            record.Json.ValueKind == JsonValueKind.Object)
        {
            composers = [record.Json];
        }

        int maximumComposers = Math.Max(0, _context.Limits.MaximumFiles);
        if (composers.Count > maximumComposers)
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Cursor composer payload '{record.Key}' contains {composers.Count} " +
                $"records; only {maximumComposers} were read.");
        }

        foreach (JsonElement composer in composers.Take(maximumComposers))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (guard.DeadlineExceeded)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Cursor composer payload scan time limit was reached in " +
                    $"'{database.Path}'.");
                break;
            }

            string? composerId = _json.String(
                composer,
                "composerId",
                "composer_id",
                "id") ??
                (composers.Count == 1 ? keyComposerId : null);
            if (!_json.IsValidIdentity(composerId))
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Skipped Cursor composer with invalid identity from " +
                    $"'{database.Path}' key '{record.Key}'.");
                continue;
            }

            CursorDesktopComposerSnapshot snapshot = CreateSnapshot(
                database,
                databaseLength,
                databaseLastWrite,
                manifests,
                record,
                composer,
                composerId!,
                new CursorBubbleProjection([], [], null, null));
            CursorDesktopSessionBuilder builder = GetBuilder(
                builders,
                composerId!);
            builder.Add(snapshot);
            if (snapshot.Relationship is not null)
            {
                relationships[composerId!] = snapshot.Relationship;
            }

            if (snapshot.HasConversationHeaders)
            {
                CursorComposerHydrationInput hydrationInput = new(
                    composerId!,
                    record,
                    composer.Clone(),
                    snapshot.DataPriority);
                if (!hydrationInputs.TryGetValue(
                        composerId!,
                        out CursorComposerHydrationInput? current) ||
                    hydrationInput.DataPriority >= current.DataPriority)
                {
                    hydrationInputs[composerId!] = hydrationInput;
                }
            }

            if (progress is not null &&
                builder.HasContent &&
                !builder.IsBackgroundChild)
            {
                progress.Report(builder.Build());
            }
        }
    }

    private void ProcessHeaderPayload(
        CursorDesktopDatabase database,
        long databaseLength,
        DateTimeOffset databaseLastWrite,
        IReadOnlyDictionary<string, CursorWorkspaceManifest> manifests,
        Dictionary<string, CursorDesktopSessionBuilder> builders,
        Dictionary<string, CursorComposerRelationship> relationships,
        CursorSqliteJsonRecord record,
        IProgress<SessionCatalogEntry>? progress,
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        string? expectedComposerId = null)
    {
        IReadOnlyList<JsonElement> headers = HeaderObjects(record.Json);
        if (headers.Count == 0 && record.Json.ValueKind == JsonValueKind.Object)
        {
            headers = [record.Json];
        }

        int maximumHeaders = Math.Max(0, _context.Limits.MaximumFiles);
        if (headers.Count > maximumHeaders)
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Cursor header payload '{record.Key}' contains {headers.Count} " +
                $"records; only {maximumHeaders} were read.");
        }

        foreach (JsonElement header in headers.Take(maximumHeaders))
        {
            if (guard.DeadlineExceeded)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Cursor header payload scan time limit was reached in " +
                    $"'{database.Path}'.");
                break;
            }

            string? composerId = _json.String(
                header,
                "composerId",
                "composer_id",
                "id") ??
                expectedComposerId;
            if (!_json.IsValidIdentity(composerId))
            {
                warnings.Add(
                    $"Skipped Cursor composer header with invalid identity in " +
                    $"'{database.Path}'.");
                continue;
            }

            if (expectedComposerId is not null &&
                !string.Equals(
                    expectedComposerId,
                    composerId,
                    StringComparison.Ordinal))
            {
                warnings.Add(
                    $"Skipped Cursor composerHeaders row whose key identity " +
                    $"'{expectedComposerId}' disagrees with payload identity " +
                    $"'{composerId}'.");
                continue;
            }

            CursorDesktopComposerSnapshot snapshot = CreateSnapshot(
                database,
                databaseLength,
                databaseLastWrite,
                manifests,
                record,
                header,
                composerId!,
                new CursorBubbleProjection([], [], null, null));
            CursorDesktopSessionBuilder builder = GetBuilder(
                builders,
                composerId!);
            builder.Add(snapshot);
            if (snapshot.Relationship is not null)
            {
                relationships[composerId!] = snapshot.Relationship;
            }

            if (progress is not null &&
                builder.HasContent &&
                !builder.IsBackgroundChild)
            {
                progress.Report(builder.Build());
            }
        }
    }

    private CursorDesktopComposerSnapshot CreateSnapshot(
        CursorDesktopDatabase database,
        long databaseLength,
        DateTimeOffset databaseLastWrite,
        IReadOnlyDictionary<string, CursorWorkspaceManifest> manifests,
        CursorSqliteJsonRecord record,
        JsonElement composer,
        string composerId,
        CursorBubbleProjection projection)
    {
        (WorkspaceContext workspace, int priority, string? workspaceId) =
            ResolveWorkspace(database, manifests, composer);
        CursorComposerRelationship? relationship = Relationship(composer);
        SessionSourceProvenance composerProvenance = new(
            SessionSourceKind.CursorDesktopSqlite,
            database.Path,
            "cursor-desktop-composer-json",
            record.RawContent,
            RecordId: composerId,
            ParentRecordId: relationship?.ParentComposerId,
            DatabaseKey: record.Key);
        List<SessionSourceProvenance> sources =
        [
            composerProvenance,
            .. projection.Sources
        ];
        if (database.Manifest?.Provenance is not null)
        {
            sources.Add(database.Manifest.Provenance);
        }

        Dictionary<string, string?> metadata = new(StringComparer.Ordinal)
        {
            ["cursor.databasePath"] = database.Path,
            ["cursor.databaseKey"] = record.Key,
            ["cursor.workspaceId"] = workspaceId,
            ["cursor.parentComposerId"] = relationship?.ParentComposerId,
            ["cursor.subagentType"] = relationship?.SubagentTypeName,
            ["cursor.sideChatSeedTurnCount"] =
                relationship?.SideChatSeedTurnCount?.ToString()
        };

        return new CursorDesktopComposerSnapshot
        {
            ComposerId = composerId,
            Title = _json.String(composer, "name", "title", "subtitle"),
            CreatedAtUtc = _json.Timestamp(
                composer,
                "createdAt",
                "created_at",
                "creationTime"),
            LastUpdatedAtUtc = _json.Timestamp(
                composer,
                "lastUpdatedAt",
                "last_updated_at",
                "updatedAt"),
            Model = Model(composer) ?? projection.Model,
            Mode = _json.String(
                composer,
                "unifiedMode",
                "mode",
                "composerMode",
                "composer_mode") ?? projection.Mode,
            Workspace = workspace,
            WorkspacePriority = priority,
            HasConversationHeaders = HasConversationHeaders(composer),
            DataPriority = database.IsGlobal
                ? record.Key.StartsWith(
                    "composerData:",
                    StringComparison.Ordinal)
                    ? 100
                    : 90
                : record.Key == ComposerDataKey
                    ? 80
                    : 70,
            IsArchived = _json.Boolean(composer, "isArchived", "archived"),
            Relationship = relationship,
            Turns = projection.Turns,
            Files =
            [
                new SessionFileBinding(
                    database.Path,
                    SessionSourceKind.CursorDesktopSqlite,
                    DesktopDialect,
                    relationship is null
                        ? TranscriptFileRole.Main
                        : TranscriptFileRole.Subagent,
                    databaseLastWrite,
                    databaseLength,
                    relationship is null ? null : composerId,
                    relationship?.ParentComposerId)
            ],
            Sources = sources,
            Metadata = metadata
        };
    }

    private async Task HydrateComposersAsync(
        SqliteConnection connection,
        CursorDesktopDatabase database,
        long databaseLength,
        DateTimeOffset databaseLastWrite,
        IReadOnlyDictionary<string, CursorWorkspaceManifest> manifests,
        Dictionary<string, CursorDesktopSessionBuilder> builders,
        Dictionary<string, CursorComposerRelationship> relationships,
        IReadOnlyDictionary<string, CursorComposerHydrationInput> inputs,
        IProgress<SessionCatalogEntry>? progress,
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        CancellationToken cancellationToken)
    {
        int workerCount = Math.Max(1, Environment.ProcessorCount);
        int channelCapacity = Math.Max(2, workerCount * 2);
        Channel<CursorBubbleWorkItem> work = Channel.CreateBounded<
            CursorBubbleWorkItem>(
            new BoundedChannelOptions(channelCapacity)
            {
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        Channel<CursorHydrationResult> results = Channel.CreateBounded<
            CursorHydrationResult>(
            new BoundedChannelOptions(channelCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

        Task[] workers = Enumerable.Range(0, workerCount)
            .Select(_ => ProjectBubbleWorkItemsAsync(
                database.Path,
                work.Reader,
                results.Writer,
                guard,
                warnings,
                cancellationToken))
            .ToArray();
        Task producer = ProduceBubbleWorkItemsAsync(
            connection,
            inputs,
            work.Writer,
            guard,
            warnings,
            cancellationToken);
        Task completion = CompleteHydrationPipelineAsync(
            producer,
            workers,
            results.Writer);

        await foreach (CursorHydrationResult result in results.Reader
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            CursorDesktopComposerSnapshot snapshot = CreateSnapshot(
                database,
                databaseLength,
                databaseLastWrite,
                manifests,
                result.Input.Record,
                result.Input.Composer,
                result.Input.ComposerId,
                result.Projection);
            CursorDesktopSessionBuilder builder = GetBuilder(
                builders,
                result.Input.ComposerId);
            builder.Add(snapshot);
            if (snapshot.Relationship is not null)
            {
                relationships[result.Input.ComposerId] =
                    snapshot.Relationship;
            }

            if (progress is not null &&
                builder.HasContent &&
                !builder.IsBackgroundChild)
            {
                progress.Report(builder.Build());
            }
        }

        await completion.ConfigureAwait(false);
    }

    private async Task ProduceBubbleWorkItemsAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, CursorComposerHydrationInput> inputs,
        ChannelWriter<CursorBubbleWorkItem> writer,
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            long maximumValueBytes = MaximumValueBytes();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT key, length(CAST(value AS BLOB)),
                       CASE WHEN length(CAST(value AS BLOB)) <= $maximumBytes
                            THEN value ELSE NULL END
                FROM "cursorDiskKV"
                WHERE key LIKE 'bubbleId:%'
                ORDER BY key
                """;
            command.CommandTimeout = 2;
            command.Parameters.AddWithValue(
                "$maximumBytes",
                maximumValueBytes);

            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
            string? currentComposerId = null;
            CursorComposerHydrationInput? currentInput = null;
            List<CursorRawBubble>? currentBubbles = null;
            HashSet<string> queuedComposerIds =
                new(StringComparer.Ordinal);
            HashSet<string> truncatedComposerIds =
                new(StringComparer.Ordinal);
            int maximumRecords = Math.Max(
                0,
                _context.Limits.MaximumRecordsPerSession);

            while (await reader.ReadAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                if (guard.DeadlineExceeded)
                {
                    guard.MarkIncomplete();
                    warnings.Add(
                        $"Cursor bubble scan reached the discovery time limit in " +
                        $"'{connection.DataSource}'.");
                    break;
                }

                string key = reader.GetString(0);
                if (!TryParseBubbleKey(
                    key,
                    inputs,
                    out string? composerId,
                    out string? bubbleId,
                    out CursorComposerHydrationInput? input))
                {
                    continue;
                }

                if (!string.Equals(
                    currentComposerId,
                    composerId,
                    StringComparison.Ordinal))
                {
                    await QueueCurrentBubbleWorkItemAsync().ConfigureAwait(false);
                    currentComposerId = composerId;
                    currentInput = input;
                    currentBubbles = [];
                }

                if (currentBubbles!.Count >= maximumRecords)
                {
                    if (truncatedComposerIds.Add(composerId!))
                    {
                        guard.MarkIncomplete();
                        warnings.Add(
                            $"Cursor composer '{composerId}' exceeded the " +
                            $"{maximumRecords}-bubble limit.");
                    }

                    continue;
                }

                long valueBytes = reader.IsDBNull(1)
                    ? -1
                    : reader.GetInt64(1);
                if (reader.IsDBNull(2) ||
                    valueBytes < 0 ||
                    valueBytes > maximumValueBytes)
                {
                    guard.MarkIncomplete();
                    warnings.Add(
                        $"Skipped oversized Cursor database value '{key}' in " +
                        $"'{connection.DataSource}'.");
                    continue;
                }

                string? raw = ValueText(reader.GetValue(2));
                if (string.IsNullOrWhiteSpace(raw))
                {
                    guard.MarkIncomplete();
                    warnings.Add(
                        $"Skipped empty Cursor database value '{key}' in " +
                        $"'{connection.DataSource}'.");
                    continue;
                }

                currentBubbles.Add(new CursorRawBubble(
                    key,
                    bubbleId!,
                    raw));
            }

            await QueueCurrentBubbleWorkItemAsync().ConfigureAwait(false);
            foreach (CursorComposerHydrationInput input in inputs.Values)
            {
                if (queuedComposerIds.Add(input.ComposerId))
                {
                    await writer.WriteAsync(
                        new CursorBubbleWorkItem(
                            input,
                            []),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            async ValueTask QueueCurrentBubbleWorkItemAsync()
            {
                if (currentInput is null ||
                    currentBubbles is null ||
                    !queuedComposerIds.Add(currentInput.ComposerId))
                {
                    return;
                }

                await writer.WriteAsync(
                    new CursorBubbleWorkItem(
                        currentInput,
                        currentBubbles),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    private async Task ProjectBubbleWorkItemsAsync(
        string databasePath,
        ChannelReader<CursorBubbleWorkItem> reader,
        ChannelWriter<CursorHydrationResult> writer,
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        CancellationToken cancellationToken)
    {
        await foreach (CursorBubbleWorkItem workItem in reader
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            try
            {
                Dictionary<string, CursorSqliteJsonRecord> bubbles =
                    new(StringComparer.Ordinal);
                foreach (CursorRawBubble bubble in workItem.Bubbles)
                {
                    if (!_json.TryParse(
                        bubble.RawContent,
                        out JsonDocument? document,
                        out string? error) ||
                        document is null)
                    {
                        guard.MarkIncomplete();
                        warnings.Add(
                            $"Skipped malformed Cursor database value " +
                            $"'{bubble.Key}' in '{databasePath}': {error}");
                        continue;
                    }

                    using (document)
                    {
                        bubbles[bubble.BubbleId] =
                            new CursorSqliteJsonRecord(
                                bubble.Key,
                                bubble.RawContent,
                                document.RootElement.Clone());
                    }
                }

                CursorBubbleProjection projection =
                    _bubbleProjector.Project(
                        databasePath,
                        workItem.Input.ComposerId,
                        workItem.Input.Composer,
                        bubbles,
                        _context.Limits.MaximumRecordsPerSession,
                        guard,
                        warnings,
                        cancellationToken);
                await writer.WriteAsync(
                    new CursorHydrationResult(
                        workItem.Input,
                        projection),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Could not project Cursor composer " +
                    $"'{workItem.Input.ComposerId}': {exception.Message}");
            }
        }
    }

    private static async Task CompleteHydrationPipelineAsync(
        Task producer,
        IReadOnlyList<Task> workers,
        ChannelWriter<CursorHydrationResult> writer)
    {
        Exception? failure = null;
        try
        {
            await producer.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    private static bool TryParseBubbleKey(
        string key,
        IReadOnlyDictionary<string, CursorComposerHydrationInput> inputs,
        out string? composerId,
        out string? bubbleId,
        out CursorComposerHydrationInput? input)
    {
        const string prefix = "bubbleId:";
        composerId = null;
        bubbleId = null;
        input = null;
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string remainder = key[prefix.Length..];
        int separator = remainder.IndexOf(':');
        string? matchedComposerId = null;
        string? matchedBubbleId = null;
        CursorComposerHydrationInput? matchedInput = null;
        while (separator > 0)
        {
            string candidate = remainder[..separator];
            if (inputs.TryGetValue(
                candidate,
                out CursorComposerHydrationInput? candidateInput))
            {
                string candidateBubbleId = remainder[(separator + 1)..];
                if (candidateBubbleId.Length > 0)
                {
                    matchedComposerId = candidate;
                    matchedBubbleId = candidateBubbleId;
                    matchedInput = candidateInput;
                }
            }

            separator = remainder.IndexOf(':', separator + 1);
        }

        composerId = matchedComposerId;
        bubbleId = matchedBubbleId;
        input = matchedInput;
        return input is not null;
    }

    private async Task<IReadOnlyList<CursorSqliteJsonRecord>>
        ReadBoundedKeyValuesAsync(
            SqliteConnection connection,
            string table,
            string predicate,
            IReadOnlyList<SqliteParameter> parameters,
            int maximumRows,
            CursorDiscoveryGuard guard,
            CursorWarningCollector warnings,
            CancellationToken cancellationToken)
    {
        maximumRows = Math.Max(0, maximumRows);
        long maximumValueBytes = MaximumValueBytes();
        string sql =
            $"SELECT key, length(CAST(value AS BLOB)), " +
            $"CASE WHEN length(CAST(value AS BLOB)) <= $maximumBytes " +
            $"THEN value ELSE NULL END FROM {QuoteIdentifier(table)} " +
            $"WHERE {predicate} ORDER BY key LIMIT $maximumRows";
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 2;
        command.Parameters.AddWithValue("$maximumBytes", maximumValueBytes);
        command.Parameters.AddWithValue(
            "$maximumRows",
            MaximumRowsWithSentinel(maximumRows));
        foreach (SqliteParameter parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        List<CursorSqliteJsonRecord> records = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        int count = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (guard.DeadlineExceeded)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Cursor {table} query reached the discovery time limit in " +
                    $"'{connection.DataSource}'.");
                break;
            }

            if (count++ >= maximumRows)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Cursor {table} query reached its {maximumRows}-row limit in " +
                    $"'{connection.DataSource}'.");
                break;
            }

            string key = reader.GetString(0);
            long length = reader.IsDBNull(1) ? -1 : reader.GetInt64(1);
            if (reader.IsDBNull(2) ||
                length < 0 ||
                length > maximumValueBytes)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Skipped oversized Cursor database value '{key}' in " +
                    $"'{connection.DataSource}'.");
                continue;
            }

            string? raw = ValueText(reader.GetValue(2));
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!_json.TryParse(raw, out JsonDocument? document, out string? error) ||
                document is null)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    $"Skipped malformed Cursor database value '{key}' in " +
                    $"'{connection.DataSource}': {error}");
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind is not
                    JsonValueKind.Object and not JsonValueKind.Array)
                {
                    guard.MarkIncomplete();
                    warnings.Add(
                        $"Skipped non-object Cursor database value '{key}' in " +
                        $"'{connection.DataSource}'.");
                    continue;
                }

                records.Add(new CursorSqliteJsonRecord(
                    key,
                    raw,
                    document.RootElement.Clone()));
            }
        }

        return records;
    }

    private async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.CommandTimeout = 2;
        command.Parameters.AddWithValue("$name", tableName);
        object? value = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);
        return value is not null && value is not DBNull;
    }

    private async Task<HashSet<string>> ReadColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA table_info({QuoteIdentifier(tableName)})";
        command.CommandTimeout = 2;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private async Task<bool> HasKeyValueColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        HashSet<string> columns = await ReadColumnsAsync(
            connection,
            tableName,
            cancellationToken).ConfigureAwait(false);
        return columns.Contains("key") && columns.Contains("value");
    }

    private IReadOnlyList<JsonElement> ComposerObjects(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            return payload.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .ToArray();
        }

        if (_json.TryGetProperty(
            payload,
            "allComposers",
            out JsonElement allComposers) &&
            allComposers.ValueKind == JsonValueKind.Array)
        {
            return allComposers.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .ToArray();
        }

        if (_json.String(payload, "composerId", "composer_id") is not null ||
            HasConversationHeaders(payload))
        {
            return [payload];
        }

        return [];
    }

    private IReadOnlyList<JsonElement> HeaderObjects(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            return payload.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .ToArray();
        }

        foreach (string propertyName in new[]
                 {
                     "allComposers", "composerHeaders", "headers"
                 })
        {
            if (_json.TryGetProperty(
                payload,
                propertyName,
                out JsonElement headers) &&
                headers.ValueKind == JsonValueKind.Array)
            {
                return headers.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .ToArray();
            }
        }

        return [];
    }

    private void CollectSelectedComposerIds(
        JsonElement payload,
        HashSet<string> selected)
    {
        string? single = _json.String(
            payload,
            "selectedComposerId",
            "selected_composer_id");
        if (_json.IsValidIdentity(single))
        {
            selected.Add(single!);
        }

        foreach (string name in new[]
                 {
                     "selectedComposerIds", "selected_composer_ids"
                 })
        {
            if (_json.TryGetProperty(payload, name, out JsonElement value))
            {
                CollectIdentityValues(value, selected, depth: 0);
            }
        }
    }

    private void CollectIdentityValues(
        JsonElement value,
        HashSet<string> identities,
        int depth)
    {
        if (depth > 6)
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string? identity = value.GetString();
            if (_json.IsValidIdentity(identity))
            {
                identities.Add(identity!);
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                CollectIdentityValues(item, identities, depth + 1);
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                CollectIdentityValues(property.Value, identities, depth + 1);
            }
        }
    }

    private (WorkspaceContext Workspace, int Priority, string? WorkspaceId)
        ResolveWorkspace(
            CursorDesktopDatabase database,
            IReadOnlyDictionary<string, CursorWorkspaceManifest> manifests,
            JsonElement composer)
    {
        string? workspaceId = null;
        string? workspacePath = null;
        if (_json.TryGetProperty(
            composer,
            "workspaceIdentifier",
            out JsonElement identifier) &&
            identifier.ValueKind == JsonValueKind.Object)
        {
            workspaceId = _json.String(identifier, "id", "workspaceId");
            if (_json.TryGetProperty(identifier, "uri", out JsonElement uri))
            {
                workspacePath = uri.ValueKind == JsonValueKind.String
                    ? uri.GetString()
                    : _json.String(uri, "fsPath", "path", "external");
            }
        }

        workspaceId ??= _json.String(composer, "workspaceId", "workspace_id");
        workspacePath ??= _json.String(
            composer,
            "workspacePath",
            "workspace_path",
            "cwd");

        bool emptyWindow = string.Equals(
            workspaceId,
            "empty-window",
            StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            return (
                _workspaceResolver.ResolveDesktopWorkspace(
                    workspacePath,
                    emptyWindow),
                100,
                workspaceId);
        }

        if (!string.IsNullOrWhiteSpace(workspaceId) &&
            manifests.TryGetValue(
                workspaceId,
                out CursorWorkspaceManifest? mapped))
        {
            return (mapped.Workspace, 90, workspaceId);
        }

        if (database.Manifest is not null)
        {
            return (
                database.Manifest.Workspace,
                70,
                workspaceId ?? database.Manifest.WorkspaceId);
        }

        return (
            emptyWindow
                ? WorkspaceContext.NoWorkspace
                : WorkspaceContext.Unknown,
            emptyWindow ? 80 : 0,
            workspaceId);
    }

    private WorkspaceContext ResolveManifestWorkspace(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return WorkspaceContext.Unknown;
        }

        return _normalizer.FromPath(path);
    }

    private CursorComposerRelationship? Relationship(JsonElement composer)
    {
        if (!_json.TryGetProperty(
            composer,
            "subagentInfo",
            out JsonElement subagentInfo) ||
            subagentInfo.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? parentComposerId = _json.String(
            subagentInfo,
            "parentComposerId",
            "parent_composer_id");
        if (!_json.IsValidIdentity(parentComposerId))
        {
            return null;
        }

        int? seedCount = _json.Integer(
            subagentInfo,
            "sideChatSeedTurnCount",
            "side_chat_seed_turn_count");
        if (seedCount is < 0 or > 100_000)
        {
            seedCount = null;
        }

        return new CursorComposerRelationship(
            parentComposerId!,
            _json.String(
                subagentInfo,
                "subagentTypeName",
                "subagent_type_name",
                "type"),
            seedCount);
    }

    private string? Model(JsonElement composer)
    {
        string? model = _json.String(
            composer,
            "model",
            "modelName",
            "model_name",
            "modelId",
            "model_id");
        if (model is not null)
        {
            return model;
        }

        foreach (string name in new[]
                 {
                     "modelDetails", "modelInfo", "modelConfig", "selectedModel"
                 })
        {
            if (_json.TryGetProperty(
                composer,
                name,
                out JsonElement nested) &&
                nested.ValueKind == JsonValueKind.Object)
            {
                model = _json.String(
                    nested,
                    "model",
                    "name",
                    "modelName",
                    "id");
                if (model is not null)
                {
                    return model;
                }
            }
        }

        return null;
    }

    private bool HasConversationHeaders(JsonElement composer) =>
        new[]
        {
            "fullConversationHeadersOnly",
            "conversationHeaders",
            "fullConversation",
            "conversation"
        }.Any(name =>
            _json.TryGetProperty(composer, name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Array &&
            value.GetArrayLength() > 0);

    private bool HasRelationshipCycle(
        string composerId,
        IReadOnlyDictionary<string, CursorComposerRelationship> relationships)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        string current = composerId;
        while (relationships.TryGetValue(
            current,
            out CursorComposerRelationship? relationship))
        {
            if (!seen.Add(current))
            {
                return true;
            }

            current = relationship.ParentComposerId;
        }

        return false;
    }

    private int RelationshipDepth(
        string composerId,
        IReadOnlyDictionary<string, CursorComposerRelationship> relationships)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        string current = composerId;
        int depth = 0;
        while (relationships.TryGetValue(
            current,
            out CursorComposerRelationship? relationship) &&
            seen.Add(current))
        {
            depth++;
            current = relationship.ParentComposerId;
        }

        return depth;
    }

    private CursorDesktopSessionBuilder GetBuilder(
        Dictionary<string, CursorDesktopSessionBuilder> builders,
        string composerId)
    {
        if (!builders.TryGetValue(
            composerId,
            out CursorDesktopSessionBuilder? builder))
        {
            builder = new CursorDesktopSessionBuilder(composerId, _json);
            builders.Add(composerId, builder);
        }

        return builder;
    }

    private long MaximumValueBytes() =>
        Math.Min(
            Math.Max(0, _context.Limits.MaximumFileBytes),
            Math.Max(1, _context.Limits.MaximumLineBytes));

    private int MaximumRowsWithSentinel(int maximumRows) =>
        maximumRows == int.MaxValue ? int.MaxValue : maximumRows + 1;

    private string? ValueText(object value) =>
        value switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => Convert.ToString(value)
        };

    private string QuoteIdentifier(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private string EscapeLike(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
