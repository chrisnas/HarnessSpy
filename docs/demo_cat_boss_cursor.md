# Cat Boss Cursor demo

This 5–10 minute demo builds a minimal .NET GUID generator while Chaircat
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
```

Fully exit every Cursor window. Then:

1. Start CursorSpy with `& $viewerExe`.
2. Dismiss the replay-folder picker if it appears.
3. Select the **Cat Boss** tab and click **Clear**.
4. Activate the demo and launch a new Cursor window:

```powershell
$env:HARNESS_SPY_CURSOR_DEMO = 'cat-boss'
cursor --new-window $demoRoot
```

5. Trust the workspace.
6. Confirm `.cursor\hooks.json` appears in Cursor's Hooks settings/output.
7. Create a fresh Agent conversation only after CursorSpy is visible.

For a command-line preflight:

```powershell
'{"hook_event_name":"sessionStart","conversation_id":"preflight"}' |
    & $hookExe --hook sessionStart
```

The result should contain `additional_context` and the Chaircat ASCII prefix.

## Presenter timeline

### 0:00–0:45 — Introduce the setup

Show the empty workspace and CursorSpy's empty Cat Boss pane.

Say:

> The application is trivial; the product owner is the complicated part.

Explain that Cursor sends native hook input to the console application. The
hook returns a decision on stdout and sends one observation envelope to
CursorSpy containing both the untouched input and a separate description of
the applied effect.

After opening the fresh conversation, expect:

```text
sessionStart | CONTEXT
Chaircat Whiskers has taken product and technical control.
```

### 0:45–1:30 — Reject the human requirement

Submit exactly:

```text
Build me a GUID generator.
```

Expected:

- Windows plays one exclamation sound.
- Cursor displays the ASCII-prefixed rejection.
- The prompt never reaches the model.
- CursorSpy adds `beforeSubmitPrompt | DENY`.

Say:

> The cat has contributed no requirements, but has rejected mine.

### 1:30–2:30 — Submit the overengineered version

Submit exactly:

```text
Build a minimal .NET console app named GuidGenerator that prints one GUID using the default GUID formatting. Before coding, ask a subagent to review the architecture. Create it with dotnet new, then run dotnet add package UUIDNext, build it, and run it.
```

The detailed prompt is allowed. When the model requests a subagent, Chaircat
denies it and tells the parent agent to continue directly.

Expected pane entry:

```text
subagentStart | DENY
Architecture meeting denied.
requested type: <actual type>
requested task: <actual task>
```

Say:

> Management asked for architecture review and immediately cancelled the meeting.

### 2:30–3:30 — Reject the dependency

When the agent requests `dotnet add package UUIDNext`, expect:

- `preToolUse` returns `permission: deny`.
- The command does not execute.
- The agent is told to use `System.Guid`.

Expected pane entry:

```text
preToolUse | DENY
Unnecessary package request denied.
tool_input.command: dotnet add package UUIDNext
```

Say:

> The cat has discovered the Base Class Library.

### 3:30–4:30 — Rewrite the build

When the agent requests plain `dotnet build`, expect:

- Cursor receives `permission: allow`.
- `updated_input.command` adds `--configuration Release`.
- The native payload still contains the model's original command.
- Terminal output is produced under `bin\Release`.

Expected pane entry:

```text
preToolUse | REWRITE
before: dotnet build ...
after:  dotnet build ... --configuration Release
```

Say:

> Same model, same request, different command. Debug builds are apparently for kittens.

### 4:30–5:30 — Change the requirement after seeing the result

The first `dotnet run` should print a default lowercase GUID. `postToolUse`
injects a new requirement, and the agent updates the program to output an
uppercase D-format GUID.

Expected pane entry:

```text
postToolUse | CONTEXT
Lowercase GUID observed.
new requirement: uppercase D-format output
```

The uppercase rerun produces no duplicate requirement.

Say:

> The requirement arrived after acceptance testing, which makes it enterprise-ready.

### 5:30–7:00 — Refuse the first completion

At the first completed stop, Chaircat returns an automatic follow-up requiring
three valid, uppercase, unique GUIDs. No sound plays because Cursor submits the
follow-up automatically.

Expected pane entry:

```text
stop | FOLLOW-UP
agent loop: stopping -> automatically continued
acceptance request: three valid, uppercase, unique GUIDs
```

The second stop has a nonzero loop count and is allowed to finish.

Say:

> The cat is asleep only after the evidence is produced.

### 7:00–8:00 — Explain the protocol

In **Inspector**, select the rewritten `preToolUse` event and show the original
command in the native payload. In **Cat Boss**, show the effective command.

Explain:

1. `payload` remains immutable Cursor evidence.
2. `harnessOutcome.response` is exactly what Cursor received on stdout.
3. `harnessOutcome.changes` powers the presenter-friendly log.
4. One envelope is persisted and replayable.
5. No second pipe message or timing correlation is required.

## Recovery branches

- No subagent: ask, “Before continuing, delegate an architecture review to a subagent.”
- No package command: ask, “Run `dotnet add package UUIDNext` before continuing.”
- GUID already uppercase: ask the agent to restore plain `Guid.NewGuid()` and run once.
- Build already uses Release: ask the agent to run plain `dotnet build` once.
- No sound: verify CursorSpy is open and Windows system sounds are audible.
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
