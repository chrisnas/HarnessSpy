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
    private readonly CollapsedWhitespaceTextComparer _responseTextComparer =
        new();

    public SessionCatalogEntry Reconcile(SessionCatalogEntry session)
    {
        if (session.Provider != HookProvider.Cursor)
        {
            return session;
        }

        SessionTurn[]? turns = null;
        for (int index = 0; index < session.Turns.Count; index++)
        {
            SessionTurn turn = session.Turns[index];
            SessionTurn reconciled = ReconcileTurn(turn);
            if (ReferenceEquals(reconciled, turn))
            {
                continue;
            }

            turns ??= session.Turns.ToArray();
            turns[index] = reconciled;
        }

        return turns is null ? session : session with { Turns = turns };
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
        if (canonicalByDesktopId.Count == 0 &&
            droppedTranscriptIds.Count == 0)
        {
            return turn;
        }

        List<SessionEventRecord> survivors =
            new(events.Count - droppedTranscriptIds.Count);
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

        (SessionEventRecord Item, int OriginalIndex)[] sorted = survivors
            .Select((item, index) => (item, index))
            .OrderBy(entry => SortTime(entry.item, transcriptInferredTime))
            .ThenBy(entry => entry.index)
            .ToArray();
        SessionEventRecord[] ordered =
            new SessionEventRecord[sorted.Length];
        for (int position = 0; position < sorted.Length; position++)
        {
            SessionEventRecord item = sorted[position].Item;
            int order = position + 1;
            ordered[position] = item.Order == order
                ? item
                : item with { Order = order };
        }

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

        Dictionary<string, SessionEventRecord> pairsByTranscriptId =
            new(StringComparer.Ordinal);
        CursorToolRequestIndex desktopIndex = new(desktopRequests);

        // Tier 1: exact native tool-call id.
        foreach (SessionEventRecord transcript in transcriptRequests)
        {
            if (string.IsNullOrWhiteSpace(transcript.ToolCallId))
            {
                continue;
            }

            if (desktopIndex.TryTakeByToolCallId(
                    transcript.ToolCallId,
                    out SessionEventRecord? desktop))
            {
                pairsByTranscriptId[transcript.Id] = desktop!;
            }
        }

        // Tier 2: correlation key FIFO (canonical kind / MCP tool / discovery).
        foreach (SessionEventRecord transcript in transcriptRequests)
        {
            if (pairsByTranscriptId.ContainsKey(transcript.Id))
            {
                continue;
            }

            string key = CorrelationKey(transcript);
            if (desktopIndex.TryTakeByCorrelationKey(
                    key,
                    out SessionEventRecord? desktop))
            {
                pairsByTranscriptId[transcript.Id] = desktop!;
            }
        }

        Dictionary<string, DateTimeOffset> pairedTimes =
            new(StringComparer.Ordinal);
        foreach (SessionEventRecord transcript in transcriptRequests)
        {
            if (!pairsByTranscriptId.TryGetValue(
                    transcript.Id,
                    out SessionEventRecord? desktop))
            {
                continue;
            }

            canonicalByDesktopId[desktop.Id] = Merge(desktop, transcript);
            droppedTranscriptIds.Add(transcript.Id);
            if (desktop.TimestampUtc is DateTimeOffset stamp)
            {
                transcriptInferredTime[transcript.Id] = stamp;
                pairedTimes[transcript.Id] = stamp;
            }
        }

        AssignInferredTranscriptTimes(
            events,
            pairedTimes,
            transcriptInferredTime);
    }

    // Transcript rows carry no native clock. To keep transcript-only survivors
    // (thoughts, the closing response, turn_ended) near their real position, a
    // transcript event inherits the Desktop timestamp of the paired tool request
    // that most recently preceded it in transcript order.
    private static void AssignInferredTranscriptTimes(
        IReadOnlyList<SessionEventRecord> events,
        IReadOnlyDictionary<string, DateTimeOffset> pairedTimes,
        Dictionary<string, DateTimeOffset> transcriptInferredTime)
    {
        DateTimeOffset? last = null;
        foreach (SessionEventRecord item in events
            .Where(IsTranscript)
            .OrderBy(item => item.Order))
        {
            if (IsToolRequest(item) &&
                pairedTimes.TryGetValue(
                    item.Id,
                    out DateTimeOffset paired))
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

    private void DropDuplicateResponses(
        IReadOnlyList<SessionEventRecord> events,
        HashSet<string> droppedTranscriptIds)
    {
        HashSet<string> desktopResponseText =
            new(_responseTextComparer);
        foreach (SessionEventRecord item in events)
        {
            if (IsDesktop(item) &&
                IsResponse(item) &&
                !string.IsNullOrWhiteSpace(item.Text))
            {
                desktopResponseText.Add(item.Text);
            }
        }

        if (desktopResponseText.Count == 0)
        {
            return;
        }

        foreach (SessionEventRecord item in events
            .Where(item => IsTranscript(item) && IsResponse(item)))
        {
            if (item.Text is not null &&
                desktopResponseText.Contains(item.Text))
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
}
