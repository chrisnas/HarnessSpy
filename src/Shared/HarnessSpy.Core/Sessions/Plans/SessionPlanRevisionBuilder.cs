using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Plans;

public sealed record SessionPlanRevisionResult(
    IReadOnlyList<SessionPlanRevision> Revisions,
    int ObservedUpdateCount,
    bool HasIncompleteRevisionHistory);

// Reconstructs the distinct-content revision timeline of a plan from the current
// file snapshot plus the full-content snapshots carried by successful plan
// activities. The update count excludes the initial content and only counts
// transitions between adjacent distinct normalized contents.
public sealed class SessionPlanRevisionBuilder
{
    public SessionPlanRevisionResult Build(
        SessionPlanContentSnapshot? currentSnapshot,
        DateTimeOffset? currentTimestamp,
        SessionSourceProvenance? currentProvenance,
        IReadOnlyList<SessionPlanActivity> activities)
    {
        ArgumentNullException.ThrowIfNull(activities);

        List<Observation> observations = [];
        bool hasIncompleteHistory = false;

        foreach (SessionPlanActivity activity in activities
                     .Where(static item =>
                         item.Kind != SessionPlanActivityKind.Referenced)
                     .OrderBy(static item =>
                         item.TimestampUtc ?? DateTimeOffset.MinValue)
                     .ThenBy(static item => item.Order))
        {
            if (!activity.IsSuccessful)
            {
                continue;
            }

            if (activity.Content is SessionPlanContentSnapshot content)
            {
                observations.Add(new Observation(
                    content.ContentHash,
                    content.NormalizedContent,
                    activity.TimestampUtc,
                    activity.Order,
                    activity.Kind,
                    activity.CatalogSessionId,
                    activity.TurnId,
                    activity.SourceEventId,
                    activity.Evidence,
                    activity.Provenance,
                    IsMaterialized: true));
            }
            else
            {
                // A successful edit whose resulting body was not exposed proves
                // a change happened but cannot be counted as a distinct content.
                hasIncompleteHistory = true;
            }
        }

        // The current file is the authoritative latest content. Append it last so
        // it associates with (and does not duplicate) the most recent activity.
        if (currentSnapshot is not null)
        {
            observations.Add(new Observation(
                currentSnapshot.ContentHash,
                currentSnapshot.NormalizedContent,
                currentTimestamp,
                int.MaxValue,
                SessionPlanActivityKind.Updated,
                CatalogSessionId: null,
                TurnId: null,
                SourceEventId: null,
                InferenceEvidence.Observed,
                currentProvenance,
                IsMaterialized: true));
        }

        List<Observation> collapsed = CollapseAdjacentDuplicates(observations);
        List<SessionPlanRevision> revisions = new(collapsed.Count);
        for (int index = 0; index < collapsed.Count; index++)
        {
            Observation observation = collapsed[index];
            revisions.Add(new SessionPlanRevision
            {
                Sequence = index,
                Kind = index == 0
                    ? SessionPlanActivityKind.Created
                    : SessionPlanActivityKind.Updated,
                NormalizedContentHash = observation.Hash,
                Content = observation.Content,
                CapturedAtUtc = observation.TimestampUtc,
                Order = observation.Order,
                CatalogSessionId = observation.CatalogSessionId,
                TurnId = observation.TurnId,
                SourceEventId = observation.SourceEventId,
                IsMaterialized = observation.IsMaterialized,
                Evidence = observation.Evidence,
                Provenance = observation.Provenance
            });
        }

        int observedUpdateCount = Math.Max(0, revisions.Count - 1);
        return new SessionPlanRevisionResult(
            revisions,
            observedUpdateCount,
            hasIncompleteHistory);
    }

    private static List<Observation> CollapseAdjacentDuplicates(
        IReadOnlyList<Observation> observations)
    {
        List<Observation> collapsed = [];
        foreach (Observation observation in observations)
        {
            if (collapsed.Count > 0 &&
                string.Equals(
                    collapsed[^1].Hash,
                    observation.Hash,
                    StringComparison.Ordinal))
            {
                // Merge an identical adjacent observation, keeping the earliest
                // originating activity metadata but not counting a new revision.
                continue;
            }

            collapsed.Add(observation);
        }

        return collapsed;
    }

    private sealed record Observation(
        string Hash,
        string? Content,
        DateTimeOffset? TimestampUtc,
        int Order,
        SessionPlanActivityKind Kind,
        string? CatalogSessionId,
        string? TurnId,
        string? SourceEventId,
        InferenceEvidence Evidence,
        SessionSourceProvenance? Provenance,
        bool IsMaterialized);
}
