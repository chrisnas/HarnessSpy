namespace HarnessSpy.Core.Runtimes.Copilot;

// Versioned, case-sensitive Copilot hook catalogs. The CLI and VS Code Local
// contracts are tracked separately rather than merged into one case-insensitive
// dictionary, because the CLI can also emit a VS Code-compatible dialect while
// keeping its CLI surface identity.
public static class CopilotHookCatalog
{
    // Copilot CLI hook v1: the 14 documented configured event keys.
    public static IReadOnlyList<string> CliV1Events { get; } =
    [
        "sessionStart",
        "sessionEnd",
        "userPromptSubmitted",
        "userPromptTransformed",
        "preToolUse",
        "postToolUse",
        "postToolUseFailure",
        "permissionRequest",
        "notification",
        "agentStop",
        "subagentStart",
        "subagentStop",
        "errorOccurred",
        "preCompact"
    ];

    // VS Code Local Preview: the eight documented PascalCase events.
    public static IReadOnlyList<string> VsCodeLocalEvents { get; } =
    [
        "SessionStart",
        "UserPromptSubmit",
        "PreToolUse",
        "PostToolUse",
        "PreCompact",
        "SubagentStart",
        "SubagentStop",
        "Stop"
    ];
}
