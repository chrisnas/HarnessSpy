using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Runtimes;

// Provider-neutral mapping from an exact native tool name to a coarse tool
// kind. Shared by runtime engines and transcript parsers. Never renames the
// tool; only classifies it for trait purposes.
internal static class ToolClassifier
{
    public static CanonicalToolKind Classify(string? nativeToolName)
    {
        if (string.IsNullOrWhiteSpace(nativeToolName))
        {
            return CanonicalToolKind.Unknown;
        }

        if (RuntimeJson.IsMcpPrefixed(nativeToolName))
        {
            return CanonicalToolKind.Mcp;
        }

        return nativeToolName.ToLowerInvariant() switch
        {
            "shell" or "bash" or "powershell" => CanonicalToolKind.Shell,
            "read" or "view" => CanonicalToolKind.FileRead,
            "write" or "create" => CanonicalToolKind.FileWrite,
            "edit" or "strreplace" or "str_replace_editor" or "apply_patch" or "editfiles" =>
                CanonicalToolKind.FileEdit,
            "delete" => CanonicalToolKind.FileDelete,
            "grep" or "rg" => CanonicalToolKind.TextSearch,
            "glob" => CanonicalToolKind.FileSearch,
            "editnotebook" or "notebookedit" => CanonicalToolKind.Notebook,
            "task" or "agent" => CanonicalToolKind.Agent,
            "webfetch" or "websearch" or "web_fetch" or "web_search" =>
                CanonicalToolKind.Web,
            "askuserquestion" or "ask_user" => CanonicalToolKind.UserInteraction,
            "todowrite" or "update_todo" => CanonicalToolKind.Task,
            _ => CanonicalToolKind.Unknown
        };
    }
}
