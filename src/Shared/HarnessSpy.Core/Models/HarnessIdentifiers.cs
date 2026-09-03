namespace HarnessSpy.Core.Models;

// Stable, string-backed identities so future providers (and multiple contracts
// from one provider) can be added without editing central enums. The legacy
// HookProvider/HookSurface enums remain for envelope compatibility, but the
// runtime layer keys off these identifiers.
public static class HarnessIds
{
    public const string Cursor = "cursor";
    public const string ClaudeCode = "claude-code";
    public const string GitHubCopilot = "github-copilot";
    public const string OpenAiCodex = "openai-codex";
    public const string Unknown = "unknown";

    public static string FromProvider(HookProvider provider) => provider switch
    {
        HookProvider.Cursor => Cursor,
        HookProvider.ClaudeCode => ClaudeCode,
        HookProvider.GitHubCopilot => GitHubCopilot,
        _ => Unknown
    };
}

public static class SurfaceIds
{
    public const string CursorIde = "cursor-ide";
    public const string ClaudeCode = "claude-code";
    public const string CopilotCli = "copilot-cli";
    public const string VsCodeAgentHooks = "vscode-agent-hooks";
    public const string Unknown = "unknown";

    public static string FromSurface(HookSurface surface) => surface switch
    {
        HookSurface.CursorIde => CursorIde,
        HookSurface.ClaudeCode => ClaudeCode,
        HookSurface.CopilotCli => CopilotCli,
        HookSurface.VsCodeAgentHooks => VsCodeAgentHooks,
        _ => Unknown
    };
}

// A dialect is the concrete payload/naming contract observed on a surface. One
// surface can emit more than one dialect: Copilot CLI can emit either its
// native camelCase payloads or the VS Code-compatible PascalCase/snake_case
// payloads, which is why dialect is tracked independently of surface.
public static class DialectIds
{
    public const string CursorHook = "cursor-hook";
    public const string ClaudeHook = "claude-hook";
    public const string CopilotCliCamel = "copilot-cli-camel";
    public const string CopilotVsCodeCompat = "copilot-vscode-compat";
    public const string VsCodeLocal = "vscode-local";
    public const string Unknown = "unknown";

    // Provider-owned agent transcript JSONL dialects. One per verified format.
    public const string CursorTranscript = "cursor-transcript-jsonl";
    public const string ClaudeTranscript = "claude-transcript-jsonl";
    public const string CopilotCliTranscript = "copilot-cli-events-v1";
    public const string UnknownTranscript = "unknown-transcript-jsonl";
}

// How the session/turn scope of an observation is resolved by the shared tree
// projection. Provider engines classify each native event into one of these.
public enum ObservationScope
{
    // Belongs to the workspace root and never to a session (Cursor workspaceOpen).
    Workspace,

    // Brackets the whole session and sits directly under the session node even
    // when it carries a turn id (sessionStart/sessionEnd).
    SessionLifecycle,

    // Session-scoped: no turn grouping applies.
    Session,

    // Grouped beneath a turn/generation node.
    Turn,

    // Editor-surface hooks that stay at session level (Cursor tab hooks).
    Tab
}

// Provider-neutral semantic role used by the shared projection, presentation,
// and summary code so that no shared class branches on provider event strings.
public enum ObservationRole
{
    Generic,
    WorkspaceOpen,
    SessionStart,
    SessionEnd,
    PromptSubmitted,
    PromptTransformed,
    AgentResponse,
    AgentThought,
    ToolRequest,
    ToolSuccess,
    ToolFailure,
    InnerExecutionStart,
    InnerExecutionEnd,
    FileAccess,
    SubagentStart,
    SubagentStop,
    TurnStop,
    RuntimeError,
    Notification,
    PermissionRequest,
    PermissionDenied,
    CompactionStart,
    CompactionEnd,
    ToolBatch,
    Message,
    TaskCreated,
    TaskCompleted,
    ModelSwitchStart,
    ModelSwitchEnd,
    InstructionsLoaded,
    ConfigChange,
    DirectoryChange,
    WorkingDirectoryChange
}

// Directional glyph shown ahead of an observation node.
public enum ObservationDirection
{
    None,
    Input,
    Output
}

// Colour/emphasis category the tree uses for the node label.
public enum ObservationTone
{
    Normal,
    Thought,
    Failure,
    Stop,
    Compaction,
    Mcp,
    Permission
}

// Which shared correlation algorithm nests this observation under a parent.
public enum ToolCallMatchStrategy
{
    None,
    ToolCallId,
    Subagent,
    ExecutionEvidence,
    FileTarget,

    // Surfaces without a tool-use id (Copilot CLI) pair a tool completion to its
    // request by tool name and canonical arguments, falling back to arrival
    // order when several identical calls are in flight.
    ToolSignature
}

// The kind of inner execution/file hook, used only for summary categorisation.
public enum InnerExecutionCategory
{
    None,
    Shell,
    Mcp,
    FileRead,
    FileEdit
}
