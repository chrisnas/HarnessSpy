using System.IO;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Plans;

// Infers only presentation-level workspace placement for orphan plans. It
// never changes canonical session ownership or the artifact's observed
// Workspace value.
public sealed class SessionPlanWorkspaceInferrer
{
    public SessionPlanWorkspaceInference? Infer(
        SessionPlanArtifact artifact,
        IReadOnlyList<SessionPlanActivity> activities,
        IReadOnlyList<SessionCatalogEntry> sessions)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(sessions);

        if (artifact.BoundCatalogSessionId is not null ||
            artifact.Workspace.Kind == WorkspaceContextKind.Normal)
        {
            return null;
        }

        SessionCatalogEntry[] providerSessions = sessions
            .Where(session =>
                session.Provider == artifact.Provider &&
                session.Workspace.Kind == WorkspaceContextKind.Normal)
            .ToArray();
        if (providerSessions.Length == 0)
        {
            return null;
        }

        Dictionary<string, SessionCatalogEntry> sessionsById =
            providerSessions
                .GroupBy(
                    static session => session.CatalogSessionId,
                    StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.First(),
                    StringComparer.Ordinal);
        WorkspaceContext[] workspaces = providerSessions
            .Select(static session => session.Workspace)
            .DistinctBy(
                static workspace => workspace.Key,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        List<WorkspaceSignal> signals = [];
        AddNativeSessionSignal(
            artifact,
            providerSessions,
            signals);
        AddReferenceSignals(
            activities,
            sessionsById,
            signals);
        AddPathSignals(
            artifact.WorkspacePathHints,
            workspaces,
            signals);

        WorkspaceContext[] candidates = signals
            .Select(static signal => signal.Workspace)
            .DistinctBy(
                static workspace => workspace.Key,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length != 1)
        {
            return null;
        }

        SessionPlanWorkspaceEvidence[] evidenceItems = signals
            .Where(signal => string.Equals(
                signal.Workspace.Key,
                candidates[0].Key,
                StringComparison.OrdinalIgnoreCase))
            .Select(static signal => signal.Evidence)
            .Distinct()
            .Take(16)
            .ToArray();
        if (evidenceItems.Length == 0)
        {
            return null;
        }

        int evidenceKindCount = evidenceItems
            .Select(static item => item.Kind)
            .Distinct()
            .Count();
        bool hasNativeSessionIdentifier = evidenceItems.Any(
            static item =>
                item.Kind ==
                SessionPlanWorkspaceEvidenceKind.NativeSessionIdentifier);
        InferenceEvidence evidence =
            hasNativeSessionIdentifier || evidenceKindCount > 1
                ? InferenceEvidence.Corroborated
                : InferenceEvidence.Heuristic;

        return new SessionPlanWorkspaceInference(
            candidates[0],
            evidence,
            evidenceItems);
    }

    private void AddNativeSessionSignal(
        SessionPlanArtifact artifact,
        IReadOnlyList<SessionCatalogEntry> sessions,
        ICollection<WorkspaceSignal> signals)
    {
        if (string.IsNullOrWhiteSpace(artifact.NativeSessionIdHint))
        {
            return;
        }

        WorkspaceContext[] matches = sessions
            .Where(session => string.Equals(
                session.NativeSessionId,
                artifact.NativeSessionIdHint,
                StringComparison.OrdinalIgnoreCase))
            .Select(static session => session.Workspace)
            .DistinctBy(
                static workspace => workspace.Key,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length == 1)
        {
            signals.Add(new WorkspaceSignal(
                matches[0],
                new SessionPlanWorkspaceEvidence(
                    SessionPlanWorkspaceEvidenceKind.NativeSessionIdentifier,
                    artifact.NativeSessionIdHint!)));
        }
    }

    private void AddReferenceSignals(
        IEnumerable<SessionPlanActivity> activities,
        IReadOnlyDictionary<string, SessionCatalogEntry> sessionsById,
        ICollection<WorkspaceSignal> signals)
    {
        foreach (SessionPlanActivity activity in activities)
        {
            if (activity.Kind != SessionPlanActivityKind.Referenced ||
                !activity.SupportsWorkspaceInference ||
                activity.CatalogSessionId is not string sessionId ||
                !sessionsById.TryGetValue(
                    sessionId,
                    out SessionCatalogEntry? session))
            {
                continue;
            }

            signals.Add(new WorkspaceSignal(
                session.Workspace,
                new SessionPlanWorkspaceEvidence(
                    SessionPlanWorkspaceEvidenceKind.ReferencingSession,
                    $"{session.Title} ({session.NativeSessionId})")));
        }
    }

    private void AddPathSignals(
        IEnumerable<string> paths,
        IReadOnlyList<WorkspaceContext> workspaces,
        ICollection<WorkspaceSignal> signals)
    {
        foreach (string path in paths)
        {
            WorkspaceContext? workspace = FindMostSpecificWorkspace(
                path,
                workspaces);
            if (workspace is null)
            {
                continue;
            }

            signals.Add(new WorkspaceSignal(
                workspace,
                new SessionPlanWorkspaceEvidence(
                    SessionPlanWorkspaceEvidenceKind.PlanContentPath,
                    path)));
        }
    }

    private WorkspaceContext? FindMostSpecificWorkspace(
        string path,
        IReadOnlyList<WorkspaceContext> workspaces)
    {
        string? normalizedPath = NormalizePath(path);
        if (normalizedPath is null)
        {
            return null;
        }

        List<(WorkspaceContext Workspace, int RootLength)> matches = [];
        foreach (WorkspaceContext workspace in workspaces)
        {
            int longestRoot = -1;
            foreach (string root in workspace.DisplayRoots)
            {
                string? normalizedRoot = NormalizePath(root);
                if (normalizedRoot is not null &&
                    IsWithin(normalizedPath, normalizedRoot))
                {
                    longestRoot = Math.Max(
                        longestRoot,
                        normalizedRoot.Length);
                }
            }

            if (longestRoot >= 0)
            {
                matches.Add((workspace, longestRoot));
            }
        }

        if (matches.Count == 0)
        {
            return null;
        }

        int maximumRootLength = matches.Max(static match => match.RootLength);
        WorkspaceContext[] mostSpecific = matches
            .Where(match => match.RootLength == maximumRootLength)
            .Select(static match => match.Workspace)
            .DistinctBy(
                static workspace => workspace.Key,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return mostSpecific.Length == 1 ? mostSpecific[0] : null;
    }

    private string? NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string normalized = Path.GetFullPath(path.Trim());
            return Path.TrimEndingDirectorySeparator(normalized);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return null;
        }
    }

    private bool IsWithin(string path, string root)
    {
        if (string.Equals(
            path,
            root,
            StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = root.EndsWith(
            Path.DirectorySeparatorChar) ||
            root.EndsWith(Path.AltDirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
        return path.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed record WorkspaceSignal(
        WorkspaceContext Workspace,
        SessionPlanWorkspaceEvidence Evidence);
}
