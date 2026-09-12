using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions;

public enum SessionSourceKind
{
    CursorTranscriptJsonl,
    CursorDesktopSqlite,
    ClaudeTranscriptJsonl,
    ClaudeSessionIndex,
    CopilotEventsJsonl,
    CopilotWorkspaceYaml,
    CursorPlanMarkdown,
    ClaudePlanMarkdown,
    CopilotPlanMarkdown,
    Unknown
}

public enum SessionLifecycleState
{
    Open,
    Closed
}

public sealed record SessionSourceProvenance(
    SessionSourceKind SourceKind,
    string Path,
    string Format,
    string RawContent,
    int? LineNumber = null,
    long? ByteOffset = null,
    string? RecordId = null,
    string? ParentRecordId = null,
    string? DatabaseKey = null,
    string? ContractVersion = null);

public sealed record SessionFileBinding(
    string Path,
    SessionSourceKind SourceKind,
    string DialectId,
    TranscriptFileRole Role,
    DateTimeOffset LastWriteTimeUtc,
    long Length,
    string? AgentId = null,
    string? ParentSessionId = null);

public sealed record SessionEventRecord
{
    public required string Id { get; init; }

    public required string NativeName { get; init; }

    public required HookProvider Provider { get; init; }

    public required HookSurface Surface { get; init; }

    public required ObservationRole Role { get; init; }

    public CanonicalEventKind EventKind { get; init; } = CanonicalEventKind.ProviderSpecific;

    public CanonicalToolKind ToolKind { get; init; } = CanonicalToolKind.Unknown;

    public ObservationDirection Direction { get; init; } = ObservationDirection.None;

    public ObservationTone Tone { get; init; } = ObservationTone.Normal;

    public InferenceEvidence Evidence { get; init; } = InferenceEvidence.Observed;

    public DateTimeOffset? TimestampUtc { get; init; }

    public int Order { get; init; }

    public string? TurnId { get; init; }

    public string? ParentId { get; init; }

    public string? AssistantStepId { get; init; }

    public string? ParallelGroupId { get; init; }

    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    public string? McpServerName { get; init; }

    public string? McpToolName { get; init; }

    public string? PromptText { get; init; }

    public string? Text { get; init; }

    public string? Model { get; init; }

    public string? Mode { get; init; }

    public string? Status { get; init; }

    public string? AgentId { get; init; }

    public string? AgentType { get; init; }

    public string? Task { get; init; }

    public double? DurationMs { get; init; }

    public bool IsFailure { get; init; }

    public bool IsAborted { get; init; }

    public bool IsParallelCandidate { get; init; }

    public bool ExcludeFromSummary { get; init; }

    public IReadOnlyList<string> TargetPaths { get; init; } = [];

    public IReadOnlyList<UsageMeasurement> UsageMeasurements { get; init; } = [];

    public SkillEvidence? Skill { get; init; }

    public required SessionSourceProvenance Provenance { get; init; }
}

public sealed record SessionTurn(
    string Id,
    int Number,
    string Prompt,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    InferenceEvidence Evidence,
    IReadOnlyList<SessionEventRecord> Events);

public sealed record SessionCatalogEntry
{
    public required string CatalogSessionId { get; init; }

    public required string NativeSessionId { get; init; }

    public required HookProvider Provider { get; init; }

    public required HookSurface Surface { get; init; }

    public required WorkspaceContext Workspace { get; init; }

    public required string Title { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? LastActivityAtUtc { get; init; }

    public string? Model { get; init; }

    public string? Mode { get; init; }

    public SessionLifecycleState LifecycleState { get; init; } = SessionLifecycleState.Closed;

    public InferenceEvidence LifecycleEvidence { get; init; } = InferenceEvidence.Unavailable;

    public bool IsSelectedInHarness { get; init; }

    public IReadOnlyList<SessionFileBinding> Files { get; init; } = [];

    public IReadOnlyList<SessionTurn> Turns { get; init; } = [];

    public IReadOnlyDictionary<string, string?> Metadata { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    public IReadOnlyList<SessionSourceProvenance> Sources { get; init; } = [];
}

public sealed record SessionCatalogScanResult(
    IReadOnlyList<SessionCatalogEntry> Sessions,
    bool IsComplete,
    IReadOnlyList<string> Warnings)
{
    public static SessionCatalogScanResult Empty { get; } = new([], true, []);

    // Provider-owned plan artifacts and turn activities discovered by the same
    // scan. Kept as an init-only extra so the positional constructor and every
    // existing source/test remain source-compatible.
    public Plans.SessionPlanCatalogFragment PlanFragment { get; init; } =
        Plans.SessionPlanCatalogFragment.Empty;
}
