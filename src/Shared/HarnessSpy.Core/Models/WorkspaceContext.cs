using System.IO;
using System.Text.Json;

namespace HarnessSpy.Core.Models;

public enum WorkspaceContextKind
{
    Normal,
    NoWorkspace,
    Unknown
}

public sealed record WorkspaceContext(
    string Key,
    string DisplayName,
    IReadOnlyList<string> DisplayRoots,
    WorkspaceContextKind Kind)
{
    public static WorkspaceContext Unknown { get; } = new(
        "unknown-workspace",
        "Unknown workspace",
        Array.Empty<string>(),
        WorkspaceContextKind.Unknown);

    public static WorkspaceContext NoWorkspace { get; } = new(
        "no-workspace",
        "No workspace",
        Array.Empty<string>(),
        WorkspaceContextKind.NoWorkspace);

    public static WorkspaceContext FromPayload(JsonElement payload)
    {
        if (!payload.TryGetProperty("workspace_roots", out JsonElement rootsElement))
        {
            return Unknown;
        }

        if (rootsElement.ValueKind != JsonValueKind.Array)
        {
            return Unknown;
        }

        List<string> roots = [];
        foreach (JsonElement rootElement in rootsElement.EnumerateArray())
        {
            if (rootElement.ValueKind != JsonValueKind.String)
            {
                return Unknown;
            }

            string? root = rootElement.GetString();
            if (!string.IsNullOrWhiteSpace(root))
            {
                roots.Add(NormalizePathForDisplay(root));
            }
        }

        if (roots.Count == 0)
        {
            return NoWorkspace;
        }

        string[] distinctRoots = roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string key = "roots:" + string.Join("|", distinctRoots.Select(root => root.ToUpperInvariant()));
        string displayName = distinctRoots.Length == 1
            ? distinctRoots[0]
            : "Multiroot: " + string.Join("; ", distinctRoots);

        return new WorkspaceContext(key, displayName, distinctRoots, WorkspaceContextKind.Normal);
    }

    private static string NormalizePathForDisplay(string path)
    {
        string normalized = path.Trim();

        // Cursor IDE (non-CLI) may send workspace_roots as URI-style paths
        // with a leading '/' before the drive letter (e.g. "/c:/dev/...").
        // Strip that prefix so the path resolves correctly on Windows.
        if (normalized.Length >= 3 &&
            normalized[0] == '/' &&
            char.IsAsciiLetter(normalized[1]) &&
            normalized[2] == ':')
        {
            normalized = normalized[1..];
        }

        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch
        {
            // Keep the native payload visible even if Cursor sends an unusual root string.
        }

        normalized = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.EndsWith(':'))
        {
            normalized += Path.DirectorySeparatorChar;
        }

        return normalized;
    }
}
