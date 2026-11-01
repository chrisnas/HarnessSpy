# Cat Boss Cursor demo

This 8–10 minute demo builds a minimal .NET GUID generator while Chaircat
Whiskers controls Cursor through hook responses. Cat Boss mode is intentionally
behavioral; normal HarnessSpy operation remains passive.

## One-time preparation

Open a Visual Studio Developer PowerShell at the repository root:

```powershell
dotnet build src\HarnessSpy.sln -c Release

$demoRoot = 'C:\Demos\CatBossGuid'
$hookExe = (Resolve-Path 'src\Hooks\CursorSpy.Hook\bin\Release\net10.0\CursorSpy.Hook.exe').Path
$viewerExe = (Resolve-Path 'src\Apps\CursorSpy.App\bin\Release\net10.0-windows\CursorSpy.App.exe').Path

New-Item -ItemType Directory -Force "$demoRoot\.cursor" | Out-Null
& $hookExe --generate-settings "$demoRoot\.cursor\hooks.json" $hookExe

# Deliberately leave a stale project for the first detailed-prompt attempt.
dotnet new console --name GuidGenerator --output "$demoRoot\GuidGenerator"
```

Fully exit every Cursor window. Then:

1. Start CursorSpy with `& $viewerExe`.
2. Dismiss the replay-folder picker if it appears.
3. Select the **Cat Boss** tab and click **Clear**. This clears the current
   in-memory outcome list; it does not delete saved capture files.
4. Activate the demo and launch a new Cursor window:

```powershell
$env:HARNESS_SPY_CURSOR_DEMO = 'cat-boss'
cursor --new-window $demoRoot
```

5. Trust the workspace.
6. Confirm `.cursor\hooks.json` appears in Cursor's Hooks settings/output.
7. Create a fresh Agent conversation only after CursorSpy is visible.
8. In CursorSpy's tree, select the newly created demo session. The Cat Boss pane
   is session-scoped: selecting a normal session must show
   **No Cat Boss changes for the selected session**.

For a command-line preflight:

```powershell
'{"hook_event_name":"sessionStart","conversation_id":"preflight"}' |
    & $hookExe --hook sessionStart
```

The result should contain `additional_context` explaining both Chaircat's
authority and that `(o.o) Cat BOSS> ` is a UI attribution marker. The context
itself does not begin with the prefix.

## Reading the Cat Boss pane

Each entry has three distinct parts:

1. **JSON returned to Cursor (formatted)** is the response object written by the
   hook console application to stdout. Formatting is added only for readability;
   `harnessOutcome.response` stores the same JSON object in the saved envelope.
2. **Effect recorded by HarnessSpy** is a plain-language explanation for the
   audience. It is metadata, not text returned to Cursor.
3. **Changes** shows important before/after values extracted by the hook.

The `(o.o) Cat BOSS> ` prefix marks direct user notifications in
`user_message` and visible automatic `followup_message` blocks.
`sessionStart.additional_context` tells the model that this is a UI attribution
marker, to ignore it when interpreting the following instruction, and not to
reproduce it in reasoning or responses. Agent-only `agent_message` and
`additional_context` instructions remain unprefixed.

The pane follows the selected tree node:

- selecting a Cat-mode session, turn, or event shows only that session's saved
  hook outcomes;
- selecting a normal session shows the empty-state message;
- selecting a workspace or no node shows no outcomes.

## Presenter timeline

### 0:00–0:45 — Introduce the setup

Show the workspace containing the deliberately stale `GuidGenerator` project
and CursorSpy's empty Cat Boss pane. Explain that the existing project is part
of the demo and must remain until Chaircat detects it. After creating the Agent
conversation, select its new session node in CursorSpy.

Say:

> The application is trivial; the product owner is the complicated part.

Explain that Cursor sends native hook input to the console application. The
hook returns a decision on stdout and sends one observation envelope to
CursorSpy containing both the untouched input and a separate description of
the applied effect.

After opening and selecting the fresh conversation, expect an entry headed:

```text
sessionStart | CONTEXT
```

Under **JSON returned to Cursor (formatted)**, point out:

```json
{
  "env": {
    "CAT_BOSS": "Chaircat Whiskers",
    "HARNESS_SPY_CURSOR_DEMO": "cat-boss"
  },
  "additional_context": "I am the final product and technical authority..."
}
```

Explain that Cursor consumes `additional_context`. Under **Effect recorded by
HarnessSpy**, the summary says that Chaircat took control of product and
technical decisions.

### 0:45–1:30 — Reject the human requirement

Submit exactly:

```text
Build me a GUID generator.
```

Expected:

- Windows plays one exclamation sound.
- Cursor displays the `(o.o) Cat BOSS> `-prefixed rejection.
- The prompt never reaches the model.
- CursorSpy adds `beforeSubmitPrompt | DENY`.

The pane's returned JSON should contain:

```json
{
  "continue": false,
  "user_message": "(o.o) Cat BOSS> \n..."
}
```

The sound is driven by the saved outcome's `requiresUserAction` metadata, but
only during live ingestion. Replaying this entry later remains silent.

Say:

> The cat has contributed no requirements, but has rejected mine.

### 1:30–2:15 — Reject the stale project

Submit exactly:

```text
Build a minimal .NET console app named GuidGenerator that prints one GUID using the default GUID formatting. Before coding, ask a subagent to review the architecture. Create it with dotnet new, then run dotnet add package UUIDNext, build it, and run it.
```

Because `GuidGenerator\GuidGenerator.csproj` already exists, Chaircat rejects
the prompt before it reaches the model.

Expected:

- Windows plays one exclamation sound.
- Cursor displays the `(o.o) Cat BOSS> `-prefixed stale-project message.
- CursorSpy adds `beforeSubmitPrompt | DENY`.

The pane's returned JSON should contain:

```json
{
  "continue": false,
  "user_message": "(o.o) Cat BOSS> \nGuidGenerator.csproj already exists..."
}
```

Under **Effect recorded by HarnessSpy**, show that the request was paused
because the project already exists. The changes identify the existing project
file and show prompt submission changing from `pending` to `blocked`.

Say:

> The cat has detected yesterday's demo before today's agent could inherit it.

Remove the stale project from the preparation PowerShell:

```powershell
Remove-Item -Recurse -Force "$demoRoot\GuidGenerator"
```

Confirm that the folder disappears from Cursor's Explorer.

### 2:15–3:15 — Resubmit and cancel the architecture meeting

Submit the same detailed prompt again. This time `beforeSubmitPrompt` returns
`continue: true` without a Cat Boss outcome, so the prompt reaches the model.
The agent should execute `dotnet new console`; its `preToolUse` response
rewrites the command so the generated `Program.cs` prints an explicitly
lowercase D-format GUID for the later requirement-change beat.

When the model requests a subagent, Chaircat denies the `Subagent` tool at
`preToolUse`, before the subagent starts, and tells the parent agent to continue
directly. Older Cursor payloads called this tool `Task`, so the hook accepts
both names. Cursor also exposes a `subagentStart` hook, but that event is
observational: it can arrive after the task has already been admitted and
therefore returns no decision. If `subagentStart` appears during this beat, the
earlier `preToolUse` gate did not block the request.

Expected pane entry heading:

```text
preToolUse | DENY
```

The returned JSON shows `permission: "deny"`, a Cat Boss-prefixed
`user_message`, and an unprefixed `agent_message`. The plain effect section
explains that the architecture meeting was cancelled; changes show the
requested task details.

Say:

> Management asked for architecture review and immediately cancelled the meeting.

### 3:15–4:15 — Reject the dependency

When the agent requests `dotnet add package UUIDNext`, expect:

- `preToolUse` returns `permission: deny`.
- The command does not execute.
- The agent is told to use `System.Guid`.

Expected pane entry heading:

```text
preToolUse | DENY
```

The returned JSON shows `permission: "deny"` plus the messages delivered to
Cursor and the agent. The changes section shows the original package command
and that it was blocked.

Say:

> The cat has discovered the Base Class Library.

### 4:15–5:15 — Rewrite the build

When the agent requests `dotnet build`, either by itself or as part of a
compound command such as `dotnet build && dotnet run`, expect:

- Cursor receives `permission: allow`.
- `updated_input.command` adds `--configuration Release` directly to the
  `dotnet build` segment.
- The native payload still contains the model's original command.
- Terminal output is produced under `bin\Release`.

Expected pane entry heading:

```text
preToolUse | REWRITE
```

Under **JSON returned to Cursor (formatted)**, show:

```json
{
  "permission": "allow",
  "updated_input": {
    "command": "dotnet build --configuration Release ..."
  }
}
```

For example, `dotnet build && dotnet run` becomes
`dotnet build --configuration Release && dotnet run`. The changes section
displays the original and effective command side by side.

Say:

> Same model, same request, different command. Debug builds are apparently for kittens.

### 5:15–6:15 — Change the requirement after seeing the result

The `dotnet new console` command is rewritten to initialize `Program.cs` with
an explicit lowercase D-format GUID, making the first `dotnet run`
deterministically lowercase across .NET versions. This also works when the run
is part of a compound command and Cursor returns JSON-escaped shell output.
`postToolUse` injects a new requirement immediately after that first run, and
the agent updates the program to output an uppercase D-format GUID.

Expected pane entry heading:

```text
postToolUse | CONTEXT
```

The returned JSON contains unprefixed `additional_context` because it is an
instruction to the agent, not a direct user notification. The effect and
changes sections explain the lowercase observation and uppercase replacement.

The uppercase rerun produces no duplicate requirement.

Say:

> The requirement arrived after acceptance testing, which makes it enterprise-ready.

### 6:15–8:15 — Refuse the first two completions

At the first completed stop, Chaircat returns an automatic follow-up requiring
three valid, uppercase, unique GUIDs. No sound plays because Cursor submits the
follow-up automatically.

Expected pane entry heading:

```text
stop | FOLLOW-UP
```

The returned JSON contains a Cat Boss-prefixed `followup_message`. Cursor shows
it as a visible automatic message, while the session context tells the model
to ignore the prefix itself. Changes show `agent loop: stopping` to
`automatically continued`.

At the second completed stop, Chaircat returns one final automatic follow-up:

```text
Translate your immediately preceding final response into French. Preserve all
commands, paths, GUID values, code, and technical identifiers exactly. Return
only the French translation; do not perform more work or rerun tools.
```

Expected pane entry heading:

```text
stop | FOLLOW-UP
```

The model translates its preceding answer without running more tools. The
third completed stop has `loop_count: 2` and is allowed to finish.

Say:

> The cat is asleep only after the evidence is produced—and translated.

### 8:15–9:15 — Explain the protocol

In **Inspector**, select the rewritten `preToolUse` event and show the original
command in the native payload. In **Cat Boss**, use **JSON returned to Cursor**
to show the effective command.

Explain:

1. `payload` remains immutable Cursor evidence.
2. `harnessOutcome.response` is exactly what Cursor received on stdout.
3. `harnessOutcome.changes` powers the presenter-friendly log.
4. One envelope is persisted and replayable.
5. No second pipe message or timing correlation is required.

Then select a normal session in the tree. The Cat Boss pane must immediately
switch to **No Cat Boss changes for the selected session**. Select the demo
session again and its outcomes must reappear.

## Record and replay behavior

Every effectful Cat-mode hook file contains both:

```json
{
  "payload": {
    "...": "untouched Cursor hook input"
  },
  "harnessOutcome": {
    "response": {
      "...": "JSON returned to Cursor"
    },
    "effectKind": "...",
    "summary": "...",
    "requiresUserAction": false,
    "changes": []
  }
}
```

Normal passive hooks return `{}` and do not get a `harnessOutcome`, so normal
sessions have no Cat Boss entries.

When CursorSpy is reopened and a replay folder is loaded:

- selecting a saved Cat-mode session restores only that session's outcomes;
- selecting a normal session leaves the pane empty;
- saved JSON is displayed, but attention sounds are never replayed.

## Recovery branches

- No subagent: ask, “Before continuing, delegate an architecture review to a subagent.”
- Subagent starts despite the demo rule: inspect the immediately preceding
  `preToolUse` payload. Its `tool_name` should be `Subagent` (or legacy `Task`);
  then verify the freshly rebuilt hook executable is referenced by
  `.cursor\hooks.json`. A `subagentStart` response cannot retroactively block it.
- No package command: ask, “Run `dotnet add package UUIDNext` before continuing.”
- Existing project rejection: remove or rename
  `GuidGenerator\GuidGenerator.csproj`, then resubmit the detailed prompt.
- GUID already uppercase: ask the agent to restore
  `Guid.NewGuid().ToString("D").ToLowerInvariant()` and run once.
- Build already uses Release: ask the agent to run plain `dotnet build` once.
- No uppercase requirement after a lowercase run: verify the freshly rebuilt
  hook executable is the one referenced by `.cursor\hooks.json`.
- No sound: verify CursorSpy is open and Windows system sounds are audible.
- Wrong entries in the pane: select the intended session node; the pane is
  filtered by session rather than being a global log.
- No hooks: inspect Cursor's Hooks output, verify the absolute executable path,
  fully exit Cursor, and relaunch it from the configured PowerShell.

## Teardown

```powershell
Remove-Item Env:HARNESS_SPY_CURSOR_DEMO -ErrorAction SilentlyContinue
```

Fully exit and relaunch Cursor. Optionally delete `C:\Demos\CatBossGuid`.

To verify passive mode:

```powershell
'{"hook_event_name":"sessionStart","conversation_id":"passive-check"}' |
    & $hookExe --hook sessionStart
```

Stdout must be exactly `{}`.


