using System.IO;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Plans;

// Joins discovered plan artifacts and activities to the final, merged session
// catalog. Binding is conservative: a plan is bound only on explicit path,
// explicit session directory, or a unique structured-content match. Conflicting
// evidence leaves the plan an orphan rather than guessing.
public sealed class SessionPlanCatalogAssembler
{
    private readonly SessionPlanContentNormalizer _normalizer = new();
    private readonly SessionPlanRevisionBuilder _revisionBuilder = new();

    public IReadOnlyList<SessionPlanArtifact> Assemble(
        IReadOnlyList<SessionCatalogEntry> sessions,
        SessionPlanCatalogFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(fragment);

        if (fragment.IsEmpty)
        {
            return [];
        }

        Dictionary<string, SessionCatalogEntry> sessionsById =
            new(StringComparer.Ordinal);
        foreach (SessionCatalogEntry session in sessions)
        {
            sessionsById[session.CatalogSessionId] = session;
        }

        IReadOnlyList<SessionPlanArtifact> merged = MergeArtifacts(fragment.Artifacts);
        Dictionary<string, List<SessionPlanActivity>> byArtifact =
            new(StringComparer.Ordinal);
        Dictionary<string, List<SessionPlanActivity>> sessionScopedContent =
            new(StringComparer.Ordinal);
        foreach (SessionPlanArtifact artifact in merged)
        {
            byArtifact[artifact.CatalogPlanId] = [];
        }

        CorrelateActivities(
            merged,
            fragment.Activities,
            byArtifact,
            sessionScopedContent);

        List<SessionPlanArtifact> bound = [];
        foreach (SessionPlanArtifact artifact in merged)
        {
            bound.Add(ResolveBinding(
                artifact,
                byArtifact[artifact.CatalogPlanId],
                sessionsById));
        }

        // Session-scoped content (Claude ExitPlanMode without a path) is added
        // only when its session owns exactly one plan, so it is never guessed
        // onto the wrong plan.
        Dictionary<string, int> boundPerSession = new(StringComparer.Ordinal);
        foreach (SessionPlanArtifact artifact in bound)
        {
            if (artifact.BoundCatalogSessionId is string sessionId)
            {
                boundPerSession[sessionId] =
                    boundPerSession.GetValueOrDefault(sessionId) + 1;
            }
        }

        List<SessionPlanArtifact> finalized = new(bound.Count);
        foreach (SessionPlanArtifact artifact in bound)
        {
            List<SessionPlanActivity> activities = byArtifact[artifact.CatalogPlanId];
            if (artifact.BoundCatalogSessionId is string sessionId &&
                boundPerSession.GetValueOrDefault(sessionId) == 1 &&
                sessionScopedContent.TryGetValue(
                    sessionId,
                    out List<SessionPlanActivity>? scoped))
            {
                activities = [.. activities, .. scoped];
            }

            finalized.Add(BuildRevisions(artifact, activities));
        }

        return finalized
            .OrderBy(static item => item.Provider)
            .ThenBy(static item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<SessionPlanArtifact> MergeArtifacts(
        IReadOnlyList<SessionPlanArtifact> artifacts)
    {
        Dictionary<string, SessionPlanArtifact> merged = new(StringComparer.Ordinal);
        foreach (SessionPlanArtifact artifact in artifacts)
        {
            if (!merged.TryGetValue(artifact.CatalogPlanId, out SessionPlanArtifact? current))
            {
                merged[artifact.CatalogPlanId] = artifact;
                continue;
            }

            SessionPlanArtifact primary = Prefer(current, artifact);
            SessionPlanArtifact secondary =
                ReferenceEquals(primary, current) ? artifact : current;
            HashSet<string> alternates = new(
                primary.AlternatePaths,
                StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(secondary.PrimaryPath) &&
                !string.Equals(
                    secondary.PrimaryPath,
                    primary.PrimaryPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                alternates.Add(secondary.PrimaryPath);
            }

            foreach (string alternate in secondary.AlternatePaths)
            {
                alternates.Add(alternate);
            }

            merged[artifact.CatalogPlanId] = primary with
            {
                AlternatePaths = [.. alternates],
                Sources = primary.Sources
                    .Concat(secondary.Sources)
                    .DistinctBy(
                        static source => source.Path,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StructuredKey = primary.StructuredKey ?? secondary.StructuredKey,
                SuggestedCatalogSessionId =
                    primary.SuggestedCatalogSessionId ??
                    secondary.SuggestedCatalogSessionId,
                SuggestedBindingReason =
                    primary.SuggestedBindingReason != SessionPlanBindingReason.Unbound
                        ? primary.SuggestedBindingReason
                        : secondary.SuggestedBindingReason
            };
        }

        return [.. merged.Values];
    }

    private static SessionPlanArtifact Prefer(
        SessionPlanArtifact first,
        SessionPlanArtifact second)
    {
        bool firstHasContent = !string.IsNullOrEmpty(first.CurrentMarkdown);
        bool secondHasContent = !string.IsNullOrEmpty(second.CurrentMarkdown);
        if (firstHasContent != secondHasContent)
        {
            return firstHasContent ? first : second;
        }

        return (first.LastModifiedAtUtc ?? DateTimeOffset.MinValue) >=
            (second.LastModifiedAtUtc ?? DateTimeOffset.MinValue)
            ? first
            : second;
    }

    private void CorrelateActivities(
        IReadOnlyList<SessionPlanArtifact> artifacts,
        IReadOnlyList<SessionPlanActivity> activities,
        IDictionary<string, List<SessionPlanActivity>> byArtifact,
        IDictionary<string, List<SessionPlanActivity>> sessionScopedContent)
    {
        Dictionary<string, List<SessionPlanArtifact>> byPath =
            new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<SessionPlanArtifact>> byStructuredKey =
            new(StringComparer.Ordinal);
        foreach (SessionPlanArtifact artifact in artifacts)
        {
            foreach (string path in ArtifactPaths(artifact))
            {
                Add(byPath, NormalizePath(path), artifact);
            }

            if (!string.IsNullOrEmpty(artifact.StructuredKey))
            {
                Add(byStructuredKey, artifact.StructuredKey!, artifact);
            }
        }

        foreach (SessionPlanActivity activity in activities)
        {
            SessionPlanArtifact? target = null;
            if (!string.IsNullOrWhiteSpace(activity.Locator.Path) &&
                byPath.TryGetValue(
                    NormalizePath(activity.Locator.Path!),
                    out List<SessionPlanArtifact>? pathMatches) &&
                pathMatches.Count == 1)
            {
                target = pathMatches[0];
            }
            else if (!string.IsNullOrWhiteSpace(activity.Locator.StructuredKey) &&
                byStructuredKey.TryGetValue(
                    activity.Locator.StructuredKey!,
                    out List<SessionPlanArtifact>? keyMatches) &&
                keyMatches.Count == 1)
            {
                target = keyMatches[0];
            }

            if (target is not null)
            {
                byArtifact[target.CatalogPlanId].Add(activity);
                continue;
            }

            // A path-less content activity (Claude ExitPlanMode) is retained for
            // its session and attached later if that session owns one plan.
            if (activity.Locator.IsEmpty &&
                activity.Content is not null &&
                activity.CatalogSessionId is string sessionId)
            {
                Add(sessionScopedContent, sessionId, activity);
            }
        }
    }

    private SessionPlanArtifact ResolveBinding(
        SessionPlanArtifact artifact,
        IReadOnlyList<SessionPlanActivity> activities,
        IReadOnlyDictionary<string, SessionCatalogEntry> sessionsById)
    {
        List<SessionPlanActivity> ownership = activities
            .Where(activity => IsOwnership(activity) &&
                activity.CatalogSessionId is not null &&
                sessionsById.ContainsKey(activity.CatalogSessionId))
            .ToList();

        HashSet<string> candidateSessions = ownership
            .Select(static activity => activity.CatalogSessionId!)
            .ToHashSet(StringComparer.Ordinal);

        SessionPlanBindingReason reason = SessionPlanBindingReason.Unbound;
        InferenceEvidence evidence = InferenceEvidence.Unavailable;

        if (artifact.SuggestedCatalogSessionId is string suggested &&
            sessionsById.ContainsKey(suggested))
        {
            candidateSessions.Add(suggested);
        }

        if (candidateSessions.Count > 1)
        {
            return artifact with
            {
                BoundCatalogSessionId = null,
                BoundTurnId = null,
                BindingReason = SessionPlanBindingReason.Ambiguous,
                BindingEvidence = InferenceEvidence.Ambiguous,
                Activities = OrderActivities(activities)
            };
        }

        if (candidateSessions.Count == 0)
        {
            return artifact with
            {
                BoundCatalogSessionId = null,
                BoundTurnId = null,
                BindingReason = SessionPlanBindingReason.Unavailable,
                BindingEvidence = InferenceEvidence.Unavailable,
                Activities = OrderActivities(activities)
            };
        }

        string sessionId = candidateSessions.Single();
        (reason, evidence) = BindingStrength(artifact, ownership, sessionId);

        SessionPlanActivity? creator = ownership
            .Where(activity =>
                string.Equals(
                    activity.CatalogSessionId,
                    sessionId,
                    StringComparison.Ordinal) &&
                activity.TurnId is not null)
            .OrderBy(static activity => activity.Kind == SessionPlanActivityKind.Created ? 0 : 1)
            .ThenBy(static activity => activity.TimestampUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(static activity => activity.Order)
            .FirstOrDefault();

        SessionCatalogEntry session = sessionsById[sessionId];
        return artifact with
        {
            BoundCatalogSessionId = sessionId,
            BoundTurnId = creator?.TurnId,
            BindingReason = reason,
            BindingEvidence = evidence,
            Workspace = session.Workspace.Kind == WorkspaceContextKind.Unknown
                ? artifact.Workspace
                : session.Workspace,
            Activities = OrderActivities(activities)
        };
    }

    private static (SessionPlanBindingReason Reason, InferenceEvidence Evidence)
        BindingStrength(
            SessionPlanArtifact artifact,
            IReadOnlyList<SessionPlanActivity> ownership,
            string sessionId)
    {
        bool copilotDirectory =
            artifact.SuggestedCatalogSessionId is string suggested &&
            string.Equals(suggested, sessionId, StringComparison.Ordinal) &&
            artifact.SuggestedBindingReason ==
                SessionPlanBindingReason.CopilotSessionDirectory;
        if (copilotDirectory)
        {
            return (
                SessionPlanBindingReason.CopilotSessionDirectory,
                InferenceEvidence.Observed);
        }

        bool explicitPath = ownership.Any(activity =>
            string.Equals(activity.CatalogSessionId, sessionId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(activity.Locator.Path));
        if (explicitPath)
        {
            return (SessionPlanBindingReason.ExplicitPlanPath, InferenceEvidence.Observed);
        }

        bool structured = ownership.Any(activity =>
            string.Equals(activity.CatalogSessionId, sessionId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(activity.Locator.StructuredKey));
        if (structured)
        {
            return (
                SessionPlanBindingReason.CursorStructuredContentMatch,
                InferenceEvidence.Corroborated);
        }

        return (
            SessionPlanBindingReason.ProviderCreateOrWriteEvent,
            InferenceEvidence.Derived);
    }

    private SessionPlanArtifact BuildRevisions(
        SessionPlanArtifact artifact,
        IReadOnlyList<SessionPlanActivity> activities)
    {
        SessionPlanContentSnapshot? current =
            artifact.RevisionSnapshot ??
            _normalizer.Snapshot(artifact.CurrentMarkdown);
        SessionSourceProvenance? currentProvenance = artifact.Sources.Count > 0
            ? artifact.Sources[0]
            : null;

        SessionPlanRevisionResult result = _revisionBuilder.Build(
            current,
            artifact.LastModifiedAtUtc,
            currentProvenance,
            activities);

        DateTimeOffset? createdAt = result.Revisions.Count > 0
            ? result.Revisions[0].CapturedAtUtc
            : artifact.CreatedAtUtc;

        return artifact with
        {
            Revisions = result.Revisions,
            ObservedUpdateCount = result.ObservedUpdateCount,
            HasIncompleteRevisionHistory = result.HasIncompleteRevisionHistory,
            CreatedAtUtc = artifact.CreatedAtUtc ?? createdAt
        };
    }

    private static IReadOnlyList<SessionPlanActivity> OrderActivities(
        IReadOnlyList<SessionPlanActivity> activities) =>
        activities
            .OrderBy(static activity =>
                activity.TimestampUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(static activity => activity.Order)
            .ToArray();

    private static bool IsOwnership(SessionPlanActivity activity) =>
        activity.Kind is SessionPlanActivityKind.Created
            or SessionPlanActivityKind.Updated ||
        activity.EstablishesOwnership;

    private static IEnumerable<string> ArtifactPaths(SessionPlanArtifact artifact)
    {
        if (!string.IsNullOrWhiteSpace(artifact.PrimaryPath))
        {
            yield return artifact.PrimaryPath!;
        }

        foreach (string path in artifact.AlternatePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return path;
        }
    }

    private static void Add<TKey, TValue>(
        IDictionary<TKey, List<TValue>> map,
        TKey key,
        TValue value)
    {
        if (!map.TryGetValue(key, out List<TValue>? list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(value);
    }
}
