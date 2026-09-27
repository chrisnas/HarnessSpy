# Claude Code transcript extraction contract

Authoritative spec that the Claude transcript parser
([ClaudeTranscriptDialectParser.cs](../src/Shared/HarnessSpy.Core/Runtimes/Claude/ClaudeTranscriptDialectParser.cs))
and its tests implement against. Update this file whenever the parser or a
fixture changes.

## Scope boundary with SessionViewer

This contract describes the **hook-side transcript enrichment parser** used by
the ClaudeSpy runtime. SessionViewer has a separate, broader passive-history
pipeline:

- `ClaudeTranscriptDialectParser` emits visible assistant thinking plus
  tool evidence, and projects high-value `turn_duration`, `cost-state`, and
  skill attachments as metadata-only turn/session evidence. It also projects
  `prompt_snapshot` attachments as session- or subagent-scoped system prompts.
  Other row families remain durable source evidence without timeline nodes.
- SessionViewer uses `Sessions/Claude/ClaudeSessionCatalogBuilder.cs`. It reads
  main, recovery, and recursively nested subagent JSONL, optional
  `sessions-index.json`, subagent `.meta.json` files, and `plans\*.md`.
- SessionViewer additionally projects `turn_duration`, `compact_boundary`,
  `skill_listing`, `skill_activated`, `prompt_snapshot`, titles, modes,
  permission modes, and the latest `cost-state` snapshot into its catalog model
  or metadata. Assistant `text` is a visible response in SessionViewer but
  remains enrichment-only in the Spy parser.

Both paths reuse `ClaudeTranscriptSemantics` and
`SystemPromptTextNormalizer` for row meaning. They do not share discovery,
storage, or reconciliation.

See [`session_claude.md`](session_claude.md) for the complete
SessionViewer source and reconstruction contract.

## Discovery

- Path fields: `transcript_path` (main) and `agent_transcript_path` (subagent).
- Availability: `transcript_path` appears on the first real `SessionStart`;
  `agent_transcript_path` appears only on `SubagentStop`.
- Location/naming: `%USERPROFILE%\.claude\projects\<slug>\<session_id>.jsonl`
  (main) and `...\<session_id>\subagents\agent-<agent_id>.jsonl` (subagent).
- Cleanup: subagent files, and empty-session mains, are deleted aggressively
  (most were gone within seconds during the audit). Backfill and durably copy
  immediately on discovery.

## Contract version

- Producer: Claude Code.
- Verified version: Claude Code `2.1.251`, model `claude-sonnet-5`.
- Dialect id: `claude-transcript-jsonl`.

## Record inventory

| `type` | Handling |
|--------|----------|
| `assistant` | content blocks `thinking`, `redacted_thinking`, `text`, `tool_use` |
| `user` | content blocks `text`, `tool_result` (+ structured `toolUseResult`) |
| `mode`, `permission-mode`, `atis-latch`, `last-prompt`, `ai-title`, `agent-name`, `queue-operation`, `fork-context-ref` | metadata; durably captured, not turned into nodes |
| `system` subtype `turn_duration` | typed turn-scoped duration/accounting evidence; other system subtypes remain raw capture |
| `attachment` subtype `skill_listing` / `skill_activated` | metadata-only `Available` / `Invoked` skill evidence |
| `attachment` subtype `prompt_snapshot` | normalized system-prompt snapshot; session-scoped unless attributed to a subagent |
| other `attachment` subtype | raw capture only |
| `file-history-snapshot`/`file-history-delta` | metadata; captured |
| `cost-state` | metadata-only session snapshots for cost, lines, duration, and per-model usage; summary aggregation selects the latest final snapshot |

Assistant content blocks: `thinking` (197), `tool_use` (363), `text` (113);
user rows carry `tool_result` (363). Large sessions use one block per row and
link steps by `uuid`/`parentUuid`; multi-block rows are tolerated.

## Native ids and correlation keys

| Concern | Hook | Transcript | Match |
|---------|------|------------|-------|
| Session | `session_id` | `sessionId` | exact |
| Turn | `prompt_id` | `promptId` | exact |
| Tool call | `tool_use_id` | `tool_use.id` / `tool_result.tool_use_id` | exact |
| Subagent | `agent_id` | `agentId` / filename | exact |
| Record chain | - | `uuid`/`parentUuid` | chronological link only, not semantic parentage |

## Extraction-to-hook mapping

| Transcript record | Target | Reconciliation key |
|-------------------|--------|--------------------|
| `thinking` block | transcript-only readable or opaque thought node | none (no hook counterpart) |
| `text` block | metadata-only turn evidence, avoiding a duplicate of `MessageDisplay`/`Stop.last_assistant_message` | carried `promptId` |
| `tool_use` block | evidence attached to the canonical pre-tool request | exact `tool_use.id` |
| `tool_result` + `toolUseResult` | success/failure evidence attached to that same pre-tool request | exact `tool_use_id` |
| assistant `usage` | typed measurements on the first projected assistant fragment, or hidden turn evidence when no block projects | source record plus typed snapshot/delta behavior |
| `turn_duration` | metadata-only evidence on the owning turn | carried `promptId` |
| `cost-state` | metadata-only session evidence and accounting rows | latest `FinalSnapshot` per metric |
| `prompt_snapshot` | normalized system-prompt observation outside ordinary main-session turns | session scope, or subagent scope when `agentId` is present |
| subagent transcript row | evidence carrying `SubagentId`, attached to matching `SubagentStart` when available | exact `agent_id` from binding/row |

## Provider-specific semantics

- The verified fixture contains opaque thinking: `thinking` is empty and
  `signature` is present but not human-readable; the token count is
  `usage.output_tokens_details.thinking_tokens`. The parser also supports
  readable `thinking` text. Both the Spy parser and SessionViewer treat
  `redacted_thinking` as opaque even when no signature is available.
- Assistant usage is read once per assistant row and attached to the first
  projected block, or to metadata-only usage evidence when no block projects.
  Source-record dedupe and typed behavior prevent repeated snapshots from being
  summed.
- Native `mcp__server__tool` names are preserved. Shared semantics split the
  native prefix and honor `attributionMcpServer`/`attributionMcpTool` for both
  live Spy and SessionViewer.
- The shared Claude transcript semantics recognize interruption, explicit
  error/success fields, denial state, and error payloads in both live Spy and
  SessionViewer.
- System-prompt snapshots are normalized and hashed through the shared
  `SystemPromptTextNormalizer`; repeated snapshots are coalesced by the WPF
  projection rather than duplicated under every turn.
- `deferred_tools_delta` is availability metadata, not an invocation.

## Skill / usage / opaque states

- In the hook-enrichment runtime, `skill_listing` produces metadata-only
  `Available`, `skill_activated` produces `Invoked`, and a `Skill` tool or
  `attributionSkill`/`skill` on assistant content produces `Invoked`.
  Neither pipeline upgrades that evidence to `Loaded` or
  `ExecutionCorroborated` without a record that explicitly proves that stage.
- The hook-side parser and SessionViewer both expose typed assistant usage,
  turn duration, and session-scoped cost-state. Shared aggregation respects
  request/turn/session scope and delta/cumulative/final behavior.
- Opaque states: an empty thinking block with a signature and every
  `redacted_thinking` block is Opaque; readable thinking remains Observed.
  Deleted subagent transcripts are
  Unavailable (`MissedBeforeCapture`) in the hook-side capture pipeline.

## Privacy notes

Hook and transcript rows embed complete source files (`tool_response`,
`originalFile`, `tool_result.content`), attachment content, and absolute paths.
Fixtures must strip these while preserving shape and ids.

## Known unknowns / unverified

For the verified hook-enrichment fixtures, most subagent lifetimes (files were
deleted), `TaskCreated`/`TaskCompleted`, `MessageDisplay`, `PermissionDenied`,
`StopFailure`, `UserPromptExpansion`, and non-empty background task arrays
remain unverified. Plaintext thinking is supported by both parsers but was not
present in the audited fixture.
