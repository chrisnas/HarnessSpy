using System.Text;
using System.Text.RegularExpressions;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Cursor;

internal sealed class CursorSessionWorkspaceInferrer
{
    private const int MinimumPlacementScore = 60;
    private const int MinimumWinningMargin = 15;

    private readonly Regex _rootedPathPattern = new(
        @"(?i)[a-z]:[\\/][^<>\r\n""'`|)]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<SessionCatalogEntry> Infer(
        IReadOnlyList<SessionCatalogEntry> sessions,
        IReadOnlyList<WorkspaceContext> knownWorkspaces,
        IReadOnlyList<SessionCatalogEntry>? evidenceSessions = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(knownWorkspaces);

        IReadOnlyList<SessionCatalogEntry> evidence =
            evidenceSessions ?? sessions;
        WorkspaceContext[] candidates = knownWorkspaces
            .Concat(evidence.Select(static session => session.Workspace))
            .Where(IsSingleRootWorkspace)
            .DistinctBy(
                static workspace => workspace.Key,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return sessions
            .Select(session => InferSession(
                session,
                evidence,
                candidates))
            .ToArray();
    }

    private SessionCatalogEntry InferSession(
        SessionCatalogEntry session,
        IReadOnlyList<SessionCatalogEntry> evidenceSessions,
        IReadOnlyList<WorkspaceContext> candidates)
    {
        if (IsSingleRootWorkspace(session.Workspace))
        {
            return session with { WorkspaceInference = null };
        }

        if (session.Workspace.DisplayRoots.Count > 1 &&
            session.Metadata.TryGetValue(
                "cursor.workspaceResolution",
                out string? resolution) &&
            string.Equals(
                resolution,
                "explicit-workspace-roots",
                StringComparison.Ordinal))
        {
            return session with { WorkspaceInference = null };
        }

        List<WorkspaceSignal> signals = [];
        AddNativeSessionSignal(
            session,
            evidenceSessions,
            signals);
        AddProjectKeySignal(
            session,
            candidates,
            signals);
        AddObservedRootSignals(
            session,
            candidates,
            signals);
        AddEventPathSignals(
            session,
            candidates,
            signals);
        AddPromptPathSignals(
            session,
            candidates,
            signals);

        SessionWorkspaceInference? inference = BuildInference(signals);
        return session with { WorkspaceInference = inference };
    }

    private void AddNativeSessionSignal(
        SessionCatalogEntry session,
        IReadOnlyList<SessionCatalogEntry> evidenceSessions,
        ICollection<WorkspaceSignal> signals)
    {
        WorkspaceContext[] matches = evidenceSessions
            .Where(candidate =>
                !ReferenceEquals(candidate, session) &&
                string.Equals(
                    candidate.NativeSessionId,
                    session.NativeSessionId,
                    StringComparison.OrdinalIgnoreCase) &&
                IsSingleRootWorkspace(candidate.Workspace))
            .Select(static candidate => candidate.Workspace)
            .DistinctBy(
                static workspace => workspace.Key,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length != 1)
        {
            return;
        }

        AddSignal(
            signals,
            matches[0],
            SessionWorkspaceEvidenceKind.NativeSessionIdentifier,
            session.NativeSessionId,
            95);
    }

    private void AddProjectKeySignal(
        SessionCatalogEntry session,
        IReadOnlyList<WorkspaceContext> candidates,
        ICollection<WorkspaceSignal> signals)
    {
        if (!session.Metadata.TryGetValue(
                "cursor.projectKey",
                out string? projectKey) ||
            string.IsNullOrWhiteSpace(projectKey) ||
            string.Equals(
                projectKey,
                "empty-window",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        WorkspaceContext[] matches = candidates
            .Where(candidate => string.Equals(
                ProjectKey(candidate.DisplayRoots[0]),
                projectKey,
                StringComparison.OrdinalIgnoreCase))
            .DistinctBy(
                static workspace => workspace.Key,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length != 1)
        {
            return;
        }

        AddSignal(
            signals,
            matches[0],
            SessionWorkspaceEvidenceKind.ProviderProjectKey,
            projectKey,
            90);
    }

    private void AddObservedRootSignals(
        SessionCatalogEntry session,
        IReadOnlyList<WorkspaceContext> candidates,
        ICollection<WorkspaceSignal> signals)
    {
        if (session.Workspace.DisplayRoots.Count < 2)
        {
            return;
        }

        foreach (string root in session.Workspace.DisplayRoots)
        {
            WorkspaceContext? workspace = FindMostSpecificWorkspace(
                root,
                candidates);
            if (workspace is not null)
            {
                AddSignal(
                    signals,
                    workspace,
                    SessionWorkspaceEvidenceKind.ObservedWorkspaceRoot,
                    root,
                    70);
            }
        }
    }

    private void AddEventPathSignals(
        SessionCatalogEntry session,
        IReadOnlyList<WorkspaceContext> candidates,
        ICollection<WorkspaceSignal> signals)
    {
        IEnumerable<SessionEventRecord> records = session.SessionEvents
            .Concat(session.Turns.SelectMany(static turn => turn.Events))
            .Where(static record => string.IsNullOrWhiteSpace(record.AgentId));
        foreach (string path in records.SelectMany(
                     static record => record.TargetPaths))
        {
            WorkspaceContext? workspace = FindMostSpecificWorkspace(
                path,
                candidates);
            if (workspace is not null)
            {
                AddSignal(
                    signals,
                    workspace,
                    SessionWorkspaceEvidenceKind.SessionPath,
                    path,
                    75);
            }
        }
    }

    private void AddPromptPathSignals(
        SessionCatalogEntry session,
        IReadOnlyList<WorkspaceContext> candidates,
        ICollection<WorkspaceSignal> signals)
    {
        foreach (SessionTurn turn in session.Turns)
        {
            foreach (Match match in _rootedPathPattern.Matches(turn.Prompt))
            {
                string path = match.Value.Trim().TrimEnd('.', ',', ';');
                WorkspaceContext? workspace = FindMostSpecificWorkspace(
                    path,
                    candidates);
                if (workspace is not null)
                {
                    AddSignal(
                        signals,
                        workspace,
                        SessionWorkspaceEvidenceKind.PromptPath,
                        path,
                        65);
                }
            }
        }
    }

    private SessionWorkspaceInference? BuildInference(
        IReadOnlyList<WorkspaceSignal> signals)
    {
        RankedWorkspace[] ranked = signals
            .GroupBy(
                static signal => signal.Workspace.Key,
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                WorkspaceSignal[] items = group.ToArray();
                int kindCount = items
                    .Select(static item => item.Evidence.Kind)
                    .Distinct()
                    .Count();
                int score = Math.Min(
                    99,
                    items.Max(static item => item.Evidence.Score) +
                    Math.Max(0, kindCount - 1) * 5);
                return new RankedWorkspace(
                    items[0].Workspace,
                    score,
                    items
                        .Select(static item => item.Evidence)
                        .Distinct()
                        .Take(16)
                        .ToArray());
            })
            .Where(static item => item.Score >= MinimumPlacementScore)
            .OrderByDescending(static item => item.Score)
            .ThenBy(
                static item => item.Workspace.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ranked.Length == 0)
        {
            return null;
        }

        RankedWorkspace winner = ranked[0];
        bool ambiguous =
            ranked.Length > 1 &&
            winner.Score - ranked[1].Score < MinimumWinningMargin;
        if (ambiguous)
        {
            return new SessionWorkspaceInference(
                WorkspaceContext.Unknown,
                InferenceEvidence.Ambiguous,
                winner.Score,
                ranked
                    .Where(item =>
                        winner.Score - item.Score < MinimumWinningMargin)
                    .SelectMany(static item => item.EvidenceItems)
                    .Distinct()
                    .Take(16)
                    .ToArray(),
                ranked
                    .Take(5)
                    .Select(static item => new SessionWorkspaceCandidate(
                        item.Workspace,
                        item.Score))
                    .ToArray());
        }

        int evidenceKindCount = winner.EvidenceItems
            .Select(static item => item.Kind)
            .Distinct()
            .Count();
        InferenceEvidence evidence =
            winner.EvidenceItems.Any(static item =>
                item.Kind ==
                SessionWorkspaceEvidenceKind.NativeSessionIdentifier) ||
            evidenceKindCount > 1
                ? InferenceEvidence.Corroborated
                : winner.EvidenceItems.Any(static item =>
                    item.Kind ==
                    SessionWorkspaceEvidenceKind.ProviderProjectKey)
                    ? InferenceEvidence.Derived
                    : InferenceEvidence.Heuristic;

        return new SessionWorkspaceInference(
            winner.Workspace,
            evidence,
            winner.Score,
            winner.EvidenceItems,
            ranked
                .Skip(1)
                .Take(4)
                .Select(static item => new SessionWorkspaceCandidate(
                    item.Workspace,
                    item.Score))
                .ToArray());
    }

    private WorkspaceContext? FindMostSpecificWorkspace(
        string path,
        IReadOnlyList<WorkspaceContext> candidates)
    {
        string? normalizedPath = NormalizePath(path);
        if (normalizedPath is null)
        {
            return null;
        }

        List<(WorkspaceContext Workspace, int RootLength)> matches = [];
        foreach (WorkspaceContext candidate in candidates)
        {
            string? root = NormalizePath(candidate.DisplayRoots[0]);
            if (root is not null && IsWithin(normalizedPath, root))
            {
                matches.Add((candidate, root.Length));
            }
        }

        if (matches.Count == 0)
        {
            return null;
        }

        int longest = matches.Max(static match => match.RootLength);
        WorkspaceContext[] mostSpecific = matches
            .Where(match => match.RootLength == longest)
            .Select(static match => match.Workspace)
            .DistinctBy(
                static workspace => workspace.Key,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return mostSpecific.Length == 1 ? mostSpecific[0] : null;
    }

    private string ProjectKey(string root)
    {
        string? normalized = NormalizePath(root);
        if (normalized is null)
        {
            return string.Empty;
        }

        StringBuilder builder = new(normalized.Length);
        bool lastWasSeparator = false;
        foreach (char character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private string? NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string candidate = path.Trim().Trim('"');
        if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) &&
            uri.IsFile)
        {
            candidate = uri.LocalPath;
        }

        if (!Path.IsPathRooted(candidate))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(candidate));
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

        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ||
            root.EndsWith(Path.AltDirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
        return path.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSingleRootWorkspace(WorkspaceContext workspace) =>
        workspace.Kind == WorkspaceContextKind.Normal &&
        workspace.DisplayRoots.Count == 1;

    private static void AddSignal(
        ICollection<WorkspaceSignal> signals,
        WorkspaceContext workspace,
        SessionWorkspaceEvidenceKind kind,
        string value,
        int score)
    {
        SessionWorkspaceEvidence evidence = new(
            kind,
            value,
            score);
        if (!signals.Any(signal =>
                string.Equals(
                    signal.Workspace.Key,
                    workspace.Key,
                    StringComparison.OrdinalIgnoreCase) &&
                signal.Evidence == evidence))
        {
            signals.Add(new WorkspaceSignal(workspace, evidence));
        }
    }

    private sealed record WorkspaceSignal(
        WorkspaceContext Workspace,
        SessionWorkspaceEvidence Evidence);

    private sealed record RankedWorkspace(
        WorkspaceContext Workspace,
        int Score,
        IReadOnlyList<SessionWorkspaceEvidence> EvidenceItems);
}
