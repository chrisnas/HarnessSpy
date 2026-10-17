using HarnessSpy.Core.Sessions;

namespace HarnessSpy.Core.Services;

public readonly record struct SessionSourceFileStamp(
    long Length,
    DateTimeOffset LastWriteTimeUtc);

public sealed class SessionSourceSnapshot : IEquatable<SessionSourceSnapshot>
{
    public SessionSourceSnapshot(
        IReadOnlyDictionary<string, SessionSourceFileStamp> files,
        bool canValidate)
    {
        Files = files;
        CanValidate = canValidate;
    }

    public IReadOnlyDictionary<string, SessionSourceFileStamp> Files { get; }

    public bool CanValidate { get; }

    public bool Equals(SessionSourceSnapshot? other)
    {
        if (other is null ||
            CanValidate != other.CanValidate ||
            Files.Count != other.Files.Count)
        {
            return false;
        }

        foreach ((string path, SessionSourceFileStamp stamp) in Files)
        {
            if (!other.Files.TryGetValue(
                    path,
                    out SessionSourceFileStamp candidate) ||
                candidate != stamp)
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) =>
        obj is SessionSourceSnapshot snapshot && Equals(snapshot);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(CanValidate);
        hash.Add(Files.Count);
        foreach ((string path, SessionSourceFileStamp stamp) in Files.OrderBy(
                     static pair => pair.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            hash.Add(path, StringComparer.OrdinalIgnoreCase);
            hash.Add(stamp);
        }

        return hash.ToHashCode();
    }
}

public sealed class SessionSourceSnapshotter
{
    private readonly EnumerationOptions _enumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip =
            FileAttributes.ReparsePoint |
            FileAttributes.System
    };

    public Task<SessionSourceSnapshot> CaptureAsync(
        ISessionCatalogSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Task.Run(
            () => Capture(source, cancellationToken),
            cancellationToken);
    }

    private SessionSourceSnapshot Capture(
        ISessionCatalogSource source,
        CancellationToken cancellationToken)
    {
        Dictionary<string, SessionSourceFileStamp> files =
            new(StringComparer.OrdinalIgnoreCase);
        bool hasConfiguredRoot = false;
        bool complete = true;
        foreach (string root in source.WatchRoots
                     .Where(static root => !string.IsNullOrWhiteSpace(root))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            hasConfiguredRoot = true;
            try
            {
                if (File.Exists(root))
                {
                    AddFile(root, files);
                    continue;
                }

                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (string path in Directory.EnumerateFiles(
                             root,
                             "*",
                             _enumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsCatalogFile(path))
                    {
                        AddFile(path, files);
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    NotSupportedException or
                    System.Security.SecurityException)
            {
                complete = false;
            }
        }

        return new SessionSourceSnapshot(
            files,
            hasConfiguredRoot && complete);
    }

    private void AddFile(
        string path,
        IDictionary<string, SessionSourceFileStamp> files)
    {
        FileInfo file = new(path);
        if (!file.Exists ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return;
        }

        files[file.FullName] = new SessionSourceFileStamp(
            file.Length,
            new DateTimeOffset(
                DateTime.SpecifyKind(
                    file.LastWriteTimeUtc,
                    DateTimeKind.Utc)));
    }

    private bool IsCatalogFile(string path)
    {
        string fileName = Path.GetFileName(path);
        string extension = Path.GetExtension(path);
        return extension.Equals(
                ".jsonl",
                StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(
                ".vscdb",
                StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(
                ".vscdb-wal",
                StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(
                "workspace.yaml",
                StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(
                "workspace.json",
                StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(
                "sessions-index.json",
                StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(
                ".session_cache.json",
                StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(
                ".md",
                StringComparison.OrdinalIgnoreCase);
    }
}
