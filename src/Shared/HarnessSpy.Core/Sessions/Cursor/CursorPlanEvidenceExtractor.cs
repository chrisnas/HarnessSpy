using System.IO;
using System.Text.RegularExpressions;

namespace HarnessSpy.Core.Sessions.Cursor;

// Extracts non-authoritative workspace evidence from Cursor-owned plan
// documents and transcript records. The resulting paths are hints only; the
// plan assembler decides whether they identify one unambiguous workspace.
public sealed partial class CursorPlanEvidenceExtractor
{
    public IReadOnlyList<string> ExtractPlanPaths(
        string? text,
        IEnumerable<string>? targetPaths = null)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        if (targetPaths is not null)
        {
            foreach (string path in targetPaths)
            {
                AddPlanPath(path, paths);
            }
        }

        if (string.IsNullOrWhiteSpace(text) ||
            !text.Contains(".plan.md", StringComparison.OrdinalIgnoreCase))
        {
            return [.. paths];
        }

        foreach (Match match in FilePlanUriPattern().Matches(text))
        {
            AddFileUri(match.Value, paths, requirePlanExtension: true);
        }

        foreach (Match match in WindowsPlanPathPattern().Matches(text))
        {
            AddPlanPath(UnescapeJsonPath(match.Value), paths);
        }

        foreach (Match match in PosixPlanPathPattern().Matches(text))
        {
            if (!IsFileUriPath(text, match.Index))
            {
                AddPlanPath(match.Value, paths);
            }
        }

        return [.. paths.Order(StringComparer.OrdinalIgnoreCase)];
    }

    public IReadOnlyList<string> ExtractWorkspacePathHints(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in MarkdownWindowsPathPattern().Matches(markdown))
        {
            AddWorkspacePath(match.Groups["path"].Value, paths);
        }

        foreach (Match match in InlineCodeWindowsPathPattern().Matches(markdown))
        {
            AddWorkspacePath(match.Groups["path"].Value, paths);
        }

        foreach (Match match in FileUriPattern().Matches(markdown))
        {
            AddFileUri(match.Value, paths, requirePlanExtension: false);
        }

        return [.. paths.Order(StringComparer.OrdinalIgnoreCase)];
    }

    public string? ExtractNativeSessionIdHint(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        Match match = LeadingSessionIdPattern().Match(markdown);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private void AddPlanPath(string? candidate, ISet<string> paths)
    {
        string? normalized = NormalizeCandidate(candidate);
        if (normalized is not null &&
            normalized.EndsWith(".plan.md", StringComparison.OrdinalIgnoreCase))
        {
            paths.Add(normalized);
        }
    }

    private void AddWorkspacePath(string? candidate, ISet<string> paths)
    {
        string? normalized = NormalizeCandidate(candidate);
        if (normalized is not null && Path.IsPathRooted(normalized))
        {
            paths.Add(normalized);
        }
    }

    private void AddFileUri(
        string candidate,
        ISet<string> paths,
        bool requirePlanExtension)
    {
        string unescaped = candidate.Replace("\\/", "/", StringComparison.Ordinal);
        if (!Uri.TryCreate(unescaped, UriKind.Absolute, out Uri? uri) ||
            !uri.IsFile)
        {
            return;
        }

        string localPath = Uri.UnescapeDataString(uri.LocalPath);
        if (localPath.Length >= 3 &&
            localPath[0] == '/' &&
            char.IsAsciiLetter(localPath[1]) &&
            localPath[2] == ':')
        {
            localPath = localPath[1..];
        }

        localPath = localPath.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);
        if (requirePlanExtension)
        {
            AddPlanPath(localPath, paths);
        }
        else
        {
            AddWorkspacePath(localPath, paths);
        }
    }

    private string? NormalizeCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        string normalized = candidate
            .Trim()
            .Trim('"', '\'', '<', '>')
            .Replace("\\\\", "\\", StringComparison.Ordinal);
        return normalized.Length == 0 ? null : normalized;
    }

    private string UnescapeJsonPath(string path) =>
        path.Replace("\\\\", "\\", StringComparison.Ordinal);

    private bool IsFileUriPath(string text, int pathIndex)
    {
        const string prefix = "file://";
        int prefixIndex = pathIndex - prefix.Length;
        return prefixIndex >= 0 &&
            text.AsSpan(prefixIndex, prefix.Length).Equals(
                prefix,
                StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        @"file:\\?/\\?/\\?/[^""'\s<>]*?\.plan\.md",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilePlanUriPattern();

    [GeneratedRegex(
        @"[A-Za-z]:\\(?:[^""'\r\n]*?)\.plan\.md",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPlanPathPattern();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9:])/(?!/)[^""'\r\n]*?\.plan\.md",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PosixPlanPathPattern();

    [GeneratedRegex(
        @"\]\((?<path>[A-Za-z]:[\\/][^)]+)\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownWindowsPathPattern();

    [GeneratedRegex(
        @"`(?<path>[A-Za-z]:[\\/][^`\r\n]+)`",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlineCodeWindowsPathPattern();

    [GeneratedRegex(
        @"file:///[^""'\s<>)]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileUriPattern();

    [GeneratedRegex(
        @"\A(?:\uFEFF)?<!--\s*(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\s*-->",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingSessionIdPattern();
}
