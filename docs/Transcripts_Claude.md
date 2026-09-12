# Claude Code transcript extraction contract

Authoritative spec that the Claude transcript parser
([ClaudeTranscriptDialectParser.cs](Shared/HarnessSpy.Core/Runtimes/Claude/ClaudeTranscriptDialectParser.cs))
and its tests implement against. Update this file whenever the parser or a
fixture changes.

## Scope boundary with SessionViewer

This contract describes the **hook-side transcript enrichment parser** used by
the ClaudeSpy runtime. SessionViewer has a separate, broader passive-history
pipeline:

- `ClaudeTranscriptDialectParser` emits observations only for assistant and
  user rows. Other row families remain durable source evidence but do not
  become runtime tree nodes here.
- SessionViewer uses `Sessions/Claude/ClaudeSessionCatalogBuilder.cs`. It reads
  main, recovery, and recursively nested subagent JSONL, optional
  `sessions-index.json`, and subagent `.meta.json` files.
- SessionViewer additionally projects `turn_duration`, `compact_boundary`,
  `skill_listing`, `skill_activated`, titles, modes, permission modes, and the
  latest `cost-state` snapshot into its catalog model or metadata.

See [`session_claude.md`](../docs/session_claude.md) for the complete
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
| `assistant` | content blocks `thinking`, `text`, `tool_use` |
| `user` | content blocks `text`, `tool_result` (+ structured `toolUseResult`) |
| `mode`, `permission-mode`, `atis-latch`, `last-prompt`, `ai-title`, `agent-name`, `queue-operation`, `fork-context-ref` | metadata; durably captured, not turned into nodes |
| `system` (`stop_hook_summary`, `turn_duration`, `away_summary`, `compact_boundary`) | metadata; captured |
| `attachment` (`skill_listing`, `deferred_tools_delta`, `agent_listing_delta`, `file`, `edited_text_file`, `compact_file_reference`, `read_truncation_notice`, `plan_mode`, `plan_mode_exit`) | metadata; captured |
| `file-history-snapshot`/`file-history-delta` | metadata; captured |
| `cost-state` | raw metadata in the hook-side capture; SessionViewer separately selects the latest snapshot |

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
| `text` block | transcript-only assistant message | none |
| `tool_use` block | evidence attached to the canonical pre-tool request | exact `tool_use.id` |
| `tool_result` + `toolUseResult` | success/failure evidence attached to that same pre-tool request | exact `tool_use_id` |
| assistant `usage` | typed measurements on a projected thinking fragment | source row/provenance dedupe |
| `cost-state` | no hook-side observation node | raw capture only; SessionViewer handles it separately |
| subagent transcript row | transcript observation carrying subagent-file provenance | no exact subagent reconciliation key is currently set by this parser |

## Provider-specific semantics

- The verified fixture contains opaque thinking: `thinking` is empty and
  `signature` is present but not human-readable; the token count is
  `usage.output_tokens_details.thinking_tokens`. The parser also supports
  readable `thinking` text. SessionViewer additionally treats
  `redacted_thinking` as opaque even when no signature is available.
- In the hook-side parser, assistant usage is read once per assistant row but
  is currently attached to every projected `thinking` fragment. If a row has
  no projected thinking block, that parser emits no usage measurement for the
  row. SessionViewer is broader: it attaches usage to the first projected
  assistant block (or a hidden usage event), deduplicates by message ID plus
  usage JSON, and selects the latest `cost-state`.
- Native `mcp__server__tool` names are preserved. The hook-side parser
  classifies/tones the request as MCP but does not populate structured
  `McpServerName`/`McpToolName` from attribution fields. SessionViewer does
  split the native prefix and honors
  `attributionMcpServer`/`attributionMcpTool`.
- The hook-side parser currently marks a tool result failed only when
  `toolUseResult.interrupted` is true. SessionViewer additionally recognizes
  explicit error/success fields, denial state, and error payloads.
- `deferred_tools_delta` is availability metadata, not an invocation.

## Skill / usage / opaque states

- In the hook-enrichment runtime, attachment rows are retained as source
  evidence but are not emitted as transcript nodes. In SessionViewer,
  `skill_listing` produces `Available`, `skill_activated` produces `Invoked`,
  and `attributionSkill`/`skill` on assistant content produces `Invoked`.
  Neither pipeline upgrades that evidence to `Loaded` or
  `ExecutionCorroborated` without a record that explicitly proves that stage.
- The hook-side parser emits turn-scoped assistant token measurements only as
  described above. SessionViewer additionally projects `turn_duration` and
  session-scoped `cost-state`, with snapshot/delta behavior tracked explicitly.
- Opaque states: an empty/redacted thinking block with a signature is Opaque;
  readable thinking remains Observed. Deleted subagent transcripts are
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
