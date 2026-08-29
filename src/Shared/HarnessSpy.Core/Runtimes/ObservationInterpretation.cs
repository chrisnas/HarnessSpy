using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Runtimes;

// How a curated inspector field row is produced from the native payload. The
// generic rendering lives in HookObservation; engines only choose which keys
// to surface and how to flatten them, so no provider owns the rendering.
public enum FieldSpecKind
{
    Scalar,
    ScalarPreferred,
    ObjectMembers,
    JsonStringMembers,
    ArrayMembers,
    FlattenedArrayMembers,
    AllTopLevel,
    PreCompactSummary
}

public sealed record FieldSpec(FieldSpecKind Kind, params string[] Names)
{
    public static FieldSpec AllTopLevel { get; } = new(FieldSpecKind.AllTopLevel);
}

// The provider-neutral interpretation of a single native observation, produced
// once by the owning harness runtime engine. The shared projection,
// presentation, and summary code consume this instead of branching on native
// event/tool strings.
public sealed record ObservationInterpretation
{
    // The exact native event/item name, chosen by the engine. This is the
    // identity shown in the UI and used for correlation. It is never renamed
    // into another provider's vocabulary.
    public required string NativeEventName { get; init; }

    public required ObservationScope Scope { get; init; }

    public required ObservationRole Role { get; init; }

    public CanonicalEventKind EventKind { get; init; } = CanonicalEventKind.ProviderSpecific;

    public CanonicalToolKind ToolKind { get; init; } = CanonicalToolKind.Unknown;

    public CorrelationQuality CorrelationQuality { get; init; } = CorrelationQuality.Exact;

    // Extracted native identifiers. These are read from the untouched payload;
    // the payload itself is never rewritten with aliases.
    public string? SessionId { get; init; }

    public string? TurnId { get; init; }

    public string? ToolCallId { get; init; }

    // Every tool_use_id bundled into a PostToolBatch, in payload order. Used
    // to group the calls' PreToolUse nodes under a synthetic batch parent when
    // there is more than one (see ToolCallId for the single-call case).
    public IReadOnlyList<string> BatchToolCallIds { get; init; } = [];

    public string? SubagentId { get; init; }

    public string? SubagentType { get; init; }

    public string? ToolName { get; init; }

    public string? McpServerName { get; init; }

    public string? PromptText { get; init; }

    public string? AssistantText { get; init; }

    public string? TargetFilePath { get; init; }

    public string? Task { get; init; }

    public string? Status { get; init; }

    // Correlation descriptors used by the shared tree projection.
    public ToolCallMatchStrategy MatchStrategy { get; init; } = ToolCallMatchStrategy.None;

    // The native "before…" event name used as the in-flight tracking key for an
    // inner execution hook (e.g. Cursor "beforeShellExecution").
    public string? InnerExecutionKind { get; init; }

    // The owning pre tool name an inner execution start nests under (e.g.
    // "Shell" or "MCP:{tool}").
    public string? InnerExecutionOwnerTool { get; init; }

    // The set of owning pre tool names a file-access hook may nest under
    // (e.g. ["Read"] or ["Write","StrReplace","EditNotebook"]).
    public IReadOnlyList<string> FileAccessOwnerTools { get; init; } = [];

    public InnerExecutionCategory InnerCategory { get; init; } = InnerExecutionCategory.None;

    // True when this observation opens a tool-call node the projection tracks
    // for parallel-wave detection and post pairing.
    public bool OpensToolCall { get; init; }

    // True when this observation opens a subagent node.
    public bool OpensSubagent { get; init; }

    // Surfaces that lack an exact turn id (Copilot CLI/VS Code) derive turns
    // from prompt/stop boundaries. These flags let the shared projection group
    // such turns without any provider check.
    public bool ParticipatesInDerivedTurns { get; init; }

    public bool StartsDerivedTurn { get; init; }

    // Presentation.
    public string? HeaderDetail { get; init; }

    public ObservationDirection Direction { get; init; } = ObservationDirection.None;

    public ObservationTone Tone { get; init; } = ObservationTone.Normal;

    public string? HoverText { get; init; }

    public bool ShowClockTimestamp { get; init; }

    public IReadOnlyList<FieldSpec> DetailFieldSpecs { get; init; } = [FieldSpec.AllTopLevel];

    // Summary categorisation flags.
    public bool CountsAsFailure { get; init; }

    public bool IsAbortedStop { get; init; }

    public bool HasTokenCounts { get; init; }
}
