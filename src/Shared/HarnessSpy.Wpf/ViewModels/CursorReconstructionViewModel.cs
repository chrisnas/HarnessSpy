using System.Collections.ObjectModel;
using System.IO;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Cursor.Inspection;

namespace HarnessSpy.Wpf.ViewModels;

public enum CursorReconstructionItemKind
{
    Folder,
    Workspace,
    Session,
    Group,
    ComposerRecord,
    Bubble,
    Turn,
    Event,
    Relation
}

public sealed class CursorReconstructionItemViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isRelationHighlighted;
    private string _summary;

    public CursorReconstructionItemViewModel(
        CursorReconstructionItemKind kind,
        string header,
        string summary,
        string rule,
        CursorReconstructedWorkspace? workspace = null,
        CursorReconstructedSession? session = null,
        CursorReconstructionBubble? bubble = null,
        CursorIndexedRecord? record = null,
        SessionTurn? turn = null,
        SessionEventRecord? eventRecord = null,
        string? folderPath = null,
        IReadOnlyList<CursorTurnBubbleRelation>? relations = null)
    {
        Kind = kind;
        Header = header;
        _summary = summary;
        Rule = rule;
        Workspace = workspace;
        Session = session;
        Bubble = bubble;
        Record = record;
        Turn = turn;
        EventRecord = eventRecord;
        FolderPath = folderPath;
        Relations = relations ?? [];
    }

    public CursorReconstructionItemKind Kind { get; }

    public string Header { get; }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string Rule { get; }

    public CursorReconstructedWorkspace? Workspace { get; }

    public CursorReconstructedSession? Session { get; }

    public CursorReconstructionBubble? Bubble { get; }

    public CursorIndexedRecord? Record { get; }

    public SessionTurn? Turn { get; }

    public SessionEventRecord? EventRecord { get; }

    public string? FolderPath { get; }

    public IReadOnlyList<CursorTurnBubbleRelation> Relations { get; }

    public string Badge => Kind switch
    {
        CursorReconstructionItemKind.Workspace => "Workspace",
        CursorReconstructionItemKind.Session => "Composer",
        CursorReconstructionItemKind.ComposerRecord => "SQLite",
        CursorReconstructionItemKind.Bubble => "Bubble",
        CursorReconstructionItemKind.Turn => "Turn",
        CursorReconstructionItemKind.Relation => "Source",
        CursorReconstructionItemKind.Event => EventRecord?.Role.ToString() ??
            "Event",
        _ => string.Empty
    };

    public bool HasBadge => Badge.Length > 0;

    public bool IsWorkspace =>
        Kind == CursorReconstructionItemKind.Workspace;

    public bool IsFolderLike =>
        Kind is CursorReconstructionItemKind.Folder or
            CursorReconstructionItemKind.Workspace;

    public bool IsRelationHighlighted
    {
        get => _isRelationHighlighted;
        private set => SetProperty(ref _isRelationHighlighted, value);
    }

    public bool IsSession =>
        Kind == CursorReconstructionItemKind.Session;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ObservableCollection<CursorReconstructionItemViewModel> Children
    {
        get;
    } = [];

    public void SetRelationHighlighted(bool value) =>
        IsRelationHighlighted = value;

    public void RefreshSummary()
    {
        if (Kind == CursorReconstructionItemKind.Session &&
            Session is not null)
        {
            Summary = !Session.IsInspectionLoaded
                ? "Details load on selection"
                : !Session.IsHydrated
                    ? $"{Session.Bubbles.Count:N0} bubbles, turns not loaded"
                    : $"{Session.Bubbles.Count:N0} bubbles, " +
                        $"{Session.Turns.Count:N0} turns";
        }
    }
}

public sealed class CursorReconstructionViewModel : ObservableObject, IDisposable
{
    private const int RawPreviewBytes = 1024 * 1024;

    private readonly CursorReconstructionCatalogBuilder _builder;
    private readonly CursorReconstructionSessionHydrator _hydrator;
    private readonly ISessionRawSourceResolver _rawSourceResolver;
    private readonly CursorReconstructionFolderProjector _folderProjector =
        new();
    private readonly Dictionary<string, CursorIndexedRecord>
        _sessionInspectionRecords = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inspectedSessionIds =
        new(StringComparer.Ordinal);
    private IReadOnlyList<SessionCatalogEntry> _sessions = [];
    private IReadOnlyList<string> _sessionWarnings = [];
    private CancellationTokenSource? _selectionCancellation;
    private CursorReconstructionItemViewModel? _selectedItem;
    private string _rawSource = string.Empty;
    private string _jsonSource = string.Empty;
    private string _rawStatus = string.Empty;
    private bool _disposed;

    public CursorReconstructionViewModel(SessionDiscoveryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _builder = new CursorReconstructionCatalogBuilder(context);
        _hydrator = new CursorReconstructionSessionHydrator(context);
        _rawSourceResolver = new SessionRawSourceResolver();
    }

    public ObservableCollection<CursorReconstructionItemViewModel> Roots
    {
        get;
    } = [];

    public ObservableCollection<CursorReconstructionItemViewModel>
        WorkspaceStage { get; } = [];

    public ObservableCollection<CursorReconstructionItemViewModel>
        ComposerStage { get; } = [];

    public ObservableCollection<CursorReconstructionItemViewModel>
        BubbleStage { get; } = [];

    public ObservableCollection<CursorReconstructionItemViewModel>
        OutputStage { get; } = [];

    public ObservableCollection<CursorCellViewModel> EvidenceFields { get; } =
        [];

    public CursorReconstructionItemViewModel? SelectedItem
    {
        get => _selectedItem;
        private set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedTitle));
                OnPropertyChanged(nameof(SelectedSummary));
                OnPropertyChanged(nameof(SelectedRule));
                OnPropertyChanged(nameof(CanOpenPhysicalSource));
                OnPropertyChanged(nameof(CanGoToSourceBubble));
            }
        }
    }

    public bool HasSelection => SelectedItem is not null;

    public string SelectedTitle => SelectedItem?.Header ?? string.Empty;

    public string SelectedSummary => SelectedItem?.Summary ?? string.Empty;

    public string SelectedRule => SelectedItem?.Rule ?? string.Empty;

    public string RawSource
    {
        get => _rawSource;
        private set => SetProperty(ref _rawSource, value);
    }

    public string JsonSource
    {
        get => _jsonSource;
        private set => SetProperty(ref _jsonSource, value);
    }

    public string RawStatus
    {
        get => _rawStatus;
        private set => SetProperty(ref _rawStatus, value);
    }

    public bool CanOpenPhysicalSource =>
        PhysicalSource(SelectedItem) is not null;

    public bool CanGoToSourceBubble =>
        SelectedItem?.Kind != CursorReconstructionItemKind.Bubble &&
        SelectedSourceBubble() is not null;

    public async Task<IReadOnlyList<string>> RefreshAsync(
        CursorInspectionCatalogSnapshot inspection,
        CancellationToken cancellationToken)
    {
        CursorReconstructionCatalog catalog = await _builder.BuildAsync(
            inspection,
            cancellationToken).ConfigureAwait(true);
        CacheSessions(catalog);
        PopulateRoots(catalog);
        return catalog.Warnings;
    }

    public async Task<IReadOnlyList<string>> RefreshSkeletonAsync(
        CancellationToken cancellationToken)
    {
        CursorReconstructionCatalog catalog =
            await _builder.BuildSkeletonAsync(cancellationToken)
                .ConfigureAwait(true);
        _sessionInspectionRecords.Clear();
        _inspectedSessionIds.Clear();
        CacheSessions(catalog);
        PopulateRoots(catalog);
        return catalog.Warnings;
    }

    public IReadOnlyList<string> ApplyInspection(
        CursorInspectionCatalogSnapshot inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        _sessionInspectionRecords.Clear();
        _inspectedSessionIds.Clear();
        CursorReconstructionCatalog catalog = _builder.ApplyInspection(
            _sessions,
            _sessionWarnings,
            inspection);
        PopulateRoots(catalog);
        return catalog.Warnings;
    }

    public IReadOnlyList<string> ApplySessionInspection(
        string sessionId,
        CursorInspectionCatalogSnapshot inspection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(inspection);
        foreach (CursorIndexedRecord record in inspection.Records)
        {
            _sessionInspectionRecords[record.Row.StableKey] = record;
        }

        _inspectedSessionIds.Add(sessionId);
        CursorReconstructionCatalog catalog =
            _builder.ApplySessionInspections(
                _sessions,
                _sessionWarnings,
                _sessionInspectionRecords.Values.ToArray(),
                _inspectedSessionIds);
        CursorReconstructionItemViewModel? node = FindSession(sessionId);
        CursorReconstructedSession? inspected = catalog.Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .FirstOrDefault(session => string.Equals(
                session.Session.NativeSessionId,
                sessionId,
                StringComparison.Ordinal));
        if (node?.Session is CursorReconstructedSession existing &&
            inspected is not null)
        {
            existing.ApplyInspection(inspected);
            node.RefreshSummary();
        }

        return catalog.Warnings;
    }

    public CursorReconstructionItemViewModel? FindSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return EnumerateItems()
            .FirstOrDefault(item => string.Equals(
                item.Session?.Session.NativeSessionId,
                sessionId,
                StringComparison.Ordinal));
    }

    private void CacheSessions(CursorReconstructionCatalog catalog)
    {
        _sessions = catalog.Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Select(session => session.Session)
            .ToArray();
        _sessionWarnings = catalog.Warnings;
    }

    private void PopulateRoots(CursorReconstructionCatalog catalog)
    {
        Roots.Clear();
        CursorReconstructionItemViewModel[] workspaces = catalog.Workspaces
            .Select(BuildWorkspace)
            .ToArray();
        foreach (CursorReconstructionItemViewModel root in
                 _folderProjector.Project(workspaces))
        {
            Roots.Add(root);
        }
        SelectedItem = null;
        PopulatePipeline(null);
        PopulateEvidence(null);
        RawSource = string.Empty;
        JsonSource = string.Empty;
        RawStatus = string.Empty;
    }

    public async Task SelectAsync(
        CursorReconstructionItemViewModel? item,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        CancellationTokenSource next =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = next.Token;
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _selectionCancellation,
            next);
        previous?.Cancel();
        try
        {
            await EnsureSessionHydratedAsync(item, token)
                .ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            EnsureSessionChildren(item);
            UpdateRelationHighlights(item);
            SelectTreeItem(item);
            SelectedItem = item;
            PopulatePipeline(item);
            PopulateEvidence(item);
            await LoadRawSourceAsync(item, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _selectionCancellation,
                null,
                next);
            next.Dispose();
        }
    }

    public CursorRowReference? SelectedPhysicalSource() =>
        PhysicalSource(SelectedItem);

    public CursorReconstructionItemViewModel? SelectedSourceBubble()
    {
        CursorTurnBubbleRelation? relation = SelectedItem?.Relations
            .OrderByDescending(item => item.StartsTurn)
            .FirstOrDefault();
        if (relation is null)
        {
            return null;
        }

        string stableKey = relation.Bubble.Record.Row.StableKey;
        return BubbleStage.FirstOrDefault(item =>
            string.Equals(
                item.Bubble?.Record.Row.StableKey,
                stableKey,
                StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancellationTokenSource? cancellation = Interlocked.Exchange(
            ref _selectionCancellation,
            null);
        cancellation?.Cancel();
    }

    private CursorReconstructionItemViewModel BuildWorkspace(
        CursorReconstructedWorkspace workspace)
    {
        CursorReconstructionItemViewModel node = new(
            CursorReconstructionItemKind.Workspace,
            WorkspaceHeader(workspace.Workspace),
            $"{workspace.Sessions.Count:N0} sessions",
            "workspaceIdentifier.id/path is resolved through " +
            "workspaceStorage/<id>/workspace.json.",
            workspace: workspace);
        foreach (CursorReconstructedSession session in workspace.Sessions)
        {
            node.Children.Add(BuildSession(workspace, session));
        }

        return node;
    }

    private string WorkspaceHeader(WorkspaceContext workspace)
    {
        if (workspace.Kind != WorkspaceContextKind.Normal ||
            workspace.DisplayRoots.Count != 1)
        {
            return workspace.DisplayName;
        }

        string path = workspace.DisplayRoots[0].TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string? name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name)
            ? workspace.DisplayName
            : name;
    }

    private CursorReconstructionItemViewModel BuildSession(
        CursorReconstructedWorkspace workspace,
        CursorReconstructedSession session)
        =>
        new(
            CursorReconstructionItemKind.Session,
            session.Session.Title,
            SessionSummary(session),
            "composerId joins composer headers/data, bubble keys, " +
            "workspace metadata, and matching JSONL transcripts.",
            workspace,
            session);

    private string SessionSummary(CursorReconstructedSession session) =>
        !session.IsInspectionLoaded
            ? "Details load on selection"
            : !session.IsHydrated
                ? $"{session.Bubbles.Count:N0} bubbles, turns not loaded"
                : $"{session.Bubbles.Count:N0} bubbles, " +
                    $"{session.Turns.Count:N0} turns";

    private async Task EnsureSessionHydratedAsync(
        CursorReconstructionItemViewModel? selected,
        CancellationToken cancellationToken)
    {
        if (selected?.Session is not CursorReconstructedSession session ||
            !session.IsInspectionLoaded ||
            session.IsHydrated)
        {
            return;
        }

        CursorReconstructionHydration hydration =
            await _hydrator.HydrateAsync(
                session,
                cancellationToken).ConfigureAwait(true);
        session.ApplyHydration(hydration);
        CursorReconstructionItemViewModel? sessionNode = EnumerateItems()
            .FirstOrDefault(item => ReferenceEquals(
                item.Session,
                session));
        sessionNode?.RefreshSummary();
    }

    private void EnsureSessionChildren(
        CursorReconstructionItemViewModel? selected)
    {
        if (selected?.Session is not CursorReconstructedSession session ||
            selected.Workspace is not CursorReconstructedWorkspace workspace ||
            !session.IsInspectionLoaded)
        {
            return;
        }

        CursorReconstructionItemViewModel? node = EnumerateItems()
            .FirstOrDefault(item => ReferenceEquals(
                item.Session,
                session));
        if (node is null || node.Children.Count > 0)
        {
            return;
        }

        CursorTurnBubbleRelationIndex relationIndex = new(session);
        CursorReconstructionItemViewModel composers = new(
            CursorReconstructionItemKind.Group,
            "Composer records",
            $"{session.ComposerRecords.Count:N0} physical rows",
            "The same composerId can occur in global and workspace databases.",
            workspace,
            session);
        foreach (CursorIndexedRecord record in session.ComposerRecords)
        {
            composers.Children.Add(ComposerItem(workspace, session, record));
        }

        CursorReconstructionItemViewModel bubbles = new(
            CursorReconstructionItemKind.Group,
            "Ordered bubbles",
            $"{session.Bubbles.Count:N0} physical rows",
            "conversationHeaders preserve bubble order; bubbleId keys provide bodies.",
            workspace,
            session);
        foreach (CursorReconstructionBubble bubble in session.Bubbles)
        {
            bubbles.Children.Add(BubbleItem(
                workspace,
                session,
                bubble,
                relationIndex));
        }

        CursorReconstructionItemViewModel turns = new(
            CursorReconstructionItemKind.Group,
            "Reconstructed turns",
            $"{session.Turns.Count:N0} turns",
            "User bubbles start turns; request/tool identifiers attach subsequent events.",
            workspace,
            session);
        foreach (SessionTurn turn in session.Turns)
        {
            turns.Children.Add(TurnItem(
                workspace,
                session,
                turn,
                relationIndex));
        }

        node.Children.Add(composers);
        node.Children.Add(bubbles);
        node.Children.Add(turns);
        node.IsExpanded = true;
    }

    private CursorReconstructionItemViewModel ComposerItem(
        CursorReconstructedWorkspace workspace,
        CursorReconstructedSession session,
        CursorIndexedRecord record) =>
        new(
            CursorReconstructionItemKind.ComposerRecord,
            record.Classification.StorageKey ??
                record.Row.DisplayText,
            $"{Path.GetFileName(record.Row.DatabasePath)} / " +
            record.Row.TableName,
            "The storage key or JSON payload declares the native composerId.",
            workspace,
            session,
            record: record);

    private CursorReconstructionItemViewModel BubbleItem(
        CursorReconstructedWorkspace workspace,
        CursorReconstructedSession session,
        CursorReconstructionBubble bubble,
        CursorTurnBubbleRelationIndex relationIndex)
    {
        IReadOnlyList<CursorTurnBubbleRelation> relations =
            relationIndex.ForBubble(bubble);
        CursorTurnBubbleRelation? relation = relations.FirstOrDefault();
        return new CursorReconstructionItemViewModel(
            CursorReconstructionItemKind.Bubble,
            $"{bubble.Sequence}. " +
            (bubble.Record.Classification.BubbleId ??
                bubble.Record.Classification.StorageKey),
            relation is null
                ? bubble.IsReferencedByComposer
                    ? "Referenced by composer conversation headers"
                    : "Unreferenced bubble row"
                : relation.StartsTurn
                    ? $"Starts Turn {relation.Turn.Number}"
                    : $"Contributes to Turn {relation.Turn.Number}",
            bubble.Rule,
            workspace,
            session,
            bubble,
            bubble.Record,
            turn: relation?.Turn,
            relations: relations);
    }

    private CursorReconstructionItemViewModel TurnItem(
        CursorReconstructedWorkspace workspace,
        CursorReconstructedSession session,
        SessionTurn turn,
        CursorTurnBubbleRelationIndex relationIndex)
    {
        IReadOnlyList<CursorTurnBubbleRelation> relations =
            relationIndex.ForTurn(turn);
        CursorReconstructionItemViewModel node = new(
            CursorReconstructionItemKind.Turn,
            $"Turn {turn.Number}: {Preview(turn.Prompt, 100)}",
            $"{relations.Count:N0} source bubbles, " +
            $"{turn.Events.Count:N0} events",
            "A user prompt starts the turn; requestId and tool-call ids attach " +
            "thoughts, tools, results, and responses.",
            workspace,
            session,
            turn: turn,
            relations: relations);
        foreach (CursorTurnBubbleRelation relation in relations)
        {
            node.Children.Add(RelationItem(
                workspace,
                session,
                relation));
        }

        foreach (SessionEventRecord item in turn.Events)
        {
            node.Children.Add(EventItem(
                workspace,
                session,
                turn,
                item,
                relationIndex));
        }

        return node;
    }

    private CursorReconstructionItemViewModel RelationItem(
        CursorReconstructedWorkspace workspace,
        CursorReconstructedSession session,
        CursorTurnBubbleRelation relation) =>
        new(
            CursorReconstructionItemKind.Relation,
            relation.StartsTurn
                ? $"Prompt source: {relation.BubbleId}"
                : $"Contributing bubble: {relation.BubbleId}",
            $"{relation.Events.Count:N0} projected event" +
                (relation.Events.Count == 1 ? string.Empty : "s"),
            relation.Rule,
            workspace,
            session,
            relation.Bubble,
            relation.Bubble.Record,
            relation.Turn,
            relations: [relation]);

    private CursorReconstructionItemViewModel EventItem(
        CursorReconstructedWorkspace workspace,
        CursorReconstructedSession session,
        SessionTurn turn,
        SessionEventRecord item,
        CursorTurnBubbleRelationIndex relationIndex)
    {
        IReadOnlyList<CursorTurnBubbleRelation> relations =
            relationIndex.ForEvent(item);
        return new CursorReconstructionItemViewModel(
            CursorReconstructionItemKind.Event,
            $"{item.Role}: {EventText(item)}",
            EventSummary(item, relations.FirstOrDefault()),
            EventRule(item),
            workspace,
            session,
            turn: turn,
            eventRecord: item,
            relations: relations);
    }

    private void PopulatePipeline(CursorReconstructionItemViewModel? selected)
    {
        WorkspaceStage.Clear();
        ComposerStage.Clear();
        BubbleStage.Clear();
        OutputStage.Clear();
        if (selected?.Workspace is not CursorReconstructedWorkspace workspace)
        {
            return;
        }

        CursorReconstructionItemViewModel? workspaceNode = EnumerateItems()
            .FirstOrDefault(item => ReferenceEquals(
                item.Workspace,
                workspace));
        if (workspaceNode is null)
        {
            return;
        }

        WorkspaceStage.Add(workspaceNode);
        if (selected.Session is not CursorReconstructedSession session)
        {
            foreach (CursorReconstructionItemViewModel candidate
                     in workspaceNode.Children)
            {
                ComposerStage.Add(candidate);
            }

            return;
        }

        CursorReconstructionItemViewModel? sessionNode =
            workspaceNode.Children.FirstOrDefault(item =>
                ReferenceEquals(item.Session, session));
        if (sessionNode is null)
        {
            return;
        }

        ComposerStage.Add(sessionNode);
        CursorReconstructionItemViewModel? composers = sessionNode.Children
            .FirstOrDefault(item =>
                item.Header == "Composer records");
        foreach (CursorReconstructionItemViewModel record
                 in composers?.Children ?? [])
        {
            ComposerStage.Add(record);
        }

        CursorReconstructionItemViewModel? bubbles = sessionNode.Children
            .FirstOrDefault(item =>
                item.Header == "Ordered bubbles");
        foreach (CursorReconstructionItemViewModel bubble
                 in bubbles?.Children ?? [])
        {
            BubbleStage.Add(bubble);
        }

        CursorReconstructionItemViewModel? turns = sessionNode.Children
            .FirstOrDefault(item =>
                item.Header == "Reconstructed turns");
        foreach (CursorReconstructionItemViewModel turnItem
                 in turns?.Children ?? [])
        {
            OutputStage.Add(turnItem);
            foreach (CursorReconstructionItemViewModel item
                     in turnItem.Children)
            {
                OutputStage.Add(item);
            }
        }
    }

    private void PopulateEvidence(CursorReconstructionItemViewModel? item)
    {
        EvidenceFields.Clear();
        if (item is null)
        {
            return;
        }

        AddEvidence("Entity", item.Kind.ToString());
        AddEvidence("Rule", item.Rule);
        if (item.Session is CursorReconstructedSession session)
        {
            AddEvidence("Composer id", session.Session.NativeSessionId);
            AddEvidence("Workspace", session.Session.Workspace.DisplayName);
            AddEvidence("Model", session.Model ?? session.Session.Model);
            AddEvidence("Mode", session.Mode ?? session.Session.Mode);
            AddEvidence(
                "Reconstruction warnings",
                session.Warnings.Count == 0
                    ? null
                    : string.Join(Environment.NewLine, session.Warnings));
        }

        if (item.Turn is SessionTurn turn)
        {
            AddEvidence("Turn id", turn.Id);
            AddEvidence("Turn number", turn.Number.ToString());
            AddEvidence(
                item.Kind == CursorReconstructionItemKind.Turn
                    ? "Prompt"
                    : "Related turn prompt",
                turn.Prompt);
            CursorTurnBubbleRelation? promptSource = item.Relations
                .FirstOrDefault(relation => relation.StartsTurn);
            AddEvidence(
                "Prompt source bubble",
                promptSource?.BubbleId);
            AddEvidence(
                "Prompt SQLite key",
                promptSource?.Bubble.Record.Classification.StorageKey);
            AddEvidence(
                "Request / generation id",
                promptSource?.Events
                    .FirstOrDefault(record =>
                        record.Role == ObservationRole.PromptSubmitted)
                    ?.ParentId);
            AddEvidence(
                "Contributing bubbles",
                item.Relations.Count == 0
                    ? null
                    : string.Join(
                        Environment.NewLine,
                        item.Relations.Select(relation =>
                            $"{(relation.StartsTurn ? "prompt" : "contributes")}: " +
                            relation.BubbleId)));
        }

        if (item.Record is CursorIndexedRecord record)
        {
            AddEvidence("Database", record.Row.DatabasePath);
            AddEvidence("Table", record.Row.TableName);
            AddEvidence("Storage key", record.Classification.StorageKey);
            AddEvidence("Record kind", record.Classification.Kind.ToString());
            AddEvidence("Bubble id", record.Classification.BubbleId);
        }

        SessionSourceProvenance? provenance = Provenance(item);
        if (provenance is not null)
        {
            AddEvidence("Source kind", provenance.SourceKind.ToString());
            AddEvidence("Source path", provenance.Path);
            AddEvidence("Database key", provenance.DatabaseKey);
            AddEvidence("Record id", provenance.RecordId);
            AddEvidence("Parent record", provenance.ParentRecordId);
            AddEvidence("Line", provenance.LineNumber?.ToString());
            AddEvidence("Byte offset", provenance.ByteOffset?.ToString());
        }

        if (item.EventRecord?.SupplementalProvenance.Count > 0)
        {
            AddEvidence(
                "Supplemental sources",
                string.Join(
                    Environment.NewLine,
                    item.EventRecord.SupplementalProvenance.Select(source =>
                        $"{source.SourceKind}: {source.Path} " +
                        $"{source.DatabaseKey ?? source.RecordId}")));
        }
    }

    private async Task LoadRawSourceAsync(
        CursorReconstructionItemViewModel? item,
        CancellationToken cancellationToken)
    {
        RawSource = string.Empty;
        JsonSource = string.Empty;
        RawStatus = string.Empty;
        SessionSourceProvenance? provenance = Provenance(item);
        if (provenance is null)
        {
            RawStatus = "No physical source is associated with this node.";
            return;
        }

        try
        {
            SessionRawSourceContent content = await _rawSourceResolver.LoadAsync(
                provenance,
                RawPreviewBytes,
                cancellationToken).ConfigureAwait(true);
            RawSource = content.Content;
            JsonSource = CursorRowViewModel.FormatJson(content.Content);
            RawStatus = content.Warning ??
                (content.IsTruncated
                    ? $"Preview truncated at {RawPreviewBytes:N0} bytes."
                    : provenance.Format);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                Microsoft.Data.Sqlite.SqliteException)
        {
            RawStatus = $"Could not load evidence: {exception.Message}";
        }
    }

    private SessionSourceProvenance? Provenance(
        CursorReconstructionItemViewModel? item) =>
        RowProvenance(item?.Record) ??
        item?.EventRecord?.Provenance ??
        item?.Turn?.Events.FirstOrDefault()?.Provenance ??
        item?.Session?.Session.Sources.FirstOrDefault();

    private SessionSourceProvenance? RowProvenance(
        CursorIndexedRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        string? databaseKey = DatabaseKey(record);
        if (string.IsNullOrWhiteSpace(databaseKey))
        {
            return null;
        }

        return new SessionSourceProvenance(
            SessionSourceKind.CursorDesktopSqlite,
            record.Row.DatabasePath,
            "cursor-sqlite-inspection",
            DatabaseKey: databaseKey,
            RawSource: new SessionRawSourceReference(
                SessionRawSourceKind.SqliteValue,
                record.Row.DatabasePath,
                DatabaseTable: record.Row.TableName,
                DatabaseKey: databaseKey));
    }

    private string? DatabaseKey(CursorIndexedRecord record)
    {
        foreach (string columnName in new[] { "key", "composerId", "id" })
        {
            KeyValuePair<string, object?> match =
                record.Row.KeyValues.FirstOrDefault(item =>
                    string.Equals(
                        item.Key,
                        columnName,
                        StringComparison.OrdinalIgnoreCase));
            if (match.Value is not null)
            {
                return Convert.ToString(match.Value);
            }
        }

        string? storageKey = record.Classification.StorageKey;
        const string composerHeaderPrefix = "composerHeaders:";
        return string.Equals(
                   record.Row.TableName,
                   "composerHeaders",
                   StringComparison.OrdinalIgnoreCase) &&
               storageKey?.StartsWith(
                   composerHeaderPrefix,
                   StringComparison.Ordinal) == true
            ? storageKey[composerHeaderPrefix.Length..]
            : storageKey;
    }

    private CursorRowReference? PhysicalSource(
        CursorReconstructionItemViewModel? item)
    {
        if (item?.Record is CursorIndexedRecord record)
        {
            return record.Row;
        }

        SessionSourceProvenance? provenance = PhysicalProvenance(item);
        SessionRawSourceReference? source = provenance?.RawSource;
        if (source?.Kind != SessionRawSourceKind.SqliteValue ||
            string.IsNullOrWhiteSpace(source.DatabaseTable) ||
            string.IsNullOrWhiteSpace(source.DatabaseKey))
        {
            return null;
        }

        string keyColumn = string.Equals(
            source.DatabaseTable,
            "composerHeaders",
            StringComparison.OrdinalIgnoreCase)
            ? "composerId"
            : "key";
        return new CursorRowReference(
            source.Path,
            source.DatabaseTable,
            new Dictionary<string, object?>
            {
                [keyColumn] = source.DatabaseKey
            });
    }

    private SessionSourceProvenance? PhysicalProvenance(
        CursorReconstructionItemViewModel? item)
    {
        IEnumerable<SessionSourceProvenance> sources =
            item?.EventRecord is SessionEventRecord eventRecord
                ? new[] { eventRecord.Provenance }
                    .Concat(eventRecord.SupplementalProvenance)
                : item?.Turn is SessionTurn turn
                    ? turn.Events.SelectMany(eventItem =>
                        new[] { eventItem.Provenance }
                            .Concat(eventItem.SupplementalProvenance))
                    : item?.Session?.Session.Sources ?? [];
        return sources.FirstOrDefault(source =>
            source.RawSource?.Kind == SessionRawSourceKind.SqliteValue);
    }

    private void UpdateRelationHighlights(
        CursorReconstructionItemViewModel? selected)
    {
        HashSet<string> turnIds = selected?.Relations
            .Select(relation => relation.Turn.Id)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        if (selected?.Turn is SessionTurn turn)
        {
            turnIds.Add(turn.Id);
        }

        HashSet<string> bubbleKeys = selected?.Relations
            .Select(relation => relation.Bubble.Record.Row.StableKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        if (selected?.Bubble is CursorReconstructionBubble bubble)
        {
            bubbleKeys.Add(bubble.Record.Row.StableKey);
        }

        foreach (CursorReconstructionItemViewModel item in EnumerateItems())
        {
            bool highlight = (item.Kind is
                    CursorReconstructionItemKind.Bubble or
                    CursorReconstructionItemKind.Turn or
                    CursorReconstructionItemKind.Relation) &&
                item.Relations.Any(relation =>
                    turnIds.Contains(relation.Turn.Id) ||
                    bubbleKeys.Contains(
                        relation.Bubble.Record.Row.StableKey));
            item.SetRelationHighlighted(highlight);
        }
    }

    private void SelectTreeItem(CursorReconstructionItemViewModel? selected)
    {
        foreach (CursorReconstructionItemViewModel root in Roots)
        {
            SelectTreeItem(root, selected);
        }
    }

    private bool SelectTreeItem(
        CursorReconstructionItemViewModel node,
        CursorReconstructionItemViewModel? selected)
    {
        node.IsSelected = ReferenceEquals(node, selected);
        bool contains = node.IsSelected;
        foreach (CursorReconstructionItemViewModel child in node.Children)
        {
            contains |= SelectTreeItem(child, selected);
        }

        if (contains)
        {
            node.IsExpanded = true;
        }

        return contains;
    }

    private IEnumerable<CursorReconstructionItemViewModel> EnumerateItems() =>
        Roots.SelectMany(EnumerateItem);

    private IEnumerable<CursorReconstructionItemViewModel> EnumerateItem(
        CursorReconstructionItemViewModel item)
    {
        yield return item;
        foreach (CursorReconstructionItemViewModel child in item.Children)
        {
            foreach (CursorReconstructionItemViewModel descendant in
                     EnumerateItem(child))
            {
                yield return descendant;
            }
        }
    }

    private void AddEvidence(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            EvidenceFields.Add(new CursorCellViewModel(name, "Text", value));
        }
    }

    private string EventText(SessionEventRecord item) =>
        Preview(
            item.ToolName ??
            item.McpToolName ??
            item.PromptText ??
            item.Text ??
            item.NativeName,
            100);

    private string EventSummary(
        SessionEventRecord item,
        CursorTurnBubbleRelation? relation)
    {
        List<string> parts = [item.EventKind.ToString()];
        if (relation is not null)
        {
            parts.Add($"bubble={relation.BubbleId}");
        }

        if (!string.IsNullOrWhiteSpace(item.ToolCallId))
        {
            parts.Add($"toolCallId={item.ToolCallId}");
        }

        if (!string.IsNullOrWhiteSpace(item.ParentId))
        {
            parts.Add($"parent={item.ParentId}");
        }

        return string.Join(", ", parts);
    }

    private string EventRule(SessionEventRecord item) =>
        item.Role switch
        {
            ObservationRole.PromptSubmitted =>
                "A user bubble starts a new turn.",
            ObservationRole.AgentThought =>
                "Thinking blocks or thought-marked assistant bubbles become thoughts.",
            ObservationRole.ToolRequest =>
                "toolFormerData creates a tool request; toolCallId/requestId " +
                "provides correlation.",
            ObservationRole.ToolSuccess or ObservationRole.ToolFailure =>
                "Tool output/status is paired to the matching tool request.",
            ObservationRole.AgentResponse =>
                "A non-tool assistant bubble becomes the assistant response.",
            _ => "The bubble role, type, and identifiers determine this event."
        };

    private string Preview(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(empty)";
        }

        string normalized = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return normalized.Length <= maximum
            ? normalized
            : normalized[..(maximum - 1)] + "…";
    }
}
