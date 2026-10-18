using System.Text.Json;
using HarnessSpy.Core.Sessions.Cursor.Inspection;
using Microsoft.Data.Sqlite;

namespace HarnessSpy.Tests;

public sealed class CursorSqliteInspectionTests
{
    [Fact]
    public async Task SchemaReaderFindsColumnsPrimaryKeysAndRowIdSupport()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = await CreateDatabaseAsync(
                root,
                """
                CREATE TABLE composite (
                    tenant TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    payload BLOB,
                    generated TEXT GENERATED ALWAYS AS (
                        tenant || ':' || sequence
                    ) STORED,
                    PRIMARY KEY (sequence, tenant)
                );
                CREATE TABLE heap (value TEXT);
                CREATE TABLE no_rowid (
                    tenant TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    value TEXT,
                    PRIMARY KEY (tenant, sequence)
                ) WITHOUT ROWID;
                CREATE TABLE numbered (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    value TEXT
                );
                """);

            await using SqliteConnection connection =
                new CursorSqliteConnectionFactory().Create(path);
            await connection.OpenAsync();
            IReadOnlyList<CursorSqliteTableSchema> tables =
                await new CursorSqliteSchemaReader().ReadTablesAsync(
                    connection);

            Assert.Equal(
                ["composite", "heap", "no_rowid", "numbered"],
                tables.Select(static table => table.Name));
            Assert.DoesNotContain(
                tables,
                static table => table.Name == "sqlite_sequence");

            CursorSqliteTableSchema composite = tables.Single(
                static table => table.Name == "composite");
            Assert.True(composite.HasRowId);
            Assert.Equal(
                ["sequence", "tenant"],
                composite.PrimaryKeyColumns.Select(
                    static column => column.Name));
            Assert.True(composite.FindColumn("generated")!.IsHidden);

            CursorSqliteTableSchema heap = tables.Single(
                static table => table.Name == "heap");
            Assert.Equal("rowid", heap.RowIdAlias);
            Assert.Empty(heap.PrimaryKeyColumns);

            CursorSqliteTableSchema noRowId = tables.Single(
                static table => table.Name == "no_rowid");
            Assert.False(noRowId.HasRowId);
            Assert.True(noRowId.SupportsExactLookup);
            Assert.Equal(
                [1, 2],
                noRowId.PrimaryKeyColumns.Select(
                    static column => column.PrimaryKeyOrder));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task RowReaderPagesFiltersAndReloadsPrimaryKeyRows()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = await CreateDatabaseAsync(
                root,
                """
                CREATE TABLE records (
                    key TEXT PRIMARY KEY,
                    value BLOB,
                    count INTEGER
                );
                INSERT INTO records(key, value, count) VALUES
                    ('bubbleId:one', X'01', 1),
                    ('composerData:one', 'first', 2),
                    ('composerData:two', 'second', 3),
                    ('literal%key', 'percent', 4);
                """);

            await using SqliteConnection connection =
                new CursorSqliteConnectionFactory().Create(path);
            await connection.OpenAsync();
            CursorSqliteSchemaReader schemaReader = new();
            CursorSqliteTableSchema table =
                (await schemaReader.ReadTableAsync(connection, "records"))!;
            CursorSqliteRowReader rowReader = new(schemaReader);

            CursorSqlitePagedResult firstPage =
                await rowReader.ReadPageAsync(
                    connection,
                    table,
                    offset: 0,
                    pageSize: 2);
            Assert.True(firstPage.HasMore);
            Assert.Equal(2, firstPage.Rows.Count);
            Assert.All(
                firstPage.Rows,
                static row => Assert.Equal(
                    CursorSqliteRowLocatorKind.PrimaryKey,
                    row.Locator!.Kind));

            CursorSqlitePagedResult composers =
                await rowReader.ReadPageAsync(
                    connection,
                    table,
                    offset: 0,
                    pageSize: 10,
                    new CursorSqliteRowFilter(
                        "key",
                        "composerData:",
                        CursorSqliteFilterMode.Prefix));
            Assert.False(composers.HasMore);
            Assert.Equal(
                ["composerData:one", "composerData:two"],
                composers.Rows.Select(RowKey));

            CursorSqlitePagedResult escapedPrefix =
                await rowReader.ReadPageAsync(
                    connection,
                    table,
                    offset: 0,
                    pageSize: 10,
                    new CursorSqliteRowFilter(
                        "key",
                        "literal%",
                        CursorSqliteFilterMode.Prefix));
            Assert.Equal("literal%key", RowKey(Assert.Single(
                escapedPrefix.Rows)));

            CursorSqlitePagedResult exact =
                await rowReader.ReadPageAsync(
                    connection,
                    table,
                    offset: 0,
                    pageSize: 10,
                    new CursorSqliteRowFilter(
                        "key",
                        "composerData:two",
                        CursorSqliteFilterMode.Exact));
            CursorSqliteRowSnapshot selected = Assert.Single(exact.Rows);
            CursorSqliteRowSnapshot? reloaded = await rowReader.ReadRowAsync(
                connection,
                table,
                selected.Locator!);
            Assert.NotNull(reloaded);
            Assert.Equal(RowKey(selected), RowKey(reloaded));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                rowReader.ReadPageAsync(
                    connection,
                    table,
                    offset: 0,
                    pageSize: 10,
                    new CursorSqliteRowFilter(
                        "value",
                        "first",
                        CursorSqliteFilterMode.Exact)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                rowReader.ReadPageAsync(
                    connection,
                    table,
                    offset: 0,
                    pageSize: rowReader.MaximumPageSize + 1));

            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                rowReader.ReadPageAsync(
                    connection,
                    table,
                    offset: 0,
                    pageSize: 10,
                    cancellationToken: cancellation.Token));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task RowReaderUsesRowIdWhenNoPrimaryKeyExists()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = await CreateDatabaseAsync(
                root,
                """
                CREATE TABLE heap (value TEXT);
                INSERT INTO heap(value) VALUES ('first'), ('second');
                """);

            await using SqliteConnection connection =
                new CursorSqliteConnectionFactory().Create(path);
            await connection.OpenAsync();
            CursorSqliteSchemaReader schemaReader = new();
            CursorSqliteTableSchema table =
                (await schemaReader.ReadTableAsync(connection, "heap"))!;
            CursorSqliteRowReader rowReader = new(schemaReader);

            CursorSqlitePagedResult page = await rowReader.ReadPageAsync(
                connection,
                table,
                offset: 0,
                pageSize: 10);
            CursorSqliteRowSnapshot row = page.Rows[1];
            Assert.Equal(
                CursorSqliteRowLocatorKind.RowId,
                row.Locator!.Kind);
            Assert.Equal("rowid", Assert.Single(row.Locator.Parts).ColumnName);

            CursorSqliteRowSnapshot? reloaded =
                await rowReader.ReadRowAsync(
                    connection,
                    table,
                    row.Locator);
            Assert.NotNull(reloaded);
            Assert.Equal(
                "second",
                reloaded.Cells.Single(
                    static cell => cell.Column.Name == "value").Value);

            CursorSqliteRowSnapshot? exact = await rowReader.ReadRowAsync(
                connection,
                table,
                new CursorSqliteRowLocator(
                    table.Name,
                    CursorSqliteRowLocatorKind.ExactColumns,
                    [new CursorSqliteLocatorPart("value", "second")]));
            Assert.NotNull(exact);
            Assert.Equal(
                Assert.Single(row.Locator.Parts).Value,
                Assert.Single(exact.Locator!.Parts).Value);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task RowReaderReloadsWithoutRowIdCompositeKeys()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = await CreateDatabaseAsync(
                root,
                """
                CREATE TABLE records (
                    tenant TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    value TEXT,
                    PRIMARY KEY (tenant, sequence)
                ) WITHOUT ROWID;
                INSERT INTO records(tenant, sequence, value)
                VALUES ('alpha', 1, 'one'), ('alpha', 2, 'two');
                """);

            await using SqliteConnection connection =
                new CursorSqliteConnectionFactory().Create(path);
            await connection.OpenAsync();
            CursorSqliteSchemaReader schemaReader = new();
            CursorSqliteTableSchema table =
                (await schemaReader.ReadTableAsync(connection, "records"))!;
            CursorSqliteRowReader rowReader = new(schemaReader);

            Assert.False(table.HasRowId);
            CursorSqliteRowSnapshot row = Assert.Single(
                (await rowReader.ReadPageAsync(
                    connection,
                    table,
                    offset: 1,
                    pageSize: 1)).Rows);
            Assert.Equal(
                ["tenant", "sequence"],
                row.Locator!.Parts.Select(static part => part.ColumnName));

            CursorSqliteRowSnapshot? reloaded =
                await rowReader.ReadRowAsync(
                    connection,
                    table,
                    row.Locator);
            Assert.NotNull(reloaded);
            Assert.Equal(
                "two",
                reloaded.Cells.Single(
                    static cell => cell.Column.Name == "value").Value);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task RowReaderPreservesNullBlobTextAndNumericValues()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = await CreateDatabaseAsync(
                root,
                """
                CREATE TABLE values_table (
                    id INTEGER PRIMARY KEY,
                    null_value,
                    blob_value BLOB,
                    text_value TEXT,
                    integer_value INTEGER,
                    real_value REAL
                );
                INSERT INTO values_table(
                    id,
                    null_value,
                    blob_value,
                    text_value,
                    integer_value,
                    real_value)
                VALUES (
                    1,
                    NULL,
                    X'0001FF',
                    'line1' || char(10) || 'line2',
                    42,
                    1.5);
                """);

            await using SqliteConnection connection =
                new CursorSqliteConnectionFactory().Create(path);
            await connection.OpenAsync();
            CursorSqliteSchemaReader schemaReader = new();
            CursorSqliteTableSchema table =
                (await schemaReader.ReadTableAsync(
                    connection,
                    "values_table"))!;
            CursorSqliteRowSnapshot row = Assert.Single(
                (await new CursorSqliteRowReader(schemaReader).ReadPageAsync(
                    connection,
                    table,
                    offset: 0,
                    pageSize: 10)).Rows);

            CursorSqliteCell nullCell = Cell(row, "null_value");
            Assert.Equal(CursorSqliteCellKind.Null, nullCell.Kind);
            Assert.Null(nullCell.Value);
            Assert.Null(nullCell.ByteLength);
            Assert.Equal("NULL", nullCell.Preview);

            CursorSqliteCell blobCell = Cell(row, "blob_value");
            Assert.Equal(CursorSqliteCellKind.Blob, blobCell.Kind);
            Assert.Equal(new byte[] { 0, 1, 255 }, blobCell.Value);
            Assert.Equal(3, blobCell.ByteLength);
            Assert.Equal("0x0001FF", blobCell.Preview);

            CursorSqliteCell textCell = Cell(row, "text_value");
            Assert.Equal(CursorSqliteCellKind.Text, textCell.Kind);
            Assert.Equal(11, textCell.ByteLength);
            Assert.Equal("line1\\nline2", textCell.Preview);

            CursorSqliteCell integerCell = Cell(row, "integer_value");
            Assert.Equal(CursorSqliteCellKind.Integer, integerCell.Kind);
            Assert.Equal(42L, integerCell.Value);
            Assert.Equal(2, integerCell.ByteLength);

            CursorSqliteCell realCell = Cell(row, "real_value");
            Assert.Equal(CursorSqliteCellKind.Real, realCell.Kind);
            Assert.Equal(1.5, realCell.Value);
            Assert.Equal(3, realCell.ByteLength);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task RowReaderBoundsLargeCellValuesAndKeepsOriginalLength()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = await CreateDatabaseAsync(
                root,
                """
                CREATE TABLE records (
                    key TEXT PRIMARY KEY,
                    value TEXT
                );
                INSERT INTO records(key, value)
                VALUES ('large', replace(hex(zeroblob(1024)), '00', 'ab'));
                """);

            await using SqliteConnection connection =
                new CursorSqliteConnectionFactory().Create(path);
            await connection.OpenAsync();
            CursorSqliteSchemaReader schemaReader = new();
            CursorSqliteTableSchema table =
                (await schemaReader.ReadTableAsync(connection, "records"))!;
            CursorSqliteRowReader rowReader = new(
                schemaReader,
                maximumCellBytes: 1024);

            CursorSqliteRowSnapshot row = Assert.Single(
                (await rowReader.ReadPageAsync(
                    connection,
                    table,
                    offset: 0,
                    pageSize: 10)).Rows);
            CursorSqliteCell value = Cell(row, "value");

            Assert.True(value.IsTruncated);
            Assert.Equal(2048, value.ByteLength);
            Assert.True(Assert.IsType<string>(value.Value).Length <= 256);
            Assert.NotNull(row.Locator);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task RowReaderQuotesIdentifiersDiscoveredFromSchema()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = await CreateDatabaseAsync(
                root,
                """
                CREATE TABLE "odd""records" (
                    "key" TEXT PRIMARY KEY,
                    "value""text" TEXT
                );
                INSERT INTO "odd""records"("key", "value""text")
                VALUES ('one', 'payload');
                """);

            await using SqliteConnection connection =
                new CursorSqliteConnectionFactory().Create(path);
            await connection.OpenAsync();
            CursorSqliteSchemaReader schemaReader = new();
            CursorSqliteTableSchema table =
                (await schemaReader.ReadTableAsync(
                    connection,
                    "odd\"records"))!;

            CursorSqliteRowSnapshot row = Assert.Single(
                (await new CursorSqliteRowReader(schemaReader).ReadPageAsync(
                    connection,
                    table,
                    offset: 0,
                    pageSize: 10)).Rows);
            Assert.Equal(
                "payload",
                Cell(row, "value\"text").Value);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task ConnectionFactoryAlwaysOpensDatabaseReadOnly()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = await CreateDatabaseAsync(
                root,
                """
                CREATE TABLE records (key TEXT PRIMARY KEY);
                INSERT INTO records(key) VALUES ('one');
                """);
            CursorSqliteConnectionFactory factory = new();
            CursorDatabaseDescriptor descriptor = new(
                path,
                CursorDatabaseKind.Custom);

            await using SqliteConnection connection =
                factory.Create(descriptor);
            SqliteConnectionStringBuilder settings = new(
                connection.ConnectionString);
            Assert.Equal(SqliteOpenMode.ReadOnly, settings.Mode);
            Assert.Equal(SqliteCacheMode.Private, settings.Cache);
            Assert.False(settings.Pooling);
            Assert.Equal(2, settings.DefaultTimeout);

            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO records(key) VALUES ('forbidden')";
            await Assert.ThrowsAsync<SqliteException>(
                command.ExecuteNonQueryAsync);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task DiscoveryFindsGlobalWorkspaceAndCustomDatabases()
    {
        string root = CreateTempDirectory();
        try
        {
            string globalPath = Path.Combine(
                root,
                "Cursor",
                "User",
                "globalStorage",
                "state.vscdb");
            await CreateDatabaseAsync(
                Path.GetDirectoryName(globalPath)!,
                "CREATE TABLE global_table (key TEXT);",
                globalPath);

            string workspacePath = Path.Combine(root, "work", "repo");
            Directory.CreateDirectory(workspacePath);
            string workspaceDirectory = Path.Combine(
                root,
                "Cursor",
                "User",
                "workspaceStorage",
                "workspace-one");
            string workspaceDatabase = Path.Combine(
                workspaceDirectory,
                "state.vscdb");
            await CreateDatabaseAsync(
                workspaceDirectory,
                "CREATE TABLE workspace_table (key TEXT);",
                workspaceDatabase);
            string manifestPath = Path.Combine(
                workspaceDirectory,
                "workspace.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(new
                {
                    folder = new Uri(workspacePath).AbsoluteUri
                }));

            string noManifestDirectory = Path.Combine(
                root,
                "Cursor",
                "User",
                "workspaceStorage",
                "workspace-two");
            string noManifestDatabase = Path.Combine(
                noManifestDirectory,
                "state.vscdb");
            await CreateDatabaseAsync(
                noManifestDirectory,
                "CREATE TABLE workspace_table (key TEXT);",
                noManifestDatabase);

            string manifestOnlyDirectory = Path.Combine(
                root,
                "Cursor",
                "User",
                "workspaceStorage",
                "without-database");
            Directory.CreateDirectory(manifestOnlyDirectory);
            string manifestOnlyPath = Path.Combine(
                manifestOnlyDirectory,
                "workspace.json");
            await File.WriteAllTextAsync(
                manifestOnlyPath,
                JsonSerializer.Serialize(new
                {
                    folder = new Uri(workspacePath).AbsoluteUri
                }));
            string encodedDriveDirectory = Path.Combine(
                root,
                "Cursor",
                "User",
                "workspaceStorage",
                "encoded-drive");
            Directory.CreateDirectory(encodedDriveDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(encodedDriveDirectory, "workspace.json"),
                """{"folder":"file:///c%3A/Conferences/2026_CLITools"}""");

            CursorDatabaseDiscovery discovery = new(root);
            IReadOnlyList<CursorDatabaseDescriptor> databases =
                await discovery.DiscoverAsync();

            Assert.Equal(3, databases.Count);
            CursorDatabaseDescriptor global = databases[0];
            Assert.Equal(CursorDatabaseKind.Global, global.Kind);
            Assert.Equal(Path.GetFullPath(globalPath), global.Path);

            CursorDatabaseDescriptor workspace = databases[1];
            Assert.Equal(CursorDatabaseKind.Workspace, workspace.Kind);
            Assert.Equal("workspace-one", workspace.WorkspaceStorageId);
            Assert.Equal(Path.GetFullPath(manifestPath), workspace.WorkspaceManifestPath);
            Assert.Equal(
                Path.GetFullPath(workspacePath),
                Path.GetFullPath(workspace.WorkspacePath!));

            CursorDatabaseDescriptor withoutManifest = databases[2];
            Assert.Equal("workspace-two", withoutManifest.WorkspaceStorageId);
            Assert.Null(withoutManifest.WorkspaceManifestPath);
            Assert.Null(withoutManifest.WorkspacePath);

            CursorDatabaseDescriptor custom =
                discovery.CreateCustomDatabase(workspaceDatabase);
            Assert.Equal(CursorDatabaseKind.Custom, custom.Kind);
            Assert.Equal(Path.GetFullPath(workspaceDatabase), custom.Path);
            Assert.Null(custom.WorkspaceStorageId);

            IReadOnlyList<CursorWorkspaceEvidence> workspaces =
                await discovery.DiscoverWorkspaceEvidenceAsync();
            Assert.Equal(3, workspaces.Count);
            CursorWorkspaceEvidence manifestOnly = workspaces.Single(item =>
                item.StorageId == "without-database");
            Assert.Equal(
                Path.GetFullPath(manifestOnlyPath),
                manifestOnly.ManifestPath);
            Assert.Null(manifestOnly.DatabasePath);
            Assert.Equal(
                Path.GetFullPath(workspacePath),
                Path.GetFullPath(manifestOnly.WorkspacePath!));
            CursorWorkspaceEvidence encodedDrive = workspaces.Single(item =>
                item.StorageId == "encoded-drive");
            Assert.Equal(
                Path.GetFullPath(
                    @"C:\Conferences\2026_CLITools"),
                encodedDrive.WorkspacePath,
                ignoreCase: true);
            Assert.False(encodedDrive.WorkspacePath!.StartsWith(
                "/",
                StringComparison.Ordinal));
            Assert.DoesNotContain("/", encodedDrive.WorkspacePath);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private string RowKey(CursorSqliteRowSnapshot row) =>
        Assert.IsType<string>(Cell(row, "key").Value);

    private CursorSqliteCell Cell(
        CursorSqliteRowSnapshot row,
        string columnName) =>
        row.Cells.Single(cell => string.Equals(
            cell.Column.Name,
            columnName,
            StringComparison.Ordinal));

    private string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "HarnessSpy-CursorSqliteInspection-Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private Task<string> CreateDatabaseAsync(
        string root,
        string sql,
        string? databasePath = null) =>
        CreateDatabaseCoreAsync(
            databasePath ?? Path.Combine(root, "state.vscdb"),
            sql);

    private async Task<string> CreateDatabaseCoreAsync(
        string path,
        string sql)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using SqliteConnection connection = new($"Data Source={path}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
        return path;
    }

    private void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
