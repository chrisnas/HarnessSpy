using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions;

public sealed class SessionCatalogMerger
{
    public IReadOnlyList<SessionCatalogEntry> Merge(
        IEnumerable<SessionCatalogEntry> sessions)
    {
        Dictionary<string, SessionCatalogEntry> merged =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (SessionCatalogEntry session in sessions)
        {
            if (merged.TryGetValue(session.CatalogSessionId, out SessionCatalogEntry? current))
            {
                merged[session.CatalogSessionId] = MergePair(current, session);
            }
            else
            {
                merged[session.CatalogSessionId] = session;
            }
        }

        return merged.Values
            .OrderBy(static session => session.Workspace.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(static session => session.LastActivityAtUtc)
            .ThenBy(static session => session.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SessionCatalogEntry MergePair(
        SessionCatalogEntry first,
        SessionCatalogEntry second)
    {
        Dictionary<string, string?> metadata =
            new(first.Metadata, StringComparer.Ordinal);
        foreach ((string key, string? value) in second.Metadata)
        {
            if (!metadata.ContainsKey(key) || !string.IsNullOrWhiteSpace(value))
            {
                metadata[key] = value;
            }
        }

        SessionCatalogEntry richer = Richness(second) > Richness(first) ? second : first;
        return richer with
        {
            Workspace = first.Workspace.Kind == WorkspaceContextKind.Unknown
                ? second.Workspace
                : first.Workspace,
            Title = PreferredTitle(first, second),
            StartedAtUtc = Minimum(first.StartedAtUtc, second.StartedAtUtc),
            LastActivityAtUtc = Maximum(first.LastActivityAtUtc, second.LastActivityAtUtc),
            Model = second.Model ?? first.Model,
            Mode = second.Mode ?? first.Mode,
            LifecycleState =
                first.LifecycleState == SessionLifecycleState.Open ||
                second.LifecycleState == SessionLifecycleState.Open
                    ? SessionLifecycleState.Open
                    : SessionLifecycleState.Closed,
            LifecycleEvidence = Stronger(first.LifecycleEvidence, second.LifecycleEvidence),
            IsSelectedInHarness = first.IsSelectedInHarness || second.IsSelectedInHarness,
            Files = first.Files
                .Concat(second.Files)
                .DistinctBy(static file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Turns = MergeTurns(first.Turns, second.Turns),
            Metadata = metadata,
            Sources = first.Sources
                .Concat(second.Sources)
                .DistinctBy(
                    static source =>
                        $"{source.Path}|{source.DatabaseKey}|{source.LineNumber}|{source.RecordId}",
                    StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static IReadOnlyList<SessionTurn> MergeTurns(
        IReadOnlyList<SessionTurn> first,
        IReadOnlyList<SessionTurn> second)
    {
        Dictionary<string, SessionTurn> turns =
            first.ToDictionary(static turn => turn.Id, StringComparer.Ordinal);

        foreach (SessionTurn candidate in second)
        {
            if (!turns.TryGetValue(candidate.Id, out SessionTurn? existing))
            {
                turns[candidate.Id] = candidate;
                continue;
            }

            Dictionary<string, SessionEventRecord> events =
                existing.Events.ToDictionary(static item => item.Id, StringComparer.Ordinal);
            foreach (SessionEventRecord item in candidate.Events)
            {
                events.TryAdd(item.Id, item);
            }

            turns[candidate.Id] = existing with
            {
                Prompt = string.IsNullOrWhiteSpace(existing.Prompt)
                    ? candidate.Prompt
                    : existing.Prompt,
                StartedAtUtc = Minimum(existing.StartedAtUtc, candidate.StartedAtUtc),
                EndedAtUtc = Maximum(existing.EndedAtUtc, candidate.EndedAtUtc),
                Evidence = Stronger(existing.Evidence, candidate.Evidence),
                Events = events.Values
                    .OrderBy(static item => item.TimestampUtc ?? DateTimeOffset.MinValue)
                    .ThenBy(static item => item.Order)
                    .ToArray()
            };
        }

        return turns.Values
            .OrderBy(static turn => turn.Number)
            .ThenBy(static turn => turn.StartedAtUtc)
            .ToArray();
    }

    private static int Richness(SessionCatalogEntry session) =>
        session.Turns.Sum(static turn => turn.Events.Count) +
        session.Sources.Count +
        (session.Workspace.Kind == WorkspaceContextKind.Unknown ? 0 : 10);

    private static string PreferredTitle(
        SessionCatalogEntry first,
        SessionCatalogEntry second)
    {
        bool firstIsId = string.Equals(
            first.Title,
            first.NativeSessionId,
            StringComparison.OrdinalIgnoreCase);
        bool secondIsId = string.Equals(
            second.Title,
            second.NativeSessionId,
            StringComparison.OrdinalIgnoreCase);

        if (firstIsId && !secondIsId)
        {
            return second.Title;
        }

        return first.Title;
    }

    private static DateTimeOffset? Minimum(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second :
        second is null ? first :
        first < second ? first : second;

    private static DateTimeOffset? Maximum(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second :
        second is null ? first :
        first > second ? first : second;

    private static InferenceEvidence Stronger(
        InferenceEvidence first,
        InferenceEvidence second)
    {
        static int Rank(InferenceEvidence evidence) => evidence switch
        {
            InferenceEvidence.Observed => 6,
            InferenceEvidence.Corroborated => 5,
            InferenceEvidence.Derived => 4,
            InferenceEvidence.Heuristic => 3,
            InferenceEvidence.Opaque => 2,
            InferenceEvidence.Ambiguous => 1,
            _ => 0
        };

        return Rank(second) > Rank(first) ? second : first;
    }
}
