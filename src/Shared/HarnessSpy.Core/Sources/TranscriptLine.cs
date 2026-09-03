using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sources;

// One raw JSONL row read from a provider transcript, plus the exact file
// coordinates a dialect parser needs to attach provenance. The tailer produces
// these; a parser turns each into zero or more observations without ever
// mutating the raw text.
public sealed record TranscriptLine(
    string Raw,
    string NormalizedPath,
    long ByteOffset,
    int LineNumber,
    long FileGeneration,
    TranscriptFileRole FileRole,
    HookProvider Provider,
    HookSurface Surface,
    string DialectId,
    string ScopedSessionId,
    string? NativeSessionId = null,
    string? AgentId = null,
    string? CapturedPath = null,
    string? ContractVersion = null,
    DateTimeOffset? ObservedAtUtc = null,
    string? TurnHint = null)
{
    // Fallback capture time used when a fragment carries no native timestamp,
    // so transcript-only nodes still order deterministically near their turn.
    public DateTimeOffset EffectiveObservedAtUtc => ObservedAtUtc ?? DateTimeOffset.UtcNow;

    // Builds the provenance for a fragment at the given content-block index.
    public ObservationProvenance Provenance(
        int blockIndex,
        TranscriptCompleteness completeness,
        string? recordId = null,
        string? parentRecordId = null,
        string? turnId = null,
        string? interactionId = null,
        string? toolCallId = null,
        string? discoveryHookEventId = null) =>
        new(
            ObservationSourceKind.TranscriptFile,
            DialectId,
            NormalizedPath,
            ByteOffset,
            LineNumber,
            blockIndex,
            FileGeneration,
            completeness,
            CapturedPath,
            recordId,
            parentRecordId,
            turnId,
            interactionId,
            toolCallId,
            discoveryHookEventId,
            ContractVersion);
}
