using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes.Claude;
using HarnessSpy.Core.Runtimes.Copilot;
using HarnessSpy.Core.Runtimes.Cursor;

namespace HarnessSpy.Core.Sources;

// Selects the transcript parser for a dialect id. Adding a future provider's
// transcript format is a matter of registering one parser here; the tailer,
// capture store, and reconciler are dialect-agnostic.
public static class TranscriptDialectParserRegistry
{
    private static readonly CursorTranscriptDialectParser Cursor = new();
    private static readonly ClaudeTranscriptDialectParser Claude = new();
    private static readonly CopilotCliTranscriptDialectParser CopilotCli = new();
    private static readonly UnknownTranscriptDialectParser Unknown = new();

    public static ITranscriptDialectParser Resolve(string dialectId) => dialectId switch
    {
        DialectIds.CursorTranscript => Cursor,
        DialectIds.ClaudeTranscript => Claude,
        DialectIds.CopilotCliTranscript => CopilotCli,
        _ => Unknown
    };

    public static bool IsSupported(string dialectId) => dialectId is
        DialectIds.CursorTranscript or
        DialectIds.ClaudeTranscript or
        DialectIds.CopilotCliTranscript;

    // The provider/surface a transcript dialect belongs to, so a replayed
    // sidecar row can be rehydrated without a live hook present.
    public static (HookProvider Provider, HookSurface Surface) SurfaceFor(string dialectId) => dialectId switch
    {
        DialectIds.CursorTranscript => (HookProvider.Cursor, HookSurface.CursorIde),
        DialectIds.ClaudeTranscript => (HookProvider.ClaudeCode, HookSurface.ClaudeCode),
        DialectIds.CopilotCliTranscript => (HookProvider.GitHubCopilot, HookSurface.CopilotCli),
        _ => (HookProvider.Unknown, HookSurface.Unknown)
    };
}
