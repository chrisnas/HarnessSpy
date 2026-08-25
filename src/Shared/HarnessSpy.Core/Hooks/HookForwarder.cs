using System.IO.Pipes;
using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Hooks;

public interface IHookPayloadSink
{
    Task ForwardAsync(ReadOnlyMemory<byte> payloadLine, CancellationToken cancellationToken);
}

public sealed class NamedPipePayloadSink(
    string pipeName,
    TimeSpan? timeout = null) : IHookPayloadSink
{
    private static readonly byte[] NewLine = [(byte)'\n'];
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMilliseconds(150);

    public async Task ForwardAsync(
        ReadOnlyMemory<byte> payloadLine,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        await using NamedPipeClientStream pipe = new(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
        await pipe.WriteAsync(payloadLine, timeoutSource.Token).ConfigureAwait(false);
        await pipe.WriteAsync(NewLine, timeoutSource.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeoutSource.Token).ConfigureAwait(false);
    }
}

public sealed class HookForwarder
{
    public const string DefaultPipeName = "HarnessSpy.Cursor.Ingest.v1";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IHookPayloadSink _sink;
    private readonly HookProcessOptions _options;
    private readonly IHookDiagnostics _diagnostics;
    private readonly IHookRuntimeDetector _runtimeDetector;
    private readonly IReadOnlyDictionary<string, string?> _environment;

    public HookForwarder(
        IHookPayloadSink sink,
        IHookDiagnostics? diagnostics = null)
        : this(
            sink,
            new HookProcessOptions(ProviderProfile.Cursor),
            diagnostics,
            new HookRuntimeDetector(),
            HookEnvironment.Capture())
    {
    }

    public HookForwarder(
        IHookPayloadSink sink,
        HookProcessOptions options,
        IHookDiagnostics? diagnostics = null,
        IHookRuntimeDetector? runtimeDetector = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        _sink = sink;
        _options = options;
        _diagnostics = diagnostics ?? NullHookDiagnostics.Instance;
        _runtimeDetector = runtimeDetector ?? new HookRuntimeDetector();
        _environment = environment ?? HookEnvironment.Capture();
    }

    public async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        HookProcessOptions effectiveOptions = HookArguments.Apply(args, _options);

        try
        {
            string rawPayload = await input
                .ReadToEndAsync(cancellationToken)
                .ConfigureAwait(false);
            rawPayload = rawPayload.TrimStart('\uFEFF');

            using JsonDocument source = JsonDocument.Parse(rawPayload);
            JsonElement payload = source.RootElement.Clone();
            string rawEventName =
                ReadString(payload, "hook_event_name") ??
                effectiveOptions.ConfiguredEventName ??
                "unknownHook";
            string sessionId =
                ReadString(payload, "conversation_id") ??
                ReadString(payload, "session_id") ??
                ReadString(payload, "sessionId") ??
                "unknownSession";

            HookSurface detectedSurface = _runtimeDetector.Detect(
                effectiveOptions.Profile,
                payload,
                _environment);

            if (_runtimeDetector.IsAccepted(effectiveOptions.Profile, detectedSurface))
            {
                ObservationEnvelope envelope = new(
                    ObservationEnvelope.CurrentIngressVersion,
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    effectiveOptions.Profile.Provider,
                    effectiveOptions.Profile.Surface,
                    detectedSurface,
                    ObservationSourceKind.Hook,
                    effectiveOptions.ConfiguredEventName,
                    rawEventName,
                    effectiveOptions.SourceConfigurationId,
                    SourceFilePath: null,
                    ParseStatus: "valid",
                    Payload: payload);

                string? sourceFilePath = await _diagnostics
                    .SaveObservationAsync(
                        effectiveOptions.Profile.Provider,
                        sessionId,
                        rawEventName,
                        envelope,
                        cancellationToken)
                    .ConfigureAwait(false);

                envelope = envelope with { SourceFilePath = sourceFilePath };
                byte[] encodedEnvelope =
                    JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

                try
                {
                    await _sink
                        .ForwardAsync(encodedEnvelope, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception forwardException)
                {
                    await SafeLogAsync(
                        $"Failed to forward '{rawEventName}' to '{effectiveOptions.Profile.PipeName}'.",
                        forwardException,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            await SafeLogAsync(
                "Unexpected failure while processing a hook payload.",
                ex,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (effectiveOptions.Profile.NoOpResponse == HookNoOpResponse.EmptyJsonObject)
            {
                await output.WriteAsync("{}").ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static string? ReadString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private async Task SafeLogAsync(
        string context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await _diagnostics
                .LogErrorAsync(context, exception, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Observers are fail-open by design.
        }
    }
}
