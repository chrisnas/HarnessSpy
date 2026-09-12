using System.Threading.Channels;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Core.Sessions.Cursor;

/// <summary>
/// Combines Cursor's passive JSONL transcripts with the richer read-only
/// Cursor Desktop catalog. Entries that share a native Composer id use the
/// stable <c>cursor:&lt;nativeId&gt;</c> catalog identity.
/// </summary>
public sealed class CursorSessionCatalogSource : IStreamingSessionCatalogSource
{
    private readonly CursorTranscriptSessionCatalogSource _transcripts;
    private readonly CursorDesktopSessionCatalogSource _desktop;
    private readonly CursorPlanCatalogSource _planSource;
    private readonly CursorPlanActivityExtractor _planActivityExtractor = new();
    private readonly SessionCatalogMerger _merger = new();
    private readonly CursorJsonReader _json = new();
    private readonly IReadOnlyList<string> _watchRoots;

    public CursorSessionCatalogSource()
        : this(new SessionDiscoveryContext())
    {
    }

    public CursorSessionCatalogSource(SessionDiscoveryContext context)
        : this(context, new WorkspaceNormalizer())
    {
    }

    public CursorSessionCatalogSource(
        SessionDiscoveryContext context,
        WorkspaceNormalizer workspaceNormalizer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workspaceNormalizer);

        _transcripts = new CursorTranscriptSessionCatalogSource(
            context,
            workspaceNormalizer);
        _desktop = new CursorDesktopSessionCatalogSource(
            context,
            workspaceNormalizer);
        _planSource = new CursorPlanCatalogSource(context);
        _watchRoots = _transcripts.WatchRoots
            .Concat(_desktop.WatchRoots)
            .Append(_planSource.PlansRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string Name => "Cursor local sessions";

    public HookProvider Provider => HookProvider.Cursor;

    public IReadOnlyList<string> WatchRoots => _watchRoots;

    public Task<SessionCatalogScanResult> ScanAsync(
        CancellationToken cancellationToken) =>
        ScanAsync(progress: null, cancellationToken);

    public async Task<SessionCatalogScanResult> ScanAsync(
        IProgress<IReadOnlyList<SessionCatalogEntry>>? progress,
        CancellationToken cancellationToken)
    {
        ProgressiveCursorReporter? cursorProgress =
            progress is null
                ? null
                : new ProgressiveCursorReporter(
                    progress,
                    _merger,
                    cancellationToken);

        Task<SessionCatalogScanResult> transcriptScan =
            _transcripts.ScanAsync(cancellationToken);
        Task<SessionCatalogScanResult> desktopScan =
            _desktop.ScanAsync(
                readBubbles: true,
                cursorProgress,
                cancellationToken);
        Task<SessionPlanScanResult> planScan =
            _planSource.ScanAsync(cancellationToken);
        try
        {
            await Task.WhenAll(transcriptScan, desktopScan)
                .ConfigureAwait(false);
            if (cursorProgress is not null)
            {
                await ReportTranscriptSessionsAsync(
                    transcriptScan,
                    cursorProgress).ConfigureAwait(false);
            }
        }
        finally
        {
            if (cursorProgress is not null)
            {
                await cursorProgress.CompleteAsync().ConfigureAwait(false);
            }
        }

        SessionCatalogScanResult transcriptResult =
            await transcriptScan.ConfigureAwait(false);
        SessionCatalogScanResult desktopResult =
            await desktopScan.ConfigureAwait(false);
        IReadOnlyList<SessionCatalogEntry> normalizedTranscripts =
            NormalizeTranscriptCatalogIds(
                transcriptResult.Sessions,
                desktopResult.Sessions);
        IReadOnlyList<SessionCatalogEntry> merged = _merger.Merge(
            desktopResult.Sessions.Concat(normalizedTranscripts));
        IReadOnlyList<CursorSideChatRelationship> sideChats = merged
            .Select(SideChatRelationship)
            .Where(relationship => relationship is not null)
            .Cast<CursorSideChatRelationship>()
            .ToArray();
        SessionCatalogEntry[] reconciled = merged
            .Select(session => ReconcileTurns(session, sideChats))
            .OrderBy(
                session => session.Workspace.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(session => session.LastActivityAtUtc)
            .ThenBy(session => session.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] warnings = transcriptResult.Warnings
            .Concat(desktopResult.Warnings)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Cursor's global store keeps a record for every composer ever opened,
        // including many empty "new chat" shells that were never associated
        // with a workspace. Drop those empty, workspace-less entries as noise
        // while keeping real orphaned conversations (which still have content).
        SessionCatalogEntry[] visible = reconciled
            .Where(session => !IsEmptyOrphan(session))
            .ToArray();

        SessionPlanScanResult planResult =
            await planScan.ConfigureAwait(false);
        IReadOnlyList<SessionPlanActivity> planActivities =
            _planActivityExtractor.Extract(visible);
        SessionPlanCatalogFragment planFragment = new()
        {
            Artifacts = planResult.Fragment.Artifacts,
            Activities = planActivities
        };
        string[] combinedWarnings = warnings
            .Concat(planResult.Warnings)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new SessionCatalogScanResult(
            visible,
            transcriptResult.IsComplete &&
                desktopResult.IsComplete &&
                planResult.IsComplete,
            combinedWarnings)
        {
            PlanFragment = planFragment
        };
    }

    private static async Task ReportTranscriptSessionsAsync(
        Task<SessionCatalogScanResult> transcriptScan,
        IProgress<SessionCatalogEntry> progress)
    {
        SessionCatalogScanResult result =
            await transcriptScan.ConfigureAwait(false);
        foreach (SessionCatalogEntry session in result.Sessions
                     .OrderBy(
                         static item => item.Workspace.DisplayName,
                         StringComparer.OrdinalIgnoreCase)
                     .ThenByDescending(
                         static item => item.LastActivityAtUtc)
                     .ThenBy(
                         static item => item.Title,
                         StringComparer.OrdinalIgnoreCase))
        {
            progress.Report(session);
        }
    }

    private static bool IsEmptyOrphan(SessionCatalogEntry session) =>
        session.Turns.Count == 0 &&
        session.Workspace.Kind != WorkspaceContextKind.Normal;

    // Receives genuinely enriched Composer sessions from the SQLite reader as
    // soon as each session's bubbles have been parsed. Small growing batches
    // replace their skeleton counterparts in place; the final source result
    // still performs cross-source/side-chat reconciliation authoritatively.
    private sealed class ProgressiveCursorReporter
        : IProgress<SessionCatalogEntry>
    {
        // Sessions arrive faster than the UI can project them, so snapshots are
        // published on a fixed cadence: each emit drains everything discovered
        // since the last one. This keeps the tree filling visibly without
        // flooding the pipeline with hundreds of tiny updates.
        private static readonly TimeSpan ProgressInterval =
            TimeSpan.FromMilliseconds(120);

        private readonly Channel<SessionCatalogEntry> _pending =
            Channel.CreateUnbounded<SessionCatalogEntry>(
                new UnboundedChannelOptions
                {
                    SingleReader = true
                });
        private readonly Dictionary<string, SessionCatalogEntry> _sessions =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly SessionCatalogMerger _merger;
        private readonly Task _pumpTask;

        public ProgressiveCursorReporter(
            IProgress<IReadOnlyList<SessionCatalogEntry>> progress,
            SessionCatalogMerger merger,
            CancellationToken cancellationToken)
        {
            _merger = merger;
            _pumpTask = PumpAsync(progress, cancellationToken);
        }

        public void Report(SessionCatalogEntry value)
        {
            _pending.Writer.TryWrite(value);
        }

        public async Task CompleteAsync()
        {
            _pending.Writer.TryComplete();
            await _pumpTask.ConfigureAwait(false);
        }

        private async Task PumpAsync(
            IProgress<IReadOnlyList<SessionCatalogEntry>> progress,
            CancellationToken cancellationToken)
        {
            bool dirty = false;
            while (await _pending.Reader
                .WaitToReadAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                while (_pending.Reader.TryRead(out SessionCatalogEntry? value))
                {
                    Absorb(value);
                    dirty = true;
                }

                if (dirty)
                {
                    Publish(progress);
                    dirty = false;
                }

                await Task.Delay(ProgressInterval, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Emit anything that arrived during the final delay window.
            while (_pending.Reader.TryRead(out SessionCatalogEntry? value))
            {
                Absorb(value);
                dirty = true;
            }

            if (dirty)
            {
                Publish(progress);
            }
        }

        private void Absorb(SessionCatalogEntry value)
        {
            _sessions[value.CatalogSessionId] =
                _sessions.TryGetValue(
                    value.CatalogSessionId,
                    out SessionCatalogEntry? current)
                    ? _merger.Merge([current, value]).Single()
                    : value;
        }

        private void Publish(
            IProgress<IReadOnlyList<SessionCatalogEntry>> progress)
        {
            progress.Report(
                _sessions.Values
                    .OrderBy(
                        static session => session.Workspace.DisplayName,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(
                        static session => session.LastActivityAtUtc)
                    .ThenBy(
                        static session => session.Title,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        }
    }

    private IReadOnlyList<SessionCatalogEntry> NormalizeTranscriptCatalogIds(
        IReadOnlyList<SessionCatalogEntry> transcripts,
        IReadOnlyList<SessionCatalogEntry> desktopSessions)
    {
        ILookup<string, SessionCatalogEntry> desktopByNativeId =
            desktopSessions.ToLookup(
                session => session.NativeSessionId,
                StringComparer.OrdinalIgnoreCase);
        ILookup<string, SessionCatalogEntry> transcriptsByNativeId =
            transcripts.ToLookup(
                session => session.NativeSessionId,
                StringComparer.OrdinalIgnoreCase);

        List<SessionCatalogEntry> normalized = [];
        foreach (SessionCatalogEntry transcript in transcripts)
        {
            SessionCatalogEntry[] desktopMatches =
                desktopByNativeId[transcript.NativeSessionId].ToArray();
            if (desktopMatches.Length == 0)
            {
                normalized.Add(transcript);
                continue;
            }

            bool workspaceMatch = desktopMatches.Any(desktop =>
                transcript.Workspace.Kind != WorkspaceContextKind.Unknown &&
                desktop.Workspace.Kind != WorkspaceContextKind.Unknown &&
                string.Equals(
                    transcript.Workspace.Key,
                    desktop.Workspace.Key,
                    StringComparison.OrdinalIgnoreCase));
            bool unambiguous = desktopMatches.Length == 1 &&
                transcriptsByNativeId[transcript.NativeSessionId].Count() == 1;
            normalized.Add(workspaceMatch || unambiguous
                ? transcript with
                {
                    CatalogSessionId = $"cursor:{transcript.NativeSessionId}"
                }
                : transcript);
        }

        return normalized;
    }

    private SessionCatalogEntry ReconcileTurns(
        SessionCatalogEntry session,
        IReadOnlyList<CursorSideChatRelationship> sideChats)
    {
        List<SessionTurn> turns = session.Turns.ToList();
        RemoveInheritedSideChatTranscriptTurns(session, turns);
        Dictionary<string, string?> metadata = new(
            session.Metadata,
            StringComparer.Ordinal);
        RemoveInheritedChildTurns(session, sideChats, turns, metadata);

        List<SessionTurn> consolidated = [];
        foreach (IGrouping<(int Number, string Prompt), SessionTurn> group in turns
            .GroupBy(turn => (
                turn.Number,
                _json.NormalizeUserPrompt(turn.Prompt))))
        {
            SessionTurn[] candidates = group.ToArray();
            if (candidates.Length == 1 ||
                string.IsNullOrWhiteSpace(group.Key.Prompt))
            {
                consolidated.AddRange(candidates);
                continue;
            }

            SessionTurn preferred = candidates
                .OrderByDescending(turn => turn.Id.Contains(
                    ":request:",
                    StringComparison.Ordinal))
                .ThenByDescending(turn => turn.Events.Count)
                .First();
            Dictionary<string, SessionEventRecord> events =
                new(StringComparer.Ordinal);
            foreach (SessionTurn candidate in candidates)
            {
                foreach (SessionEventRecord item in candidate.Events)
                {
                    events.TryAdd(
                        item.Id,
                        item.TurnId == preferred.Id
                            ? item
                            : item with { TurnId = preferred.Id });
                }
            }

            consolidated.Add(preferred with
            {
                StartedAtUtc = candidates
                    .Select(turn => turn.StartedAtUtc)
                    .Where(value => value is not null)
                    .Min(),
                EndedAtUtc = candidates
                    .Select(turn => turn.EndedAtUtc)
                    .Where(value => value is not null)
                    .Max(),
                Evidence = Strongest(candidates.Select(turn => turn.Evidence)),
                Events = events.Values
                    .OrderBy(item => item.TimestampUtc ?? DateTimeOffset.MinValue)
                    .ThenBy(item => item.Order)
                    .ToArray()
            });
        }

        SessionTurn[] ordered = consolidated
            .OrderBy(turn => turn.Number)
            .ThenBy(turn => turn.StartedAtUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(turn => turn.Id, StringComparer.Ordinal)
            .Select((turn, index) => turn with { Number = index + 1 })
            .ToArray();
        return session with
        {
            Turns = ordered,
            Metadata = metadata
        };
    }

    private void RemoveInheritedSideChatTranscriptTurns(
        SessionCatalogEntry session,
        List<SessionTurn> turns)
    {
        if (!session.Metadata.TryGetValue(
                "cursor.subagentType",
                out string? subagentType) ||
            !string.Equals(
                subagentType,
                "side-chat",
                StringComparison.OrdinalIgnoreCase) ||
            !session.Metadata.TryGetValue(
                "cursor.sideChatSeedTurnCount",
                out string? rawCount) ||
            !int.TryParse(rawCount, out int seedCount) ||
            seedCount <= 0)
        {
            return;
        }

        SessionTurn[] transcriptOnly = turns
            .Where(turn =>
                turn.Events.Count > 0 &&
                turn.Events.All(item =>
                    item.Provenance.SourceKind ==
                    SessionSourceKind.CursorTranscriptJsonl))
            .OrderBy(turn => turn.Number)
            .Take(seedCount)
            .ToArray();
        foreach (SessionTurn inherited in transcriptOnly)
        {
            turns.Remove(inherited);
        }
    }

    private void RemoveInheritedChildTurns(
        SessionCatalogEntry session,
        IReadOnlyList<CursorSideChatRelationship> sideChats,
        List<SessionTurn> turns,
        Dictionary<string, string?> metadata)
    {
        foreach (CursorSideChatRelationship relationship in sideChats.Where(
            item => string.Equals(
                item.ParentComposerId,
                session.NativeSessionId,
                StringComparison.Ordinal)))
        {
            SessionTurn[] inherited = turns
                .Where(turn =>
                    turn.Events.Count > 0 &&
                    turn.Events.All(item => string.Equals(
                        item.AgentId,
                        relationship.ChildComposerId,
                        StringComparison.Ordinal)))
                .OrderBy(turn => turn.Number)
                .Take(relationship.SeedTurnCount)
                .ToArray();
            foreach (SessionTurn turn in inherited)
            {
                turns.Remove(turn);
            }

            string key = MetadataKey(relationship.ChildComposerId);
            metadata[$"cursor.sideChat.{key}.parentComposerId"] =
                relationship.ParentComposerId;
            metadata[$"cursor.sideChat.{key}.seedTurnCount"] =
                relationship.SeedTurnCount.ToString();
            metadata[$"cursor.sideChat.{key}.skippedInheritedTurns"] =
                inherited.Length.ToString();
        }
    }

    private CursorSideChatRelationship? SideChatRelationship(
        SessionCatalogEntry session)
    {
        if (!session.Metadata.TryGetValue(
                "cursor.subagentType",
                out string? type) ||
            !string.Equals(type, "side-chat", StringComparison.OrdinalIgnoreCase) ||
            !session.Metadata.TryGetValue(
                "cursor.parentComposerId",
                out string? parentComposerId) ||
            string.IsNullOrWhiteSpace(parentComposerId))
        {
            return null;
        }

        int seedCount = 0;
        if (session.Metadata.TryGetValue(
                "cursor.sideChatSeedTurnCount",
                out string? rawSeedCount) &&
            int.TryParse(rawSeedCount, out int parsedSeedCount) &&
            parsedSeedCount > 0)
        {
            seedCount = parsedSeedCount;
        }

        return new CursorSideChatRelationship(
            session.NativeSessionId,
            parentComposerId,
            seedCount);
    }

    private string MetadataKey(string value)
    {
        char[] characters = value
            .Select(character => char.IsLetterOrDigit(character) ||
                character is '-' or '_'
                    ? character
                    : '_')
            .ToArray();
        return new string(characters);
    }

    private InferenceEvidence Strongest(
        IEnumerable<InferenceEvidence> values)
    {
        InferenceEvidence strongest = InferenceEvidence.Unavailable;
        int strongestRank = 0;
        foreach (InferenceEvidence value in values)
        {
            int rank = value switch
            {
                InferenceEvidence.Observed => 7,
                InferenceEvidence.Corroborated => 6,
                InferenceEvidence.Derived => 5,
                InferenceEvidence.Heuristic => 4,
                InferenceEvidence.Opaque => 3,
                InferenceEvidence.Ambiguous => 2,
                _ => 1
            };
            if (rank > strongestRank)
            {
                strongest = value;
                strongestRank = rank;
            }
        }

        return strongest;
    }

    private sealed record CursorSideChatRelationship(
        string ChildComposerId,
        string ParentComposerId,
        int SeedTurnCount);
}
