# Cursor transcript extraction contract

Authoritative spec that the Cursor transcript parser
([CursorTranscriptDialectParser.cs](Shared/HarnessSpy.Core/Runtimes/Cursor/CursorTranscriptDialectParser.cs))
and its tests implement against. Update this file whenever the parser or a
fixture changes.

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
| `role:"user"` | `message.content[].type:"text"` | PromptSubmitted (enrichment); extracts `<user_query>` and `<manually_attached_skills>` |
| `role:"assistant"` | `message.content[]` of `text` and `tool_use` blocks | one step, expanded into ordered fragments |
| `type:"turn_ended"` | `status` (`success`/`error`), optional `error` | TurnStop (enrichment) |

Assistant content blocks: `text`, `tool_use`. There is no `thinking` block.

## Native ids and correlation keys

Cursor transcripts carry no timestamps, record ids, conversation/generation
ids, or tool-call ids. Correlation is therefore heuristic:

| Concern | Hook (authoritative) | Transcript |
|---------|----------------------|------------|
| Session | `conversation_id`/`session_id` | none (reuses the discovering hook's scoped session) |
| Turn | `generation_id` | none (aligned by prompt fingerprint/ordinal) |
| Tool call | `tool_use_id` | none (matched by tool signature + order) |

## Extraction-to-hook mapping

| Transcript field/record | Target | Reconciliation key |
|-------------------------|--------|--------------------|
| user `text` (`<user_query>`) | enrich `beforeSubmitPrompt` | session + prompt fingerprint |
| `<manually_attached_skills>` | prompt-node skill evidence (Attached) | n/a |
| `tool_use` Read/Grep/Shell/Write/StrReplace | enrich matching `preToolUse` | session + tool kind + argument hash + order |
| `tool_use` Glob/TodoWrite/ReadLints | transcript-only node | n/a (no hook counterpart) |
| `tool_use` GetDynamicTools | transcript-only MCP-discovery node | `DynamicToolDiscoveryFor` |
| `tool_use` CallDynamicTool | enrich MCP `preToolUse`/`beforeMCPExecution` | tool signature |
| leading `text` before tools | heuristic thought | overridden by `afterAgentThought` |
| terminal `text` | heuristic response | overridden by `afterAgentResponse` |
| `turn_ended` | enrich `stop` | aligned turn |

## Provider-specific semantics

- MCP triple: `preToolUse.tool_name = "MCP:<tool>"`, the
  `beforeMCPExecution`/`afterMCPExecution` pair (`mcp_server_name`), and the
  transcript `GetDynamicTools` -> `CallDynamicTool` chain. Discovery is a
  distinct node; the call links to the canonical MCP pre-tool node.
- `Write`<->`StrReplace` is directional matching evidence only; native names
  are always displayed. The transcript can emit either name.

## Skill / usage / opaque states

- Skill stages observed: Available, Attached, Invoked (`/skill`), Loaded
  (`SKILL.md` read). Execution is only ever corroborated, never asserted.
- Usage: hook-only. Neither Cursor source exposes cost, thinking-token counts,
  or context/compaction usage.
- Opaque states: none (Cursor has no thinking blocks).

## Privacy notes

Cursor hook payloads embed `user_email`, full prompts, full file contents on
read, shell output, and absolute paths. Fixtures must be redacted.

## Known unknowns / unverified

Subagents, background agents, permission hooks, compaction, tab hooks, and
transcript schema evolution are unverified and remain capability-gated.
