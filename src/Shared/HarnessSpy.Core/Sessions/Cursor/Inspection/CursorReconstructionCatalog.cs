using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;

namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public sealed record CursorReconstructionBubble(
    CursorIndexedRecord Record,
    int Sequence,
    bool IsReferencedByComposer,
    string Rule);

public sealed class CursorReconstructedSession
{
    private IReadOnlyList<CursorIndexedRecord> _composerRecords;
    private IReadOnlyList<CursorReconstructionBubble> _bubbles;
    private IReadOnlyList<SessionTurn> _turns;

    public CursorReconstructedSession(
        SessionCatalogEntry session,
        IReadOnlyList<CursorIndexedRecord> composerRecords,
        IReadOnlyList<CursorReconstructionBubble> bubbles,
        bool isInspectionLoaded = true)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _composerRecords = composerRecords ??
            throw new ArgumentNullException(nameof(composerRecords));
        _bubbles = bubbles ?? throw new ArgumentNullException(nameof(bubbles));
        _turns = session.Turns;
        IsInspectionLoaded = isInspectionLoaded;
    }

    public SessionCatalogEntry Session { get; }

    public IReadOnlyList<CursorIndexedRecord> ComposerRecords =>
        _composerRecords;

    public IReadOnlyList<CursorReconstructionBubble> Bubbles => _bubbles;

    public IReadOnlyList<SessionTurn> Turns => _turns;

    public bool IsInspectionLoaded { get; private set; }

    public string? Model { get; private set; }

    public string? Mode { get; private set; }

    public bool IsHydrated { get; private set; }

    public IReadOnlyList<string> Warnings { get; private set; } = [];

    public void ApplyInspection(CursorReconstructedSession inspected)
    {
        ArgumentNullException.ThrowIfNull(inspected);
        if (!string.Equals(
                Session.NativeSessionId,
                inspected.Session.NativeSessionId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Inspection must describe the same Cursor session.",
                nameof(inspected));
        }

        _composerRecords = inspected.ComposerRecords;
        _bubbles = inspected.Bubbles;
        _turns = inspected.Turns;
        IsInspectionLoaded = inspected.IsInspectionLoaded;
        Model = inspected.Model;
        Mode = inspected.Mode;
        IsHydrated = inspected.IsHydrated;
        Warnings = inspected.Warnings;
    }

    public void ApplyHydration(CursorReconstructionHydration hydration)
    {
        ArgumentNullException.ThrowIfNull(hydration);
        _turns = hydration.Turns;
        Model = hydration.Model;
        Mode = hydration.Mode;
        Warnings = hydration.Warnings;
        IsHydrated = true;
    }
}

public sealed record CursorReconstructedWorkspace(
    WorkspaceContext Workspace,
    IReadOnlyList<CursorReconstructedSession> Sessions);

public sealed record CursorReconstructionCatalog(
    IReadOnlyList<CursorReconstructedWorkspace> Workspaces,
    IReadOnlyList<string> Warnings);

public sealed class CursorReconstructionCatalogBuilder
{
    private readonly CursorDesktopSessionCatalogSource _sessionSource;

    public CursorReconstructionCatalogBuilder(SessionDiscoveryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _sessionSource = new CursorDesktopSessionCatalogSource(context);
    }

    public async Task<CursorReconstructionCatalog> BuildAsync(
        CursorInspectionCatalogSnapshot inspection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        SessionCatalogScanResult sessions = await _sessionSource.ScanHeadersAsync(
            cancellationToken).ConfigureAwait(false);
        return Build(sessions, inspection.Records, isInspectionLoaded: true);
    }

    public async Task<CursorReconstructionCatalog> BuildSkeletonAsync(
        CancellationToken cancellationToken = default)
    {
        SessionCatalogScanResult sessions = await _sessionSource.ScanHeadersAsync(
            cancellationToken).ConfigureAwait(false);
        return Build(sessions, [], isInspectionLoaded: false);
    }

    public CursorReconstructionCatalog ApplyInspection(
        IReadOnlyList<SessionCatalogEntry> sessions,
        IReadOnlyList<string> sessionWarnings,
        CursorInspectionCatalogSnapshot inspection)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(sessionWarnings);
        ArgumentNullException.ThrowIfNull(inspection);
        return Build(
            new SessionCatalogScanResult(
                sessions,
                IsComplete: true,
                sessionWarnings),
            inspection.Records,
            isInspectionLoaded: true);
    }

    public CursorReconstructionCatalog ApplySessionInspections(
        IReadOnlyList<SessionCatalogEntry> sessions,
        IReadOnlyList<string> sessionWarnings,
        IReadOnlyList<CursorIndexedRecord> records,
        IReadOnlySet<string> loadedSessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(sessionWarnings);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(loadedSessionIds);
        return Build(
            new SessionCatalogScanResult(
                sessions,
                IsComplete: true,
                sessionWarnings),
            records,
            session => loadedSessionIds.Contains(session.NativeSessionId));
    }

    private CursorReconstructionCatalog Build(
        SessionCatalogScanResult sessions,
        IReadOnlyList<CursorIndexedRecord> records,
        bool isInspectionLoaded)
        => Build(sessions, records, _ => isInspectionLoaded);

    private CursorReconstructionCatalog Build(
        SessionCatalogScanResult sessions,
        IReadOnlyList<CursorIndexedRecord> records,
        Func<SessionCatalogEntry, bool> isInspectionLoaded)
    {
        CursorReconstructedSession[] reconstructed = sessions.Sessions
            .Select(session => Reconstruct(
                session,
                records,
                isInspectionLoaded(session)))
            .ToArray();
        CursorReconstructedWorkspace[] workspaces = reconstructed
            .GroupBy(
                item => EffectiveWorkspace(item.Session).Key,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new CursorReconstructedWorkspace(
                EffectiveWorkspace(group.First().Session),
                group
                    .OrderByDescending(item =>
                        item.Session.LastActivityAtUtc)
                    .ThenBy(
                        item => item.Session.Title,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .OrderBy(
                item => item.Workspace.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new CursorReconstructionCatalog(
            workspaces,
            sessions.Warnings);
    }

    private CursorReconstructedSession Reconstruct(
        SessionCatalogEntry session,
        IReadOnlyList<CursorIndexedRecord> records,
        bool isInspectionLoaded)
    {
        CursorIndexedRecord[] composers = records
            .Where(record => ComposerIds(record).Contains(
                session.NativeSessionId,
                StringComparer.Ordinal))
            .Where(record => record.Classification.Kind is
                CursorRecordKind.ComposerData or
                CursorRecordKind.ComposerHeader or
                CursorRecordKind.ComposerHeaders)
            .ToArray();
        CursorIndexedRecord[] bubbles = records
            .Where(record =>
                record.Classification.Kind == CursorRecordKind.Bubble &&
                string.Equals(
                    record.Classification.ComposerId,
                    session.NativeSessionId,
                    StringComparison.Ordinal))
            .ToArray();
        string[] referencedIds = composers
            .SelectMany(record => record.References)
            .Where(reference =>
                reference.Kind == CursorRelationKind.ConversationBubble &&
                (reference.SourceContextId is null ||
                    string.Equals(
                        reference.SourceContextId,
                        session.NativeSessionId,
                        StringComparison.Ordinal)))
            .Select(reference => reference.TargetId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, int> order = referencedIds
            .Select((bubbleId, index) => (bubbleId, index))
            .ToDictionary(
                item => item.bubbleId,
                item => item.index,
                StringComparer.Ordinal);
        CursorReconstructionBubble[] orderedBubbles = bubbles
            .OrderBy(record =>
                record.Classification.BubbleId is string bubbleId &&
                order.TryGetValue(bubbleId, out int index)
                    ? index
                    : int.MaxValue)
            .ThenBy(
                record => record.Classification.StorageKey,
                StringComparer.Ordinal)
            .Select((record, index) =>
            {
                bool referenced =
                    record.Classification.BubbleId is string bubbleId &&
                    order.ContainsKey(bubbleId);
                return new CursorReconstructionBubble(
                    record,
                    index + 1,
                    referenced,
                    referenced
                        ? "conversationHeaders[].bubbleId + composerId " +
                            "resolves bubbleId:<composerId>:<bubbleId>."
                        : "The bubble key names this composer, but no loaded " +
                            "conversation header references it.");
            })
            .ToArray();
        return new CursorReconstructedSession(
            session,
            composers,
            orderedBubbles,
            isInspectionLoaded);
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
                     .Distinct(StringComparer.Ordinal))
        {
            yield return composerId;
        }
    }

    private WorkspaceContext EffectiveWorkspace(SessionCatalogEntry session) =>
        session.WorkspaceInference?.Workspace.Kind ==
            WorkspaceContextKind.Normal
            ? session.WorkspaceInference.Workspace
            : session.Workspace;
}
