# HarnessSpy

HarnessSpy is a .NET 10 Windows solution for observing agent hook activity from
Cursor, Claude Code, GitHub Copilot CLI, and VS Code agent hooks.

The implementation is independent from the reference POC under
`..\CursorSpy\POC`. It does not reference or modify that tree.

## Projects

- `Shared/HarnessSpy.Core`: hook transport, diagnostics, runtime detection,
  source adapters, per-harness/per-surface runtime engines, lossless native
  observations, storage, replay, and named pipes. See
  [architecture.md](architecture.md) for the runtime/source design.
- `Shared/HarnessSpy.Agent.Abstractions`: provider-neutral contracts reserved
  for future SDK-driven agent creation and streaming.
- `Shared/HarnessSpy.Wpf`: the shared workspace/session/turn tree, summaries,
  search, inspector, replay, deletion, and application host.
- `Apps/*Spy.App`: three thin WPF composition roots.
- `Hooks/*Spy.Hook`: three short-lived fail-open hook executables.
- `Tests/HarnessSpy.Tests`: parity, provider, replay, pipe, process, and future
  agent-contract tests.

## Build and test

```powershell
dotnet build C:\dev\research\AI\HarnessSpy\src\HarnessSpy.sln
dotnet test C:\dev\research\AI\HarnessSpy\src\HarnessSpy.sln
```

Run one viewer before invoking its hook:

```powershell
dotnet run --project C:\dev\research\AI\HarnessSpy\src\Apps\CursorSpy.App
dotnet run --project C:\dev\research\AI\HarnessSpy\src\Apps\ClaudeSpy.App
dotnet run --project C:\dev\research\AI\HarnessSpy\src\Apps\CopilotSpy.App
```

The viewers use separate current-user-only pipes:

- `HarnessSpy.Cursor.Ingest.v1`
- `HarnessSpy.Claude.Ingest.v1`
- `HarnessSpy.Copilot.Ingest.v1`

Captured envelopes are stored under
`%LOCALAPPDATA%\HarnessSpy\<Provider>\Payloads`. Viewer convenience settings
are stored under `%APPDATA%\HarnessSpy\<Provider>\settings.json`.

## Manual hook activation

The files under `Config` are inert examples that use a `<...>` executable
placeholder instead of an absolute path. Regenerate them with the per-provider
installer path below (stamping your real executable), inspect the file, and
manually merge or supply it to the host. HarnessSpy never overwrites existing
host settings.

### Cursor

Merge `Config\Cursor\hooks.example.json` into either:

- project: `.cursor\hooks.json`
- user: `%USERPROFILE%\.cursor\hooks.json`

All 21 native Cursor events are included.

### Claude Code

Requires Claude Code `2.1.196+` for exact `prompt_id` turn grouping.

Two tested passive profiles are generated from the case-sensitive
`ClaudeHookCatalog` so the repository never ships an absolute author path:

- `Config\Claude\settings.example.json` — the Safe profile with 28 events.
- `Config\Claude\settings.full.example.json` — the Full profile with 32 events.

Regenerate them (and stamp your real executable path) with the installer path:

```powershell
ClaudeSpy.Hook.exe --generate-settings safe  <out> <ClaudeSpy.Hook.exe>
ClaudeSpy.Hook.exe --generate-settings full  <out> <ClaudeSpy.Hook.exe>
```

Prefer an isolated one-run settings file:

```powershell
claude --settings C:\path\to\settings.example.json
```

Both profiles deliberately exclude `WorktreeCreate`: its command must return a
worktree path and cannot use the passive silent forwarder. The Full profile
adds the high-volume/sensitive events `MessageDisplay`, `FileChanged`,
`Elicitation`, and `ElicitationResult`; enable it only when you need them.
`PreModelSwitch`/`PostModelSwitch` are registered but their payload schemas are
treated as open-ended until documented.

Cursor and Copilot can import repository Claude settings. The Claude hook binary
checks runtime evidence and silently ignores foreign invocations.

### GitHub Copilot CLI and VS Code

`Config\Copilot\harness-spy.example.json` registers all 14 documented Copilot
CLI hook events (version `1`). Regenerate it with the installer path:

```powershell
CopilotSpy.Hook.exe --generate-settings <out> <CopilotSpy.Hook.exe>
```

Copilot CLI passes the configured event name and an explicit runtime/dialect id
to the hook because native CLI payloads omit `hook_event_name`. The CLI can also
emit a VS Code-compatible PascalCase/snake_case dialect; that dialect keeps CLI
surface identity and is never reclassified as actual VS Code. The VS Code Local
Preview surface has its own eight-event contract with exact `tool_use_id`/
`agent_id` correlation.

Copilot CLI correlation is derived, not exact: turns are derived from
`userPromptSubmitted`..`agentStop`, and subagents correlate heuristically by
name because `subagentStart` lacks the `agentId` supplied by `subagentStop`.

GitHub cloud coding-agent collection is out of scope: an authenticated HTTPS
relay and a Linux-compatible collector are required from its ephemeral Linux
sandbox. Copilot SDK, OpenTelemetry, and ACP ingestion are documented follow-ups
in [architecture.md](architecture.md).

## Passive safety contract

- Cursor and Copilot hooks write exactly `{}` and exit `0`.
- Claude hooks write no stdout and exit `0`.
- Successful hooks write no stderr.
- Parsing, persistence, diagnostics, timeout, and missing-viewer failures are
  swallowed so observation cannot deny or mutate host behavior.
- Hook output never contains permission, continuation, context, or rewritten
  input fields.

## Native identity architecture

Each provider's exact native hook and tool names are displayed, persisted, and
used for correlation. `Bash`/`PowerShell` are never renamed to `Shell`, `Edit`
is never renamed to `Write`, and Claude events keep their PascalCase names.
Semantic traits (scope, direction, category, correlation role) are derived
sidecar metadata only; the shared WPF tree, inspector, and summaries branch on
those traits, never on provider event-name strings or casing.

Per-harness, per-surface runtime engines (Cursor, Claude, Copilot CLI, VS Code
Local, and an unknown fallback) interpret native events into those traits. A
`HarnessRuntimeRegistry` selects the engine; unknown providers/events always
appear in deterministic time order rather than being dropped.

New captures use versioned envelopes containing provider, configured and
detected surfaces, raw event name, and the untouched provider payload. The
viewer also accepts the original Cursor `hp_*.json` files and legacy raw
payloads. Every observation is placed by `(EffectiveTimestamp, IngestionOrdinal,
EventId)`, so late-arriving events land at the correct position.

Explicit source/surface/dialect metadata (`HARNESS_SPY_RUNTIME_ID`) wins over
payload casing heuristics; VS Code is never inferred from PascalCase/snake_case
alone because Copilot CLI can emit that same dialect.

See [architecture.md](architecture.md) for the full runtime/source model and the
CodexSpy and Copilot SDK/OTel/ACP follow-up blueprints.

## Future SDK agent support

`HarnessSpy.Agent.Abstractions` defines optional create, resume, send,
interrupt, cancel, approval, capability, and notification contracts. The
initial applications inject `UnavailableAgentProvider`, so SDK controls stay
hidden and hook monitoring has no SDK dependency.

Future provider packages can publish notifications through the same canonical
model, replay path, and WPF UI:

- Cursor: first-party `cursor-sdk-bridge` using its `sdk.v1` Connect/protobuf
  contract from C#.
- Claude: a TypeScript sidecar using
  `@anthropic-ai/claude-agent-sdk` and the versioned duplex contract under
  `Contracts\AgentHost\v1`.
- Copilot: the official `GitHub.Copilot.SDK` .NET package.

The short-lived hook pipe remains one-way. SDK commands, approvals,
cancellation, health, reconnect, and long-lived streams use a separate duplex
boundary. Hook and SDK identifier namespaces are retained independently unless
a pinned-version conformance test proves an exact relationship.

## Data sensitivity

Hook payloads can contain prompts, source content, command output, paths,
transcript locations, and secrets. Captures are plaintext local files. Review
and redact them before sharing, and delete captures when they are no longer
needed.
