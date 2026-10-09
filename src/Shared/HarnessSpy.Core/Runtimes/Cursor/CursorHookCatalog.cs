namespace HarnessSpy.Core.Runtimes.Cursor;

// Case-sensitive Cursor hook names used to generate a complete hooks.json
// profile. Unknown future events still render through CursorRuntimeEngine's
// fallback path; this catalog defines registration, not event acceptance.
public static class CursorHookCatalog
{
    public static IReadOnlyList<string> NativeEvents { get; } =
    [
        "sessionStart",
        "sessionEnd",
        "beforeSubmitPrompt",
        "afterAgentThought",
        "afterAgentResponse",
        "preToolUse",
        "postToolUse",
        "postToolUseFailure",
        "beforeShellExecution",
        "afterShellExecution",
        "beforeMCPExecution",
        "afterMCPExecution",
        "beforeReadFile",
        "afterFileEdit",
        "subagentStart",
        "subagentStop",
        "preCompact",
        "stop",
        "workspaceOpen",
        "beforeTabFileRead",
        "afterTabFileEdit"
    ];
}
