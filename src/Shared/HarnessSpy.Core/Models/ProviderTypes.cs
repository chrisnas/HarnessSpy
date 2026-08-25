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
    Hook,
    Sdk,
    Synthetic,
    Replay
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
