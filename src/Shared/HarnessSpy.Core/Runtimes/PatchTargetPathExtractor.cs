namespace HarnessSpy.Core.Runtimes;

internal sealed class PatchTargetPathExtractor
{
    private static readonly string[] _headers =
    [
        "*** Add File: ",
        "*** Update File: ",
        "*** Delete File: ",
        "*** Move to: "
    ];

    public IReadOnlyList<string> Extract(string? patch)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            return [];
        }

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        using StringReader reader = new(patch);
        while (reader.ReadLine() is string line)
        {
            foreach (string header in _headers)
            {
                if (!line.StartsWith(header, StringComparison.Ordinal))
                {
                    continue;
                }

                string path = line[header.Length..].Trim();
                if (path.Length > 0)
                {
                    paths.Add(path);
                }

                break;
            }
        }

        return paths.ToArray();
    }
}
