using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public sealed class CursorRelationIndex
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CursorInspectionRelation>>
        _outbound;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CursorInspectionRelation>>
        _inbound;

    public CursorRelationIndex(IReadOnlyList<CursorIndexedRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        Dictionary<string, List<CursorInspectionRelation>> outbound =
            new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<CursorInspectionRelation>> inbound =
            new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CursorIndexedRecord[]> composers = records
            .Where(record => record.Classification.Kind is
                CursorRecordKind.ComposerData or
                CursorRecordKind.ComposerHeader or
                CursorRecordKind.ComposerHeaders)
            .SelectMany(record => ComposerIds(record).Select(composerId => (
                ComposerId: composerId,
                Record: record)))
            .GroupBy(
                item => item.ComposerId,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(item => item.Record)
                    .DistinctBy(item => item.Row.StableKey)
                    .ToArray(),
                StringComparer.Ordinal);
        Dictionary<string, CursorIndexedRecord[]> bubbles = records
            .Where(item =>
                item.Classification.Kind == CursorRecordKind.Bubble &&
                !string.IsNullOrWhiteSpace(item.Classification.ComposerId) &&
                !string.IsNullOrWhiteSpace(item.Classification.BubbleId))
            .GroupBy(
                item => BubbleKey(
                    item.Classification.ComposerId!,
                    item.Classification.BubbleId!),
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);

        foreach (CursorIndexedRecord record in records)
        {
            foreach (CursorLogicalReference reference in record.References)
            {
                IReadOnlyList<CursorIndexedRecord> targets = Targets(
                    record,
                    reference,
                    composers,
                    bubbles);
                AddRelations(
                    record,
                    reference,
                    targets,
                    outbound,
                    inbound);
            }
        }

        AddDuplicateRelations(records, outbound, inbound);
        _outbound = Freeze(outbound);
        _inbound = Freeze(inbound);
    }

    public IReadOnlyList<CursorInspectionRelation> Outbound(
        CursorRowReference row) =>
        _outbound.TryGetValue(
            row.StableKey,
            out IReadOnlyList<CursorInspectionRelation>? relations)
            ? relations
            : [];

    public IReadOnlyList<CursorInspectionRelation> Inbound(
        CursorRowReference row) =>
        _inbound.TryGetValue(
            row.StableKey,
            out IReadOnlyList<CursorInspectionRelation>? relations)
            ? relations
            : [];

    private IReadOnlyList<CursorIndexedRecord> Targets(
        CursorIndexedRecord source,
        CursorLogicalReference reference,
        IReadOnlyDictionary<string, CursorIndexedRecord[]> composers,
        IReadOnlyDictionary<string, CursorIndexedRecord[]> bubbles)
    {
        if (reference.Kind == CursorRelationKind.ConversationBubble)
        {
            string? composerId = reference.SourceContextId ??
                source.Classification.ComposerId;
            if (string.IsNullOrWhiteSpace(composerId) ||
                !bubbles.TryGetValue(
                    BubbleKey(composerId, reference.TargetId),
                    out CursorIndexedRecord[]? matchingBubbles))
            {
                return [];
            }

            return matchingBubbles;
        }

        if (reference.Kind == CursorRelationKind.Workspace)
        {
            return [];
        }

        if (!composers.TryGetValue(
                reference.TargetId,
                out CursorIndexedRecord[]? matchingComposers))
        {
            return [];
        }

        CursorIndexedRecord[] others = matchingComposers
            .Where(item =>
                !string.Equals(
                    item.Row.StableKey,
                    source.Row.StableKey,
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item =>
                item.Classification.Kind == CursorRecordKind.ComposerData)
            .ToArray();
        return others;
    }

    private void AddRelations(
        CursorIndexedRecord source,
        CursorLogicalReference reference,
        IReadOnlyList<CursorIndexedRecord> targets,
        Dictionary<string, List<CursorInspectionRelation>> outbound,
        Dictionary<string, List<CursorInspectionRelation>> inbound)
    {
        if (targets.Count == 0)
        {
            if (reference.Kind == CursorRelationKind.DeclaresComposer)
            {
                return;
            }

            Add(
                new CursorInspectionRelation(
                    reference.Kind,
                    source.Row,
                    null,
                    null,
                    reference.TargetId,
                    reference.Evidence,
                    CursorRelationResolution.Unresolved,
                    reference.Explanation),
                outbound,
                inbound);
            return;
        }

        CursorRelationResolution resolution = targets.Count == 1
            ? CursorRelationResolution.Resolved
            : CursorRelationResolution.Ambiguous;
        foreach (CursorIndexedRecord target in targets)
        {
            Add(
                new CursorInspectionRelation(
                    reference.Kind,
                    source.Row,
                    target.Row,
                    null,
                    reference.TargetId,
                    reference.Evidence,
                    resolution,
                    reference.Explanation),
                outbound,
                inbound);
        }
    }

    private void AddDuplicateRelations(
        IReadOnlyList<CursorIndexedRecord> records,
        Dictionary<string, List<CursorInspectionRelation>> outbound,
        Dictionary<string, List<CursorInspectionRelation>> inbound)
    {
        foreach (IGrouping<(CursorRecordKind Kind, string Id), CursorIndexedRecord>
                 group in records
                     .Where(item =>
                         !string.IsNullOrWhiteSpace(
                             item.Classification.ComposerId) &&
                         item.Classification.Kind is
                             CursorRecordKind.ComposerData or
                             CursorRecordKind.ComposerHeader)
                     .GroupBy(item => (
                         item.Classification.Kind,
                         item.Classification.ComposerId!)))
        {
            CursorIndexedRecord[] duplicates = group.ToArray();
            if (duplicates.Length < 2)
            {
                continue;
            }

            foreach (CursorIndexedRecord source in duplicates)
            {
                foreach (CursorIndexedRecord target in duplicates.Where(item =>
                             !string.Equals(
                                 item.Row.StableKey,
                                 source.Row.StableKey,
                                 StringComparison.OrdinalIgnoreCase)))
                {
                    Add(
                        new CursorInspectionRelation(
                            CursorRelationKind.DuplicateRecord,
                            source.Row,
                            target.Row,
                            null,
                            group.Key.Id,
                            InferenceEvidence.Corroborated,
                            CursorRelationResolution.Resolved,
                            "The same composer record exists in another database row."),
                        outbound,
                        inbound);
                }
            }
        }
    }

    private void Add(
        CursorInspectionRelation relation,
        Dictionary<string, List<CursorInspectionRelation>> outbound,
        Dictionary<string, List<CursorInspectionRelation>> inbound)
    {
        AddTo(outbound, relation.Source.StableKey, relation);
        if (relation.Target is not null)
        {
            AddTo(inbound, relation.Target.StableKey, relation);
        }
    }

    private void AddTo(
        Dictionary<string, List<CursorInspectionRelation>> index,
        string key,
        CursorInspectionRelation relation)
    {
        if (!index.TryGetValue(
                key,
                out List<CursorInspectionRelation>? values))
        {
            values = [];
            index[key] = values;
        }

        if (!values.Any(item =>
                item.Kind == relation.Kind &&
                string.Equals(
                    item.Target?.StableKey,
                    relation.Target?.StableKey,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    item.LogicalTarget,
                    relation.LogicalTarget,
                    StringComparison.Ordinal)))
        {
            values.Add(relation);
        }
    }

    private IReadOnlyDictionary<string, IReadOnlyList<CursorInspectionRelation>>
        Freeze(Dictionary<string, List<CursorInspectionRelation>> values) =>
        values.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<CursorInspectionRelation>)item.Value
                .OrderBy(relation => relation.Kind)
                .ThenBy(
                    relation => relation.Target?.DisplayText,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);

    private string BubbleKey(string composerId, string bubbleId) =>
        composerId + "\0" + bubbleId;

    private IEnumerable<string> ComposerIds(CursorIndexedRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.Classification.ComposerId))
        {
            yield return record.Classification.ComposerId;
        }

        foreach (string composerId in record.References
                     .Where(reference =>
                         reference.Kind == CursorRelationKind.DeclaresComposer)
                     .Select(reference => reference.TargetId)
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal))
        {
            yield return composerId;
        }
    }
}
