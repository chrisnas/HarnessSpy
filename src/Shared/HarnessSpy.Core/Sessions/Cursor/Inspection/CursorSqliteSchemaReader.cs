using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public sealed class CursorSqliteSchemaReader
{
    private readonly int _commandTimeoutSeconds;

    private sealed record TableEntry(string Name, string? CreateSql);

    public CursorSqliteSchemaReader(int commandTimeoutSeconds = 2)
    {
        if (commandTimeoutSeconds is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeoutSeconds));
        }

        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    public async Task<IReadOnlyList<CursorSqliteTableSchema>> ReadTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();

        List<TableEntry> entries = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT name, sql
                FROM sqlite_master
                WHERE type = 'table'
                  AND name NOT LIKE 'sqlite\_%' ESCAPE '\'
                ORDER BY name COLLATE NOCASE, name
                """;
            command.CommandTimeout = _commandTimeoutSeconds;
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                entries.Add(new TableEntry(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }

        List<CursorSqliteTableSchema> tables = [];
        foreach (TableEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CursorSqliteTableSchema? table = await ReadDiscoveredTableAsync(
                connection,
                entry,
                cancellationToken).ConfigureAwait(false);
            if (table is not null)
            {
                tables.Add(table);
            }
        }

        return tables;
    }

    public async Task<CursorSqliteTableSchema?> ReadTableAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        cancellationToken.ThrowIfCancellationRequested();

        TableEntry? entry = await FindTableAsync(
            connection,
            tableName,
            cancellationToken).ConfigureAwait(false);
        return entry is null
            ? null
            : await ReadDiscoveredTableAsync(
                connection,
                entry,
                cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        cancellationToken.ThrowIfCancellationRequested();

        return await FindTableAsync(
            connection,
            tableName,
            cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task<TableEntry?> FindTableAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name, sql
            FROM sqlite_master
            WHERE type = 'table'
              AND name = $name
              AND name NOT LIKE 'sqlite\_%' ESCAPE '\'
            LIMIT 1
            """;
        command.CommandTimeout = _commandTimeoutSeconds;
        command.Parameters.AddWithValue("$name", tableName);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.IsDBNull(0))
        {
            return null;
        }

        return new TableEntry(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private async Task<CursorSqliteTableSchema?> ReadDiscoveredTableAsync(
        SqliteConnection connection,
        TableEntry entry,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CursorSqliteColumnSchema> columns;
        try
        {
            columns = await ReadColumnsWithPragmaAsync(
                connection,
                entry.Name,
                "table_xinfo",
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            columns = await ReadColumnsWithPragmaAsync(
                connection,
                entry.Name,
                "table_info",
                cancellationToken).ConfigureAwait(false);
        }

        if (columns.Count == 0)
        {
            columns = await ReadColumnsWithPragmaAsync(
                connection,
                entry.Name,
                "table_info",
                cancellationToken).ConfigureAwait(false);
        }

        if (columns.Count == 0 &&
            !await TableExistsAsync(
                connection,
                entry.Name,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string? rowIdAlias = await FindRowIdAliasAsync(
            connection,
            entry.Name,
            columns,
            cancellationToken).ConfigureAwait(false);
        return new CursorSqliteTableSchema(
            entry.Name,
            entry.CreateSql,
            columns,
            rowIdAlias);
    }

    private async Task<IReadOnlyList<CursorSqliteColumnSchema>>
        ReadColumnsWithPragmaAsync(
            SqliteConnection connection,
            string tableName,
            string pragma,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA {pragma}({QuoteIdentifier(tableName)})";
        command.CommandTimeout = _commandTimeoutSeconds;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

        Dictionary<string, int> ordinals =
            new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < reader.FieldCount; index++)
        {
            ordinals[reader.GetName(index)] = index;
        }

        List<CursorSqliteColumnSchema> columns = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int ordinal = Integer(reader, ordinals, "cid", -1);
            string? name = Text(reader, ordinals, "name");
            if (ordinal < 0 || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            int primaryKeyOrder = Integer(reader, ordinals, "pk", 0);
            columns.Add(new CursorSqliteColumnSchema(
                ordinal,
                name,
                Text(reader, ordinals, "type") ?? string.Empty,
                Integer(reader, ordinals, "notnull", 0) == 0,
                Text(reader, ordinals, "dflt_value"),
                primaryKeyOrder > 0 ? primaryKeyOrder : null,
                Integer(reader, ordinals, "hidden", 0) != 0));
        }

        return columns
            .OrderBy(static column => column.Ordinal)
            .ToArray();
    }

    private async Task<string?> FindRowIdAliasAsync(
        SqliteConnection connection,
        string tableName,
        IReadOnlyList<CursorSqliteColumnSchema> columns,
        CancellationToken cancellationToken)
    {
        string[] candidates = ["rowid", "_rowid_", "oid"];
        foreach (string candidate in candidates)
        {
            if (columns.Any(column => string.Equals(
                    column.Name,
                    candidate,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT {candidate} FROM {QuoteIdentifier(tableName)} LIMIT 0";
                command.CommandTimeout = _commandTimeoutSeconds;
                await using SqliteDataReader reader =
                    await command.ExecuteReaderAsync(cancellationToken)
                        .ConfigureAwait(false);
                return candidate;
            }
            catch (SqliteException)
            {
                // WITHOUT ROWID tables reject all three aliases.
            }
        }

        return null;
    }

    private int Integer(
        SqliteDataReader reader,
        IReadOnlyDictionary<string, int> ordinals,
        string name,
        int fallback)
    {
        if (!ordinals.TryGetValue(name, out int ordinal) ||
            reader.IsDBNull(ordinal))
        {
            return fallback;
        }

        try
        {
            return Convert.ToInt32(
                reader.GetValue(ordinal),
                CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidCastException or
            OverflowException)
        {
            return fallback;
        }
    }

    private string? Text(
        SqliteDataReader reader,
        IReadOnlyDictionary<string, int> ordinals,
        string name)
    {
        if (!ordinals.TryGetValue(name, out int ordinal) ||
            reader.IsDBNull(ordinal))
        {
            return null;
        }

        return Convert.ToString(
            reader.GetValue(ordinal),
            CultureInfo.InvariantCulture);
    }

    private string QuoteIdentifier(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
