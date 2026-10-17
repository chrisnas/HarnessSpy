using HarnessSpy.Core.Services;

namespace HarnessSpy.Wpf.ViewModels;

public sealed class SessionViewerPreferencesViewModel : ObservableObject
{
    private const int MinimumRawPreviewBytes = 4 * 1024;
    private const int MaximumRawPreviewBytes = 16 * 1024 * 1024;

    private readonly Action<SessionViewerPreferencesViewModel>? _persist;
    private bool _keepDisabledProviderCache;
    private SessionCatalogProfile _catalogProfile;
    private SessionRawSourceMode _rawSourceMode;
    private int _rawPreviewBytes;
    private bool _includeRawSourceInSearch;
    private bool _expandWorkspacesOnStartup;

    public SessionViewerPreferencesViewModel(
        SessionViewerSettings? settings = null,
        Action<SessionViewerPreferencesViewModel>? persist = null)
    {
        settings ??= new SessionViewerSettings();
        _persist = persist;
        _keepDisabledProviderCache = settings.KeepDisabledProviderCache;
        _catalogProfile = settings.CatalogProfile;
        _rawSourceMode = settings.RawSourceMode;
        _rawPreviewBytes = Math.Clamp(
            settings.RawPreviewBytes,
            MinimumRawPreviewBytes,
            MaximumRawPreviewBytes);
        _includeRawSourceInSearch = settings.IncludeRawSourceInSearch;
        _expandWorkspacesOnStartup = settings.ExpandWorkspacesOnStartup;
    }

    public IReadOnlyList<SessionCatalogProfile> CatalogProfiles { get; } =
        Enum.GetValues<SessionCatalogProfile>();

    public IReadOnlyList<SessionRawSourceMode> RawSourceModes { get; } =
        Enum.GetValues<SessionRawSourceMode>();

    public bool KeepDisabledProviderCache
    {
        get => _keepDisabledProviderCache;
        set => SetAndPersist(ref _keepDisabledProviderCache, value);
    }

    public SessionCatalogProfile CatalogProfile
    {
        get => _catalogProfile;
        set => SetAndPersist(ref _catalogProfile, value);
    }

    public SessionRawSourceMode RawSourceMode
    {
        get => _rawSourceMode;
        set => SetAndPersist(ref _rawSourceMode, value);
    }

    public int RawPreviewBytes
    {
        get => _rawPreviewBytes;
        set
        {
            int normalized = Math.Clamp(
                value,
                MinimumRawPreviewBytes,
                MaximumRawPreviewBytes);
            SetAndPersist(ref _rawPreviewBytes, normalized);
        }
    }

    public bool IncludeRawSourceInSearch
    {
        get => _includeRawSourceInSearch;
        set => SetAndPersist(ref _includeRawSourceInSearch, value);
    }

    public bool ExpandWorkspacesOnStartup
    {
        get => _expandWorkspacesOnStartup;
        set => SetAndPersist(ref _expandWorkspacesOnStartup, value);
    }

    private void SetAndPersist<T>(ref T field, T value)
    {
        if (SetProperty(ref field, value))
        {
            _persist?.Invoke(this);
        }
    }
}
