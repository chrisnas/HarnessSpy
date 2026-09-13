using System.Text;
using HarnessSpy.Core.Hooks;
using HarnessSpy.Core.Runtimes.Copilot;

// Installer/generator path: `CopilotSpy.Hook --generate-settings <outputPath> [exePath]`
// writes a Copilot CLI harness-spy.json hooks file. When exePath is omitted a
// placeholder is written for the installer to replace.
if (args.Length >= 2 && args[0] == "--generate-settings")
{
    string outputPath = args[1];
    string executablePath = args.Length >= 3
        ? args[2]
        : CopilotSettingsGenerator.ExecutablePlaceholder;

    string json = CopilotSettingsGenerator.GenerateCli(executablePath);
    await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
    Console.Error.WriteLine($"Wrote Copilot CLI profile to {outputPath}.");
    return 0;
}

// Installer/generator path: `CopilotSpy.Hook --generate-vs-settings <outputPath> [exePath]`
// writes a VS Code Local agent-hooks profile (PascalCase events, the
// vscode-agent-hooks runtime id). Install it in a VS Code-only location such as
// .vscode/hooks via the chat.hookFilesLocations setting, never in the shared
// .github/hooks folder that the Copilot CLI also reads.
if (args.Length >= 2 && args[0] == "--generate-vs-settings")
{
    string outputPath = args[1];
    string executablePath = args.Length >= 3
        ? args[2]
        : CopilotSettingsGenerator.ExecutablePlaceholder;

    string json = CopilotSettingsGenerator.GenerateVsCode(executablePath);
    await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
    Console.Error.WriteLine($"Wrote Copilot VS Code profile to {outputPath}.");
    return 0;
}

ProviderProfile profile = ProviderProfile.Copilot;
using StreamReader input = new(
    Console.OpenStandardInput(),
    new UTF8Encoding(false),
    detectEncodingFromByteOrderMarks: true);

HookForwarder forwarder = new(
    new NamedPipePayloadSink(profile.PipeName),
    new HookProcessOptions(profile),
    new FileHookDiagnostics(FileHookDiagnostics.GetDefaultDirectory(profile)));

return await forwarder
    .RunAsync(args, input, Console.Out)
    .ConfigureAwait(false);
