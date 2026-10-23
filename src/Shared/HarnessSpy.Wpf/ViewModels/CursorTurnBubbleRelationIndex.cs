using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Cursor.Inspection;

namespace HarnessSpy.Wpf.ViewModels;

public sealed record CursorTurnBubbleRelation(
    SessionTurn Turn,
    CursorReconstructionBubble Bubble,
    bool StartsTurn,
    IReadOnlyList<SessionEventRecord> Events)
{
    public string BubbleId =>
        Bubble.Record.Classification.BubbleId ??
        Bubble.Record.Classification.StorageKey ??
        Bubble.Record.Row.DisplayText;

    public string Rule => StartsTurn
        ? "The PromptSubmitted event provenance points to the user bubble " +
            "that starts this turn and supplies SessionTurn.Prompt."
        : "Event provenance points to an assistant bubble that contributes " +
            "thoughts, tools, results, or a response to this turn.";
}

internal sealed class CursorTurnBubbleRelationIndex
{
    private readonly IReadOnlyDictionary<string, CursorReconstructionBubble>
        _bubblesById;
    private readonly IReadOnlyDictionary<string, CursorReconstructionBubble>
        _bubblesByKey;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<
        CursorTurnBubbleRelation>> _byTurn;
    private readonly IReadOnlyDictionary<string, CursorTurnBubbleRelation>
        _byEvent;
    private readonly IReadOnlyDictionary<string, CursorTurnBubbleRelation>
        _byBubble;

    public CursorTurnBubbleRelationIndex(CursorReconstructedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _bubblesById = session.Bubbles
            .Where(bubble =>
                !string.IsNullOrWhiteSpace(
                    bubble.Record.Classification.BubbleId))
            .GroupBy(
                bubble => bubble.Record.Classification.BubbleId!,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        _bubblesByKey = session.Bubbles
            .Where(bubble =>
                !string.IsNullOrWhiteSpace(
                    bubble.Record.Classification.StorageKey))
            .GroupBy(
                bubble => bubble.Record.Classification.StorageKey!,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);

        Dictionary<string, IReadOnlyList<CursorTurnBubbleRelation>> byTurn =
            new(StringComparer.Ordinal);
        Dictionary<string, CursorTurnBubbleRelation> byEvent =
            new(StringComparer.Ordinal);
        Dictionary<string, CursorTurnBubbleRelation> byBubble =
            new(StringComparer.Ordinal);
        foreach (SessionTurn turn in session.Turns)
        {
            List<(CursorReconstructionBubble Bubble,
                SessionEventRecord Event)> resolved = [];
            foreach (SessionEventRecord item in turn.Events)
            {
                CursorReconstructionBubble? bubble = Resolve(item.Provenance);
                if (bubble is not null)
                {
                    resolved.Add((bubble, item));
                }
            }

            CursorTurnBubbleRelation[] relations = resolved
                .GroupBy(
                    item => item.Bubble.Record.Row.StableKey,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    SessionEventRecord[] events = group
                        .Select(item => item.Event)
                        .OrderBy(item => item.Order)
                        .ToArray();
                    return new CursorTurnBubbleRelation(
                        turn,
                        group.First().Bubble,
                        events.Any(item =>
                            item.Role == ObservationRole.PromptSubmitted),
                        events);
                })
                .OrderByDescending(relation => relation.StartsTurn)
                .ThenBy(relation => relation.Bubble.Sequence)
                .ToArray();
            byTurn[turn.Id] = relations;
            foreach (CursorTurnBubbleRelation relation in relations)
            {
                byBubble.TryAdd(
                    relation.Bubble.Record.Row.StableKey,
                    relation);
                foreach (SessionEventRecord item in relation.Events)
                {
                    byEvent[item.Id] = relation;
                }
            }
        }

        _byTurn = byTurn;
        _byEvent = byEvent;
        _byBubble = byBubble;
    }

    public IReadOnlyList<CursorTurnBubbleRelation> ForTurn(SessionTurn turn) =>
        _byTurn.TryGetValue(
            turn.Id,
            out IReadOnlyList<CursorTurnBubbleRelation>? relations)
            ? relations
            : [];

    public IReadOnlyList<CursorTurnBubbleRelation> ForBubble(
        CursorReconstructionBubble bubble) =>
        _byBubble.TryGetValue(
            bubble.Record.Row.StableKey,
            out CursorTurnBubbleRelation? relation)
            ? [relation]
            : [];

    public IReadOnlyList<CursorTurnBubbleRelation> ForEvent(
        SessionEventRecord item) =>
        _byEvent.TryGetValue(
            item.Id,
            out CursorTurnBubbleRelation? relation)
            ? [relation]
            : [];

    private CursorReconstructionBubble? Resolve(
        SessionSourceProvenance provenance)
    {
        if (!string.IsNullOrWhiteSpace(provenance.RecordId) &&
            _bubblesById.TryGetValue(
                provenance.RecordId,
                out CursorReconstructionBubble? byId))
        {
            return byId;
        }

        return !string.IsNullOrWhiteSpace(provenance.DatabaseKey) &&
            _bubblesByKey.TryGetValue(
                provenance.DatabaseKey,
                out CursorReconstructionBubble? byKey)
                ? byKey
                : null;
    }
}
