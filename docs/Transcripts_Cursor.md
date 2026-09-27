# Cursor transcript extraction contract

Authoritative spec that the Cursor transcript parser
([CursorTranscriptDialectParser.cs](../src/Shared/HarnessSpy.Core/Runtimes/Cursor/CursorTranscriptDialectParser.cs))
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

See [`session_cursor.md`](session_cursor.md) for the complete SessionViewer
source and reconstruction contract, and
[`architecture.md`](../src/architecture.md#transcript-source-implemented) for
the hook-side discovery/capture pipeline.

## Discovery

- Path field: `transcript_path` on Cursor hook payloads.
- Availability: always null on `sessionStart` and commonly absent from the
  first few observed events; the registry backfills once the path first
  appears. The exact count is an observed producer detail, not a parser rule.
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
conversation/generation IDs, or tool-call IDs. Correlation is therefore
heuristic. Before parsing, `TranscriptTurnTracker` derives one
`transcript-cursor-turn:N` key for each span from a user row through its
`turn_ended` row. If discovery starts after the user row, the first assistant
or `turn_ended` row starts a fallback interaction.

When prompt or tool evidence later binds to a hook turn, the WPF projection
aliases the derived transcript key to the hook's `generation_id` and moves any
already-projected transcript children into that turn. A genuinely unmatched
interaction keeps its namespaced key. SessionViewer accepts optional native
fields when newer rows provide them, but never fabricates native IDs when they
are absent.

| Concern | Hook (authoritative) | Transcript |
|---------|----------------------|------------|
| Session | `conversation_id`/`session_id` | none (reuses the discovering hook's scoped session) |
| Turn | `generation_id` | no native ID; derived `transcript-cursor-turn:N`, then aliased to the matching hook turn when evidence binds |
| Tool call | `tool_use_id` | none; tool requests attempt a queued signature match |

## Extraction-to-hook mapping

| Transcript field/record | Target | Reconciliation key |
|-------------------------|--------|--------------------|
| user `text` | evidence on `beforeSubmitPrompt` when normalized `<user_query>` text matches; fallback transcript prompt otherwise; a match also aliases the derived transcript turn | provider-scoped session + normalized prompt text, FIFO |
| `<manually_attached_skills>` | `Attached` skill evidence on the canonical prompt, including source path | parsed from `name:`/`Path:` inside the block |
| any `tool_use` | matching canonical pre-tool node when possible | provider-scoped session + canonical tool kind + normalized complete input; FIFO for repeated signatures |
| unmatched `tool_use` | standalone transcript tool node | no safe match |
| `GetDynamicTools` | standalone MCP-toned discovery node unless its signature happens to match a hook | no explicit discovery-to-call relationship |
| `CallDynamicTool` | MCP-classified tool; may attach to a matching pre-tool node | ordinary tool signature |
| assistant `text` in a row containing any tool | evidence on an exact-text `afterAgentThought`; fallback heuristic thought otherwise | provider-scoped session + role + normalized text, FIFO |
| assistant `text` in a tool-free row | evidence on an exact-text `afterAgentResponse`; fallback response otherwise | provider-scoped session + role + normalized text, FIFO |
| `turn_ended` | evidence on an equivalent-status `stop`; fallback transcript turn-stop otherwise | normalized completed/aborted status, FIFO |

## Provider-specific semantics

- The parser marks `GetDynamicTools` with MCP tone and classifies
  `CallDynamicTool` as MCP. It does not currently create a
  `DynamicToolDiscoveryFor` edge or join the before/after MCP hook triple.
- Summary KPIs classify tools through `CursorToolSemantics`
  ([CursorToolSemantics.cs](../src/Shared/HarnessSpy.Core/Runtimes/Cursor/CursorToolSemantics.cs)):
  `GetDynamicTools`/`get_mcp_tools` are dynamic-tool discovery, counted as
  tools but never as MCP executions. Only real executions (`CallDynamicTool`
  and Desktop's flattened `mcp-<server>-<tool>`) feed the MCP count.
- SessionViewer uses the same `CursorToolSemantics` classification rules after
  its separate `CursorSessionEventReconciler`; it does not reuse the live
  hook/transcript reconciliation pipeline.
- A transcript-only tool request that never matches a hook is counted once in
  CursorSpy's tool KPI; the matched-and-nested duplicate is marked secondary so
  it is not double counted. A parallel wave made only of transcript-only calls
  shows no duration, and a mixed wave derives its duration from hook-anchored
  members alone (transcript rows carry no clock).
- Content blocks from one assistant row share a derived assistant-step key
  based on transcript provenance. This can group matching hook tool requests as
  parallel without fabricating a provider-native id.
- Native tool names are always displayed. Signature matching uses the
  canonical tool category, so `Write` (`FileWrite`) and `StrReplace`
  (`FileEdit`) do **not** currently correlate with each other.

## Skill / usage / opaque states

- This parser emits `Attached` only, from
  `<manually_attached_skills>`. The evidence now enriches the canonical prompt
  and contributes its explicit stage to turn/session skill summaries. Slash
  commands remain discoverable through the prompt's derived `SlashCommands`
  property, but the parser does not convert a slash command or `SKILL.md` read
  into another `SkillEvidence` stage.
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
