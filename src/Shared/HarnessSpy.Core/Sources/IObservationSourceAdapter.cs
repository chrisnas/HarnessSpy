using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sources;

// Converts a raw source record (hook invocation envelope, replay record, and in
// future an SDK callback, JSON-RPC/JSONL frame, telemetry record, or cloud
// record) into the same lossless HookObservation without normalizing native
// names. Adding a future Codex source is a matter of adding an adapter here
// plus a runtime engine and a registry entry.
public interface IObservationSourceAdapter
{
    ObservationSourceKind SourceKind { get; }

    // Returns true and yields an observation when the record is understood.
    bool TryConvert(string record, out HookObservation? observation);
}
