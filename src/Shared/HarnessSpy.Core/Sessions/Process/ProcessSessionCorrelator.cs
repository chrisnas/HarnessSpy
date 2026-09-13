using System.IO;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Process;

public sealed class ProcessSessionCorrelator
{
    public IReadOnlyList<SessionCatalogEntry> Apply(
        IReadOnlyList<SessionCatalogEntry> sessions,
        IReadOnlyList<RunningSessionHint> hints)
    {
        List<SessionCatalogEntry> result = new(sessions.Count);
        foreach (SessionCatalogEntry session in sessions)
        {
            RunningSessionHint? exact = FindExact(session, hints);
            if (exact is not null)
            {
                result.Add(session with
                {
                    LifecycleState = SessionLifecycleState.Open,
                    LifecycleEvidence = exact.Evidence,
                    Metadata = WithProcessMetadata(session.Metadata, exact)
                });
                continue;
            }

            if (session.Provider == HookProvider.Cursor &&
                session.IsSelectedInHarness &&
                hints.Any(static hint =>
                    hint.Provider == HookProvider.Cursor &&
                    string.Equals(
                        Path.GetFileNameWithoutExtension(hint.ProcessName),
                        "Cursor",
                        StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(session with
                {
                    LifecycleState = SessionLifecycleState.Open,
                    LifecycleEvidence = InferenceEvidence.Corroborated
                });
                continue;
            }

            RunningSessionHint? uniqueWorkspace = FindUniqueWorkspace(session, sessions, hints);
            if (uniqueWorkspace is not null)
            {
                result.Add(session with
                {
                    LifecycleState = SessionLifecycleState.Open,
                    LifecycleEvidence = InferenceEvidence.Heuristic,
                    Metadata = WithProcessMetadata(session.Metadata, uniqueWorkspace)
                });
                continue;
            }

            result.Add(session with
            {
                LifecycleState = SessionLifecycleState.Closed,
                LifecycleEvidence = InferenceEvidence.Unavailable
            });
        }

        return result;
    }

    private static RunningSessionHint? FindExact(
        SessionCatalogEntry session,
        IReadOnlyList<RunningSessionHint> hints)
    {
        foreach (RunningSessionHint hint in hints)
        {
            if (hint.Provider != session.Provider)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(hint.NativeSessionId) &&
                string.Equals(
                    hint.NativeSessionId,
                    session.NativeSessionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return hint;
            }

            if (!string.IsNullOrWhiteSpace(hint.TranscriptPath) &&
                session.Files.Any(
                    file => PathsEqual(file.Path, hint.TranscriptPath)))
            {
                return hint;
            }
        }

        return null;
    }

    private static RunningSessionHint? FindUniqueWorkspace(
        SessionCatalogEntry target,
        IReadOnlyList<SessionCatalogEntry> sessions,
        IReadOnlyList<RunningSessionHint> hints)
    {
        if (target.Workspace.Kind != WorkspaceContextKind.Normal)
        {
            return null;
        }

        SessionCatalogEntry[] sameWorkspace = sessions
            .Where(session =>
                session.Provider == target.Provider &&
                string.Equals(
                    session.Workspace.Key,
                    target.Workspace.Key,
                    StringComparison.Ordinal))
            .ToArray();
        if (sameWorkspace.Length != 1)
        {
            return null;
        }

        foreach (RunningSessionHint hint in hints)
        {
            if (hint.Provider != target.Provider ||
                string.IsNullOrWhiteSpace(hint.WorkingDirectory))
            {
                continue;
            }

            if (target.Workspace.DisplayRoots.Any(
                root => PathsEqual(root, hint.WorkingDirectory)))
            {
                return hint;
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string?> WithProcessMetadata(
        IReadOnlyDictionary<string, string?> metadata,
        RunningSessionHint hint)
    {
        Dictionary<string, string?> updated =
            new(metadata, StringComparer.Ordinal)
            {
                ["process_id"] = hint.ProcessId > 0 ? hint.ProcessId.ToString() : null,
                ["process_name"] = hint.ProcessName
            };
        return updated;
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first).TrimEnd('\\', '/'),
                Path.GetFullPath(second).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
    }
}
