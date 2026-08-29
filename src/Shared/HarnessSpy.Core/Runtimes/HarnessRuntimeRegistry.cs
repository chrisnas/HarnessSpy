using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes.Claude;
using HarnessSpy.Core.Runtimes.Copilot;
using HarnessSpy.Core.Runtimes.Cursor;

namespace HarnessSpy.Core.Runtimes;

// Selects the runtime engine for a given (harness, surface, dialect). Adding a
// future harness/surface (e.g. Codex) is a matter of registering a new engine
// here plus a source adapter, without touching shared projection or WPF code.
public static class HarnessRuntimeRegistry
{
    private static readonly CursorRuntimeEngine Cursor = new();
    private static readonly ClaudeRuntimeEngine Claude = new();
    private static readonly CopilotCliRuntimeEngine CopilotCli = new();
    private static readonly VsCodeLocalRuntimeEngine VsCodeLocal = new();

    public static IHarnessRuntimeEngine Resolve(
        string harnessId,
        string surfaceId,
        string dialectId)
    {
        if (harnessId == HarnessIds.Cursor)
        {
            return Cursor;
        }

        if (harnessId == HarnessIds.ClaudeCode)
        {
            return Claude;
        }

        if (harnessId == HarnessIds.GitHubCopilot)
        {
            // An actual VS Code Local observation uses the VS Code engine; the
            // CLI (including its VS Code-compatible dialect) keeps CLI identity.
            if (surfaceId == SurfaceIds.VsCodeAgentHooks)
            {
                return VsCodeLocal;
            }

            return CopilotCli;
        }

        return UnknownRuntimeEngine.Instance;
    }

    // Convenience overload from the legacy provider/surface enums used during
    // envelope parsing.
    public static IHarnessRuntimeEngine Resolve(HookProvider provider, HookSurface surface) =>
        Resolve(
            HarnessIds.FromProvider(provider),
            SurfaceIds.FromSurface(surface),
            DialectFor(provider, surface));

    public static string DialectFor(HookProvider provider, HookSurface surface) => provider switch
    {
        HookProvider.Cursor => DialectIds.CursorHook,
        HookProvider.ClaudeCode => DialectIds.ClaudeHook,
        HookProvider.GitHubCopilot => surface == HookSurface.VsCodeAgentHooks
            ? DialectIds.VsCodeLocal
            : DialectIds.CopilotCliCamel,
        _ => DialectIds.Unknown
    };
}
