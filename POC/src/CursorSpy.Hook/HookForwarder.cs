using System.IO.Pipes;
using System.Text.Json;

namespace CursorSpy.Hook;

public interface IHookPayloadSink
{
    Task ForwardAsync(ReadOnlyMemory<byte> payloadLine, CancellationToken cancellationToken);
}

internal sealed class NamedPipeUnavailableException(string pipeName, Exception innerException)
    : IOException($"The named pipe '{pipeName}' is unavailable.", innerException);

public sealed class NamedPipePayloadSink(
    string pipeName = HookForwarder.DefaultPipeName,
    TimeSpan? timeout = null) : IHookPayloadSink
{
    private static readonly byte[] NewLine = [(byte)'\n'];
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMilliseconds(150);

    public async Task ForwardAsync(ReadOnlyMemory<byte> payloadLine, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The WPF viewer is optional. An unavailable listener is expected
            // when it is not running, so let the forwarder distinguish this
            // from a failure after a connection was established.
            throw new NamedPipeUnavailableException(pipeName, exception);
        }

        await pipe.WriteAsync(payloadLine, timeoutSource.Token).ConfigureAwait(false);
        await pipe.WriteAsync(NewLine, timeoutSource.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeoutSource.Token).ConfigureAwait(false);
    }
}

public sealed class HookForwarder(IHookPayloadSink sink, IHookDiagnostics? diagnostics = null)
{
    public const string DefaultPipeName = "HarnessSpy.Ingest.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHookDiagnostics _diagnostics = diagnostics ?? NullHookDiagnostics.Instance;

    public async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        // The host cannot tell us which hook fired once stdin is empty, so the
        // registered hook name is passed explicitly via --hook and used to label
        // diagnostics even when the payload never arrives.
        string? configuredHookName = ReadConfiguredHookName(args);
        string rawPayload = string.Empty;
        string hookEventName = configuredHookName ?? "unknownHook";
        string sessionId = "unknownSession";
        string? sourceFilePath = null;

        try
        {
            rawPayload = await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            // Defensively drop a leading UTF-8/UTF-16 BOM character; JsonDocument.Parse
            // rejects it as an invalid start-of-value token.
            rawPayload = rawPayload.TrimStart('\uFEFF');

            hookEventName = ReadHookEventName(rawPayload, configuredHookName);
            sessionId = ReadStringProperty(rawPayload, "session_id");

            // Persist the raw payload first so we can confirm the hook was invoked
            // even when parsing or pipe forwarding later fails. The returned path
            // travels with the envelope so the viewer can tie a live observation
            // back to its on-disk file (e.g. to delete a captured session).
            sourceFilePath = await _diagnostics
                .SavePayloadAsync(sessionId, hookEventName, rawPayload, cancellationToken)
                .ConfigureAwait(false);

            JsonElement payload;
            using (JsonDocument source = JsonDocument.Parse(rawPayload))
            {
                payload = source.RootElement.Clone();
            }

            var envelope = new ObservationEnvelope(
                IngressVersion: 1,
                EventId: Guid.NewGuid(),
                ObservedAtUtc: DateTimeOffset.UtcNow,
                SourceFilePath: sourceFilePath,
                Payload: payload);

            byte[] encodedEnvelope = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

            try
            {
                await sink.ForwardAsync(encodedEnvelope, cancellationToken).ConfigureAwait(false);
            }
            catch (NamedPipeUnavailableException)
            {
                // The viewer is not running. The payload is already persisted
                // and can be loaded later, so this is not an error.
            }
            catch (Exception forwardException)
            {
                await _diagnostics.LogErrorAsync(
                    $"Failed to forward '{hookEventName}' payload to the named pipe '{DefaultPipeName}'.",
                    forwardException,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Cursor hooks are observational for this POC; failures must not affect Cursor.
            sourceFilePath ??= await SafeSavePayloadAsync(
                sessionId,
                hookEventName,
                rawPayload,
                cancellationToken).ConfigureAwait(false);

            string payloadDescription = rawPayload.Length == 0
                ? "Payload length: 0 (empty stdin)."
                : $"Payload length: {rawPayload.Length}.";
            string payloadPath = sourceFilePath ?? "(payload could not be saved)";
            await SafeLogAsync(
                    $"Unexpected failure while processing the '{hookEventName}' hook payload. " +
                    $"Failed payload file: {payloadPath}. {payloadDescription}",
                    ex,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await output.WriteAsync("{}").ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }

        return 0;
    }

    private static string ReadHookEventName(string rawPayload, string? configuredHookName)
    {
        string value = ReadStringProperty(rawPayload, "hook_event_name");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return configuredHookName ?? "unknownHook";
    }

    private static string? ReadConfiguredHookName(string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--hook" && index + 1 < args.Length)
            {
                string value = args[index + 1];
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    private static string ReadStringProperty(string rawPayload, string propertyName)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(rawPayload);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(propertyName, out JsonElement property) &&
                property.ValueKind == JsonValueKind.String)
            {
                string? value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        catch (JsonException)
        {
        }

        return string.Empty;
    }

    private async Task<string?> SafeSavePayloadAsync(
        string sessionId,
        string hookEventName,
        string rawPayload,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _diagnostics
                .SavePayloadAsync(sessionId, hookEventName, rawPayload, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task SafeLogAsync(string context, Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            await _diagnostics.LogErrorAsync(context, exception, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Never let diagnostics failures surface to Cursor.
        }
    }

    private sealed record ObservationEnvelope(
        int IngressVersion,
        Guid EventId,
        DateTimeOffset ObservedAtUtc,
        string? SourceFilePath,
        JsonElement Payload);
}
