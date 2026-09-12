using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Process;

public sealed class RunningSessionProbeFactory
{
    private readonly ProcessInventoryReader _inventory;

    public RunningSessionProbeFactory(ProcessInventoryReader? inventory = null)
    {
        _inventory = inventory ?? new ProcessInventoryReader();
    }

    public IReadOnlyList<IRunningSessionProbe> CreateDefault()
    {
        return
        [
            new CommandLineSessionProbe(
                HookProvider.Cursor,
                ["Cursor", "cursor-agent", "agent"],
                _inventory),
            new ClaudeRunningSessionProbe(_inventory),
            new CommandLineSessionProbe(
                HookProvider.GitHubCopilot,
                ["copilot"],
                _inventory),
            new CommandLineSessionProbe(
                HookProvider.GitHubCopilot,
                ["node"],
                _inventory,
                ["@github/copilot", "copilot-cli"])
        ];
    }
}
