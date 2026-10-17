using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Core.Sessions.Claude;

// Recovers Claude plans whose provider-owned markdown was deleted after the
// transcript recorded a successful write/approval and its exact plan path.
public sealed class ClaudeTranscriptPlanArtifactBuilder
{
    public IReadOnlyList<SessionPlanArtifact> Build(
        IReadOnlyList<SessionCatalogEntry> sessions,
        IReadOnlyList<SessionPlanArtifact> fileArtifacts,
        IReadOnlyList<SessionPlanActivity> activities)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(fileArtifacts);
        ArgumentNullException.ThrowIfNull(activities);

        Dictionary<string, SessionCatalogEntry> sessionsById = sessions
            .GroupBy(
                static session => session.CatalogSessionId,
                StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.Ordinal);
        HashSet<string> existingPaths = fileArtifacts
            .SelectMany(ArtifactPaths)
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> existingIds = fileArtifacts
            .Select(static artifact => artifact.CatalogPlanId)
            .ToHashSet(StringComparer.Ordinal);
        SessionPlanActivity[] claudeActivities = activities
            .Where(static activity =>
                activity.Provider == HookProvider.ClaudeCode)
            .ToArray();
        Dictionary<string, int> pathCountBySession = claudeActivities
            .Where(static activity =>
                activity.CatalogSessionId is not null &&
                !string.IsNullOrWhiteSpace(activity.Locator.Path))
            .GroupBy(
                static activity => activity.CatalogSessionId!,
                StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                group => group
                    .Select(activity =>
                        NormalizePath(activity.Locator.Path!))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                StringComparer.Ordinal);

        List<SessionPlanArtifact> recovered = [];
        foreach (IGrouping<string, SessionPlanActivity> pathGroup in
                 claudeActivities
                     .Where(static activity =>
                         !string.IsNullOrWhiteSpace(activity.Locator.Path))
                     .GroupBy(
                         activity => NormalizePath(activity.Locator.Path!),
                         StringComparer.OrdinalIgnoreCase))
        {
            string path = pathGroup.Key;
            string stem = Path.GetFileNameWithoutExtension(path);
            string planId = SessionPlanIdentity.Create(
                HookProvider.ClaudeCode,
                stem);
            if (existingPaths.Contains(path))
            {
                continue;
            }

            bool hasCurrentFileArtifact = existingIds.Contains(planId);
            SessionPlanActivity[] pathActivities = pathGroup.ToArray();
            HashSet<string> pathSessionIds = pathActivities
                .Where(static activity =>
                    activity.CatalogSessionId is not null)
                .Select(static activity => activity.CatalogSessionId!)
                .ToHashSet(StringComparer.Ordinal);
            SessionPlanActivity[] scopedActivities = claudeActivities
                .Where(activity =>
                    activity.Locator.IsEmpty &&
                    activity.CatalogSessionId is string sessionId &&
                    pathSessionIds.Contains(sessionId) &&
                    pathCountBySession.GetValueOrDefault(sessionId) == 1)
                .ToArray();
            SessionPlanActivity[] proof =
                [.. pathActivities, .. scopedActivities];
            if (!proof.Any(static activity =>
                    activity.IsSuccessful &&
                    (activity.Kind is
                        SessionPlanActivityKind.Created or
                        SessionPlanActivityKind.Updated ||
                     activity.ConfirmsArtifactExistence)))
            {
                continue;
            }

            SessionPlanActivity? latestContent = proof
                .Where(static activity =>
                    activity.IsSuccessful &&
                    activity.Content is not null)
                .OrderByDescending(static activity =>
                    activity.TimestampUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(static activity => activity.Order)
                .FirstOrDefault();
            SessionPlanContentSnapshot? content = latestContent?.Content;
            SessionCatalogEntry[] owners = pathSessionIds
                .Where(sessionsById.ContainsKey)
                .Select(sessionId => sessionsById[sessionId])
                .DistinctBy(
                    static session => session.CatalogSessionId,
                    StringComparer.Ordinal)
                .ToArray();
            WorkspaceContext[] ownerWorkspaces = owners
                .Select(static owner => owner.Workspace)
                .Where(static candidate =>
                    candidate.Kind == WorkspaceContextKind.Normal)
                .DistinctBy(
                    static candidate => candidate.Key,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            WorkspaceContext workspace = ownerWorkspaces.Length == 1
                ? ownerWorkspaces[0]
                : WorkspaceContext.Unknown;
            SessionPlanActivity[] existenceEvidence = proof
                .Where(static activity =>
                    activity.IsSuccessful &&
                    (activity.Kind is
                        SessionPlanActivityKind.Created or
                        SessionPlanActivityKind.Updated ||
                     activity.ConfirmsArtifactExistence))
                .ToArray();
            DateTimeOffset? createdAt = existenceEvidence
                .Select(static activity => activity.TimestampUtc)
                .Where(static timestamp => timestamp is not null)
                .Min();
            DateTimeOffset? modifiedAt = existenceEvidence
                .Select(static activity => activity.TimestampUtc)
                .Where(static timestamp => timestamp is not null)
                .Max();
            SessionSourceProvenance[] sources = proof
                .Select(static activity => activity.Provenance)
                .DistinctBy(
                    static source =>
                        $"{source.Path}|{source.LineNumber}|{source.RecordId}",
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

            recovered.Add(new SessionPlanArtifact
            {
                CatalogPlanId = planId,
                Provider = HookProvider.ClaudeCode,
                Surface = HookSurface.ClaudeCode,
                Title = FirstHeading(content?.NormalizedContent) ?? stem,
                Workspace = workspace,
                PrimaryPath = path,
                CurrentMarkdown = hasCurrentFileArtifact
                    ? null
                    : content?.NormalizedContent,
                CurrentContentHash = hasCurrentFileArtifact
                    ? null
                    : content?.ContentHash,
                RevisionSnapshot = hasCurrentFileArtifact
                    ? null
                    : content,
                CreatedAtUtc = createdAt,
                LastModifiedAtUtc = modifiedAt,
                NativeSessionIdHint = owners.Length == 1
                    ? owners[0].NativeSessionId
                    : null,
                Sources = sources,
                Metadata = new Dictionary<string, string?>(
                    StringComparer.Ordinal)
                {
                    ["claude.plan.slug"] = stem,
                    ["claude.plan.transcriptBacked"] = "true",
                    ["claude.plan.sourceFileMissing"] = "true"
                }
            });
        }

        return recovered;
    }

    private static IEnumerable<string> ArtifactPaths(
        SessionPlanArtifact artifact)
    {
        if (!string.IsNullOrWhiteSpace(artifact.PrimaryPath))
        {
            yield return artifact.PrimaryPath;
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
            return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return path;
        }
    }

    private static string? FirstHeading(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        foreach (string line in content.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                return trimmed[2..].Trim();
            }
        }

        return null;
    }
}
