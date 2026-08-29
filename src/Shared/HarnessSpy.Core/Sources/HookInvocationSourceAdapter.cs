using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sources;

// Live hook-invocation source: parses the versioned ObservationEnvelope emitted
// by the forwarders over the named pipe. Named Pipe is the only implemented
// live transport in this scope; SDK/protocol/telemetry/cloud adapters are
// reserved as follow-ups.
public sealed class HookInvocationSourceAdapter : IObservationSourceAdapter
{
    public ObservationSourceKind SourceKind => ObservationSourceKind.Hook;

    public bool TryConvert(string record, out HookObservation? observation) =>
        HookObservation.TryParse(record, out observation);
}
