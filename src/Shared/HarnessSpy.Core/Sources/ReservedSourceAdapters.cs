namespace HarnessSpy.Core.Sources;

// Reserved source-adapter contracts for higher-fidelity ingestion that is out
// of scope for this iteration. They exist so future work (Copilot SDK/OTel/ACP,
// Codex app-server/exec/telemetry/cloud) can plug in without reshaping the
// observation model or shared UI. None are implemented here.

// SDK lifecycle callbacks and streaming events (e.g. Copilot SDK, Codex SDK).
public interface ISdkEventSourceAdapter : IObservationSourceAdapter;

// Long-lived NDJSON / JSON-RPC protocol streams over stdio or TCP
// (e.g. Copilot ACP, Codex app-server).
public interface IProtocolStreamSourceAdapter : IObservationSourceAdapter;

// OpenTelemetry spans/events used as passive enrichment, reconciled with live
// observations through source ids and traces.
public interface ITelemetrySourceAdapter : IObservationSourceAdapter;

// Authenticated cloud polling/audit APIs (e.g. Copilot cloud, Codex cloud).
public interface ICloudPollingSourceAdapter : IObservationSourceAdapter;
