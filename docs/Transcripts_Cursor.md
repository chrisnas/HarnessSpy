# Cursor transcript extraction contract

Authoritative spec that the Cursor transcript parser
([CursorTranscriptDialectParser.cs](Shared/HarnessSpy.Core/Runtimes/Cursor/CursorTranscriptDialectParser.cs))
and its tests implement against. Update this file whenever the parser or a
fixture changes.

## Scope boundary with SessionViewer

This contract describes the **hook-side transcript enrichment parser** used by
the CursorSpy runtime. It is intentionally narrower than SessionViewer's
passive-history reader:

- `CursorTranscriptDialectParser` implements only the sparse dialect verified
  by its fixtures: user text, assistant `text`/`tool_use`, and `turn_ended`.
- SessionViewer uses
  `Sessions/Cursor/CursorTranscriptParser.cs`, independently discovers current,
  legacy, and child transcript files, and capability-detects optional
  `thinking`, `tool_result`/`tool-result`, `tool-call`, `command_output`,
  timestamps, IDs, model, and mode fields.
- SessionViewer also joins transcript history with Cursor Desktop SQLite.
  None of those SQLite records pass through `CursorTranscriptDialectParser`.

See [`session_cursor.md`](../docs/session_cursor.md) for the complete
SessionViewer source and reconstruction contract.

## Discovery

- Path field: `transcript_path` on Cursor hook payloads.
- Availability: always null on `sessionStart` and commonly for the first 2-7
  events; the registry backfills once the path first appears.
- Location/naming: `%USERPROFILE%\.cursor\projects\<slug>\agent-transcripts\<conversation_id>\<conversation_id>.jsonl`.
- Subagent transcript pointer: none observed. Cursor subagent transcript
  discovery is capability-gated until a real capture proves its path/schema.

## Contract version

- Producer: Cursor IDE agent.
- Verified version: Cursor `3.7.27`.
- Dialect id: `cursor-transcript-jsonl`.

## Record inventory

| Row | Shape | Handling |
|-----|-------|----------|
| `role:"user"` | first `message.content[]` block with `type:"text"` | `PromptSubmitted`; retains the full text and detects the first manually attached skill |
| `role:"assistant"` | `message.content[]` of `text` and `tool_use` blocks | one step, expanded into ordered fragments |
| `type:"turn_ended"` | `status` (`success`/`error`) | `TurnStop`; `error` status marks the stop aborted |

In the verified Cursor 3.7.27 fixture, assistant content blocks are `text` and
`tool_use`; there is no `thinking` block. This is a statement about the
verified hook-enrichment dialect, not a claim that every Cursor version or the
SessionViewer reader is limited to those two block types.

## Native ids and correlation keys

The verified sparse Cursor rows carry no timestamps, record IDs,
conversation/generation IDs, or tool-call IDs. Correlation in this parser is
therefore heuristic. SessionViewer accepts those fields when newer rows provide
them, but never fabricates native IDs when they are absent.

| Concern | Hook (authoritative) | Transcript |
|---------|----------------------|------------|
| Session | `conversation_id`/`session_id` | none (reuses the discovering hook's scoped session) |
| Turn | `generation_id` | none; the current parser does not correlate prompt/stop fragments to a hook turn |
| Tool call | `tool_use_id` | none; tool requests attempt a queued signature match |

## Extraction-to-hook mapping

| Transcript field/record | Target | Reconciliation key |
|-------------------------|--------|--------------------|
| user `text` | standalone transcript prompt | no prompt-correlation implementation |
| `<manually_attached_skills>` | `Attached` skill evidence on that prompt | parsed from `name:` inside the block |
| any `tool_use` | matching canonical pre-tool node when possible | provider-scoped session + canonical tool kind + normalized primary path/command; FIFO for repeated signatures |
| unmatched `tool_use` | standalone transcript tool node | no safe match |
| `GetDynamicTools` | standalone MCP-toned discovery node unless its signature happens to match a hook | no explicit discovery-to-call relationship |
| `CallDynamicTool` | MCP-classified tool; may attach to a matching pre-tool node | ordinary tool signature |
| assistant `text` in a row containing any tool | standalone heuristic thought | no thought-hook correlation |
| assistant `text` in a tool-free row | standalone heuristic response | no response-hook correlation |
| `turn_ended` | standalone transcript turn-stop node | no stop-hook correlation |

## Provider-specific semantics

- The parser marks `GetDynamicTools` with MCP tone and classifies
  `CallDynamicTool` as MCP. It does not currently create a
  `DynamicToolDiscoveryFor` edge or join the before/after MCP hook triple.
- Native tool names are always displayed. Signature matching uses the
  canonical tool category, so `Write` (`FileWrite`) and `StrReplace`
  (`FileEdit`) do **not** currently correlate with each other.

## Skill / usage / opaque states

- This parser emits `Attached` only, from
  `<manually_attached_skills>`. Slash commands remain discoverable through the
  prompt's derived `SlashCommands` property, but the parser does not convert a
  slash command or `SKILL.md` read into another `SkillEvidence` stage.
- Usage: hook-only. Neither Cursor source exposes cost, thinking-token counts,
  or context/compaction usage.
- Opaque states: none in the verified hook-enrichment dialect; its fixture has
  no `thinking` blocks.

## Privacy notes

Cursor hook payloads embed `user_email`, full prompts, full file contents on
read, shell output, and absolute paths. Fixtures must be redacted.

## Known unknowns / unverified

For the hook-enrichment parser, subagent transcript pointers, background-agent
lineage, permission rows, compaction rows, tab rows, and newer schema variants
remain unverified and capability-gated.

SessionViewer support is broader but should not be confused with hook evidence:
it discovers child JSONL files under
`agent-transcripts\<session>\subagents\`, attaches them to the parent session,
and reads Cursor Desktop `subagentInfo` relationships. Those relationships are
passive provider-file evidence, not proof that a corresponding hook payload was
captured.
