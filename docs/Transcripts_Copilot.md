# Copilot transcript extraction contract

Authoritative spec that the Copilot CLI transcript parser
([CopilotCliTranscriptDialectParser.cs](Shared/HarnessSpy.Core/Runtimes/Copilot/CopilotCliTranscriptDialectParser.cs))
and its tests implement against. Update this file whenever the parser or a
fixture changes.

## Scope boundary with SessionViewer

This contract describes the **hook-side transcript enrichment parser** used by
the CopilotSpy runtime. SessionViewer uses a separate and substantially broader
passive-history projector:

- `CopilotCliTranscriptDialectParser` emits nodes for
  `assistant.message`, tool execution, and permission request/completion rows.
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
- Verified versions: Copilot `1.0.81` and `1.0.82`.
- Dialect id: `copilot-cli-events-v1`.
- Envelope: every row is `{ type, data, id, timestamp, parentId }`.

## Record inventory

| `type` | Handling |
|--------|----------|
| `session.start` / `session.shutdown` | raw row captured; no hook-side observation node |
| `session.usage_checkpoint` | raw row captured; no hook-side usage projection |
| `user.message` | raw row captured; no hook-side prompt node |
| `system.message` | raw row captured; no hook-side system/skill node |
| `assistant.turn_start` / `assistant.turn_end` | raw row captured; no hook-side turn-boundary node |
| `assistant.message` | projects final-answer `content`, `toolRequests[]`, and opaque reasoning |
| `tool.execution_start` / `tool.execution_complete` | tool lifecycle |
| `permission.requested` / `permission.completed` | permission flow |
| `hook.start` / `hook.end` | raw row captured; no observation node |
| `model_change`, `auto_mode_resolved` | raw row captured; no observation node |

## Native ids and correlation keys

| Concern | Hook | Transcript | Match |
|---------|------|------------|-------|
| Session | `sessionId` | parser uses the `TranscriptLine.NativeSessionId` supplied by discovery; it does not read `data.sessionId` | scoped identity supplied by the registry |
| Turn | derived (`userPromptSubmitted`..`agentStop`) | the hook-side parser does not project `turnId`/`interactionId` | no turn reconciliation |
| Tool call | often absent | `toolCallId` on projected tool records | exact only if the hook also has that ID; otherwise signature/FIFO for tool requests |
| Record chain | - | `id`/`parentId` | chronological link only |

## Extraction-to-hook mapping

| Transcript record | Target | Reconciliation key |
|-------------------|--------|--------------------|
| `assistant.message.toolRequests[]` | evidence on a matching canonical pre-tool request, otherwise standalone tool node | exact shared ID, else provider-scoped tool signature/FIFO |
| `tool.execution_start`/`complete` | evidence on the canonical pre-tool request when an exact shared ID exists | `toolCallId`; execution rows do not signature-match |
| `permission.requested`/`completed` | standalone permission node | no tool-call or `parentId` reconciliation is implemented |
| `reasoningOpaque`/`encryptedContent` | transcript-only opaque thought node | none |
| `assistant.message.content` (final answer) | transcript-only assistant message | none |
| `session.shutdown.tokenDetails` | no hook-side dashboard projection | raw capture only; SessionViewer handles it separately |

## Provider-specific semantics

- The hook-side parser reads `mcpServerName` and `toolCallId` on tool
  requests/executions. It uses `mcpToolName` only in the display header and
  does not populate structured `McpToolName`; `toolTitle` is not read. For
  permissions it recognizes `permissionRequest.kind:"mcp"` and nested approval
  kind. Flat hyphenated names are never split.
- `assistant.message.toolRequests[]` is expanded into one transcript
  observation per request. Each request independently attempts exact-ID or
  signature/FIFO correlation with a canonical pre-tool hook.
- In the verified hook-enrichment fixture, reasoning is opaque. The hook-side
  parser does not extract `reasoningTokens` or any other usage from Copilot
  transcript rows. SessionViewer supports usage extraction and also supports
  readable `assistant.reasoning.content` and
  `assistant.message.reasoningText`; `reasoningOpaque` and
  `encryptedContent` remain opaque.

## Skill / usage / opaque states

- The hook-enrichment parser does not project skill rows. For rows that resolve
  to a turn, SessionViewer maps `system.message.skills[]` to `Available` and
  maps `skill.*` records to `Available`, `Attached`, `Invoked`, `Loaded`, or
  `ExecutionCorroborated` according to the exact event type. A usage checkpoint
  that merely lists a `skill` tool is not proof of invocation.
- The hook-enrichment parser emits no Copilot transcript usage measurements.
  SessionViewer separately handles per-event deltas, cumulative checkpoints,
  and final shutdown snapshots while preserving units such as `totalNanoAiu`.
- In the hook-side parser, `reasoningOpaque`/`encryptedContent` is Opaque and
  readable reasoning events are not projected. SessionViewer projects both
  readable and opaque forms.

## Privacy notes

`system.message` (full system prompt), user prompts, tool results, and quota
ids are present. Fixtures must be redacted.

## Known unknowns / unverified

The verified hook-enrichment fixtures do not establish subagent execution,
skill execution, compaction, `postToolUseFailure`, `errorOccurred`, multi-turn
work after the first discovered path, or any VS Code Local
transcript/SDK/OTel pointer. SessionViewer has capability-based handlers for
`subagent.*`, `skill.*`, and event names containing `compact`, but those
handlers do not turn an unverified provider schema into a guaranteed contract.
