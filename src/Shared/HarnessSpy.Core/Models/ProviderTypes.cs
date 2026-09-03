namespace HarnessSpy.Core.Models;

public enum HookProvider
{
    Unknown,
    Cursor,
    ClaudeCode,
    GitHubCopilot
}

public enum HookSurface
{
    Unknown,
    CursorIde,
    ClaudeCode,
    CopilotCli,
    VsCodeAgentHooks
}

public enum ObservationSourceKind
{
    // Ordinals 0-3 are persisted in existing v2 envelopes and must stay stable.
    Hook,
    Sdk,
    Synthetic,
    Replay,

    // Reserved for future higher-fidelity sources. Appended so old captures
    // keep replaying correctly.
    ProtocolStream,
    OpenTelemetry,
    CloudApi,

    // Provider-owned agent transcript JSONL (Cursor/Claude/Copilot CLI) tailed
    // and durably captured as a secondary source. Appended last to keep the
    // ordinals of persisted v2 captures stable.
    TranscriptFile
}

public enum AgentRuntimeKind
{
    Unknown,
    Local,
    Cloud,
    Remote
}

public enum CorrelationQuality
{
    None,
    Exact,
    ObservedVersionSpecific,
    Derived,
    Heuristic,
    Ambiguous
}

public enum CanonicalEventKind
{
    ProviderSpecific,
    WorkspaceOpened,
    SessionStarted,
    SessionEnded,
    PromptSubmitted,
    PromptTransformed,
    AssistantMessage,
    AssistantThought,
    ToolRequested,
    ToolSucceeded,
    ToolFailed,
    PermissionRequested,
    PermissionDenied,
    SubagentStarted,
    SubagentCompleted,
    CompactionStarted,
    CompactionCompleted,
    TurnCompleted,
    RuntimeError,
    Notification
}

public enum CanonicalToolKind
{
    Unknown,
    Shell,
    FileRead,
    FileWrite,
    FileEdit,
    FileDelete,
    TextSearch,
    FileSearch,
    Notebook,
    Mcp,
    Agent,
    Web,
    UserInteraction,
    Task
}
