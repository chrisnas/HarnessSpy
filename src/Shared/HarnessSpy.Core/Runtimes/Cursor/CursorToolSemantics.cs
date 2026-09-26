using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Runtimes.Cursor;

// Shared Cursor-specific tool semantics used by both the CursorSpy live/replay
// summaries and the SessionViewer passive catalog. Cursor exposes the same
// logical tool under different native names depending on the source (the agent
// transcript vs Cursor Desktop's SQLite store), so correlation keys are
// computed from a normalized canonical identity while the observed native name
// is always preserved for display.
public static class CursorToolSemantics
{
    // Native tool names that only enumerate the MCP tools available to the
    // agent. They are genuine tool calls (counted as tools) but never MCP
    // executions, so they must be kept out of MCP KPIs.
    public static bool IsDynamicToolDiscovery(string? nativeToolName) =>
        string.Equals(
            nativeToolName,
            "GetDynamicTools",
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            nativeToolName,
            "get_mcp_tools",
            StringComparison.OrdinalIgnoreCase);

    // Maps a Cursor native tool name from either dialect onto a stable
    // canonical category. The Desktop store uses "*_v2"/"*_raw_search" aliases
    // for the same operations the agent transcript records under their short
    // names, so both must resolve to one category for correlation.
    public static CanonicalToolKind CanonicalKind(string? nativeToolName)
    {
        if (string.IsNullOrWhiteSpace(nativeToolName))
        {
            return CanonicalToolKind.Unknown;
        }

        return nativeToolName.ToLowerInvariant() switch
        {
            "run_terminal_command_v2" or "run_terminal_command" => CanonicalToolKind.Shell,
            "ripgrep_raw_search" or "grep_search" => CanonicalToolKind.TextSearch,
            "glob_file_search" => CanonicalToolKind.FileSearch,
            "read_file_v2" or "read_file" => CanonicalToolKind.FileRead,
            "edit_file_v2" or "edit_file" or "apply_patch_v2" => CanonicalToolKind.FileEdit,
            "write_file_v2" or "write_file" or "create_file_v2" => CanonicalToolKind.FileWrite,
            "delete_file_v2" or "delete_file" => CanonicalToolKind.FileDelete,
            _ => ToolClassifier.Classify(nativeToolName)
        };
    }

    // True when the observed tool call is an actual MCP execution (not a
    // discovery call). Applies to both the transcript CallDynamicTool form and
    // Desktop's flattened "mcp-<server>-<tool>" form.
    public static bool IsMcpExecution(
        CanonicalToolKind toolKind,
        string? nativeToolName,
        string? mcpServerName)
    {
        if (IsDynamicToolDiscovery(nativeToolName))
        {
            return false;
        }

        if (toolKind == CanonicalToolKind.Mcp)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(mcpServerName))
        {
            return true;
        }

        return string.Equals(
            nativeToolName,
            "CallDynamicTool",
            StringComparison.Ordinal);
    }

    // A stable correlation key that lets the same logical tool request coming
    // from the transcript and the Desktop store resolve to a single entry.
    // FileWrite and FileEdit share one bucket because Cursor's Desktop store
    // reports agent "Write" calls as "edit_file_v2".
    public static string CorrelationKey(
        CanonicalToolKind toolKind,
        string? nativeToolName,
        string? mcpServerName,
        string? mcpToolName)
    {
        if (IsDynamicToolDiscovery(nativeToolName))
        {
            return "discovery";
        }

        if (IsMcpExecution(toolKind, nativeToolName, mcpServerName))
        {
            string tool = NormalizeMcpTool(mcpToolName ?? nativeToolName);
            return "mcp:" + tool;
        }

        CanonicalToolKind kind = toolKind == CanonicalToolKind.Unknown
            ? CanonicalKind(nativeToolName)
            : toolKind;
        if (kind is CanonicalToolKind.FileWrite or CanonicalToolKind.FileEdit)
        {
            return "file-mutate";
        }

        return "kind:" + kind;
    }

    // Extracts the bare MCP tool name from either the transcript's structured
    // fields or Desktop's flattened "mcp-<server>-<tool>" identity.
    private static string NormalizeMcpTool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Trim();
        if (normalized.StartsWith("MCP:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[4..];
        }

        if (normalized.StartsWith("mcp-", StringComparison.OrdinalIgnoreCase))
        {
            int lastSeparator = normalized.LastIndexOf('-');
            if (lastSeparator > 3 && lastSeparator < normalized.Length - 1)
            {
                normalized = normalized[(lastSeparator + 1)..];
            }
        }

        return normalized.ToLowerInvariant();
    }
}
