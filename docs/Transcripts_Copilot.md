# Copilot transcript extraction contract

Authoritative spec that the Copilot CLI transcript parser
([CopilotCliTranscriptDialectParser.cs](../src/Shared/HarnessSpy.Core/Runtimes/Copilot/CopilotCliTranscriptDialectParser.cs))
and its tests implement against. Update this file whenever the parser or a
fixture changes.

## Scope boundary with SessionViewer

This contract describes the **hook-side transcript enrichment parser** used by
the CopilotSpy runtime. SessionViewer uses a separate and substantially broader
passive-history projector:

- `CopilotCliTranscriptDialectParser` emits conversational nodes for
  assistant reasoning/final answers, attaches tool execution and permission
  outcomes as evidence, and projects usage/model/context records as
  metadata-only session evidence. It also projects `session.start`/
  `session.resume` lifecycle evidence and conversation `system.message` rows as
  system-prompt snapshots.
- SessionViewer uses `Sessions/Copilot/CopilotSessionProjector.cs`. It also
  projects user/system messages, turn boundaries, readable
  `assistant.reasoning`, session lifecycle/context/model/mode records,
  `permission.denied`, `session.error`, `subagent.*`, `skill.*`, `model.*`,
  and compaction event families.
- SessionViewer independently scans current session directories for
  `events.jsonl`, `workspace.yaml`, and colocated `plan.md`. It also supports a
  legacy flat JSONL fallback for events only and does not depend on the
  hook-provided `transcriptPath`.

See [`session_copilot.md`](session_copilot.md) for the complete
SessionViewer source and reconstruction contract.

## Discovery

- Path field: `transcriptPath`, verified on `agentStop` (not `sessionStart`);
  the runtime also accepts it on `sessionEnd` if a producer supplies it.
- Location/naming: `%USERPROFILE%\.copilot\session-state\<sessionId>\events.jsonl`.
- Conventional-path early discovery is **not implemented**. The Spy registry
  registers only a path supplied by an accepted hook payload and never scans
  sibling `session-state` directories.
- VS Code Local: no transcript pointer, no real capture. Remains hooks-only.

## Contract version

- Producer: `copilot-agent`, schema version `1`.
- Verified versions: Copilot `1.0.81`, `1.0.82`, and `1.0.86`.
- Dialect id: `copilot-cli-events-v1`.
- Minimum envelope used by the hook parser:
  `{ type, data, id, timestamp, parentId }`; rows can additionally carry
  `agentId`. SessionViewer also consumes `ephemeral`.

## Record inventory

| `type` | Handling |
|--------|----------|
| `session.start` / `session.resume` | metadata-only native lifecycle bounds; hook lifecycle remains the live structural authority |
| `session.shutdown` | metadata-only final usage/accounting snapshot |
| `session.usage_checkpoint` | metadata-only cumulative usage/accounting snapshot |
| `user.message` | starts/identifies a transcript interaction for following rows; no duplicate hook-side prompt node |
| `system.message` with `role:"system"` | normalized system-prompt snapshot at session scope, or subagent scope when `agentId` is present; other system messages remain raw |
| `assistant.turn_start` / `assistant.turn_end` | raw row captured; no hook-side turn-boundary node |
| `assistant.reasoning` / `assistant.message` | readable or opaque reasoning, final-answer `content`, usage, and `toolRequests[]` |
| `tool.execution_start` / `tool.execution_complete` | exact-id lifecycle plus success/failure/abort/result/duration evidence |
| `permission.requested` / `permission.completed` / `permission.denied` | exact-id permission flow and outcome |
| `hook.start` / `hook.end` (observed producer rows) | no dedicated parser case; durably captured as raw rows without observation nodes |
| model/mode/context/permission change families | compact metadata-only session evidence |

## Native ids and correlation keys

| Concern | Hook | Transcript | Match |
|---------|------|------------|-------|
| Session | `sessionId` | parser uses the `TranscriptLine.NativeSessionId` supplied by discovery; it does not read `data.sessionId` | scoped identity supplied by the registry |
| Turn | derived (`userPromptSubmitted`..`agentStop`) | `interactionId` spans the user interaction; hook-side key is `transcript-interaction:<id>` while SessionViewer uses `interaction:<id>`; native `turnId` identifies one model/tool step | timestamp alignment joins captured hook turns; unmatched history keeps its namespaced key; native `turnId` remains provenance |
| Tool call | often absent | `toolCallId` on projected tool records | exact shared ID, or canonical-argument signature for the request followed by an exact transcript-ID alias |
| Record chain | - | `id`/`parentId` | chronological link only |

## Extraction-to-hook mapping

| Transcript record | Target | Reconciliation key |
|-------------------|--------|--------------------|
| `session.start` / `session.resume` | metadata-only native lifecycle evidence | provider-scoped session |
| conversation `system.message` | system-prompt snapshot outside ordinary main-session turns | normalized content hash plus session/subagent scope |
| `assistant.message.toolRequests[]` | evidence on a matching canonical pre-tool request, otherwise standalone tool node pending late-hook promotion | exact shared ID, else provider-scoped canonical-argument signature |
| `tool.execution_start`/`complete` | evidence on the canonical pre-tool request, or on a pending transcript request until its hook arrives | `toolCallId` learned from the matched request |
| `permission.requested`/`completed` | evidence on the matching tool request when `toolCallId` is present; otherwise standalone permission node | direct or nested `toolCallId` |
| readable `assistant.reasoning` / `reasoningText` | transcript-only observed thought node | native interaction/assistant-step provenance |
| `reasoningOpaque`/`encryptedContent` | transcript-only opaque thought node | none |
| `assistant.message.content` (final answer) | transcript-only assistant message | none |
| message/request usage | turn/request typed measurements | source-record dedupe |
| checkpoint/shutdown usage and provider accounting | session cumulative/final snapshots shown in dashboards | final snapshot supersedes checkpoint/deltas |

## Provider-specific semantics

- The hook-side parser reads `mcpServerName`, `mcpToolName`, and `toolCallId`
  on tool requests/executions. Copilot 1.0.86 execution rows use `toolName`;
  older rows used `name`, and both are accepted. `toolTitle` remains raw-only. For
  permissions it recognizes `permissionRequest.kind:"mcp"`, nested approval
  kind, and direct or nested tool-call ids. Flat hyphenated names are never split.
- `assistant.message.toolRequests[]` is expanded into one transcript
  observation per request. Each request independently attempts exact-ID or
  canonical-argument correlation with a canonical pre-tool hook. The signature
  normalizes the complete `arguments`/`toolArgs`/`tool_input`/`input` value so
  parallel shell calls do not depend on arrival-order FIFO.
- `user.message` starts one namespaced transcript interaction. Every following
  assistant model step remains in that interaction until the next user message.
  The WPF projection aligns it to a captured hook-derived turn by timestamp;
  unmatched historical interactions remain separate and cannot collide with a
  process-local `derived-N`. Copilot's native `turnId` resets for each
  interaction and is not a tree generation id.
- `session.start`, `session.resume`, and `session.shutdown` clear the active
  transcript interaction so rows from a later lifecycle segment cannot inherit
  the previous turn.
- The live parser and SessionViewer support readable
  `assistant.reasoning.content`, `assistant.message.reasoningText`, and opaque
  `reasoningOpaque`/`encryptedContent`. Both use the shared usage extractor for
  token, duration, latency, premium-request, and nano-AIU fields.
- Conversation system prompts are normalized and hashed in both pipelines.
  Repeated snapshots are coalesced by the WPF projection.

## Skill / usage / opaque states

- The hook-enrichment parser does not project skill rows. For rows that resolve
  to a turn, SessionViewer maps `system.message.skills[]` to `Available` and
  maps `skill.*` records to `Available`, `Attached`, `Invoked`, `Loaded`, or
  `ExecutionCorroborated` according to the exact event type. A usage checkpoint
  that merely lists a `skill` tool is not proof of invocation.
- Live Spy and SessionViewer both handle per-event deltas, cumulative
  checkpoints, and final shutdown snapshots while preserving units such as
  `totalNanoAiu`. Shared aggregation prevents checkpoint/final double-counting.
- `reasoningOpaque`/`encryptedContent` is Opaque; readable
  `assistant.reasoning` and `reasoningText` is Observed in both live Spy and
  SessionViewer.

## Privacy notes

`system.message` (full system prompt), user prompts, tool results, and quota
ids are present. Fixtures must be redacted.

## Known unknowns / unverified

The verified hook-enrichment fixtures do not establish subagent execution,
skill execution, compaction, `postToolUseFailure`, `errorOccurred`, or any VS Code Local
transcript/SDK/OTel pointer. SessionViewer has capability-based handlers for
`subagent.*`, `skill.*`, and event names containing `compact`, but those
handlers do not turn an unverified provider schema into a guaranteed contract.
