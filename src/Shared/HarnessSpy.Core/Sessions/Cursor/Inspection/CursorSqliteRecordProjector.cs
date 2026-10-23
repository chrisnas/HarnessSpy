using System.Globalization;

namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public sealed class CursorSqliteRecordProjector
{
    private readonly CursorRecordClassifier _classifier;
    private readonly CursorComposerReferenceExtractor _referenceExtractor;

    public CursorSqliteRecordProjector(
        CursorRecordClassifier? classifier = null,
        CursorComposerReferenceExtractor? referenceExtractor = null)
    {
        _classifier = classifier ?? new CursorRecordClassifier();
        _referenceExtractor = referenceExtractor ??
            new CursorComposerReferenceExtractor();
    }

    public CursorIndexedRecord Project(
        CursorDatabaseDescriptor database,
        CursorSqliteRowSnapshot row,
        IReadOnlySet<string> knownComposerIds,
        long? pageOrdinal = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(knownComposerIds);

        IReadOnlyDictionary<string, object?> cells = Cells(row);
        CursorRecordClassification classification = _classifier.Classify(
            row.Table.Name,
            cells,
            knownComposerIds);
        IReadOnlyList<CursorLogicalReference> references =
            _referenceExtractor.Extract(classification);
        return new CursorIndexedRecord(
            Reference(database, row, pageOrdinal),
            classification with { RawJson = null },
            references);
    }

    public IReadOnlyDictionary<string, object?> Cells(
        CursorSqliteRowSnapshot row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Cells.ToDictionary(
            cell => cell.Column.Name,
            cell => cell.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public CursorRowReference Reference(
        CursorDatabaseDescriptor database,
        CursorSqliteRowSnapshot row,
        long? pageOrdinal = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(row);

        if (row.Locator?.Kind == CursorSqliteRowLocatorKind.PrimaryKey)
        {
            return new CursorRowReference(
                database.Path,
                row.Table.Name,
                row.Locator.Parts.ToDictionary(
                    part => part.ColumnName,
                    part => part.Value,
                    StringComparer.OrdinalIgnoreCase));
        }

        if (row.Locator?.Kind == CursorSqliteRowLocatorKind.RowId &&
            row.Locator.Parts[0].Value is object value)
        {
            return new CursorRowReference(
                database.Path,
                row.Table.Name,
                new Dictionary<string, object?>(
                    StringComparer.OrdinalIgnoreCase),
                Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }

        return new CursorRowReference(
            database.Path,
            row.Table.Name,
            new Dictionary<string, object?>(
                StringComparer.OrdinalIgnoreCase),
            PageOrdinal: pageOrdinal);
    }
}
