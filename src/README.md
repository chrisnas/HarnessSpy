# HarnessSpy

HarnessSpy is a .NET 10 Windows solution for observing agent hook activity from
Cursor, Claude Code, GitHub Copilot CLI, and VS Code agent hooks.

The implementation is independent from the reference POC under
`..\CursorSpy\POC`. It does not reference or modify that tree.

## Projects

- `Shared/HarnessSpy.Core`: hook transport, diagnostics, provider detection,
  adapters, normalized observations, storage, replay, and named pipes.
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

The files under `Config` are inert examples with absolute Debug executable
paths. Build first, inspect the file, and manually merge or supply it to the
host. HarnessSpy never overwrites existing host settings.

### Cursor

Merge `Config\Cursor\hooks.example.json` into either:

- project: `.cursor\hooks.json`
- user: `%USERPROFILE%\.cursor\hooks.json`

All 21 native Cursor events are included.

### Claude Code

Prefer an isolated one-run settings file:

```powershell
claude --settings C:\dev\research\AI\HarnessSpy\src\Config\Claude\settings.example.json
```

Alternatively merge the hooks into `.claude\settings.local.json`,
`.claude\settings.json`, or `%USERPROFILE%\.claude\settings.json`.

The default template deliberately excludes `WorktreeCreate`, because defining
that hook replaces Claude's normal worktree creation. High-volume or sensitive
events such as `MessageDisplay`, `FileChanged`, and elicitation are also not
enabled by default.

Cursor and Copilot can import repository Claude settings. The Claude hook
binary checks runtime evidence and silently ignores foreign invocations.

### GitHub Copilot CLI and VS Code

Copy or merge `Config\Copilot\harness-spy.example.json` under
`.github\hooks\`. Copilot CLI uses its lower-camel event configuration and
passes the configured event name to the hook because native CLI payloads may
omit `hook_event_name`.

VS Code can load and translate the same file into PascalCase/snake-case hook
payloads. CopilotSpy detects and supports both local formats. GitHub cloud
coding-agent collection is not included because a local Windows executable and
named pipe are unavailable from its ephemeral Linux environment.

## Passive safety contract

- Cursor and Copilot hooks write exactly `{}` and exit `0`.
- Claude hooks write no stdout and exit `0`.
- Successful hooks write no stderr.
- Parsing, persistence, diagnostics, timeout, and missing-viewer failures are
  swallowed so observation cannot deny or mutate host behavior.
- Hook output never contains permission, continuation, context, or rewritten
  input fields.

## Replay and normalization

New captures use versioned envelopes containing provider, configured and
detected surfaces, raw event name, and the untouched provider payload. The
viewer also accepts the original Cursor `hp_*.json` files.

Provider adapters derive a common event/tool vocabulary for tree projection,
but the inspector always displays the raw provider payload. Unknown future
events and tools are retained instead of rejected.

Copilot CLI lacks documented exact turn and tool-call IDs. Its turns are
derived from prompt/stop boundaries and ambiguous concurrent operations are not
presented as exact correlations.

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
