using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarnessSpy.Core.Runtimes.Copilot;

// Generates a Copilot CLI harness-spy.json hooks file for the passive
// HarnessSpy profile. The installer supplies the real CopilotSpy.Hook path; the
// checked-in example uses a placeholder so the repository never ships an
// absolute author path. An explicit runtime and dialect id are passed to the
// executable so its runtime identity is authoritative rather than inferred.
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
