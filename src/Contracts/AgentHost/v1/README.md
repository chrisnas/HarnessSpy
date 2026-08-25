# AgentHost v1

`AgentHost.v1` is the future duplex boundary between HarnessSpy and SDK providers
that cannot run directly in the .NET viewer. Messages are newline-delimited JSON
objects validated by `agent-host.schema.json`.

The protocol keeps commands and events separate from the short-lived hook ingest
pipe. Implementations must negotiate capabilities, preserve unknown fields,
sequence events, acknowledge durable progress, support reconnect cursors, and
never serialize provider credentials.

No vendor bridge is shipped in the initial hook-monitoring release.
