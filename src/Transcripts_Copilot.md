# Copilot transcript extraction contract

Authoritative spec that the Copilot CLI transcript parser
([CopilotCliTranscriptDialectParser.cs](Shared/HarnessSpy.Core/Runtimes/Copilot/CopilotCliTranscriptDialectParser.cs))
and its tests implement against. Update this file whenever the parser or a
fixture changes.

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
| `session.start` / `session.shutdown` | metadata; captured (shutdown carries final token totals) |
| `session.usage_checkpoint` | cumulative usage metadata |
| `user.message` | user prompt |
| `system.message` | system prompt / available skills |
| `assistant.turn_start` / `assistant.turn_end` | turn boundaries |
| `assistant.message` | `content` (final answer), `toolRequests[]`, `reasoningOpaque`/`encryptedContent` |
| `tool.execution_start` / `tool.execution_complete` | tool lifecycle |
| `permission.requested` / `permission.completed` | permission flow |
| `hook.start` / `hook.end` | evidence of already-captured hooks |
| `model_change`, `auto_mode_resolved` | metadata |

## Native ids and correlation keys

| Concern | Hook | Transcript | Match |
|---------|------|------------|-------|
| Session | `sessionId` | `data.sessionId` | exact |
| Turn | derived (`userPromptSubmitted`..`agentStop`) | `turnId`/`interactionId` | transcript exact, hooks derived |
| Tool call | none | `toolCallId` | signature + occurrence, then adopt id |
| Record chain | - | `id`/`parentId` | chronological link only |

## Extraction-to-hook mapping

| Transcript record | Target | Reconciliation key |
|-------------------|--------|--------------------|
| `assistant.message.toolRequests[]` | enrich `preToolUse` | tool signature + occurrence (adopt `toolCallId`) |
| `tool.execution_start`/`complete` | enrich `preToolUse`/`postToolUse` | `toolCallId` / signature |
| `permission.requested`/`completed` | permission node | `parentId` chain |
| `reasoningOpaque`/`encryptedContent` | transcript-only opaque thought node | none |
| `assistant.message.content` (final answer) | transcript-only assistant message | none |
| `session.shutdown.tokenDetails` | session consumption dashboard | latest wins |

## Provider-specific semantics

- MCP is transcript-authoritative: `mcpServerName`, `mcpToolName`, `toolTitle`,
  and `toolCallId` on tool requests/executions, and `kind:"mcp"` on permission
  records. The flat `<server>-<tool>` hook name is never split on the hyphen;
  the server/tool split comes from the transcript or the permission slash form.
- Batched pre-tool hooks: one transcript hook invocation may describe several
  parallel tools, so N hook `preToolUse` envelopes map to one transcript hook
  record. Expand `toolRequests[]`/hook input before matching.
- Reasoning is opaque; only `reasoningTokens` is exposed as consumption.

## Skill / usage / opaque states

- Skills: `system.message` proves Available and usage checkpoints list a
  `skill` tool, but no session invoked it; Loaded/Execution are not asserted.
- Usage: per-model-call, cumulative checkpoints, and a final shutdown snapshot;
  native units such as `totalNanoAiu` are preserved (never relabelled as cost).
- Opaque states: `reasoningOpaque`/`encryptedContent` are Opaque.

## Privacy notes

`system.message` (full system prompt), user prompts, tool results, and quota
ids are present. Fixtures must be redacted.

## Known unknowns / unverified

Subagents, `skill` tool execution, compaction, `postToolUseFailure`,
`errorOccurred`, multi-turn work after the first discovered path, and any VS
Code Local transcript/SDK/OTel pointer.
