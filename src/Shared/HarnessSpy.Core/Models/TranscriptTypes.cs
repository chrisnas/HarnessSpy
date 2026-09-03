namespace HarnessSpy.Core.Models;

// How confident a derived fact is. Kept as an explicit sidecar so heuristic
// transcript correlation is never presented with the same authority as an
// exact native id or field.
public enum InferenceEvidence
{
    // Explicit native field or exact provider id.
    Observed,

    // Two independent source signals agree (e.g. hook + transcript).
    Corroborated,

    // Deterministic boundary/ordinal mapping (e.g. turn sequence).
    Derived,

    // Unique-best content/signature match.
    Heuristic,

    // Multiple candidates; not safely merged.
    Ambiguous,

    // The provider withheld the data (deleted transcript, missing hook).
    Unavailable,

    // Present but not human-readable (redacted thinking, encrypted reasoning).
    Opaque
}

// The lifecycle stage of a skill for one session/turn. Every stage is preserved
// with its source so "available" is never presented as "used".
public enum SkillEvidenceStage
{
    Available,
    Attached,
    Invoked,
    Loaded,
    ExecutionCorroborated
}

// The scope a usage measurement applies to.
public enum UsageScope
{
    Request,
    Turn,
    Session
}

// How a usage measurement accumulates. This prevents summing propagated Claude
// snapshots, Cursor response+stop duplicates, or Copilot checkpoint+shutdown
// totals.
public enum UsageBehavior
{
    Delta,
    CumulativeSnapshot,
    FinalSnapshot,
    Unknown
}

// The completeness of a captured transcript source row at capture time.
public enum TranscriptCompleteness
{
    Complete,
    PartialAtCapture,
    SourceDeletedBefore
}

// Whether HarnessSpy durably captured a session's transcript before the
// provider deleted it. Surfaced so replay fidelity is never silently lower
// than live spying.
public enum EnrichmentCaptureState
{
    None,
    LiveCaptured,
    BackfilledBeforeDeletion,
    MissedBeforeCapture,
    Unsupported
}

// Navigable relationships between nodes that do not replace the existing
// tool/subagent parent-child rules.
public enum TranscriptRelationshipKind
{
    EvidenceOf,
    PreviousTranscriptRecord,
    ToolRequestResult,
    SameAssistantStep,
    ParallelBatchMember,
    DynamicToolDiscoveryFor,
    SubagentConversation,
    FileHistoryForMessage,
    CompactionPreserved,
    AttachmentForPrompt,
    HookInvocationEvidence
}

// Whether a transcript file is the main session log or a subagent sidechain.
public enum TranscriptFileRole
{
    Main,
    Subagent
}

// A single native usage figure with explicit scope, accumulation behaviour, and
// origin so summaries can aggregate correctly instead of double-counting.
public sealed record UsageMeasurement(
    string Name,
    long Value,
    string Unit,
    UsageScope Scope,
    UsageBehavior Behavior,
    string SourceRecordId,
    bool IsComplete = true);

// One skill lifecycle observation with the record that proved it.
public sealed record SkillEvidence(
    string SkillName,
    SkillEvidenceStage Stage,
    InferenceEvidence Evidence,
    string? SourcePath = null);

// A transcript file discovered from a hook payload. The registry keys these by
// provider-scoped session id (main) or agent id (subagent).
public sealed record TranscriptReference(
    string ScopedSessionId,
    string Path,
    TranscriptFileRole Role,
    string DialectId,
    string? AgentId = null);
