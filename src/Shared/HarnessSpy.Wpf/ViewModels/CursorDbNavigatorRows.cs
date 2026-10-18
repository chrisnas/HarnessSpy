using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using HarnessSpy.Core.Sessions.Cursor.Inspection;
using HarnessSpy.Wpf.Input;

namespace HarnessSpy.Wpf.ViewModels;

public enum CursorSchemaNodeKind
{
    Database,
    Table
}

public sealed class CursorSchemaNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;

    public CursorSchemaNodeViewModel(
        CursorSchemaNodeKind kind,
        string header,
        string summary,
        object model,
        string? badge = null)
    {
        Kind = kind;
        Header = header;
        Summary = summary;
        Model = model;
        Badge = badge ?? string.Empty;
    }

    public CursorSchemaNodeKind Kind { get; }

    public string Header { get; }

    public string Summary { get; }

    public object Model { get; }

    public string Badge { get; }

    public bool HasBadge => Badge.Length > 0;

    public FontWeight FontWeight =>
        Kind == CursorSchemaNodeKind.Database
            ? FontWeights.SemiBold
            : FontWeights.Normal;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ObservableCollection<CursorSchemaNodeViewModel> Children { get; } =
        [];
}

public sealed class CursorRowViewModel
{
    public CursorRowViewModel(
        object source,
        CursorRowReference reference,
        IReadOnlyDictionary<string, object?> cells,
        CursorRecordClassification classification,
        long valueBytes,
        string preview)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        Cells = cells ?? throw new ArgumentNullException(nameof(cells));
        Classification = classification ??
            throw new ArgumentNullException(nameof(classification));
        LocatorText = reference.KeyValues.Count > 0
            ? string.Join(
                ", ",
                reference.KeyValues.Select(item => $"{item.Key}={item.Value}"))
            : reference.RowId is null
                ? "page row"
                : $"rowid {reference.RowId}";
        KeyText = classification.StorageKey ??
            Text(Cell("composerId")) ??
            string.Empty;
        RecordKindText = classification.Kind.ToString();
        SizeText = valueBytes < 0 ? string.Empty : FormatBytes(valueBytes);
        Preview = preview;
        RawText = classification.RawJson ??
            Text(Cell("value")) ??
            string.Empty;
        JsonText = FormatJson(RawText);
    }

    public object Source { get; }

    public CursorRowReference Reference { get; }

    public IReadOnlyDictionary<string, object?> Cells { get; }

    public CursorRecordClassification Classification { get; }

    public string LocatorText { get; }

    public string KeyText { get; }

    public string RecordKindText { get; }

    public string SizeText { get; }

    public string Preview { get; }

    public string RawText { get; }

    public string JsonText { get; }

    private object? Cell(string name)
    {
        foreach ((string key, object? value) in Cells)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private string? Text(object? value) =>
        value switch
        {
            null or DBNull => null,
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => Convert.ToString(value)
        };

    public static string FormatJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(
                document.RootElement,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes:N0} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:N1} KB";
        }

        return $"{bytes / (1024d * 1024d):N1} MB";
    }
}

public sealed record CursorCellViewModel(
    string Name,
    string TypeName,
    string? Value) : IInspectorFieldRow;

public sealed class CursorRelationViewModel
{
    public CursorRelationViewModel(
        CursorInspectionRelation relation,
        bool inbound = false)
    {
        Relation = relation ?? throw new ArgumentNullException(nameof(relation));
        KindText = relation.Kind.ToString();
        TargetText = inbound
            ? relation.Source.DisplayText
            : relation.Target?.DisplayText ??
                ExternalTargetText(relation.ExternalTarget) ??
                relation.LogicalTarget;
        EvidenceText = relation.Evidence.ToString();
        StatusText = relation.Resolution.ToString();
        Explanation = relation.Explanation;
    }

    public CursorInspectionRelation Relation { get; }

    public string KindText { get; }

    public string TargetText { get; }

    public string EvidenceText { get; }

    public string StatusText { get; }

    public string Explanation { get; }

    private string? ExternalTargetText(CursorExternalReference? target)
    {
        if (target is null)
        {
            return null;
        }

        return target.LineNumber is int line
            ? $"{target.Path} : line {line}"
            : target.Path;
    }
}
