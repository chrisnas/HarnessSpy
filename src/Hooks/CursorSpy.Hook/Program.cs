using System.Text;
using HarnessSpy.Core.Hooks;
using HarnessSpy.Core.Runtimes.Cursor;

// Installer/generator path: `CursorSpy.Hook --generate-settings <outputPath> [exePath]`
// writes a complete Cursor hooks.json profile. The camel-case spelling is
// accepted as an alias. When exePath is omitted, an installer-safe placeholder
// is written instead of an author-specific absolute path.
if (args.Length >= 2 &&
    args[0] is "--generate-settings" or "--generateSettings")
{
    string outputPath = args[1];
    string executablePath = args.Length >= 3
        ? args[2]
        : CursorSettingsGenerator.ExecutablePlaceholder;

    string json = CursorSettingsGenerator.Generate(executablePath);
    await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
    Console.Error.WriteLine($"Wrote Cursor profile to {outputPath}.");
    return 0;
}

ProviderProfile profile = ProviderProfile.Cursor;
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
