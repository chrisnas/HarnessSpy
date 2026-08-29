using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sources;

// Independent transport dimensions kept separate from the payload dialect and
// execution surface. A single provider action can later be observed by more
// than one source (hook + SDK + telemetry) and reconciled by provenance.
public enum ObservationTransport
{
    NamedPipe,
    File,
    Stdio,
    JsonRpc,
    Http,
    InProcess
}

public enum ExecutionLocation
{
    Unknown,
    Local,
    Remote,
    Cloud
}

// Describes where an observation came from and with what confidence its runtime
// was identified. Explicit configuration metadata is authoritative; payload
// heuristics are a fallback with lower confidence.
public sealed record ObservationSourceDescriptor(
    ObservationSourceKind SourceKind,
    ObservationTransport Transport,
    ExecutionLocation ExecutionLocation,
    string DeclaredHarnessId,
    string DeclaredSurfaceId,
    string DialectId,
    string? RuntimeVersion,
    string? ContractVersion,
    RuntimeDetectionConfidence Confidence)
{
    public static ObservationSourceDescriptor ForHook(
        string harnessId,
        string surfaceId,
        string dialectId) =>
        new(
            ObservationSourceKind.Hook,
            ObservationTransport.NamedPipe,
            ExecutionLocation.Local,
            harnessId,
            surfaceId,
            dialectId,
            RuntimeVersion: null,
            ContractVersion: null,
            RuntimeDetectionConfidence.Declared);
}

public enum RuntimeDetectionConfidence
{
    // Detected only from payload shape (casing/fields); ambiguous.
    Heuristic,

    // Detected from strong payload evidence (transcript_path, ids).
    PayloadEvidence,

    // Supplied by explicit configuration/transport metadata; authoritative.
    Declared
}
