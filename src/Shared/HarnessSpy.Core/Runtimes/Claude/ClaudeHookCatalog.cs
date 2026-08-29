namespace HarnessSpy.Core.Runtimes.Claude;

// The exact, case-sensitive set of Claude Code hook events HarnessSpy knows
// about, sourced from the Claude Code hooks reference plus the v2.1.251
// model-switch additions. Unknown events still render via the engine's default
// path; this catalog exists for profile generation and tests, not to gate
// rendering.
public static class ClaudeHookCatalog
{
    // The 31 events documented in the hooks reference.
    public static IReadOnlyList<string> DocumentedEvents { get; } =
    [
        "SessionStart",
        "Setup",
        "UserPromptSubmit",
        "UserPromptExpansion",
        "MessageDisplay",
        "PreToolUse",
        "PermissionRequest",
        "PermissionDenied",
        "PostToolUse",
        "PostToolUseFailure",
        "PostToolBatch",
        "Notification",
        "SubagentStart",
        "SubagentStop",
        "TaskCreated",
        "TaskCompleted",
        "Stop",
        "StopFailure",
        "TeammateIdle",
        "InstructionsLoaded",
        "ConfigChange",
        "CwdChanged",
        "DirectoryAdded",
        "FileChanged",
        "WorktreeCreate",
        "WorktreeRemove",
        "PreCompact",
        "PostCompact",
        "Elicitation",
        "ElicitationResult",
        "SessionEnd"
    ];

    // v2.1.251 additions whose payload schemas are not yet published; kept
    // open-ended.
    public static IReadOnlyList<string> ModelSwitchEvents { get; } =
    [
        "PreModelSwitch",
        "PostModelSwitch"
    ];

    // The Safe passive profile: high-signal events with no interactive
    // permission control and no high-volume/sensitive streams. Excludes
    // WorktreeCreate (its command must return a worktree path and cannot use
    // the passive silent forwarder).
    public static IReadOnlyList<string> SafeProfileEvents { get; } =
    [
        "SessionStart",
        "Setup",
        "UserPromptSubmit",
        "UserPromptExpansion",
        "PreToolUse",
        "PermissionRequest",
        "PermissionDenied",
        "PostToolUse",
        "PostToolUseFailure",
        "PostToolBatch",
        "Notification",
        "SubagentStart",
        "SubagentStop",
        "TaskCreated",
        "TaskCompleted",
        "Stop",
        "StopFailure",
        "TeammateIdle",
        "InstructionsLoaded",
        "ConfigChange",
        "CwdChanged",
        "DirectoryAdded",
        "WorktreeRemove",
        "PreCompact",
        "PostCompact",
        "SessionEnd",
        "PreModelSwitch",
        "PostModelSwitch"
    ];

    // The Full profile additionally enables the high-volume/sensitive events.
    public static IReadOnlyList<string> FullProfileOnlyEvents { get; } =
    [
        "MessageDisplay",
        "FileChanged",
        "Elicitation",
        "ElicitationResult"
    ];

    public static IReadOnlyList<string> FullProfileEvents { get; } =
        [.. SafeProfileEvents, .. FullProfileOnlyEvents];

    // Never registered by a passive profile.
    public const string WorktreeCreate = "WorktreeCreate";
}
