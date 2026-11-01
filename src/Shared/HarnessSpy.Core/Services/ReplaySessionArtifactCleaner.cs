using System.IO;

namespace HarnessSpy.Core.Services;

public sealed record ReplaySessionCleanupResult(
    int DeletedFileCount,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>
/// Deletes only HarnessSpy-owned replay artifacts for one provider session.
/// Provider-owned transcript paths are intentionally outside this boundary.
/// </summary>
public sealed class ReplaySessionArtifactCleaner
{
    public ReplaySessionCleanupResult Delete(
        string scopedSessionId,
        IEnumerable<string> payloadFiles,
        IEnumerable<string> payloadFolders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopedSessionId);
        ArgumentNullException.ThrowIfNull(payloadFiles);
        ArgumentNullException.ThrowIfNull(payloadFolders);

        List<string> errors = [];
        string[] folders = NormalizePaths(payloadFolders, errors);
        string[] files = NormalizePaths(payloadFiles, errors);
        int deletedFileCount = 0;

        foreach (string folder in folders)
        {
            string sidecarDirectory =
                new TranscriptCaptureStore(folder).SidecarDirectory(scopedSessionId);
            deletedFileCount += DeleteSidecarDirectory(
                sidecarDirectory,
                errors);
        }

        foreach (string file in files)
        {
            if (!IsOwnedPayloadFile(file, folders))
            {
                errors.Add(
                    $"Refused to delete non-payload source file '{file}'.");
                continue;
            }

            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    deletedFileCount++;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{file}: {exception.Message}");
            }
        }

        return new ReplaySessionCleanupResult(
            deletedFileCount,
            errors);
    }

    private static string[] NormalizePaths(
        IEnumerable<string> paths,
        List<string> errors)
    {
        HashSet<string> normalized =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths.Where(
                     static path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                normalized.Add(Path.GetFullPath(path));
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                    NotSupportedException or
                    PathTooLongException)
            {
                errors.Add($"{path}: {exception.Message}");
            }
        }

        return normalized.ToArray();
    }

    private static bool IsOwnedPayloadFile(
        string file,
        IReadOnlyList<string> payloadFolders)
    {
        if (!string.Equals(
                Path.GetExtension(file),
                ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? parent = Path.GetDirectoryName(file);
        return parent is not null &&
            payloadFolders.Any(folder => string.Equals(
                parent,
                folder,
                StringComparison.OrdinalIgnoreCase));
    }

    private static int DeleteSidecarDirectory(
        string directory,
        List<string> errors)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            int fileCount = Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Count();
            Directory.Delete(directory, recursive: true);
            return fileCount;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{directory}: {exception.Message}");
            return 0;
        }
    }
}
