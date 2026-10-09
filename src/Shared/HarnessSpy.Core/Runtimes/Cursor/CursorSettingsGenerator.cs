using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarnessSpy.Core.Runtimes.Cursor;

// Generates a complete Cursor hooks.json profile. The installer supplies the
// real CursorSpy.Hook path; checked-in examples use a placeholder so they never
// contain an author-specific absolute path.
public static class CursorSettingsGenerator
{
    public const string ExecutablePlaceholder = "<CURSORSPY_HOOK_EXECUTABLE>";

    private const int TimeoutSeconds = 5;

    public static string Generate(string executablePath)
    {
        string executableCommand = QuoteIfNeeded(executablePath);
        var hooks = new JsonObject();

        foreach (string eventName in CursorHookCatalog.NativeEvents)
        {
            hooks[eventName] = new JsonArray(new JsonObject
            {
                ["command"] = $"{executableCommand} --hook {eventName}",
                ["timeout"] = TimeoutSeconds
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

    private static string QuoteIfNeeded(string executablePath) =>
        executablePath.Any(char.IsWhiteSpace)
            ? $"\"{executablePath}\""
            : executablePath;
}
