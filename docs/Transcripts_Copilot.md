# Copilot transcript extraction contract

Authoritative spec that the Copilot CLI transcript parser
([CopilotCliTranscriptDialectParser.cs](Shared/HarnessSpy.Core/Runtimes/Copilot/CopilotCliTranscriptDialectParser.cs))
and its tests implement against. Update this file whenever the parser or a
fixture changes.

## Scope boundary with SessionViewer

This contract describes the **hook-side transcript enrichment parser** used by
the CopilotSpy runtime. SessionViewer uses a separate and substantially broader
passive-history projector:

- `CopilotCliTranscriptDialectParser` emits conversational nodes for
  assistant reasoning/final answers, attaches tool execution and permission
  outcomes as evidence, and projects usage/model/context records as
  metadata-only session evidence.
- SessionViewer uses `Sessions/Copilot/CopilotSessionProjector.cs`. It also
  projects user/system messages, turn boundaries, readable
  `assistant.reasoning`, session lifecycle/context/model/mode records,
  `permission.denied`, `session.error`, `subagent.*`, `skill.*`, `model.*`,
  and compaction event families.
- SessionViewer independently scans `events.jsonl` and `workspace.yaml`,
  including the legacy flat JSONL fallback; it does not depend on the
  hook-provided `transcriptPath`.

See [`session_copilot.md`](../docs/session_copilot.md) for the complete
SessionViewer source and reconstruction contract.

## Discovery

- Path field: `transcriptPath`, present only on `agentStop` (not `sessionStart`).
- Location/naming: `%USERPROFILE%\.copilot\session-state\<sessionId>\events.jsonl`.
- Optional early discovery: a version-gated conventional path may be formed
  from an accepted `copilot-cli` session id, but only after validating the
  `session.start` producer/version/session identity. Sibling directories are
  never scanned.
- VS Code Local: no transcript pointer, no real capture. Remains hooks-only.

## Contract version

- Producer: `copilot-agent`, schema version `1`.
- Verified versions: Copilot `1.0.81`, `1.0.82`, and `1.0.86`.
- Dialect id: `copilot-cli-events-v1`.
- Envelope: every row is `{ type, data, id, timestamp, parentId }`.

## Record inventory

| `type` | Handling |
|--------|----------|
| `session.start` | raw row captured; hook `sessionStart` remains authoritative |
| `session.shutdown` | metadata-only final usage/accounting snapshot |
| `session.usage_checkpoint` | metadata-only cumulative usage/accounting snapshot |
| `user.message` | raw row captured; no hook-side prompt node |
| `system.message` | raw row captured; no hook-side system/skill node |
| `assistant.turn_start` / `assistant.turn_end` | raw row captured; no hook-side turn-boundary node |
| `assistant.reasoning` / `assistant.message` | readable or opaque reasoning, final-answer `content`, usage, and `toolRequests[]` |
| `tool.execution_start` / `tool.execution_complete` | exact-id lifecycle plus success/failure/abort/result/duration evidence |
| `permission.requested` / `permission.completed` / `permission.denied` | exact-id permission flow and outcome |
| `hook.start` / `hook.end` | raw row captured; no observation node |
| model/mode/context/permission change families | compact metadata-only session evidence |

## Native ids and correlation keys

| Concern | Hook | Transcript | Match |
|---------|------|------------|-------|
| Session | `sessionId` | parser uses the `TranscriptLine.NativeSessionId` supplied by discovery; it does not read `data.sessionId` | scoped identity supplied by the registry |
| Turn | derived (`userPromptSubmitted`..`agentStop`) | `interactionId` spans the user interaction; native `turnId` identifies one model/tool step | timestamp alignment joins captured hook turns; unmatched history keeps a namespaced `transcript-interaction:*` key; native `turnId` remains provenance |
| Tool call | often absent | `toolCallId` on projected tool records | exact shared ID, or canonical-argument signature for the request followed by an exact transcript-ID alias |
| Record chain | - | `id`/`parentId` | chronological link only |

## Extraction-to-hook mapping

| Transcript record | Target | Reconciliation key |
|-------------------|--------|--------------------|
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
- The live parser and SessionViewer support readable
  `assistant.reasoning.content`, `assistant.message.reasoningText`, and opaque
  `reasoningOpaque`/`encryptedContent`. Both use the shared usage extractor for
  token, duration, latency, premium-request, and nano-AIU fields.

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
