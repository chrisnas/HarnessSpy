using System.Text;
using CursorSpy.Hook;

// Cursor writes the hook payload to stdin as UTF-8 (with a BOM). Console.In would
// decode it using the console code page and corrupt any multi-byte content, so read
// the raw standard input stream as UTF-8 and let the reader strip the BOM.
using StreamReader input = new(
    Console.OpenStandardInput(),
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    detectEncodingFromByteOrderMarks: true);

return await new HookForwarder(new NamedPipePayloadSink(), new FileHookDiagnostics())
    .RunAsync(args, input, Console.Out)
    .ConfigureAwait(false);
