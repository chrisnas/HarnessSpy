using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes.Cursor;

namespace HarnessSpy.Core.Sessions.Cursor;

// Collapses the duplicate view Cursor produces when a session is described by
// both its agent transcript and Cursor Desktop's SQLite store. Each logical
// tool call surfaces once from each source; without reconciliation a turn shows
// every request twice (e.g. transcript "Shell" plus Desktop
// "run_terminal_command_v2"). This reconciler pairs those records within a turn
// and emits a single canonical event that keeps Desktop's execution metadata
// (timestamp, status, duration, result binding) while adopting the transcript's
// agent-facing name, arguments, and parallel-step grouping. Both origins are
// retained as provenance so nothing becomes unauditable.
internal sealed class CursorSessionEventReconciler
{
    public SessionCatalogEntry Reconcile(SessionCatalogEntry session)
    {
        if (session.Provider != HookProvider.Cursor)
        {
            return session;
        }

        bool changed = false;
        List<SessionTurn> turns = [];
        foreach (SessionTurn turn in session.Turns)
        {
            SessionTurn reconciled = ReconcileTurn(turn);
            changed |= !ReferenceEquals(reconciled, turn);
            turns.Add(reconciled);
        }

        return changed ? session with { Turns = turns } : session;
    }

    private SessionTurn ReconcileTurn(SessionTurn turn)
    {
        IReadOnlyList<SessionEventRecord> events = turn.Events;
        bool hasTranscript = events.Any(IsTranscript);
        bool hasDesktop = events.Any(IsDesktop);
        if (!hasTranscript || !hasDesktop)
        {
            // A single-source turn has nothing to collapse.
            return turn;
        }

        Dictionary<string, SessionEventRecord> canonicalByDesktopId =
            new(StringComparer.Ordinal);
        HashSet<string> droppedTranscriptIds = new(StringComparer.Ordinal);
        Dictionary<string, DateTimeOffset> transcriptInferredTime =
            new(StringComparer.Ordinal);

        PairToolRequests(
            events,
            canonicalByDesktopId,
            droppedTranscriptIds,
            transcriptInferredTime);
        DropDuplicateResponses(events, droppedTranscriptIds);

        List<SessionEventRecord> survivors = [];
        foreach (SessionEventRecord item in events)
        {
            if (IsTranscript(item) && droppedTranscriptIds.Contains(item.Id))
            {
                continue;
            }

            survivors.Add(
                canonicalByDesktopId.TryGetValue(item.Id, out SessionEventRecord? canonical)
                    ? canonical
                    : item);
        }

        SessionEventRecord[] ordered = survivors
            .Select((item, index) => (item, index))
            .OrderBy(entry => SortTime(entry.item, transcriptInferredTime))
            .ThenBy(entry => entry.index)
            .Select((entry, position) => entry.item with { Order = position + 1 })
            .ToArray();

        return turn with { Events = ordered };
    }

    private static void PairToolRequests(
        IReadOnlyList<SessionEventRecord> events,
        Dictionary<string, SessionEventRecord> canonicalByDesktopId,
        HashSet<string> droppedTranscriptIds,
        Dictionary<string, DateTimeOffset> transcriptInferredTime)
    {
        List<SessionEventRecord> transcriptRequests = events
            .Where(item => IsTranscript(item) && IsToolRequest(item))
            .OrderBy(item => item.Order)
            .ToList();
        List<SessionEventRecord> desktopRequests = events
            .Where(item => IsDesktop(item) && IsToolRequest(item))
            .OrderBy(item => item.Order)
            .ToList();
        if (transcriptRequests.Count == 0 || desktopRequests.Count == 0)
        {
            return;
        }

        Dictionary<SessionEventRecord, SessionEventRecord> pairs = new();
        HashSet<SessionEventRecord> usedDesktop = [];

        // Tier 1: exact native tool-call id.
        foreach (SessionEventRecord transcript in transcriptRequests)
        {
            if (string.IsNullOrWhiteSpace(transcript.ToolCallId))
            {
                continue;
            }

            SessionEventRecord? desktop = desktopRequests.FirstOrDefault(candidate =>
                !usedDesktop.Contains(candidate) &&
                string.Equals(
                    candidate.ToolCallId,
                    transcript.ToolCallId,
                    StringComparison.Ordinal));
            if (desktop is not null)
            {
                pairs[transcript] = desktop;
                usedDesktop.Add(desktop);
            }
        }

        // Tier 2: correlation key FIFO (canonical kind / MCP tool / discovery).
        foreach (SessionEventRecord transcript in transcriptRequests)
        {
            if (pairs.ContainsKey(transcript))
            {
                continue;
            }

            string key = CorrelationKey(transcript);
            SessionEventRecord? desktop = desktopRequests.FirstOrDefault(candidate =>
                !usedDesktop.Contains(candidate) &&
                string.Equals(CorrelationKey(candidate), key, StringComparison.Ordinal));
            if (desktop is not null)
            {
                pairs[transcript] = desktop;
                usedDesktop.Add(desktop);
            }
        }

        foreach ((SessionEventRecord transcript, SessionEventRecord desktop) in pairs)
        {
            canonicalByDesktopId[desktop.Id] = Merge(desktop, transcript);
            droppedTranscriptIds.Add(transcript.Id);
            if (desktop.TimestampUtc is DateTimeOffset stamp)
            {
                transcriptInferredTime[transcript.Id] = stamp;
            }
        }

        AssignInferredTranscriptTimes(events, pairs, transcriptInferredTime);
    }

    // Transcript rows carry no native clock. To keep transcript-only survivors
    // (thoughts, the closing response, turn_ended) near their real position, a
    // transcript event inherits the Desktop timestamp of the paired tool request
    // that most recently preceded it in transcript order.
    private static void AssignInferredTranscriptTimes(
        IReadOnlyList<SessionEventRecord> events,
        IReadOnlyDictionary<SessionEventRecord, SessionEventRecord> pairs,
        Dictionary<string, DateTimeOffset> transcriptInferredTime)
    {
        DateTimeOffset? last = null;
        foreach (SessionEventRecord item in events
            .Where(IsTranscript)
            .OrderBy(item => item.Order))
        {
            if (IsToolRequest(item) &&
                pairs.TryGetValue(item, out SessionEventRecord? desktop) &&
                desktop.TimestampUtc is DateTimeOffset paired)
            {
                last = paired;
            }

            if (last is DateTimeOffset carry &&
                !transcriptInferredTime.ContainsKey(item.Id))
            {
                transcriptInferredTime[item.Id] = carry;
            }
        }
    }

    private static void DropDuplicateResponses(
        IReadOnlyList<SessionEventRecord> events,
        HashSet<string> droppedTranscriptIds)
    {
        HashSet<string> desktopResponseText = events
            .Where(item => IsDesktop(item) && IsResponse(item))
            .Select(item => NormalizeText(item.Text))
            .Where(text => text.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (desktopResponseText.Count == 0)
        {
            return;
        }

        foreach (SessionEventRecord item in events
            .Where(item => IsTranscript(item) && IsResponse(item)))
        {
            if (desktopResponseText.Contains(NormalizeText(item.Text)))
            {
                droppedTranscriptIds.Add(item.Id);
            }
        }
    }

    private static SessionEventRecord Merge(
        SessionEventRecord desktop,
        SessionEventRecord transcript)
    {
        bool transcriptIsMcp = CursorToolSemantics.IsMcpExecution(
            transcript.ToolKind,
            transcript.McpToolName ?? transcript.ToolName ?? transcript.NativeName,
            transcript.McpServerName);

        return desktop with
        {
            NativeName = transcript.NativeName,
            ToolName = transcript.ToolName ?? desktop.ToolName,
            ToolKind = transcriptIsMcp || desktop.ToolKind == CanonicalToolKind.Unknown
                ? transcript.ToolKind
                : desktop.ToolKind,
            McpServerName = transcript.McpServerName ?? desktop.McpServerName,
            McpToolName = transcript.McpToolName ?? desktop.McpToolName,
            Tone = transcript.Tone == ObservationTone.Mcp ? ObservationTone.Mcp : desktop.Tone,
            AssistantStepId = transcript.AssistantStepId ?? desktop.AssistantStepId,
            ParallelGroupId = transcript.ParallelGroupId ?? desktop.ParallelGroupId,
            IsParallelCandidate =
                transcript.IsParallelCandidate || desktop.IsParallelCandidate,
            TargetPaths = transcript.TargetPaths.Count > 0
                ? transcript.TargetPaths
                : desktop.TargetPaths,
            Text = string.IsNullOrWhiteSpace(desktop.Text) ? transcript.Text : desktop.Text,
            Evidence = InferenceEvidence.Corroborated,
            SupplementalProvenance =
                [.. desktop.SupplementalProvenance, transcript.Provenance]
        };
    }

    private static DateTimeOffset SortTime(
        SessionEventRecord item,
        IReadOnlyDictionary<string, DateTimeOffset> transcriptInferredTime)
    {
        if (item.TimestampUtc is DateTimeOffset stamp)
        {
            return stamp;
        }

        return transcriptInferredTime.TryGetValue(item.Id, out DateTimeOffset inferred)
            ? inferred
            : DateTimeOffset.MinValue;
    }

    private static string CorrelationKey(SessionEventRecord item) =>
        CursorToolSemantics.CorrelationKey(
            item.ToolKind,
            item.ToolName ?? item.NativeName,
            item.McpServerName,
            item.McpToolName);

    private static bool IsTranscript(SessionEventRecord item) =>
        item.Provenance.SourceKind == SessionSourceKind.CursorTranscriptJsonl;

    private static bool IsDesktop(SessionEventRecord item) =>
        item.Provenance.SourceKind == SessionSourceKind.CursorDesktopSqlite;

    private static bool IsToolRequest(SessionEventRecord item) =>
        item.Role == ObservationRole.ToolRequest ||
        item.EventKind == CanonicalEventKind.ToolRequested;

    private static bool IsResponse(SessionEventRecord item) =>
        item.Role == ObservationRole.AgentResponse ||
        item.EventKind == CanonicalEventKind.AssistantMessage;

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
