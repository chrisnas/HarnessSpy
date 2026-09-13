using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarnessSpy.Core.Runtimes.Copilot;

// Generates the two Copilot passive-profile hook files: the Copilot CLI
// harness-spy.json and the VS Code Local agent-hooks profile. The installer
// supplies the real CopilotSpy.Hook path; the checked-in examples use a
// placeholder so the repository never ships an absolute author path. An
// explicit runtime and dialect id are passed to the executable so its runtime
// identity is authoritative rather than inferred. The two profiles use
// different schemas and must never share a file or folder (see GenerateVsCode).
public static class CopilotSettingsGenerator
{
    public const string ExecutablePlaceholder = "<COPILOTSPY_HOOK_EXECUTABLE>";

    private const int TimeoutSeconds = 5;

    public static string GenerateCli(string executablePath)
    {
        var hooks = new JsonObject();
        foreach (string eventName in CopilotHookCatalog.CliV1Events)
        {
            string command =
                $"& '{executablePath}' --event {eventName} --source copilot-cli " +
                $"--hook {eventName} --runtime github-copilot --dialect copilot-cli-camel";

            hooks[eventName] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["powershell"] = command,
                ["cwd"] = ".",
                ["timeoutSec"] = TimeoutSeconds,
                ["env"] = new JsonObject
                {
                    ["HARNESS_SPY_HOST"] = "github-copilot",
                    ["HARNESS_SPY_RUNTIME_ID"] = "github-copilot"
                }
            });
        }

        var root = new JsonObject
        {
            ["version"] = 1,
            ["hooks"] = hooks
        };

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    // Generates a VS Code Local agent-hooks profile. It deliberately differs
    // from the CLI profile because the two hosts do not share a schema: VS Code
    // uses the eight PascalCase events, the "command" field, and "timeout" in
    // seconds. The command value uses the PowerShell call operator ("& '<exe>'")
    // because VS Code runs the "command" string through PowerShell on Windows,
    // where a bare quoted path is parsed as a string literal and never executes
    // (the hook then silently never fires). The "vscode-agent-hooks" runtime id
    // is the authoritative signal that routes these observations to the VS Code
    // engine instead of the CLI one. Install the result in a VS Code-only
    // location (for example ".vscode/hooks/" via the chat.hookFilesLocations
    // setting), never in ".github/hooks/": the Copilot CLI also reads that shared
    // folder and would fire and mislabel these PascalCase entries.
    public static string GenerateVsCode(string executablePath)
    {
        var hooks = new JsonObject();
        foreach (string eventName in CopilotHookCatalog.VsCodeLocalEvents)
        {
            string command =
                $"& '{executablePath}' --event {eventName} --source vscode-local " +
                $"--hook {eventName} --runtime github-copilot --dialect vscode-local";

            hooks[eventName] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = command,
                ["cwd"] = ".",
                ["timeout"] = TimeoutSeconds,
                ["env"] = new JsonObject
                {
                    ["HARNESS_SPY_HOST"] = "github-copilot",
                    ["HARNESS_SPY_RUNTIME_ID"] = "vscode-agent-hooks"
                }
            });
        }

        var root = new JsonObject
        {
            ["version"] = 1,
            ["hooks"] = hooks
        };

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    public static IReadOnlyList<string> ReadRegisteredEvents(string settingsJson)
    {
        using JsonDocument document = JsonDocument.Parse(settingsJson);
        if (!document.RootElement.TryGetProperty("hooks", out JsonElement hooks) ||
            hooks.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return hooks.EnumerateObject().Select(property => property.Name).ToArray();
    }
}
