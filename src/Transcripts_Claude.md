# Claude Code transcript extraction contract

Authoritative spec that the Claude transcript parser
([ClaudeTranscriptDialectParser.cs](Shared/HarnessSpy.Core/Runtimes/Claude/ClaudeTranscriptDialectParser.cs))
and its tests implement against. Update this file whenever the parser or a
fixture changes.

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
| `cost-state` | cumulative session snapshot; latest wins |

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
| `thinking` block | transcript-only thought node (opaque) | none (no hook counterpart) |
| `text` block | transcript-only assistant message | none |
| `tool_use` block | enrich `PreToolUse` | `tool_use.id` |
| `tool_result` + `toolUseResult` | enrich `PostToolUse` | `tool_use_id` |
| `usage` | turn token/cache/thinking measurements | dedupe by message id + hash |
| `cost-state` | session dashboard (latest snapshot) | n/a |
| `agent_transcript_path` | subagent conversation subtree | `agent_id` |

## Provider-specific semantics

- Thinking is opaque: `thinking` is empty and `signature` is present but not
  human-readable; the token count is `usage.output_tokens_details.thinking_tokens`.
  The node shows an opaque marker, signature length, and token count.
- Usage: input/cache are cumulative snapshots, output resets per model step,
  and identical usage objects propagate across consecutive rows. Deduplicate by
  message id + usage hash; never sum every row. `cost-state` is a repeated
  cumulative snapshot; only the latest is authoritative.
- MCP: native `mcp__server__tool` is preserved; transcript attribution adds
  `attributionMcpServer`/`attributionMcpTool`. `deferred_tools_delta` is
  availability metadata, not an invocation.

## Skill / usage / opaque states

- Skills: `skill_listing` proves Available/Attached only. No slash command or
  `SKILL.md` read was captured, so Loaded/ExecutionCorroborated are not asserted.
- Usage scopes: request (per assistant row), turn (`turn_duration`), session
  (`cost-state`), with snapshot/delta behaviour tracked explicitly.
- Opaque states: thinking text is Opaque; deleted subagent transcripts are
  Unavailable (`MissedBeforeCapture`).

## Privacy notes

Hook and transcript rows embed complete source files (`tool_response`,
`originalFile`, `tool_result.content`), attachment content, and absolute paths.
Fixtures must strip these while preserving shape and ids.

## Known unknowns / unverified

Plaintext thinking, most subagent lifetimes (files deleted),
`TaskCreated`/`TaskCompleted`, `MessageDisplay`, `PermissionDenied`,
`StopFailure`, `UserPromptExpansion`, and non-empty background task arrays.
