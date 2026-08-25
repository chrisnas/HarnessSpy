using System.Text;
using HarnessSpy.Core.Hooks;

ProviderProfile profile = ProviderProfile.Claude;
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
