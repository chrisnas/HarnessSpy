using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;

namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public sealed class CursorCrossStoreCorrelationIndex
{
    private readonly IReadOnlyDictionary<
        string,
        IReadOnlyList<CursorInspectionRelation>> _relations;

    public CursorCrossStoreCorrelationIndex(
        IReadOnlyList<CursorIndexedRecord> records,
        IReadOnlyList<CursorWorkspaceEvidence> workspaces,
        IReadOnlyList<SessionCatalogEntry> transcripts)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(transcripts);

        Dictionary<string, List<CursorInspectionRelation>> relations =
            new(StringComparer.OrdinalIgnoreCase);
        ILookup<string, SessionCatalogEntry> sessionsById =
            transcripts.ToLookup(
                item => item.NativeSessionId,
                StringComparer.OrdinalIgnoreCase);

        foreach (CursorIndexedRecord record in records)
        {
            AddWorkspaceRelations(record, workspaces, relations);
            AddTranscriptRelations(record, sessionsById, relations);
        }

        _relations = relations.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<CursorInspectionRelation>)item.Value
                .OrderBy(relation => relation.Kind)
                .ThenBy(
                    relation => relation.ExternalTarget?.Path,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CursorInspectionRelation> ForRow(
        CursorRowReference row) =>
        _relations.TryGetValue(
            row.StableKey,
            out IReadOnlyList<CursorInspectionRelation>? relations)
            ? relations
            : [];

    private void AddWorkspaceRelations(
        CursorIndexedRecord record,
        IReadOnlyList<CursorWorkspaceEvidence> workspaces,
        Dictionary<string, List<CursorInspectionRelation>> relations)
    {
        foreach (CursorLogicalReference reference in record.References.Where(
                     item => item.Kind == CursorRelationKind.Workspace))
        {
            CursorWorkspaceEvidence[] matches = workspaces
                .Where(workspace => MatchesWorkspace(
                    reference.TargetId,
                    workspace))
                .ToArray();
            if (matches.Length == 0)
            {
                Add(
                    relations,
                    new CursorInspectionRelation(
                        CursorRelationKind.Workspace,
                        record.Row,
                        null,
                        null,
                        reference.TargetId,
                        reference.Evidence,
                        CursorRelationResolution.Unresolved,
                        "No workspaceStorage manifest matches this workspace reference."));
                continue;
            }

            CursorRelationResolution resolution = matches.Length == 1
                ? CursorRelationResolution.Resolved
                : CursorRelationResolution.Ambiguous;
            foreach (CursorWorkspaceEvidence match in matches)
            {
                Add(
                    relations,
                    new CursorInspectionRelation(
                        CursorRelationKind.Workspace,
                        record.Row,
                        null,
                        new CursorExternalReference(match.ManifestPath),
                        reference.TargetId,
                        matches.Length == 1
                            ? InferenceEvidence.Corroborated
                            : InferenceEvidence.Ambiguous,
                        resolution,
                        $"workspace.json maps '{match.StorageId}' to " +
                        $"'{match.WorkspacePath ?? "an unknown path"}'."));
            }
        }
    }

    private void AddTranscriptRelations(
        CursorIndexedRecord record,
        ILookup<string, SessionCatalogEntry> sessionsById,
        Dictionary<string, List<CursorInspectionRelation>> relations)
    {
        string[] composerIds = ComposerIds(record)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (composerIds.Length == 0)
        {
            return;
        }

        foreach (string composerId in composerIds)
        {
            SessionCatalogEntry[] matches = sessionsById[composerId].ToArray();
            if (matches.Length == 0)
            {
                Add(
                    relations,
                    new CursorInspectionRelation(
                        CursorRelationKind.Transcript,
                        record.Row,
                        null,
                        null,
                        composerId,
                        InferenceEvidence.Unavailable,
                        CursorRelationResolution.Unresolved,
                        "No .cursor/projects transcript has this native composer id."));
                continue;
            }

            SessionCatalogEntry[] workspaceMatches = matches.Length > 1
                ? matches.Where(item =>
                    MatchesRecordWorkspace(record, item)).ToArray()
                : [];
            bool corroborated = workspaceMatches.Length == 1;
            SessionCatalogEntry[] selectedMatches = corroborated
                ? workspaceMatches
                : matches;
            CursorRelationResolution resolution = selectedMatches.Length == 1
                ? CursorRelationResolution.Resolved
                : CursorRelationResolution.Ambiguous;
            foreach (SessionCatalogEntry match in selectedMatches)
            {
                SessionFileBinding? main = match.Files.FirstOrDefault(file =>
                    file.Role == TranscriptFileRole.Main) ??
                    match.Files.FirstOrDefault();
                if (main is null)
                {
                    continue;
                }

                SessionSourceProvenance? transcriptRow =
                    record.Classification.BubbleId is string bubbleId
                        ? match.Sources.FirstOrDefault(source =>
                            string.Equals(
                                source.RecordId,
                                bubbleId,
                                StringComparison.Ordinal))
                        : null;
                Add(
                    relations,
                    new CursorInspectionRelation(
                        CursorRelationKind.Transcript,
                        record.Row,
                        null,
                        new CursorExternalReference(
                            transcriptRow?.Path ?? main.Path,
                            match.NativeSessionId,
                            transcriptRow?.ByteOffset,
                            transcriptRow?.LineNumber),
                        composerId,
                        transcriptRow is not null || corroborated
                            ? InferenceEvidence.Corroborated
                            : selectedMatches.Length == 1
                                ? InferenceEvidence.Observed
                            : InferenceEvidence.Ambiguous,
                        resolution,
                        transcriptRow is not null
                            ? "The composer id and bubble id both match a transcript row."
                            : corroborated
                            ? "The native session id and workspace path both match."
                            : selectedMatches.Length == 1
                                ? "The transcript native session id exactly matches the composer id."
                            : "Several project contexts contain this native session id."));
            }
        }
    }

    private bool MatchesRecordWorkspace(
        CursorIndexedRecord record,
        SessionCatalogEntry session)
    {
        string[] references = record.References
            .Where(item => item.Kind == CursorRelationKind.Workspace)
            .Select(item => NormalizePathOrUri(item.TargetId))
            .ToArray();
        if (references.Length == 0)
        {
            return false;
        }

        return session.Workspace.DisplayRoots
            .Select(NormalizePathOrUri)
            .Any(root => references.Contains(
                root,
                StringComparer.OrdinalIgnoreCase));
    }

    private bool MatchesWorkspace(
        string target,
        CursorWorkspaceEvidence workspace)
    {
        if (string.Equals(
                target,
                workspace.StorageId,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(workspace.WorkspacePath))
        {
            return false;
        }

        return string.Equals(
            NormalizePathOrUri(target),
            NormalizePathOrUri(workspace.WorkspacePath),
            StringComparison.OrdinalIgnoreCase);
    }

    private string NormalizePathOrUri(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            uri.IsFile)
        {
            return Path.GetFullPath(uri.LocalPath)
                .TrimEnd(Path.DirectorySeparatorChar);
        }

        try
        {
            return Path.GetFullPath(value)
                .TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return value.Trim();
        }
    }

    private void Add(
        Dictionary<string, List<CursorInspectionRelation>> relations,
        CursorInspectionRelation relation)
    {
        if (!relations.TryGetValue(
                relation.Source.StableKey,
                out List<CursorInspectionRelation>? values))
        {
            values = [];
            relations[relation.Source.StableKey] = values;
        }

        values.Add(relation);
    }

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
