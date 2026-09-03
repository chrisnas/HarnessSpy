using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;

namespace HarnessSpy.Core.Sources;

// Safe fallback for an unrecognised or version-skewed transcript dialect. It
// exposes a valid JSON row as a generic, summary-excluded evidence node with
// the raw row preserved, and never claims semantic correlation.
internal sealed class UnknownTranscriptDialectParser : TranscriptDialectParserBase
{
    public override string DialectId => DialectIds.UnknownTranscript;

    protected override IReadOnlyList<HookObservation> ParseRow(TranscriptLine line, JsonElement row)
    {
        string nativeEvent = RuntimeJson.String(row, "type", "role") ?? "transcriptRecord";

        var builder = new InterpretationBuilder(nativeEvent)
        {
            SessionId = line.NativeSessionId,
            Role = ObservationRole.Generic,
            EventKind = CanonicalEventKind.ProviderSpecific,
            CorrelationQuality = CorrelationQuality.Heuristic,
            Evidence = InferenceEvidence.Ambiguous,
            ExcludeFromSummary = true
        };

        JsonObject payload = CloneToObject(row);
        return [Emit(line, payload, builder.Build(), line.Provenance(0, TranscriptCompleteness.Complete))];
    }
}
