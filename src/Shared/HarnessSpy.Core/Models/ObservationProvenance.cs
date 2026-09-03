namespace HarnessSpy.Core.Models;

// Where a single observation came from and how to deduplicate it. Hook
// observations leave this null; transcript-sourced observations carry the exact
// file coordinates plus any native ids so live capture, durable sidecar
// storage, and replay all agree on identity.
//
// The stable transcript dedupe key is (NormalizedPath, ByteOffset, BlockIndex).
// Native ids (RecordId/ToolCallId/...) are retained as separate correlation
// evidence, never used alone across files.
public sealed record ObservationProvenance(
    ObservationSourceKind SourceKind,
    string DialectId,
    string NormalizedPath,
    long ByteOffset,
    int LineNumber,
    int BlockIndex,
    long FileGeneration,
    TranscriptCompleteness Completeness,
    string? CapturedPath = null,
    string? RecordId = null,
    string? ParentRecordId = null,
    string? TurnId = null,
    string? InteractionId = null,
    string? ToolCallId = null,
    string? DiscoveryHookEventId = null,
    string? ContractVersion = null)
{
    // The stable identity used to avoid re-projecting a row already captured.
    public string DedupeKey =>
        $"{NormalizedPath.ToUpperInvariant()}|{ByteOffset}|{BlockIndex}";
}
