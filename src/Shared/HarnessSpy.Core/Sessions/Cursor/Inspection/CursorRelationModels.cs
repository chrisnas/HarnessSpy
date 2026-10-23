using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public enum CursorRecordKind
{
    Unknown,
    ComposerHeaders,
    ComposerData,
    ComposerHeader,
    Bubble
}

public enum CursorRelationKind
{
    DeclaresComposer,
    ComposerData,
    ConversationBubble,
    BelongsToComposer,
    ParentComposer,
    SelectedComposer,
    Workspace,
    DuplicateRecord,
    Transcript
}

public enum CursorRelationResolution
{
    Resolved,
    Unresolved,
    Ambiguous
}

public sealed record CursorRowReference(
    string DatabasePath,
    string TableName,
    IReadOnlyDictionary<string, object?> KeyValues,
    long? RowId = null,
    long? PageOrdinal = null)
{
    public string StableKey =>
        $"{Path.GetFullPath(DatabasePath)}|{TableName}|" +
        (KeyValues.Count == 0
            ? RowId is not null
                ? $"rowid:{RowId}"
                : $"page:{PageOrdinal}"
            : string.Join(
                "|",
                KeyValues
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"{item.Key}={item.Value}")));

    public string DisplayText
    {
        get
        {
            string identity = KeyValues.Count == 0
                ? RowId is not null
                    ? $"rowid {RowId}"
                    : PageOrdinal is not null
                        ? $"page row {PageOrdinal}"
                        : "unknown row"
                : string.Join(
                    ", ",
                    KeyValues.Select(item => $"{item.Key}={item.Value}"));
            return $"{Path.GetFileName(DatabasePath)} / {TableName} / {identity}";
        }
    }
}

public sealed record CursorExternalReference(
    string Path,
    string? NativeSessionId = null,
    long? ByteOffset = null,
    int? LineNumber = null);

public sealed record CursorInspectionRelation(
    CursorRelationKind Kind,
    CursorRowReference Source,
    CursorRowReference? Target,
    CursorExternalReference? ExternalTarget,
    string LogicalTarget,
    InferenceEvidence Evidence,
    CursorRelationResolution Resolution,
    string Explanation);

public sealed record CursorRecordClassification(
    CursorRecordKind Kind,
    string? StorageKey,
    string? ComposerId,
    string? BubbleId,
    string? RawJson);

public sealed record CursorLogicalReference(
    CursorRelationKind Kind,
    string TargetId,
    InferenceEvidence Evidence,
    string Explanation,
    string? SourceContextId = null);

public sealed record CursorIndexedRecord(
    CursorRowReference Row,
    CursorRecordClassification Classification,
    IReadOnlyList<CursorLogicalReference> References);

public sealed record CursorWorkspaceEvidence(
    string StorageId,
    string ManifestPath,
    string? WorkspacePath,
    string? DatabasePath);
