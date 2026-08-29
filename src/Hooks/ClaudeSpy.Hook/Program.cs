using System.Text;
using HarnessSpy.Core.Hooks;
using HarnessSpy.Core.Runtimes.Claude;

// Installer/generator path: `ClaudeSpy.Hook --generate-settings <safe|full> <outputPath> [exePath]`
// writes a Claude settings.json hooks block for the requested profile. When
// exePath is omitted a placeholder is written for the user's installer to
// replace, so the repository never ships the author's absolute path.
if (args.Length >= 3 && args[0] == "--generate-settings")
{
    string profile = args[1];
    string outputPath = args[2];
    string executablePath = args.Length >= 4
        ? args[3]
        : ClaudeSettingsGenerator.ExecutablePlaceholder;

    string json = profile.Equals("full", StringComparison.OrdinalIgnoreCase)
        ? ClaudeSettingsGenerator.GenerateFull(executablePath)
        : ClaudeSettingsGenerator.GenerateSafe(executablePath);

    await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
    Console.Error.WriteLine($"Wrote {profile} Claude profile to {outputPath}.");
    return 0;
}

ProviderProfile profile2 = ProviderProfile.Claude;
using StreamReader input = new(
    Console.OpenStandardInput(),
    new UTF8Encoding(false),
    detectEncodingFromByteOrderMarks: true);

HookForwarder forwarder = new(
    new NamedPipePayloadSink(profile2.PipeName),
    new HookProcessOptions(profile2),
    new FileHookDiagnostics(FileHookDiagnostics.GetDefaultDirectory(profile2)));

return await forwarder
    .RunAsync(args, input, Console.Out)
    .ConfigureAwait(false);
