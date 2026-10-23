using System.Collections.ObjectModel;

namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public enum CursorDatabaseKind
{
    Global,
    Workspace,
    Custom
}

public sealed record CursorDatabaseDescriptor
{
    public CursorDatabaseDescriptor(
        string path,
        CursorDatabaseKind kind,
        string? workspaceStorageId = null,
        string? workspaceManifestPath = null,
        string? workspacePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Path = System.IO.Path.GetFullPath(path);
        Kind = kind;
        WorkspaceStorageId = workspaceStorageId;
        WorkspaceManifestPath = workspaceManifestPath;
        WorkspacePath = workspacePath;
    }

    public string Path { get; }

    public CursorDatabaseKind Kind { get; }

    public string? WorkspaceStorageId { get; }

    public string? WorkspaceManifestPath { get; }

    public string? WorkspacePath { get; }
}

public sealed record CursorSqliteColumnSchema
{
    public CursorSqliteColumnSchema(
        int ordinal,
        string name,
        string declaredType,
        bool isNullable,
        string? defaultValue,
        int? primaryKeyOrder,
        bool isHidden)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Ordinal = ordinal;
        Name = name;
        DeclaredType = declaredType ?? string.Empty;
        IsNullable = isNullable;
        DefaultValue = defaultValue;
        PrimaryKeyOrder = primaryKeyOrder is > 0
            ? primaryKeyOrder
            : null;
        IsHidden = isHidden;
    }

    public int Ordinal { get; }

    public string Name { get; }

    public string DeclaredType { get; }

    public bool IsNullable { get; }

    public string? DefaultValue { get; }

    public int? PrimaryKeyOrder { get; }

    public bool IsHidden { get; }

    public bool IsPrimaryKey => PrimaryKeyOrder.HasValue;

    public bool IsKeyLike =>
        IsPrimaryKey ||
        string.Equals(Name, "key", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Name, "id", StringComparison.OrdinalIgnoreCase) ||
        Name.EndsWith("Id", StringComparison.Ordinal) ||
        Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase) ||
        Name.EndsWith("Key", StringComparison.Ordinal) ||
        Name.EndsWith("_key", StringComparison.OrdinalIgnoreCase);
}

public sealed class CursorSqliteTableSchema
{
    private readonly ReadOnlyCollection<CursorSqliteColumnSchema> _columns;
    private readonly ReadOnlyCollection<CursorSqliteColumnSchema>
        _primaryKeyColumns;

    public CursorSqliteTableSchema(
        string name,
        string? createSql,
        IEnumerable<CursorSqliteColumnSchema> columns,
        string? rowIdAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);

        Name = name;
        CreateSql = createSql;
        _columns = Array.AsReadOnly(
            columns.OrderBy(static column => column.Ordinal).ToArray());
        _primaryKeyColumns = Array.AsReadOnly(
            _columns
                .Where(static column => column.IsPrimaryKey)
                .OrderBy(static column => column.PrimaryKeyOrder)
                .ToArray());
        RowIdAlias = rowIdAlias;
    }

    public string Name { get; }

    public string? CreateSql { get; }

    public IReadOnlyList<CursorSqliteColumnSchema> Columns => _columns;

    public IReadOnlyList<CursorSqliteColumnSchema> PrimaryKeyColumns =>
        _primaryKeyColumns;

    public string? RowIdAlias { get; }

    public bool HasRowId => RowIdAlias is not null;

    public bool SupportsExactLookup =>
        _primaryKeyColumns.Count > 0 || HasRowId;

    public CursorSqliteColumnSchema? FindColumn(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _columns.FirstOrDefault(column =>
            string.Equals(
                column.Name,
                name,
                StringComparison.OrdinalIgnoreCase));
    }
}

public enum CursorSqliteRowLocatorKind
{
    PrimaryKey,
    RowId,
    ExactColumns
}

public sealed class CursorSqliteLocatorPart
{
    private readonly object? _value;

    public CursorSqliteLocatorPart(string columnName, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        ColumnName = columnName;
        _value = CopyValue(value);
    }

    public string ColumnName { get; }

    public object? Value => CopyValue(_value);

    internal object? ParameterValue => _value;

    private object? CopyValue(object? value) =>
        value switch
        {
            DBNull => null,
            byte[] bytes => bytes.ToArray(),
            _ => value
        };
}

public sealed class CursorSqliteRowLocator
{
    private readonly ReadOnlyCollection<CursorSqliteLocatorPart> _parts;

    public CursorSqliteRowLocator(
        string tableName,
        CursorSqliteRowLocatorKind kind,
        IEnumerable<CursorSqliteLocatorPart> parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(parts);

        CursorSqliteLocatorPart[] snapshot = parts.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException(
                "A row locator must contain at least one value.",
                nameof(parts));
        }

        TableName = tableName;
        Kind = kind;
        _parts = Array.AsReadOnly(snapshot);
    }

    public string TableName { get; }

    public CursorSqliteRowLocatorKind Kind { get; }

    public IReadOnlyList<CursorSqliteLocatorPart> Parts => _parts;
}

public enum CursorSqliteCellKind
{
    Null,
    Integer,
    Real,
    Text,
    Blob
}

public sealed class CursorSqliteCell
{
    private readonly object? _value;

    public CursorSqliteCell(
        CursorSqliteColumnSchema column,
        CursorSqliteCellKind kind,
        object? value,
        long? byteLength,
        string preview,
        bool isTruncated = false)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(preview);
        if (byteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        }

        Column = column;
        Kind = kind;
        _value = CopyValue(value);
        ByteLength = byteLength;
        Preview = preview;
        IsTruncated = isTruncated;
    }

    public CursorSqliteColumnSchema Column { get; }

    public CursorSqliteCellKind Kind { get; }

    public object? Value => CopyValue(_value);

    public long? ByteLength { get; }

    public string Preview { get; }

    public bool IsTruncated { get; }

    internal object? ParameterValue => _value;

    private object? CopyValue(object? value) =>
        value switch
        {
            DBNull => null,
            byte[] bytes => bytes.ToArray(),
            _ => value
        };
}

public sealed class CursorSqliteRowSnapshot
{
    private readonly ReadOnlyCollection<CursorSqliteCell> _cells;

    public CursorSqliteRowSnapshot(
        CursorSqliteTableSchema table,
        CursorSqliteRowLocator? locator,
        IEnumerable<CursorSqliteCell> cells)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(cells);

        Table = table;
        Locator = locator;
        _cells = Array.AsReadOnly(cells.ToArray());
    }

    public CursorSqliteTableSchema Table { get; }

    public CursorSqliteRowLocator? Locator { get; }

    public IReadOnlyList<CursorSqliteCell> Cells => _cells;
}

public enum CursorSqliteFilterMode
{
    Exact,
    Prefix
}

public sealed record CursorSqliteRowFilter
{
    public CursorSqliteRowFilter(
        string columnName,
        string value,
        CursorSqliteFilterMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        ArgumentNullException.ThrowIfNull(value);

        ColumnName = columnName;
        Value = value;
        Mode = mode;
    }

    public string ColumnName { get; }

    public string Value { get; }

    public CursorSqliteFilterMode Mode { get; }
}

public sealed class CursorSqlitePagedResult
{
    private readonly ReadOnlyCollection<CursorSqliteRowSnapshot> _rows;

    public CursorSqlitePagedResult(
        long offset,
        int pageSize,
        IEnumerable<CursorSqliteRowSnapshot> rows,
        bool hasMore)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        ArgumentNullException.ThrowIfNull(rows);

        Offset = offset;
        PageSize = pageSize;
        _rows = Array.AsReadOnly(rows.ToArray());
        HasMore = hasMore;
    }

    public long Offset { get; }

    public int PageSize { get; }

    public IReadOnlyList<CursorSqliteRowSnapshot> Rows => _rows;

    public bool HasMore { get; }

    public long NextOffset => Offset + _rows.Count;
}
