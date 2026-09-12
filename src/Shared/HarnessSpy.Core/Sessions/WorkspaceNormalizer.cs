using System.IO;
using System.Text;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions;

public sealed class WorkspaceNormalizer
{
    public WorkspaceContext FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return WorkspaceContext.Unknown;
        }

        string decoded = DecodeFileUri(path.Trim());
        return WorkspaceContext.FromRoot(decoded);
    }

    public WorkspaceContext FromRoots(IEnumerable<string> roots)
    {
        string[] normalized = roots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(root => FromPath(root).DisplayRoots.FirstOrDefault())
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length switch
        {
            0 => WorkspaceContext.Unknown,
            1 => WorkspaceContext.FromRoot(normalized[0]),
            _ => new WorkspaceContext(
                "roots:" + string.Join("|", normalized.Select(static root => root.ToUpperInvariant())),
                "Multiroot: " + string.Join("; ", normalized),
                normalized,
                WorkspaceContextKind.Normal)
        };
    }

    public string? DecodeCursorProjectName(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        if (encoded.Length >= 3 &&
            char.IsAsciiLetter(encoded[0]) &&
            encoded[1] == '-' &&
            encoded[2] == '-')
        {
            string driveRoot = $"{encoded[0]}:{Path.DirectorySeparatorChar}";
            return DecodeByExistingSegments(driveRoot, encoded[3..]);
        }

        return null;
    }

    public string? DecodeClaudeProjectName(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        if (encoded.Length >= 3 &&
            char.IsAsciiLetter(encoded[0]) &&
            encoded[1] == '-' &&
            encoded[2] == '-')
        {
            return DecodeByExistingSegments(
                $"{encoded[0]}:{Path.DirectorySeparatorChar}",
                encoded[3..]);
        }

        return null;
    }

    private static string DecodeFileUri(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        if (value.Length >= 3 && value[0] == '/' &&
            char.IsAsciiLetter(value[1]) && value[2] == ':')
        {
            return value[1..];
        }

        return value;
    }

    private static string? DecodeByExistingSegments(string root, string encoded)
    {
        return DecodeRecursive(root, encoded, depth: 0);
    }

    private static string? DecodeRecursive(string current, string remainder, int depth)
    {
        if (depth > 20)
        {
            return null;
        }

        if (remainder.Length == 0)
        {
            return Directory.Exists(current) ? Path.GetFullPath(current) : null;
        }

        List<int> boundaries = [];
        for (int index = 0; index < remainder.Length; index++)
        {
            if (remainder[index] == '-')
            {
                boundaries.Add(index);
            }
        }

        boundaries.Add(remainder.Length);
        foreach (int boundary in boundaries.OrderDescending())
        {
            string segment = remainder[..boundary];
            if (segment.Length == 0)
            {
                continue;
            }

            string candidate = Path.Combine(current, segment);
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            if (boundary == remainder.Length)
            {
                return Path.GetFullPath(candidate);
            }

            string? resolved = DecodeRecursive(candidate, remainder[(boundary + 1)..], depth + 1);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }
}
