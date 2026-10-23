using System.Text;
using HarnessSpy.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public sealed record CursorInspectedDatabase(
    CursorDatabaseDescriptor Database,
    IReadOnlyList<CursorSqliteTableSchema> Tables);

public sealed class CursorInspectionCatalogSnapshot
{
    private readonly IReadOnlyDictionary<string, CursorIndexedRecord> _records;

    public CursorInspectionCatalogSnapshot(
        IReadOnlyList<CursorInspectedDatabase> databases,
        IReadOnlyList<CursorIndexedRecord> records,
        IReadOnlySet<string> knownComposerIds,
        CursorRelationIndex relationIndex,
        CursorCrossStoreCorrelationIndex correlationIndex,
        IReadOnlyList<string> warnings)
    {
        Databases = databases;
        Records = records;
        KnownComposerIds = knownComposerIds;
        RelationIndex = relationIndex;
        CorrelationIndex = correlationIndex;
        Warnings = warnings;
        _records = records
            .GroupBy(record => record.Row.StableKey)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CursorInspectedDatabase> Databases { get; }

    public IReadOnlyList<CursorIndexedRecord> Records { get; }

    public IReadOnlySet<string> KnownComposerIds { get; }

    public CursorRelationIndex RelationIndex { get; }

    public CursorCrossStoreCorrelationIndex CorrelationIndex { get; }

    public IReadOnlyList<string> Warnings { get; }

    public CursorIndexedRecord? FindRecord(CursorRowReference row) =>
        _records.TryGetValue(
            row.StableKey,
            out CursorIndexedRecord? record)
            ? record
            : null;
}

public sealed class CursorInspectionCatalogBuilder
{
    private readonly SessionDiscoveryContext _context;
    private readonly CursorDatabaseDiscovery _discovery;
    private readonly CursorSqliteConnectionFactory _connectionFactory;
    private readonly CursorSqliteSchemaReader _schemaReader;
    private readonly CursorSqliteRowReader _rowReader;
    private readonly CursorSqliteRecordProjector _projector;
    private readonly CursorTranscriptEvidenceDiscovery _transcriptDiscovery;

    private sealed record RawRecord(
        CursorDatabaseDescriptor Database,
        CursorSqliteRowSnapshot Row,
        long PageOrdinal);

    private sealed class CursorInspectionByteBudget
    {
        private readonly long _maximumBytes;
        private long _retainedBytes;

        public CursorInspectionByteBudget(long maximumBytes)
        {
            _maximumBytes = Math.Max(0, maximumBytes);
        }

        public bool IsExhausted => _retainedBytes >= _maximumBytes;

        public bool TryRetain(CursorSqliteRowSnapshot row)
        {
            long bytes = row.Cells.Sum(CellBytes);
            if (bytes > _maximumBytes - _retainedBytes)
            {
                return false;
            }

            _retainedBytes += bytes;
            return true;
        }

        private long CellBytes(CursorSqliteCell cell) =>
            cell.Value switch
            {
                null => 0,
                byte[] bytes => bytes.LongLength,
                string text => Encoding.UTF8.GetByteCount(text),
                _ => 16
            };
    }

    public CursorInspectionCatalogBuilder(
        SessionDiscoveryContext context,
        CursorDatabaseDiscovery? discovery = null,
        CursorSqliteConnectionFactory? connectionFactory = null,
        CursorSqliteSchemaReader? schemaReader = null,
        CursorSqliteRowReader? rowReader = null,
        CursorSqliteRecordProjector? projector = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _discovery = discovery ??
            new CursorDatabaseDiscovery(context.ApplicationData);
        _connectionFactory = connectionFactory ??
            new CursorSqliteConnectionFactory();
        _schemaReader = schemaReader ?? new CursorSqliteSchemaReader();
        _rowReader = rowReader ?? new CursorSqliteRowReader(_schemaReader);
        _projector = projector ?? new CursorSqliteRecordProjector();
        _transcriptDiscovery = new CursorTranscriptEvidenceDiscovery(context);
    }

    public async Task<CursorInspectionCatalogSnapshot> BuildAsync(
        IReadOnlyList<string>? customDatabasePaths = null,
        CancellationToken cancellationToken = default)
    {
        List<string> warnings = [];
        List<CursorDatabaseDescriptor> descriptors =
        [
            .. await _discovery.DiscoverAsync(cancellationToken)
                .ConfigureAwait(false)
        ];
        AddCustomDatabases(descriptors, customDatabasePaths, warnings);

        List<CursorInspectedDatabase> databases = [];
        List<RawRecord> rawRecords = [];
        int maximumRecords = Math.Max(0, _context.Limits.MaximumFiles);
        CursorInspectionByteBudget byteBudget = new(
            _context.Limits.MaximumHydrationBytes);
        foreach (CursorDatabaseDescriptor database in descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using SqliteConnection connection =
                    _connectionFactory.Create(database);
                await connection.OpenAsync(cancellationToken)
                    .ConfigureAwait(false);
                IReadOnlyList<CursorSqliteTableSchema> tables =
                    await _schemaReader.ReadTablesAsync(
                        connection,
                        cancellationToken).ConfigureAwait(false);
                databases.Add(new CursorInspectedDatabase(database, tables));
                await ReadKnownRecordsAsync(
                    connection,
                    database,
                    tables,
                    rawRecords,
                    maximumRecords,
                    byteBudget,
                    warnings,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is SqliteException or IOException or
                    UnauthorizedAccessException or InvalidOperationException)
            {
                warnings.Add(
                    $"Could not inspect '{database.Path}': {exception.Message}");
            }
        }

        HashSet<string> composerIds = CollectComposerIds(rawRecords);
        CursorIndexedRecord[] records = rawRecords
            .Select(record => _projector.Project(
                record.Database,
                record.Row,
                composerIds,
                record.PageOrdinal))
            .GroupBy(record => record.Row.StableKey)
            .Select(group => group.First())
            .ToArray();
        foreach (CursorIndexedRecord record in records)
        {
            if (!string.IsNullOrWhiteSpace(record.Classification.ComposerId))
            {
                composerIds.Add(record.Classification.ComposerId);
            }

            foreach (CursorLogicalReference reference in record.References.Where(
                         item =>
                             item.Kind == CursorRelationKind.DeclaresComposer))
            {
                composerIds.Add(reference.TargetId);
            }
        }

        CursorRelationIndex relationIndex = new(records);
        IReadOnlyList<SessionCatalogEntry> transcripts =
            await ReadTranscriptsAsync(warnings, cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyList<CursorWorkspaceEvidence> workspaces =
            await _discovery.DiscoverWorkspaceEvidenceAsync(cancellationToken)
                .ConfigureAwait(false);
        CursorCrossStoreCorrelationIndex correlationIndex = new(
            records,
            workspaces,
            transcripts);
        return new CursorInspectionCatalogSnapshot(
            databases,
            records,
            composerIds,
            relationIndex,
            correlationIndex,
            warnings);
    }

    public async Task<CursorInspectionCatalogSnapshot> BuildSessionAsync(
        string composerId,
        IReadOnlyList<string>? customDatabasePaths = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composerId);
        List<string> warnings = [];
        List<CursorDatabaseDescriptor> descriptors =
        [
            .. await _discovery.DiscoverAsync(cancellationToken)
                .ConfigureAwait(false)
        ];
        AddCustomDatabases(descriptors, customDatabasePaths, warnings);

        List<CursorInspectedDatabase> databases = [];
        List<RawRecord> rawRecords = [];
        int maximumRecords = Math.Max(
            0,
            _context.Limits.MaximumRecordsPerSession);
        CursorInspectionByteBudget byteBudget = new(
            _context.Limits.MaximumHydrationBytes);
        foreach (CursorDatabaseDescriptor database in descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using SqliteConnection connection =
                    _connectionFactory.Create(database);
                await connection.OpenAsync(cancellationToken)
                    .ConfigureAwait(false);
                IReadOnlyList<CursorSqliteTableSchema> tables =
                    await _schemaReader.ReadTablesAsync(
                        connection,
                        cancellationToken).ConfigureAwait(false);
                databases.Add(new CursorInspectedDatabase(database, tables));
                await ReadKnownRecordsAsync(
                    connection,
                    database,
                    tables,
                    rawRecords,
                    maximumRecords,
                    byteBudget,
                    warnings,
                    cancellationToken,
                    composerId).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is SqliteException or IOException or
                    UnauthorizedAccessException or InvalidOperationException)
            {
                warnings.Add(
                    $"Could not inspect '{database.Path}': {exception.Message}");
            }
        }

        HashSet<string> composerIds = CollectComposerIds(rawRecords);
        composerIds.Add(composerId);
        CursorIndexedRecord[] records = rawRecords
            .Select(record => _projector.Project(
                record.Database,
                record.Row,
                composerIds,
                record.PageOrdinal))
            .GroupBy(record => record.Row.StableKey)
            .Select(group => group.First())
            .ToArray();
        return new CursorInspectionCatalogSnapshot(
            databases,
            records,
            composerIds,
            new CursorRelationIndex(records),
            new CursorCrossStoreCorrelationIndex(records, [], []),
            warnings);
    }

    private void AddCustomDatabases(
        List<CursorDatabaseDescriptor> databases,
        IReadOnlyList<string>? customDatabasePaths,
        List<string> warnings)
    {
        foreach (string path in customDatabasePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                warnings.Add($"Custom database '{fullPath}' does not exist.");
                continue;
            }

            if (databases.Any(item => string.Equals(
                    item.Path,
                    fullPath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            databases.Add(_discovery.CreateCustomDatabase(fullPath));
        }
    }

    private async Task ReadKnownRecordsAsync(
        SqliteConnection connection,
        CursorDatabaseDescriptor database,
        IReadOnlyList<CursorSqliteTableSchema> tables,
        List<RawRecord> records,
        int maximumRecords,
        CursorInspectionByteBudget byteBudget,
        List<string> warnings,
        CancellationToken cancellationToken,
        string? composerId = null)
    {
        foreach (CursorSqliteTableSchema table in tables.Where(
                     IsKnownCursorTable))
        {
            if (byteBudget.IsExhausted)
            {
                return;
            }

            bool reportedTruncation = false;
            IReadOnlyList<CursorSqliteRowFilter?> filters = Filters(
                table,
                composerId);
            foreach (CursorSqliteRowFilter? filter in filters)
            {
                long offset = 0;
                bool hasMore;
                do
                {
                    int remaining = maximumRecords - records.Count;
                    if (remaining <= 0)
                    {
                        warnings.Add(
                            $"Cursor relation indexing stopped at the " +
                            $"{maximumRecords:N0}-record limit.");
                        return;
                    }

                    int pageSize = Math.Min(
                        _rowReader.MaximumPageSize,
                        remaining);
                    CursorSqlitePagedResult page =
                        await _rowReader.ReadPageAsync(
                            connection,
                            table,
                            offset,
                            pageSize,
                            filter,
                            cancellationToken).ConfigureAwait(false);
                    for (int index = 0; index < page.Rows.Count; index++)
                    {
                        if (!byteBudget.TryRetain(page.Rows[index]))
                        {
                            warnings.Add(
                                "Cursor relation indexing stopped after " +
                                $"retaining {_context.Limits.MaximumHydrationBytes:N0} " +
                                "bytes of bounded SQLite values.");
                            return;
                        }

                        if (!reportedTruncation &&
                            page.Rows[index].Cells.Any(cell =>
                                cell.IsTruncated))
                        {
                            warnings.Add(
                                $"Some values in '{database.Path}' table " +
                                $"'{table.Name}' exceeded the " +
                                $"{_rowReader.MaximumCellBytes:N0}-byte " +
                                "inspection limit; their semantic links may be incomplete.");
                            reportedTruncation = true;
                        }

                        records.Add(new RawRecord(
                            database,
                            page.Rows[index],
                            offset + index));
                    }

                    offset = page.NextOffset;
                    hasMore = page.HasMore;
                }
                while (hasMore);
            }
        }
    }

    private IReadOnlyList<CursorSqliteRowFilter?> Filters(
        CursorSqliteTableSchema table,
        string? composerId)
    {
        CursorSqliteColumnSchema? key = table.FindColumn("key");
        if (key is null)
        {
            CursorSqliteColumnSchema? composerColumn =
                table.FindColumn("composerId");
            if (!string.IsNullOrWhiteSpace(composerId) &&
                composerColumn is not null)
            {
                return
                [
                    new CursorSqliteRowFilter(
                        composerColumn.Name,
                        composerId,
                        CursorSqliteFilterMode.Exact)
                ];
            }

            return [null];
        }

        if (string.Equals(
            table.Name,
            "cursorDiskKV",
            StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(composerId))
            {
                return
                [
                    new CursorSqliteRowFilter(
                        key.Name,
                        $"bubbleId:{composerId}:",
                        CursorSqliteFilterMode.Prefix),
                    new CursorSqliteRowFilter(
                        key.Name,
                        $"composerData:{composerId}",
                        CursorSqliteFilterMode.Exact)
                ];
            }

            return
            [
                new CursorSqliteRowFilter(
                    key.Name,
                    "bubbleId:",
                    CursorSqliteFilterMode.Prefix),
                new CursorSqliteRowFilter(
                    key.Name,
                    "composerData:",
                    CursorSqliteFilterMode.Prefix)
            ];
        }

        return
        [
            new CursorSqliteRowFilter(
                key.Name,
                "composer.composerHeaders",
                CursorSqliteFilterMode.Exact),
            new CursorSqliteRowFilter(
                key.Name,
                "composer.composerData",
                CursorSqliteFilterMode.Exact)
        ];
    }

    private HashSet<string> CollectComposerIds(
        IReadOnlyList<RawRecord> records)
    {
        HashSet<string> composerIds = new(StringComparer.Ordinal);
        HashSet<string> none = new(StringComparer.Ordinal);
        foreach (RawRecord raw in records)
        {
            CursorIndexedRecord record = _projector.Project(
                raw.Database,
                raw.Row,
                none,
                raw.PageOrdinal);
            if (!string.IsNullOrWhiteSpace(record.Classification.ComposerId))
            {
                composerIds.Add(record.Classification.ComposerId);
            }

            foreach (CursorLogicalReference reference in record.References.Where(
                         item =>
                             item.Kind == CursorRelationKind.DeclaresComposer))
            {
                composerIds.Add(reference.TargetId);
            }
        }

        return composerIds;
    }

    private async Task<IReadOnlyList<SessionCatalogEntry>> ReadTranscriptsAsync(
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _transcriptDiscovery
                .DiscoverAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidOperationException)
        {
            warnings.Add(
                $"Could not correlate Cursor transcripts: {exception.Message}");
            return [];
        }
    }

    private bool IsKnownCursorTable(CursorSqliteTableSchema table) =>
        string.Equals(
            table.Name,
            "cursorDiskKV",
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            table.Name,
            "ItemTable",
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            table.Name,
            "composerHeaders",
            StringComparison.OrdinalIgnoreCase);
}
