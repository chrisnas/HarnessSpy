using System.IO;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sessions;

namespace HarnessSpy.Wpf.ViewModels;

public sealed class SessionInspectorViewModel : ObservableObject, IDisposable
{
    private const int MaximumFullSourceBytes = 16 * 1024 * 1024;

    private readonly ISessionRawSourceResolver _rawSourceResolver;
    private readonly SessionViewerPreferencesViewModel _preferences;
    private CancellationTokenSource? _selectionCancellation;
    private SessionSourceProvenance? _selectedProvenance;
    private string _rawSource = string.Empty;
    private string _rawSourceStatus = string.Empty;
    private bool _isRawSourceLoading;
    private bool _isRawSourceTruncated;
    private bool _disposed;

    public SessionInspectorViewModel(
        ISessionRawSourceResolver rawSourceResolver,
        SessionViewerPreferencesViewModel preferences)
    {
        _rawSourceResolver = rawSourceResolver ??
            throw new ArgumentNullException(nameof(rawSourceResolver));
        _preferences = preferences ??
            throw new ArgumentNullException(nameof(preferences));
    }

    public string RawSource
    {
        get => _rawSource;
        private set
        {
            if (SetProperty(ref _rawSource, value))
            {
                OnPropertyChanged(nameof(HasRawSource));
            }
        }
    }

    public bool HasRawSource => RawSource.Length > 0;

    public string RawSourceStatus
    {
        get => _rawSourceStatus;
        private set => SetProperty(ref _rawSourceStatus, value);
    }

    public bool IsRawSourceLoading
    {
        get => _isRawSourceLoading;
        private set => SetProperty(ref _isRawSourceLoading, value);
    }

    public bool IsRawSourceTruncated
    {
        get => _isRawSourceTruncated;
        private set
        {
            if (SetProperty(ref _isRawSourceTruncated, value))
            {
                OnPropertyChanged(nameof(CanLoadFullRawSource));
            }
        }
    }

    public bool CanLoadFullRawSource =>
        IsRawSourceTruncated &&
        !IsRawSourceLoading;

    public async Task SelectAsync(
        SessionTreeNodeViewModel? node,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        CancellationTokenSource next =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _selectionCancellation,
            next);
        previous?.Cancel();
        previous?.Dispose();

        _selectedProvenance = ResolveProvenance(node);
        RawSource = string.Empty;
        RawSourceStatus = string.Empty;
        IsRawSourceTruncated = false;
        if (_selectedProvenance is null)
        {
            return;
        }

        await LoadAsync(
            _preferences.RawPreviewBytes,
            next.Token).ConfigureAwait(true);
    }

    public async Task LoadFullAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed || _selectedProvenance is null)
        {
            return;
        }

        CancellationTokenSource next =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _selectionCancellation,
            next);
        previous?.Cancel();
        previous?.Dispose();

        await LoadAsync(
            MaximumFullSourceBytes,
            next.Token).ConfigureAwait(true);
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
        cancellation?.Dispose();
    }

    private async Task LoadAsync(
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        SessionSourceProvenance? provenance = _selectedProvenance;
        if (provenance is null)
        {
            return;
        }

        IsRawSourceLoading = true;
        OnPropertyChanged(nameof(CanLoadFullRawSource));
        try
        {
            SessionRawSourceContent result = await _rawSourceResolver
                .LoadAsync(
                    provenance,
                    maximumBytes,
                    cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            RawSource = result.Content;
            IsRawSourceTruncated = result.IsTruncated;
            RawSourceStatus = result.Warning ??
                (result.IsTruncated
                    ? $"Showing the first {maximumBytes:N0} bytes."
                    : string.Empty);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                Microsoft.Data.Sqlite.SqliteException)
        {
            RawSource = string.Empty;
            RawSourceStatus =
                $"Raw source could not be loaded: {exception.Message}";
            IsRawSourceTruncated = false;
        }
        finally
        {
            IsRawSourceLoading = false;
            OnPropertyChanged(nameof(CanLoadFullRawSource));
        }
    }

    public SessionSourceProvenance? ResolveProvenance(
        SessionTreeNodeViewModel? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node.EventRecord is SessionEventRecord record)
        {
            return record.Provenance;
        }

        if (node.Plan?.Sources.FirstOrDefault() is
            SessionSourceProvenance planSource)
        {
            return planSource;
        }

        if (node.Turn?.Events.FirstOrDefault()?.Provenance is
            SessionSourceProvenance turnSource)
        {
            return turnSource;
        }

        if (node.Session?.Sources.FirstOrDefault() is
            SessionSourceProvenance sessionSource)
        {
            return sessionSource;
        }

        return string.IsNullOrWhiteSpace(node.RawSource)
            ? null
            : new SessionSourceProvenance(
                SessionSourceKind.Unknown,
                string.Empty,
                "inline",
                node.RawSource);
    }
}
