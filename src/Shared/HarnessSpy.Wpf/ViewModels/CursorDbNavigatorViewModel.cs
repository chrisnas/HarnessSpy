using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Cursor.Inspection;
using Microsoft.Data.Sqlite;

namespace HarnessSpy.Wpf.ViewModels;

public sealed class CursorDbNavigatorViewModel : ObservableObject, IDisposable
{
    private readonly SessionDiscoveryContext _context;
    private readonly CursorDbNavigatorSettings _settings;
    private readonly Action<CursorDbNavigatorSettings> _saveSettings;
    private readonly Dispatcher _dispatcher;
    private readonly CursorInspectionCatalogBuilder _catalogBuilder;
    private readonly CursorSqliteConnectionFactory _connectionFactory;
    private readonly CursorSqliteRowReader _rowReader;
    private readonly CursorSqliteRecordProjector _projector;
    private readonly CursorReconstructionViewModel _reconstruction;
    private readonly Dictionary<string, CursorInspectionCatalogSnapshot>
        _sessionInspections = new(StringComparer.Ordinal);
    private readonly List<string> _customDatabasePaths = [];
    private readonly List<NavigationLocation> _backHistory = [];
    private readonly List<NavigationLocation> _forwardHistory = [];
    private CursorInspectionCatalogSnapshot? _catalog;
    private CursorDatabaseDescriptor? _selectedDatabase;
    private CursorSqliteTableSchema? _selectedTable;
    private CursorRowViewModel? _selectedRow;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _reconstructionSelectionCancellation;
    private string? _externalEvidencePath;
    private string? _externalEvidenceText;
    private string _filterText = string.Empty;
    private string _workspaceFilterText = string.Empty;
    private string _statusText = "Ready.";
    private string _warningText = string.Empty;
    private string _selectedTableTitle = "Select a table";
    private string _breadcrumb = "No database selected";
    private string _pageStatus = string.Empty;
    private long _pageOffset;
    private int _pageSize;
    private int _selectedMainTabIndex;
    private bool _isBusy;
    private bool _canMovePreviousPage;
    private bool _canMoveNextPage;
    private bool _canNavigateBack;
    private bool _canNavigateForward;
    private bool _isPhysicalInitialized;
    private bool _isReconstructionBusy;
    private bool _suppressHistory;
    private bool _disposed;

    private sealed record NavigationLocation(
        CursorRowReference? Row,
        string? ExternalPath,
        string? FilterText);

    private sealed record CursorTableSelection(
        CursorDatabaseDescriptor Database,
        CursorSqliteTableSchema Table);

    public CursorDbNavigatorViewModel(
        SessionDiscoveryContext context,
        CursorDbNavigatorSettings settings,
        Action<CursorDbNavigatorSettings> saveSettings,
        Dispatcher dispatcher)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _saveSettings = saveSettings ??
            throw new ArgumentNullException(nameof(saveSettings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _pageSize = PageSizes.Contains(settings.PageSize)
            ? settings.PageSize
            : 200;
        _connectionFactory = new CursorSqliteConnectionFactory();
        CursorSqliteSchemaReader schemaReader = new();
        _rowReader = new CursorSqliteRowReader(schemaReader);
        _projector = new CursorSqliteRecordProjector();
        _reconstruction = new CursorReconstructionViewModel(context);
        _catalogBuilder = new CursorInspectionCatalogBuilder(
            context,
            connectionFactory: _connectionFactory,
            schemaReader: schemaReader,
            rowReader: _rowReader,
            projector: _projector);
        if (!string.IsNullOrWhiteSpace(settings.LastDatabasePath))
        {
            _customDatabasePaths.Add(settings.LastDatabasePath);
        }
    }

    public ObservableCollection<CursorSchemaNodeViewModel> SchemaRoots { get; } =
        [];

    public ObservableCollection<CursorRowViewModel> Rows { get; } = [];

    public ObservableCollection<CursorCellViewModel> SelectedCells { get; } = [];

    public ObservableCollection<CursorRelationViewModel> OutboundRelations
    {
        get;
    } = [];

    public ObservableCollection<CursorRelationViewModel> InboundRelations
    {
        get;
    } = [];

    public ObservableCollection<CursorRelationViewModel> Correlations { get; } =
        [];

    public CursorReconstructionViewModel Reconstruction => _reconstruction;

    public IReadOnlyList<int> PageSizes { get; } = [50, 100, 200, 500];

    public int PageSize
    {
        get => _pageSize;
        set => SetProperty(ref _pageSize, value);
    }

    public int SelectedMainTabIndex
    {
        get => _selectedMainTabIndex;
        set
        {
            if (SetProperty(ref _selectedMainTabIndex, value) && value == 0)
            {
                UpdateReconstructionBreadcrumb(
                    Reconstruction.SelectedItem);
            }
        }
    }

    public double DatabasePaneWidth => Math.Max(190, _settings.DatabasePaneWidth);

    public double RowsPaneWidth => Math.Max(340, _settings.RowsPaneWidth);

    public CursorRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                OnPropertyChanged(nameof(HasSelectedRow));
                OnPropertyChanged(nameof(SelectedRowTitle));
                OnPropertyChanged(nameof(SelectedRowLocation));
                OnPropertyChanged(nameof(SelectedJsonText));
                OnPropertyChanged(nameof(SelectedRawText));
            }
        }
    }

    public bool HasSelectedRow => SelectedRow is not null;

    public string SelectedRowTitle =>
        _externalEvidencePath is not null
            ? $"{ExternalEvidenceKind(_externalEvidencePath)} — " +
                Path.GetFileName(_externalEvidencePath)
            : SelectedRow is null
            ? string.Empty
            : $"{SelectedRow.RecordKindText} — {SelectedRow.KeyText}";

    public string SelectedRowLocation =>
        _externalEvidencePath ??
        SelectedRow?.Reference.DisplayText ??
        string.Empty;

    public string SelectedRawText =>
        _externalEvidenceText ??
        SelectedRow?.RawText ??
        string.Empty;

    public string SelectedJsonText =>
        _externalEvidenceText is not null
            ? CursorRowViewModel.FormatJson(_externalEvidenceText)
            : SelectedRow?.JsonText ??
                string.Empty;

    public string SelectedTableTitle
    {
        get => _selectedTableTitle;
        private set => SetProperty(ref _selectedTableTitle, value);
    }

    public string Breadcrumb
    {
        get => _breadcrumb;
        private set => SetProperty(ref _breadcrumb, value);
    }

    public string PageStatus
    {
        get => _pageStatus;
        private set => SetProperty(ref _pageStatus, value);
    }

    public string FilterText
    {
        get => _filterText;
        set => SetProperty(ref _filterText, value);
    }

    public string WorkspaceFilterText
    {
        get => _workspaceFilterText;
        set => SetProperty(ref _workspaceFilterText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string WarningText
    {
        get => _warningText;
        private set
        {
            if (SetProperty(ref _warningText, value))
            {
                OnPropertyChanged(nameof(HasWarnings));
            }
        }
    }

    public bool HasWarnings => WarningText.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanRefresh));
                OnPropertyChanged(nameof(IsComputing));
            }
        }
    }

    public bool IsComputing => IsBusy || _isReconstructionBusy;

    public bool CanRefresh => !IsBusy;

    public bool CanMovePreviousPage
    {
        get => _canMovePreviousPage;
        private set => SetProperty(ref _canMovePreviousPage, value);
    }

    public bool CanMoveNextPage
    {
        get => _canMoveNextPage;
        private set => SetProperty(ref _canMoveNextPage, value);
    }

    public bool CanNavigateBack
    {
        get => _canNavigateBack;
        private set => SetProperty(ref _canNavigateBack, value);
    }

    public bool CanNavigateForward
    {
        get => _canNavigateForward;
        private set => SetProperty(ref _canNavigateForward, value);
    }

    public Task InitializeAsync() => RefreshSessionShellsAsync();

    public Task DiscoverStoresAsync() =>
        EnsurePhysicalStorageLoadedAsync(forceReload: true);

    public Task EnsurePhysicalStorageLoadedAsync() =>
        EnsurePhysicalStorageLoadedAsync(forceReload: false);

    private Task EnsurePhysicalStorageLoadedAsync(
        bool forceReload,
        bool initializePhysicalPage = true) =>
        RunBusyAsync(
            "Discovering Cursor databases…",
            async cancellationToken =>
            {
                _dispatcher.VerifyAccess();
                if (_catalog is null || forceReload)
                {
                    _catalog = await _catalogBuilder.BuildAsync(
                        _customDatabasePaths,
                        cancellationToken).ConfigureAwait(true);
                    _isPhysicalInitialized = false;
                    PopulateSchema(_catalog);
                }

                WarningText = string.Join(
                    Environment.NewLine,
                    _catalog.Warnings
                        .Distinct(StringComparer.Ordinal)
                        .Take(20));
                if (_catalog.Databases.Count == 0)
                {
                    ClearSelection();
                    StatusText = "No readable Cursor state database was found.";
                    return;
                }

                CursorInspectedDatabase selected =
                    PreferredDatabase(_catalog.Databases);
                CursorSqliteTableSchema? table = PreferredTable(selected.Tables);
                if (table is null)
                {
                    _selectedDatabase = selected.Database;
                    SelectedTableTitle = "No user tables";
                    Breadcrumb = selected.Database.Path;
                    StatusText =
                        $"'{selected.Database.Path}' contains no user tables.";
                    return;
                }

                if (initializePhysicalPage && !_isPhysicalInitialized)
                {
                    SetSchemaSelection(selected.Database.Path, table.Name);
                    await SelectTableAsync(
                        selected.Database,
                        table,
                        cancellationToken).ConfigureAwait(true);
                    _isPhysicalInitialized = true;
                }

                StatusText =
                    $"Discovered {_catalog.Databases.Count:N0} database(s), " +
                    $"{_catalog.Databases.Sum(item => item.Tables.Count):N0} " +
                    $"table(s), and {_catalog.Records.Count:N0} indexed Cursor record(s).";
                if (SelectedMainTabIndex == 0)
                {
                    UpdateReconstructionBreadcrumb(
                        Reconstruction.SelectedItem);
                }
            });

    public async Task OpenCustomDatabaseAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!_customDatabasePaths.Contains(
                fullPath,
                StringComparer.OrdinalIgnoreCase))
        {
            _customDatabasePaths.Add(fullPath);
        }

        _settings.LastDatabasePath = fullPath;
        _saveSettings(_settings);
        await EnsurePhysicalStorageLoadedAsync(
            forceReload: true).ConfigureAwait(true);
        SelectedMainTabIndex = 1;
    }

    public Task RefreshAsync() => RefreshSessionShellsAsync();

    public async Task SelectReconstructionItemAsync(
        CursorReconstructionItemViewModel? item)
    {
        CancellationTokenSource next = new();
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _reconstructionSelectionCancellation,
            next);
        previous?.Cancel();
        SetReconstructionBusy(true);
        try
        {
            await WaitForIdleAsync(next.Token).ConfigureAwait(true);
            if (item?.Kind == CursorReconstructionItemKind.Session &&
                item.Session is CursorReconstructedSession requestedSession)
            {
                string sessionId =
                    requestedSession.Session.NativeSessionId;
                item = Reconstruction.FindSession(sessionId) ?? item;
                if (item.Session?.IsInspectionLoaded != true)
                {
                    bool loaded = await LoadSessionInspectionAsync(
                        sessionId,
                        next.Token).ConfigureAwait(true);
                    if (!loaded)
                    {
                        return;
                    }

                    item = Reconstruction.FindSession(sessionId) ?? item;
                }
            }

            await Reconstruction.SelectAsync(
                item,
                next.Token).ConfigureAwait(true);
            UpdateReconstructionBreadcrumb(item);
        }
        catch (OperationCanceledException)
            when (next.IsCancellationRequested)
        {
        }
        finally
        {
            bool isLatest = ReferenceEquals(
                Interlocked.CompareExchange(
                    ref _reconstructionSelectionCancellation,
                    null,
                    next),
                next);
            if (isLatest)
            {
                SetReconstructionBusy(false);
            }

            next.Dispose();
        }
    }

    public async Task OpenReconstructionPhysicalSourceAsync()
    {
        CursorRowReference? source =
            Reconstruction.SelectedPhysicalSource();
        if (source is null)
        {
            return;
        }

        string? sessionId =
            Reconstruction.SelectedItem?.Session?.Session.NativeSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            WarningText =
                "The selected reconstruction node has no owning session.";
            return;
        }

        await RunBusyAsync(
            "Opening physical source…",
            async cancellationToken =>
            {
                if (!_sessionInspections.TryGetValue(
                        sessionId,
                        out CursorInspectionCatalogSnapshot? inspection))
                {
                    inspection = await _catalogBuilder.BuildSessionAsync(
                        sessionId,
                        _customDatabasePaths,
                        cancellationToken).ConfigureAwait(true);
                    _sessionInspections[sessionId] = inspection;
                }

                _catalog = inspection;
                PopulateSchema(inspection);
                Rows.Clear();
                await SelectRowAsync(null).ConfigureAwait(true);
                await NavigateToRowAsync(
                    source,
                    cancellationToken).ConfigureAwait(true);
                if (SelectedRow is null)
                {
                    return;
                }

                _isPhysicalInitialized = true;
                SelectedMainTabIndex = 1;
            }).ConfigureAwait(true);
    }

    public Task GoToReconstructionSourceBubbleAsync() =>
        SelectReconstructionItemAsync(
            Reconstruction.SelectedSourceBubble());

    public void ApplyWorkspaceFilter()
    {
        if (_catalog is null)
        {
            return;
        }

        PopulateSchema(_catalog);
        StatusText = string.IsNullOrWhiteSpace(WorkspaceFilterText)
            ? $"Showing all {_catalog.Databases.Count:N0} database(s)."
            : $"Filtered databases by '{WorkspaceFilterText}'.";
    }

    public Task SelectSchemaNodeAsync(CursorSchemaNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Model is not CursorTableSelection selection)
        {
            return Task.CompletedTask;
        }

        return RunBusyAsync(
            $"Reading {selection.Table.Name}…",
            cancellationToken => SelectTableAsync(
                selection.Database,
                selection.Table,
                cancellationToken));
    }

    public Task SelectRowAsync(CursorRowViewModel? row)
    {
        CursorRowViewModel? previous = SelectedRow;
        if (!_suppressHistory &&
            previous is not null &&
            row is not null &&
            !string.Equals(
                previous.Reference.StableKey,
                row.Reference.StableKey,
                StringComparison.OrdinalIgnoreCase))
        {
            _backHistory.Add(new NavigationLocation(
                previous.Reference,
                null,
                FilterText));
            _forwardHistory.Clear();
            UpdateHistoryState();
        }

        _externalEvidencePath = null;
        _externalEvidenceText = null;
        SelectedRow = row;
        OnPropertyChanged(nameof(SelectedRowTitle));
        OnPropertyChanged(nameof(SelectedRowLocation));
        OnPropertyChanged(nameof(SelectedJsonText));
        OnPropertyChanged(nameof(SelectedRawText));
        SelectedCells.Clear();
        OutboundRelations.Clear();
        InboundRelations.Clear();
        Correlations.Clear();
        if (row?.Source is CursorSqliteRowSnapshot source)
        {
            foreach (CursorSqliteCell cell in source.Cells)
            {
                SelectedCells.Add(new CursorCellViewModel(
                    cell.Column.Name,
                    cell.IsTruncated
                        ? $"{cell.Kind} (truncated)"
                        : cell.Kind.ToString(),
                    cell.IsTruncated
                        ? CellText(cell) + Environment.NewLine +
                            $"[Value truncated; original size " +
                            $"{cell.ByteLength:N0} bytes]"
                        : CellText(cell)));
            }
        }

        if (row is not null && _catalog is not null)
        {
            foreach (CursorInspectionRelation relation in
                     _catalog.RelationIndex.Outbound(row.Reference))
            {
                OutboundRelations.Add(new CursorRelationViewModel(relation));
            }

            foreach (CursorInspectionRelation relation in
                     _catalog.RelationIndex.Inbound(row.Reference))
            {
                InboundRelations.Add(new CursorRelationViewModel(
                    relation,
                    inbound: true));
            }

            foreach (CursorInspectionRelation relation in
                     _catalog.CorrelationIndex.ForRow(row.Reference))
            {
                Correlations.Add(new CursorRelationViewModel(relation));
            }
        }

        return Task.CompletedTask;
    }

    public Task MovePreviousPageAsync() =>
        RunBusyAsync(
            "Reading previous page…",
            async cancellationToken =>
            {
                _pageOffset = Math.Max(0, _pageOffset - PageSize);
                await LoadPageAsync(cancellationToken).ConfigureAwait(true);
            });

    public Task MoveNextPageAsync() =>
        RunBusyAsync(
            "Reading next page…",
            async cancellationToken =>
            {
                _pageOffset += PageSize;
                await LoadPageAsync(cancellationToken).ConfigureAwait(true);
            });

    public Task ApplyFilterAsync() =>
        RunBusyAsync(
            "Applying row filter…",
            async cancellationToken =>
            {
                _pageOffset = 0;
                await LoadPageAsync(cancellationToken).ConfigureAwait(true);
            });

    public Task ChangePageSizeAsync()
    {
        _settings.PageSize = PageSize;
        _saveSettings(_settings);
        return RunBusyAsync(
            "Changing page size…",
            async cancellationToken =>
            {
                _pageOffset = 0;
                await LoadPageAsync(cancellationToken).ConfigureAwait(true);
            });
    }

    public async Task NavigateToRelationAsync(CursorRelationViewModel relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        if (relation.Relation.Target is CursorRowReference target)
        {
            if (SelectedRow is not null)
            {
                _backHistory.Add(new NavigationLocation(
                    SelectedRow.Reference,
                    null,
                    FilterText));
                _forwardHistory.Clear();
                UpdateHistoryState();
            }

            await RunBusyAsync(
                "Following database relation…",
                cancellationToken => NavigateToRowAsync(
                    target,
                    cancellationToken)).ConfigureAwait(true);
            return;
        }

        string? path = relation.Relation.ExternalTarget?.Path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusText =
                $"The relation target '{relation.TargetText}' is unresolved.";
            return;
        }

        NavigationLocation? current = CurrentLocation();
        if (current is not null)
        {
            _backHistory.Add(current);
            _forwardHistory.Clear();
            UpdateHistoryState();
        }

        await RunBusyAsync(
            "Loading external evidence…",
            cancellationToken => ShowExternalEvidenceAsync(
                path,
                cancellationToken)).ConfigureAwait(true);
    }

    public void NavigateBack() => _ = NavigateBackAsync();

    public async Task NavigateBackAsync()
    {
        if (_backHistory.Count == 0 || IsBusy)
        {
            return;
        }

        NavigationLocation target = Pop(_backHistory);
        NavigationLocation? current = CurrentLocation();
        if (current is not null)
        {
            _forwardHistory.Add(current);
        }

        UpdateHistoryState();
        await RestoreLocationAsync(target).ConfigureAwait(true);
    }

    public void NavigateForward() => _ = NavigateForwardAsync();

    public async Task NavigateForwardAsync()
    {
        if (_forwardHistory.Count == 0 || IsBusy)
        {
            return;
        }

        NavigationLocation target = Pop(_forwardHistory);
        NavigationLocation? current = CurrentLocation();
        if (current is not null)
        {
            _backHistory.Add(current);
        }

        UpdateHistoryState();
        await RestoreLocationAsync(target).ConfigureAwait(true);
    }

    public void SaveLayout(double databasePaneWidth, double rowsPaneWidth)
    {
        _settings.DatabasePaneWidth = databasePaneWidth;
        _settings.RowsPaneWidth = rowsPaneWidth;
        _settings.PageSize = PageSize;
        _saveSettings(_settings);
    }

    public void Dispose()
    {
        _disposed = true;
        CancellationTokenSource? cancellation = Interlocked.Exchange(
            ref _operationCancellation,
            null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        CancellationTokenSource? selectionCancellation =
            Interlocked.Exchange(
                ref _reconstructionSelectionCancellation,
                null);
        selectionCancellation?.Cancel();
        selectionCancellation?.Dispose();
        Reconstruction.Dispose();
    }

    private Task RefreshSessionShellsAsync() =>
        RunBusyAsync(
            "Loading Cursor workspaces and sessions…",
            async cancellationToken =>
            {
                _catalog = null;
                _sessionInspections.Clear();
                _isPhysicalInitialized = false;
                ClearSelection();
                IReadOnlyList<string> warnings =
                    await Reconstruction.RefreshSkeletonAsync(
                        cancellationToken).ConfigureAwait(true);
                WarningText = string.Join(
                    Environment.NewLine,
                    warnings
                        .Distinct(StringComparer.Ordinal)
                        .Take(20));
                int sessionCount = Reconstruction.Roots.Sum(
                    root => root.Children.Count);
                StatusText =
                    $"Loaded {Reconstruction.Roots.Count:N0} workspace(s) " +
                    $"and {sessionCount:N0} session(s). " +
                    "Select a session to load its details.";
                UpdateReconstructionBreadcrumb(null);
            });

    private async Task<bool> LoadSessionInspectionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        await RunBusyAsync(
            $"Loading session '{sessionId}'…",
            async cancellationToken =>
            {
                CursorInspectionCatalogSnapshot inspection =
                    await _catalogBuilder.BuildSessionAsync(
                        sessionId,
                        _customDatabasePaths,
                        cancellationToken).ConfigureAwait(true);
                _sessionInspections[sessionId] = inspection;
                IReadOnlyList<string> reconstructionWarnings =
                    Reconstruction.ApplySessionInspection(
                        sessionId,
                        inspection);
                WarningText = string.Join(
                    Environment.NewLine,
                    inspection.Warnings
                        .Concat(reconstructionWarnings)
                        .Distinct(StringComparer.Ordinal)
                        .Take(20));
                StatusText =
                    $"Loaded {inspection.Records.Count:N0} indexed row(s) " +
                    $"for session '{sessionId}'.";
            },
            cancellationToken).ConfigureAwait(true);
        return Reconstruction.FindSession(sessionId)?.Session
            ?.IsInspectionLoaded == true;
    }

    private async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        while (IsBusy && !_disposed)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(25),
                cancellationToken).ConfigureAwait(true);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void SetReconstructionBusy(bool value)
    {
        if (_isReconstructionBusy == value)
        {
            return;
        }

        _isReconstructionBusy = value;
        OnPropertyChanged(nameof(IsComputing));
    }

    private async Task SelectTableAsync(
        CursorDatabaseDescriptor database,
        CursorSqliteTableSchema table,
        CancellationToken cancellationToken)
    {
        _selectedDatabase = database;
        _selectedTable = table;
        _pageOffset = 0;
        FilterText = string.Empty;
        await LoadPageAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task LoadPageAsync(CancellationToken cancellationToken)
    {
        if (_selectedDatabase is null || _selectedTable is null)
        {
            ClearRows();
            return;
        }

        await using SqliteConnection connection =
            _connectionFactory.Create(_selectedDatabase);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(true);
        CursorSqliteRowFilter? filter = CreateFilter(_selectedTable);
        CursorSqlitePagedResult page = await _rowReader.ReadPageAsync(
            connection,
            _selectedTable,
            _pageOffset,
            PageSize,
            filter,
            cancellationToken).ConfigureAwait(true);
        Rows.Clear();
        for (int index = 0; index < page.Rows.Count; index++)
        {
            Rows.Add(CreateRowViewModel(
                _selectedDatabase,
                page.Rows[index],
                page.Offset + index));
        }

        _suppressHistory = true;
        try
        {
            await SelectRowAsync(null).ConfigureAwait(true);
        }
        finally
        {
            _suppressHistory = false;
        }

        CanMovePreviousPage = page.Offset > 0;
        CanMoveNextPage = page.HasMore;
        long first = page.Rows.Count == 0 ? 0 : page.Offset + 1;
        long last = page.Offset + page.Rows.Count;
        PageStatus = page.Rows.Count == 0
            ? "No rows"
            : $"Rows {first:N0}–{last:N0}";
        SelectedTableTitle =
            $"{_selectedTable.Name} ({_selectedTable.Columns.Count:N0} columns)";
        Breadcrumb =
            $"{_selectedDatabase.Path}  ›  {_selectedTable.Name}";
        StatusText =
            $"Read {page.Rows.Count:N0} row(s) from " +
            $"'{_selectedTable.Name}' using a read-only connection.";
    }

    private CursorSqliteRowFilter? CreateFilter(
        CursorSqliteTableSchema table)
    {
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return null;
        }

        CursorSqliteColumnSchema? column =
            table.FindColumn("key") ??
            table.PrimaryKeyColumns.FirstOrDefault() ??
            table.Columns.FirstOrDefault(item => item.IsKeyLike);
        if (column is null)
        {
            WarningText =
                $"Table '{table.Name}' has no key-like column to filter.";
            return null;
        }

        bool exact = FilterText.StartsWith("=", StringComparison.Ordinal);
        string value = exact ? FilterText[1..] : FilterText;
        return new CursorSqliteRowFilter(
            column.Name,
            value,
            exact
                ? CursorSqliteFilterMode.Exact
                : CursorSqliteFilterMode.Prefix);
    }

    private CursorRowViewModel CreateRowViewModel(
        CursorDatabaseDescriptor database,
        CursorSqliteRowSnapshot row,
        long pageOrdinal)
    {
        IReadOnlySet<string> composerIds =
            _catalog?.KnownComposerIds ??
            new HashSet<string>(StringComparer.Ordinal);
        CursorIndexedRecord projected = _projector.Project(
            database,
            row,
            composerIds,
            pageOrdinal);
        CursorIndexedRecord record =
            _catalog?.FindRecord(projected.Row) ?? projected;
        IReadOnlyDictionary<string, object?> cells = _projector.Cells(row);
        CursorSqliteCell? valueCell = row.Cells.FirstOrDefault(cell =>
            string.Equals(
                cell.Column.Name,
                "value",
                StringComparison.OrdinalIgnoreCase));
        long valueBytes = valueCell?.ByteLength ??
            row.Cells.Sum(cell => cell.ByteLength ?? 0);
        string preview = valueCell?.Preview ??
            string.Join(
                " | ",
                row.Cells
                    .Where(cell => cell.Kind != CursorSqliteCellKind.Null)
                    .Take(3)
                    .Select(cell =>
                        $"{cell.Column.Name}={cell.Preview}"));
        return new CursorRowViewModel(
            row,
            record.Row,
            cells,
            record.Classification,
            valueBytes,
            preview);
    }

    private async Task NavigateToRowAsync(
        CursorRowReference target,
        CancellationToken cancellationToken)
    {
        if (_catalog is null)
        {
            return;
        }

        CursorInspectedDatabase? database = _catalog.Databases.FirstOrDefault(
            item => string.Equals(
                item.Database.Path,
                target.DatabasePath,
                StringComparison.OrdinalIgnoreCase));
        CursorSqliteTableSchema? table = database?.Tables.FirstOrDefault(
            item => string.Equals(
                item.Name,
                target.TableName,
                StringComparison.OrdinalIgnoreCase));
        if (database is null || table is null)
        {
            WarningText =
                $"Relation target '{target.DisplayText}' is no longer available.";
            return;
        }

        CursorSqliteRowLocator? locator = CreateLocator(table, target);
        if (locator is null)
        {
            WarningText =
                "This row has neither a primary key nor a rowid and cannot " +
                "be addressed outside its original page.";
            return;
        }

        await using SqliteConnection connection =
            _connectionFactory.Create(database.Database);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(true);
        CursorSqliteRowSnapshot? row = await _rowReader.ReadRowAsync(
            connection,
            table,
            locator,
            cancellationToken).ConfigureAwait(true);
        if (row is null)
        {
            WarningText =
                $"Relation target '{target.DisplayText}' no longer exists.";
            return;
        }

        _selectedDatabase = database.Database;
        _selectedTable = table;
        FilterText = string.Empty;
        _pageOffset = 0;
        CursorRowViewModel viewModel = CreateRowViewModel(
            database.Database,
            row,
            target.PageOrdinal ?? 0);
        Rows.Clear();
        Rows.Add(viewModel);
        SetSchemaSelection(database.Database.Path, table.Name);
        _suppressHistory = true;
        try
        {
            await SelectRowAsync(viewModel).ConfigureAwait(true);
        }
        finally
        {
            _suppressHistory = false;
        }

        CanMovePreviousPage = false;
        CanMoveNextPage = false;
        PageStatus = "Direct relation target";
        SelectedTableTitle =
            $"{table.Name} ({table.Columns.Count:N0} columns)";
        Breadcrumb = $"{database.Database.Path}  ›  {table.Name}  ›  " +
            viewModel.LocatorText;
        StatusText = $"Navigated to {target.DisplayText}.";
    }

    private CursorSqliteRowLocator? CreateLocator(
        CursorSqliteTableSchema table,
        CursorRowReference target)
    {
        if (target.KeyValues.Count > 0 &&
            table.PrimaryKeyColumns.Count > 0)
        {
            List<CursorSqliteLocatorPart> parts = [];
            bool complete = true;
            foreach (CursorSqliteColumnSchema column
                     in table.PrimaryKeyColumns)
            {
                if (!TryValue(
                        target.KeyValues,
                        column.Name,
                        out object? value))
                {
                    complete = false;
                    break;
                }

                parts.Add(new CursorSqliteLocatorPart(column.Name, value));
            }

            if (complete)
            {
                return new CursorSqliteRowLocator(
                    table.Name,
                    CursorSqliteRowLocatorKind.PrimaryKey,
                    parts);
            }
        }

        if (target.RowId is not null && table.RowIdAlias is not null)
        {
            return new CursorSqliteRowLocator(
                table.Name,
                CursorSqliteRowLocatorKind.RowId,
                [
                    new CursorSqliteLocatorPart(
                        table.RowIdAlias,
                        target.RowId)
                ]);
        }

        CursorSqliteLocatorPart[] exactColumns = target.KeyValues
            .Select(item => (
                Column: table.FindColumn(item.Key),
                item.Value))
            .Where(item => item.Column is not null)
            .Select(item => new CursorSqliteLocatorPart(
                item.Column!.Name,
                item.Value))
            .ToArray();
        if (exactColumns.Length > 0)
        {
            return new CursorSqliteRowLocator(
                table.Name,
                CursorSqliteRowLocatorKind.ExactColumns,
                exactColumns);
        }

        return null;
    }

    private void PopulateSchema(CursorInspectionCatalogSnapshot catalog)
    {
        SchemaRoots.Clear();
        foreach (CursorInspectedDatabase inspected in catalog.Databases
                     .Where(item => MatchesWorkspaceFilter(item.Database))
                     .OrderBy(item => item.Database.Kind)
                     .ThenBy(
                         item => item.Database.WorkspacePath ??
                             item.Database.Path,
                         StringComparer.OrdinalIgnoreCase))
        {
            CursorDatabaseDescriptor database = inspected.Database;
            CursorSchemaNodeViewModel databaseNode = new(
                CursorSchemaNodeKind.Database,
                DatabaseHeader(database),
                $"{inspected.Tables.Count:N0} tables",
                database,
                database.Kind.ToString());
            foreach (CursorSqliteTableSchema table in inspected.Tables)
            {
                databaseNode.Children.Add(new CursorSchemaNodeViewModel(
                    CursorSchemaNodeKind.Table,
                    table.Name,
                    $"{table.Columns.Count:N0} columns",
                    new CursorTableSelection(database, table),
                    TableBadge(table.Name)));
            }

            databaseNode.IsExpanded =
                database.Kind == CursorDatabaseKind.Global;
            SchemaRoots.Add(databaseNode);
        }
    }

    private bool MatchesWorkspaceFilter(CursorDatabaseDescriptor database)
    {
        if (string.IsNullOrWhiteSpace(WorkspaceFilterText))
        {
            return true;
        }

        string filter = WorkspaceFilterText.Trim();
        return new[]
            {
                database.Path,
                database.WorkspaceStorageId,
                database.WorkspacePath
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => value!.Contains(
                filter,
                StringComparison.OrdinalIgnoreCase));
    }

    private void SetSchemaSelection(string databasePath, string tableName)
    {
        foreach (CursorSchemaNodeViewModel databaseNode in SchemaRoots)
        {
            bool databaseMatch =
                databaseNode.Model is CursorDatabaseDescriptor database &&
                string.Equals(
                    database.Path,
                    databasePath,
                    StringComparison.OrdinalIgnoreCase);
            databaseNode.IsExpanded |= databaseMatch;
            databaseNode.IsSelected = false;
            foreach (CursorSchemaNodeViewModel tableNode
                     in databaseNode.Children)
            {
                bool selected = databaseMatch &&
                    tableNode.Model is CursorTableSelection selection &&
                    string.Equals(
                        selection.Table.Name,
                        tableName,
                        StringComparison.OrdinalIgnoreCase);
                tableNode.IsSelected = selected;
            }
        }
    }

    private CursorInspectedDatabase PreferredDatabase(
        IReadOnlyList<CursorInspectedDatabase> databases) =>
        databases.FirstOrDefault(item =>
            item.Database.Kind == CursorDatabaseKind.Global) ??
        databases.First();

    private CursorSqliteTableSchema? PreferredTable(
        IReadOnlyList<CursorSqliteTableSchema> tables) =>
        tables.FirstOrDefault(table => string.Equals(
            table.Name,
            "cursorDiskKV",
            StringComparison.OrdinalIgnoreCase)) ??
        tables.FirstOrDefault(table => string.Equals(
            table.Name,
            "ItemTable",
            StringComparison.OrdinalIgnoreCase)) ??
        tables.FirstOrDefault(table => string.Equals(
            table.Name,
            "composerHeaders",
            StringComparison.OrdinalIgnoreCase)) ??
        tables.FirstOrDefault();

    private string DatabaseHeader(CursorDatabaseDescriptor database) =>
        database.Kind switch
        {
            CursorDatabaseKind.Global => "Global Storage",
            CursorDatabaseKind.Workspace =>
                database.WorkspacePath ??
                database.WorkspaceStorageId ??
                "Workspace Storage",
            _ => Path.GetFileName(database.Path)
        };

    private void UpdateReconstructionBreadcrumb(
        CursorReconstructionItemViewModel? item)
    {
        if (!string.IsNullOrWhiteSpace(item?.FolderPath))
        {
            Breadcrumb = $"Reconstruction  ›  {item.FolderPath}";
            return;
        }

        if (item?.Workspace is null)
        {
            Breadcrumb = "Reconstruction";
            return;
        }

        Breadcrumb = item.Session is null
            ? $"Reconstruction  ›  {item.Workspace.Workspace.DisplayName}"
            : $"Reconstruction  ›  " +
                $"{item.Workspace.Workspace.DisplayName}  ›  " +
                item.Session.Session.Title;
    }

    private string TableBadge(string tableName) =>
        tableName.ToLowerInvariant() switch
        {
            "cursordiskkv" => "Cursor KV",
            "itemtable" => "State",
            "composerheaders" => "Composers",
            _ => string.Empty
        };

    private NavigationLocation? CurrentLocation()
    {
        if (_externalEvidencePath is not null)
        {
            return new NavigationLocation(
                null,
                _externalEvidencePath,
                FilterText);
        }

        return SelectedRow is null
            ? null
            : new NavigationLocation(
                SelectedRow.Reference,
                null,
                FilterText);
    }

    private async Task ShowExternalEvidenceAsync(
        string path,
        CancellationToken cancellationToken)
    {
        const int maximumBytes = 512 * 1024;
        await using FileStream stream = new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous |
                    FileOptions.SequentialScan
            });
        int requested = (int)Math.Min(stream.Length, maximumBytes);
        byte[] buffer = new byte[requested];
        int total = 0;
        while (total < requested)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(total, requested - total),
                cancellationToken).ConfigureAwait(true);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        _externalEvidencePath = Path.GetFullPath(path);
        _externalEvidenceText = Encoding.UTF8.GetString(buffer, 0, total);
        if (stream.Length > total)
        {
            _externalEvidenceText +=
                $"{Environment.NewLine}{Environment.NewLine}" +
                $"[Preview truncated at {maximumBytes:N0} bytes]";
        }

        SelectedCells.Clear();
        OutboundRelations.Clear();
        InboundRelations.Clear();
        Correlations.Clear();
        OnPropertyChanged(nameof(SelectedRowTitle));
        OnPropertyChanged(nameof(SelectedRowLocation));
        OnPropertyChanged(nameof(SelectedJsonText));
        OnPropertyChanged(nameof(SelectedRawText));
        Breadcrumb = _externalEvidencePath;
        StatusText =
            $"Showing a bounded preview of {ExternalEvidenceKind(path).ToLowerInvariant()} evidence.";
    }

    private string ExternalEvidenceKind(string path) =>
        string.Equals(
            Path.GetFileName(path),
            "workspace.json",
            StringComparison.OrdinalIgnoreCase)
            ? "Workspace manifest"
            : string.Equals(
                Path.GetExtension(path),
                ".jsonl",
                StringComparison.OrdinalIgnoreCase)
                ? "Transcript"
                : "External evidence";

    private async Task RestoreLocationAsync(NavigationLocation location)
    {
        FilterText = location.FilterText ?? string.Empty;
        _suppressHistory = true;
        try
        {
            await RunBusyAsync(
                "Restoring navigation history…",
                cancellationToken =>
                    location.ExternalPath is not null
                        ? ShowExternalEvidenceAsync(
                            location.ExternalPath,
                            cancellationToken)
                        : location.Row is not null
                            ? NavigateToRowAsync(
                                location.Row,
                                cancellationToken)
                            : Task.CompletedTask).ConfigureAwait(true);
        }
        finally
        {
            _suppressHistory = false;
        }
    }

    private NavigationLocation Pop(List<NavigationLocation> values)
    {
        NavigationLocation value = values[^1];
        values.RemoveAt(values.Count - 1);
        return value;
    }

    private void UpdateHistoryState()
    {
        CanNavigateBack = _backHistory.Count > 0;
        CanNavigateForward = _forwardHistory.Count > 0;
    }

    private void ClearSelection()
    {
        _selectedDatabase = null;
        _selectedTable = null;
        SchemaRoots.Clear();
        ClearRows();
        SelectedTableTitle = "Select a table";
        Breadcrumb = "No database selected";
    }

    private void ClearRows()
    {
        Rows.Clear();
        _suppressHistory = true;
        try
        {
            SelectRowAsync(null).GetAwaiter().GetResult();
        }
        finally
        {
            _suppressHistory = false;
        }

        PageStatus = string.Empty;
        CanMovePreviousPage = false;
        CanMoveNextPage = false;
    }

    private string CellText(CursorSqliteCell cell) =>
        cell.Kind switch
        {
            CursorSqliteCellKind.Null => "NULL",
            CursorSqliteCellKind.Blob when cell.Value is byte[] bytes =>
                "0x" + Convert.ToHexString(bytes),
            _ => Convert.ToString(
                cell.Value,
                CultureInfo.InvariantCulture) ?? string.Empty
        };

    private bool TryValue(
        IReadOnlyDictionary<string, object?> values,
        string name,
        out object? value)
    {
        foreach ((string key, object? candidate) in values)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = null;
        return false;
    }

    private async Task RunBusyAsync(
        string status,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        CancellationTokenSource next =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _operationCancellation,
            next);
        previous?.Cancel();
        previous?.Dispose();
        IsBusy = true;
        StatusText = status;
        WarningText = string.Empty;
        try
        {
            await action(next.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
            when (next.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            WarningText = exception.Message;
            StatusText = "The operation did not complete.";
        }
        finally
        {
            IsBusy = false;
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _operationCancellation,
                        null,
                        next),
                    next))
            {
                next.Dispose();
            }
        }
    }
}
