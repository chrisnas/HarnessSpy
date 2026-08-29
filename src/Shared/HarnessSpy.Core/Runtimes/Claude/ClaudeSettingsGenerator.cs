using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarnessSpy.Core.Runtimes.Claude;

// Generates a Claude Code settings.json hooks block for a passive HarnessSpy
// profile. This is the installer/generator path so users do not inherit the
// repository author's absolute executable path: the caller supplies the real
// ClaudeSpy.Hook executable location (or a placeholder for the checked-in
// examples), and the event set comes from the tested catalog.
public static class ClaudeSettingsGenerator
{
    // Placeholder written into the checked-in example files. An installer
    // replaces it with the absolute path to ClaudeSpy.Hook on the user machine.
    public const string ExecutablePlaceholder = "<CLAUDESPY_HOOK_EXECUTABLE>";

    // Fast passive ceiling; measured and lowered only if ordering stays correct.
    private const int TimeoutSeconds = 5;

    public static string GenerateSafe(string executablePath) =>
        Generate(executablePath, ClaudeHookCatalog.SafeProfileEvents, "claude-safe");

    public static string GenerateFull(string executablePath) =>
        Generate(executablePath, ClaudeHookCatalog.FullProfileEvents, "claude-full");

    public static string Generate(
        string executablePath,
        IReadOnlyList<string> events,
        string sourceId)
    {
        var hooks = new JsonObject();
        foreach (string eventName in events)
        {
            var command = new JsonObject
            {
                ["type"] = "command",
                ["command"] = executablePath,
                ["args"] = new JsonArray(
                    "--event", eventName,
                    "--source", sourceId,
                    "--hook", eventName),
                ["timeout"] = TimeoutSeconds
            };

            hooks[eventName] = new JsonArray(new JsonObject
            {
                ["hooks"] = new JsonArray(command)
            });
        }

        var root = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/claude-code-settings.json",
            ["env"] = new JsonObject
            {
                ["HARNESS_SPY_HOST"] = "claude-code",
                ["HARNESS_SPY_RUNTIME_ID"] = "claude-code"
            },
            ["hooks"] = hooks
        };

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    // Reads the hook event names registered by a settings.json document, used by
    // tests to assert exact profile membership without depending on formatting.
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
