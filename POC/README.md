# Cursor Hook Spy POC

This proof of concept contains two executables:

- `CursorSpy.Hook.exe`: a short-lived Cursor hook process that reads native hook JSON on stdin, forwards one observation to a named pipe, writes `{}` to stdout, and exits fail-open.
- `CursorSpy.App.exe`: a WPF viewer that listens on `HarnessSpy.Ingest.v1` and shows captured payloads in memory.

The POC intentionally has no persistence, tray lifetime, installer, redaction, transcript enrichment, Driver control, or Claude/Copilot support.

## Build

```powershell
dotnet build C:\dev\research\AI\HarnessSpy\CursorSpy\POC\CursorSpy.POC.sln
```

## Run the viewer

Start the WPF app before triggering hooks:

```powershell
dotnet run --project C:\dev\research\AI\HarnessSpy\CursorSpy\POC\src\CursorSpy.App\CursorSpy.App.csproj
```

Events emitted while the viewer is not running are intentionally dropped.

## Synthetic smoke test

With the viewer running, send a synthetic hook payload through the real hook executable:

```powershell
$hook = "C:\dev\research\AI\HarnessSpy\CursorSpy\POC\src\CursorSpy.Hook\bin\Debug\net10.0\CursorSpy.Hook.exe"
'{"hook_event_name":"sessionStart","conversation_id":"synthetic-conversation","generation_id":"generation-1","workspace_roots":["C:\\dev\\research\\AI\\HarnessSpy"],"cursor_version":"dev","session_id":"synthetic-conversation","composer_mode":"agent","is_background_agent":false}' | & $hook
```

The hook should print `{}` and the WPF tree should show the payload under the workspace and session.

To exercise sessions outside a workspace:

```powershell
'{"hook_event_name":"beforeSubmitPrompt","conversation_id":"no-workspace-conversation","generation_id":"generation-2","workspace_roots":[],"prompt":"hello"}' | & $hook
```

The payload should appear under `No workspace`.

## Cursor hook registration

`config/hooks.example.json` contains the 18 agent hooks plus `workspaceOpen`, all pointing at the local Debug build path. Build first so the executable exists.

To use it, merge the entries into:

```text
%USERPROFILE%\.cursor\hooks.json
```

Do not overwrite existing hooks unless you intend to remove them. Cursor watches hook files and usually reloads them on save; restart Cursor if the Hooks output channel does not show the registrations.

The expected workflow is:

1. Build the solution.
2. Start `CursorSpy.App`.
3. Merge or copy the hook registrations.
4. Trigger Cursor agent activity in a workspace.
5. Use `Ctrl+F`, `F3`, and `Shift+F3` in the viewer to search the selected payload.
