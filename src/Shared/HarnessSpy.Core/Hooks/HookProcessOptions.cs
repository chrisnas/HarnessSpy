using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Hooks;

public enum HookNoOpResponse
{
    EmptyJsonObject,
    Silent
}

public sealed record ProviderProfile(
    HookProvider Provider,
    HookSurface Surface,
    string DisplayName,
    string PipeName,
    string SettingsFolderName,
    HookNoOpResponse NoOpResponse)
{
    public static ProviderProfile Cursor { get; } = new(
        HookProvider.Cursor,
        HookSurface.CursorIde,
        "CursorSpy",
        "HarnessSpy.Cursor.Ingest.v1",
        "Cursor",
        HookNoOpResponse.EmptyJsonObject);

    public static ProviderProfile Claude { get; } = new(
        HookProvider.ClaudeCode,
        HookSurface.ClaudeCode,
        "ClaudeSpy",
        "HarnessSpy.Claude.Ingest.v1",
        "Claude",
        HookNoOpResponse.Silent);

    public static ProviderProfile Copilot { get; } = new(
        HookProvider.GitHubCopilot,
        HookSurface.CopilotCli,
        "CopilotSpy",
        "HarnessSpy.Copilot.Ingest.v1",
        "Copilot",
        HookNoOpResponse.EmptyJsonObject);
}

public sealed record HookProcessOptions(
    ProviderProfile Profile,
    string? ConfiguredEventName = null,
    string? SourceConfigurationId = null,
    string? ConfiguredHookName = null);

public static class HookArguments
{
    public static HookProcessOptions Apply(string[] args, HookProcessOptions defaults)
    {
        string? eventName = defaults.ConfiguredEventName;
        string? source = defaults.SourceConfigurationId;
        string? hookName = defaults.ConfiguredHookName;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument == "--event" && index + 1 < args.Length)
            {
                eventName = args[++index];
            }
            else if (argument == "--source" && index + 1 < args.Length)
            {
                source = args[++index];
            }
            else if (argument == "--hook" && index + 1 < args.Length)
            {
                hookName = args[++index];
            }
        }

        return defaults with
        {
            ConfiguredEventName = eventName,
            SourceConfigurationId = source,
            ConfiguredHookName = hookName
        };
    }
}
