using System.Text.Json;

namespace HarnessSpy.Core.Runtimes;

// Everything an engine needs to interpret one native observation. The payload
// is the untouched provider JSON; ConfiguredEventName is the hook key the
// collector was registered under (authoritative when the payload omits an
// event discriminator, as Copilot CLI often does).
public sealed record ObservationContext(
    JsonElement Payload,
    string? PayloadEventName,
    string? ConfiguredEventName,
    string DialectId,
    DateTimeOffset ObservedAtUtc);

// A per-harness/per-surface runtime engine. It maps native events onto the
// provider-neutral ObservationInterpretation consumed by shared code.
public interface IHarnessRuntimeEngine
{
    string HarnessId { get; }

    ObservationInterpretation Interpret(ObservationContext context);
}
