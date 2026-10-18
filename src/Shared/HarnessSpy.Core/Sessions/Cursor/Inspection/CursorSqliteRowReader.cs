using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public sealed class CursorSqliteRowReader
{
    private readonly CursorSqliteSchemaReader _schemaReader;
    private readonly int _maximumPageSize;
    private readonly int _previewLength;
    private readonly int _maximumCellBytes;
    private readonly int _commandTimeoutSeconds;

    public CursorSqliteRowReader(
        CursorSqliteSchemaReader? schemaReader = null,
        int maximumPageSize = 500,
        int previewLength = 160,
        int maximumCellBytes = 256 * 1024,
        int commandTimeoutSeconds = 2)
    {
        if (maximumPageSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPageSize));
        }

        if (previewLength is < 16 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(previewLength));
        }

        if (maximumCellBytes is < 1024 or > 16 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCellBytes));
        }

        if (commandTimeoutSeconds is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeoutSeconds));
        }

        _schemaReader = schemaReader ?? new CursorSqliteSchemaReader(
            commandTimeoutSeconds);
        _maximumPageSize = maximumPageSize;
        _previewLength = previewLength;
        _maximumCellBytes = maximumCellBytes;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    public int MaximumPageSize => _maximumPageSize;

    public int MaximumCellBytes => _maximumCellBytes;

    public async Task<CursorSqlitePagedResult> ReadPageAsync(
        SqliteConnection connection,
        CursorSqliteTableSchema table,
        long offset,
        int pageSize,
        CursorSqliteRowFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (pageSize is < 1 || pageSize > _maximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        cancellationToken.ThrowIfCancellationRequested();
        CursorSqliteTableSchema currentTable = await ResolveTableAsync(
            connection,
            table.Name,
            cancellationToken).ConfigureAwait(false);
        CursorSqliteColumnSchema? filterColumn = ResolveFilterColumn(
            currentTable,
            filter);
        bool includeRowId = UsesRowId(currentTable);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = BuildPageSql(
            currentTable,
            includeRowId,
            filter,
            filterColumn);
        command.CommandTimeout = _commandTimeoutSeconds;
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue(
            "$maximumCellBytes",
            _maximumCellBytes);
        if (filter is not null)
        {
            command.Parameters.AddWithValue("$filter", filter.Value);
        }

        List<CursorSqliteRowSnapshot> rows = await ReadRowsAsync(
            command,
            currentTable,
            includeRowId,
            cancellationToken).ConfigureAwait(false);
        bool hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new CursorSqlitePagedResult(
            offset,
            pageSize,
            rows,
            hasMore);
    }

    public async Task<CursorSqliteRowSnapshot?> ReadRowAsync(
        SqliteConnection connection,
        CursorSqliteTableSchema table,
        CursorSqliteRowLocator locator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(locator);
        cancellationToken.ThrowIfCancellationRequested();

        CursorSqliteTableSchema currentTable = await ResolveTableAsync(
            connection,
            table.Name,
            cancellationToken).ConfigureAwait(false);
        ValidateLocator(currentTable, locator);
        bool includeRowId = UsesRowId(currentTable);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = BuildExactRowSql(
            currentTable,
            locator,
            includeRowId);
        command.CommandTimeout = _commandTimeoutSeconds;
        command.Parameters.AddWithValue(
            "$maximumCellBytes",
            _maximumCellBytes);
        for (int index = 0; index < locator.Parts.Count; index++)
        {
            command.Parameters.AddWithValue(
                $"$locator{index}",
                locator.Parts[index].ParameterValue ?? DBNull.Value);
        }

        List<CursorSqliteRowSnapshot> rows = await ReadRowsAsync(
            command,
            currentTable,
            includeRowId,
            cancellationToken).ConfigureAwait(false);
        if (rows.Count > 1)
        {
            throw new InvalidOperationException(
                "The row locator matched more than one row.");
        }

        return rows.SingleOrDefault();
    }

    private async Task<CursorSqliteTableSchema> ResolveTableAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        CursorSqliteTableSchema? table = await _schemaReader.ReadTableAsync(
            connection,
            tableName,
            cancellationToken).ConfigureAwait(false);
        return table ?? throw new InvalidOperationException(
            $"Table '{tableName}' is no longer present in the database.");
    }

    private CursorSqliteColumnSchema? ResolveFilterColumn(
        CursorSqliteTableSchema table,
        CursorSqliteRowFilter? filter)
    {
        if (filter is null)
        {
            return null;
        }

        CursorSqliteColumnSchema? column = table.FindColumn(filter.ColumnName);
        if (column is null || !column.IsKeyLike)
        {
            throw new ArgumentException(
                $"Column '{filter.ColumnName}' is not a discovered key-like " +
                $"column in table '{table.Name}'.",
                nameof(filter));
        }

        return column;
    }

    private string BuildPageSql(
        CursorSqliteTableSchema table,
        bool includeRowId,
        CursorSqliteRowFilter? filter,
        CursorSqliteColumnSchema? filterColumn)
    {
        StringBuilder sql = new();
        sql.Append("SELECT ");
        sql.Append(SelectList(table, includeRowId));
        sql.Append(" FROM ");
        sql.Append(QuoteIdentifier(table.Name));

        if (filter is not null && filterColumn is not null)
        {
            sql.Append(" WHERE ");
            if (filter.Mode == CursorSqliteFilterMode.Exact)
            {
                sql.Append("CAST(");
                sql.Append(QuoteIdentifier(filterColumn.Name));
                sql.Append(" AS TEXT) = $filter COLLATE BINARY");
            }
            else
            {
                sql.Append("substr(CAST(");
                sql.Append(QuoteIdentifier(filterColumn.Name));
                sql.Append(
                    " AS TEXT), 1, length($filter)) = $filter COLLATE BINARY");
            }
        }

        AppendOrderBy(sql, table, includeRowId);
        sql.Append(" LIMIT $limit OFFSET $offset");
        return sql.ToString();
    }

    private string BuildExactRowSql(
        CursorSqliteTableSchema table,
        CursorSqliteRowLocator locator,
        bool includeRowId)
    {
        StringBuilder sql = new();
        sql.Append("SELECT ");
        sql.Append(SelectList(table, includeRowId));
        sql.Append(" FROM ");
        sql.Append(QuoteIdentifier(table.Name));
        sql.Append(" WHERE ");
        for (int index = 0; index < locator.Parts.Count; index++)
        {
            if (index > 0)
            {
                sql.Append(" AND ");
            }

            string columnName = locator.Parts[index].ColumnName;
            if (locator.Kind == CursorSqliteRowLocatorKind.RowId)
            {
                sql.Append(columnName);
                sql.Append(" = ");
            }
            else
            {
                sql.Append(QuoteIdentifier(columnName));
                sql.Append(" IS ");
            }

            sql.Append($"$locator{index}");
        }

        sql.Append(" LIMIT 2");
        return sql.ToString();
    }

    private string SelectList(
        CursorSqliteTableSchema table,
        bool includeRowId)
    {
        List<string> expressions = [];
        if (includeRowId)
        {
            expressions.Add(table.RowIdAlias!);
        }

        expressions.AddRange(table.Columns.Select(column =>
        {
            string name = QuoteIdentifier(column.Name);
            string length = $"length(CAST({name} AS BLOB))";
            return
                $"CASE WHEN {name} IS NULL OR {length} <= $maximumCellBytes " +
                $"THEN {name} WHEN typeof({name}) = 'text' " +
                $"THEN substr({name}, 1, $maximumCellBytes / 4) " +
                $"ELSE substr({name}, 1, $maximumCellBytes) END";
        }));
        expressions.AddRange(
            table.Columns.Select(column =>
                $"length(CAST({QuoteIdentifier(column.Name)} AS BLOB))"));
        if (expressions.Count == 0)
        {
            throw new InvalidOperationException(
                $"Table '{table.Name}' has no readable columns.");
        }

        return string.Join(", ", expressions);
    }

    private void AppendOrderBy(
        StringBuilder sql,
        CursorSqliteTableSchema table,
        bool includeRowId)
    {
        if (table.PrimaryKeyColumns.Count > 0)
        {
            sql.Append(" ORDER BY ");
            sql.Append(string.Join(
                ", ",
                table.PrimaryKeyColumns.Select(column =>
                    QuoteIdentifier(column.Name))));
        }
        else if (includeRowId)
        {
            sql.Append(" ORDER BY ");
            sql.Append(table.RowIdAlias);
        }
    }

    private async Task<List<CursorSqliteRowSnapshot>> ReadRowsAsync(
        SqliteCommand command,
        CursorSqliteTableSchema table,
        bool includeRowId,
        CancellationToken cancellationToken)
    {
        List<CursorSqliteRowSnapshot> rows = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(CreateRow(reader, table, includeRowId));
        }

        return rows;
    }

    private CursorSqliteRowSnapshot CreateRow(
        SqliteDataReader reader,
        CursorSqliteTableSchema table,
        bool includeRowId)
    {
        int valueOffset = includeRowId ? 1 : 0;
        object? rowId = includeRowId
            ? DatabaseValue(reader, 0)
            : null;
        List<CursorSqliteCell> cells = [];
        int lengthOffset = valueOffset + table.Columns.Count;
        for (int index = 0; index < table.Columns.Count; index++)
        {
            object? value = DatabaseValue(reader, valueOffset + index);
            long? byteLength = reader.IsDBNull(lengthOffset + index)
                ? null
                : reader.GetInt64(lengthOffset + index);
            cells.Add(CreateCell(
                table.Columns[index],
                value,
                byteLength,
                byteLength > _maximumCellBytes));
        }

        CursorSqliteRowLocator? locator = CreateLocator(
            table,
            cells,
            rowId);
        return new CursorSqliteRowSnapshot(table, locator, cells);
    }

    private CursorSqliteRowLocator? CreateLocator(
        CursorSqliteTableSchema table,
        IReadOnlyList<CursorSqliteCell> cells,
        object? rowId)
    {
        if (table.PrimaryKeyColumns.Count > 0)
        {
            List<CursorSqliteLocatorPart> parts = [];
            foreach (CursorSqliteColumnSchema keyColumn
                     in table.PrimaryKeyColumns)
            {
                CursorSqliteCell cell = cells.Single(item =>
                    string.Equals(
                        item.Column.Name,
                        keyColumn.Name,
                        StringComparison.OrdinalIgnoreCase));
                if (cell.IsTruncated)
                {
                    return null;
                }

                parts.Add(new CursorSqliteLocatorPart(
                    keyColumn.Name,
                    cell.ParameterValue));
            }

            return new CursorSqliteRowLocator(
                table.Name,
                CursorSqliteRowLocatorKind.PrimaryKey,
                parts);
        }

        if (table.RowIdAlias is not null && rowId is not null)
        {
            return new CursorSqliteRowLocator(
                table.Name,
                CursorSqliteRowLocatorKind.RowId,
                [new CursorSqliteLocatorPart(table.RowIdAlias, rowId)]);
        }

        return null;
    }

    private CursorSqliteCell CreateCell(
        CursorSqliteColumnSchema column,
        object? value,
        long? byteLength,
        bool isTruncated)
    {
        CursorSqliteCellKind kind = value switch
        {
            null => CursorSqliteCellKind.Null,
            byte[] => CursorSqliteCellKind.Blob,
            string => CursorSqliteCellKind.Text,
            float or double or decimal => CursorSqliteCellKind.Real,
            sbyte or byte or short or ushort or int or uint or long or ulong =>
                CursorSqliteCellKind.Integer,
            _ => CursorSqliteCellKind.Text
        };
        return new CursorSqliteCell(
            column,
            kind,
            value,
            byteLength,
            Preview(value),
            isTruncated);
    }

    private string Preview(object? value)
    {
        if (value is null)
        {
            return "NULL";
        }

        if (value is byte[] bytes)
        {
            int maximumBytes = Math.Max(1, (_previewLength - 3) / 2);
            int count = Math.Min(bytes.Length, maximumBytes);
            string hex = "0x" + Convert.ToHexString(bytes.AsSpan(0, count));
            return count < bytes.Length ? TrimPreview(hex + "…") : hex;
        }

        string text = Convert.ToString(
            value,
            CultureInfo.InvariantCulture) ?? string.Empty;
        text = text
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return TrimPreview(text);
    }

    private string TrimPreview(string value)
    {
        if (value.Length <= _previewLength)
        {
            return value;
        }

        return value[..(_previewLength - 1)] + "…";
    }

    private void ValidateLocator(
        CursorSqliteTableSchema table,
        CursorSqliteRowLocator locator)
    {
        if (!string.Equals(
            table.Name,
            locator.TableName,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The row locator belongs to a different table.",
                nameof(locator));
        }

        if (locator.Kind == CursorSqliteRowLocatorKind.ExactColumns)
        {
            if (locator.Parts.Any(part =>
                    table.FindColumn(part.ColumnName) is null))
            {
                throw new ArgumentException(
                    "The row locator names a column that is not in the table.",
                    nameof(locator));
            }

            return;
        }

        if (table.PrimaryKeyColumns.Count > 0)
        {
            if (locator.Kind != CursorSqliteRowLocatorKind.PrimaryKey ||
                locator.Parts.Count != table.PrimaryKeyColumns.Count)
            {
                throw new ArgumentException(
                    "The row locator does not match the declared primary key.",
                    nameof(locator));
            }

            for (int index = 0;
                 index < table.PrimaryKeyColumns.Count;
                 index++)
            {
                if (!string.Equals(
                    table.PrimaryKeyColumns[index].Name,
                    locator.Parts[index].ColumnName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "The row locator does not match the declared primary key.",
                        nameof(locator));
                }
            }

            return;
        }

        if (table.RowIdAlias is null ||
            locator.Kind != CursorSqliteRowLocatorKind.RowId ||
            locator.Parts.Count != 1 ||
            !string.Equals(
                table.RowIdAlias,
                locator.Parts[0].ColumnName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The row locator does not match the table rowid.",
                nameof(locator));
        }
    }

    private bool UsesRowId(CursorSqliteTableSchema table) =>
        table.PrimaryKeyColumns.Count == 0 && table.RowIdAlias is not null;

    private object? DatabaseValue(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);

    private string QuoteIdentifier(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
