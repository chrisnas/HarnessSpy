using System.Collections.Concurrent;

namespace HarnessSpy.Core.Runtimes.Copilot;

// The parsed MCP identity of a Copilot CLI tool call. ServerName is null when a
// call is known to be MCP (it is not a built-in tool) but the server boundary
// has not yet been learned from a permission or notification event.
internal readonly record struct CopilotMcpIdentity(string? ServerName, string ToolName, string FlatName);

// Identifies MCP tool calls in Copilot CLI hook payloads using hooks-only
// signals. Unlike Claude ("mcp__server__tool") or Cursor (a dedicated
// "mcp_server_name" field), the CLI flattens an MCP call to "<server>-<tool>"
// in preToolUse/postToolUse with no marker, and the hyphen boundary is ambiguous
// because both the server and the tool may contain hyphens or underscores.
//
// Three complementary signals are combined:
//   Idea 2 - permissionRequest ("<server>/<tool>") and the Notification message
//            ("Use MCP tool: <server>/<tool>") expose the unambiguous boundary.
//   Idea 3 - a negative allowlist of the CLI's built-in tools: any tool name
//            that is not a built-in is treated as an MCP call.
//   Idea 4 - the "<server>/<tool>" pairs learned from idea 2 are cached per
//            session so the flattened preToolUse/postToolUse names can be split
//            back into their server and tool.
internal sealed class CopilotMcpToolClassifier
{
    // The GitHub Copilot CLI built-in tools (see the CLI command reference). Any
    // tool name outside this set is assumed to be provided by an MCP server.
    private static readonly HashSet<string> _builtInTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "shell",
        "bash", "read_bash", "write_bash", "stop_bash", "list_bash",
        "powershell", "read_powershell", "write_powershell", "stop_powershell", "list_powershell",
        "view", "create", "edit", "apply_patch",
        "grep", "rg", "glob",
        "web_fetch", "web_search",
        "ask_user", "skill", "task", "report_intent",
        "fetch_copilot_cli_documentation", "tool_search_tool_regex",
        "sql", "session_store_sql",
        "read_agent", "list_agents", "write_agent"
    };

    // sessionId -> (flattened "<server>-<tool>" name -> server name).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _serverBySession = new(StringComparer.Ordinal);

    // Learns the "<server>/<tool>" split carried by a permissionRequest so later
    // flattened names in the same session can be attributed to their server.
    public CopilotMcpIdentity? RegisterFromSlashName(string? sessionId, string? slashToolName)
    {
        CopilotMcpIdentity? identity = ParseSlash(slashToolName);
        if (identity is not null)
        {
            Remember(sessionId, identity.Value);
        }

        return identity;
    }

    // Learns the split from the permission Notification message, whose text is
    // "Use MCP tool: <server>/<tool>".
    public CopilotMcpIdentity? RegisterFromNotification(string? sessionId, string? message)
    {
        const string marker = "Use MCP tool:";
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        int markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        string tail = message[(markerIndex + marker.Length)..].Trim();
        return RegisterFromSlashName(sessionId, tail);
    }

    // Classifies a flattened preToolUse/postToolUse tool name. The learned
    // server map (idea 4) is consulted first for an exact split; otherwise the
    // built-in allowlist (idea 3) decides whether the call is MCP with an
    // as-yet-unknown server.
    public CopilotMcpIdentity? ClassifyFlatName(string? sessionId, string? flatToolName)
    {
        if (string.IsNullOrWhiteSpace(flatToolName))
        {
            return null;
        }

        if (sessionId is not null &&
            _serverBySession.TryGetValue(sessionId, out ConcurrentDictionary<string, string>? map) &&
            map.TryGetValue(flatToolName, out string? server))
        {
            string tool = flatToolName.StartsWith(server + "-", StringComparison.Ordinal)
                ? flatToolName[(server.Length + 1)..]
                : flatToolName;
            return new CopilotMcpIdentity(server, tool, flatToolName);
        }

        if (_builtInTools.Contains(flatToolName))
        {
            return null;
        }

        return new CopilotMcpIdentity(null, flatToolName, flatToolName);
    }

    // Drops a session's learned mappings when it starts or ends so a reused
    // session id never inherits stale state.
    public void ResetSession(string? sessionId)
    {
        if (sessionId is not null)
        {
            _serverBySession.TryRemove(sessionId, out _);
        }
    }

    private void Remember(string? sessionId, CopilotMcpIdentity identity)
    {
        if (sessionId is null || identity.ServerName is null)
        {
            return;
        }

        ConcurrentDictionary<string, string> map = _serverBySession.GetOrAdd(
            sessionId,
            static _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
        map[identity.FlatName] = identity.ServerName;
    }

    private static CopilotMcpIdentity? ParseSlash(string? slashToolName)
    {
        if (string.IsNullOrWhiteSpace(slashToolName))
        {
            return null;
        }

        // The server name never contains a slash, so the first one is the
        // boundary. The CLI flattens the same call by replacing it with a hyphen.
        int separator = slashToolName.IndexOf('/');
        if (separator <= 0 || separator >= slashToolName.Length - 1)
        {
            return null;
        }

        string server = slashToolName[..separator];
        string tool = slashToolName[(separator + 1)..];
        return new CopilotMcpIdentity(server, tool, $"{server}-{tool}");
    }
}
