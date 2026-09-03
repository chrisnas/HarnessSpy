using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sources;

// Converts one raw transcript JSONL row into zero or more native observations.
// A single assistant row can carry several content blocks (text + tool_use),
// hence zero-to-many. Parsers preserve exact native names and payloads and
// attach file provenance; correlation/deduplication happens later in the
// reconciler. Malformed or purely metadata rows return an empty list without
// throwing so a bad line can never interrupt ingestion.
public interface ITranscriptDialectParser
{
    string DialectId { get; }

    IReadOnlyList<HookObservation> Parse(TranscriptLine line);
}
