using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sources;

// Replay source: parses either a versioned envelope or a legacy raw payload
// from a saved capture. It keeps old captures loadable while new observations
// expose native names/payloads.
public sealed class ReplaySourceAdapter : IObservationSourceAdapter
{
    public ObservationSourceKind SourceKind => ObservationSourceKind.Replay;

    public bool TryConvert(string record, out HookObservation? observation) =>
        TryConvert(record, DateTimeOffset.UtcNow, sourceFilePath: string.Empty, out observation);

    public bool TryConvert(
        string record,
        DateTimeOffset observedAtUtc,
        string sourceFilePath,
        out HookObservation? observation) =>
        HookObservation.TryParseRawPayload(record, observedAtUtc, sourceFilePath, out observation);
}
