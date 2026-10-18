using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions;

public sealed class SessionDiscoveryLimits
{
    public SessionDiscoveryLimits(
        int MaximumDirectories = 10_000,
        int MaximumFiles = 25_000,
        int MaximumRecordsPerSession = 250_000,
        int MaximumLineBytes = 16 * 1024 * 1024,
        long MaximumFileBytes = 2L * 1024 * 1024 * 1024,
        TimeSpan? MaximumDuration = null,
        int MaximumHydratedSessions = int.MaxValue,
        int MaximumConcurrentHydrations = 4,
        long MaximumHydrationBytes = 128L * 1024 * 1024)
    {
        this.MaximumDirectories = MaximumDirectories;
        this.MaximumFiles = MaximumFiles;
        this.MaximumRecordsPerSession = MaximumRecordsPerSession;
        this.MaximumLineBytes = MaximumLineBytes;
        this.MaximumFileBytes = MaximumFileBytes;
        this.MaximumDuration = MaximumDuration;
        this.MaximumHydratedSessions = MaximumHydratedSessions;
        this.MaximumConcurrentHydrations = MaximumConcurrentHydrations;
        this.MaximumHydrationBytes = MaximumHydrationBytes;
    }

    public int MaximumDirectories { get; private set; }

    public int MaximumFiles { get; private set; }

    public int MaximumRecordsPerSession { get; private set; }

    public int MaximumLineBytes { get; private set; }

    public long MaximumFileBytes { get; private set; }

    public TimeSpan? MaximumDuration { get; private set; }

    public int MaximumHydratedSessions { get; private set; }

    public int MaximumConcurrentHydrations { get; private set; }

    public long MaximumHydrationBytes { get; private set; }

    public TimeSpan EffectiveMaximumDuration => MaximumDuration ?? TimeSpan.FromSeconds(30);

    public void UpdateFrom(SessionDiscoveryLimits other)
    {
        ArgumentNullException.ThrowIfNull(other);
        MaximumDirectories = other.MaximumDirectories;
        MaximumFiles = other.MaximumFiles;
        MaximumRecordsPerSession = other.MaximumRecordsPerSession;
        MaximumLineBytes = other.MaximumLineBytes;
        MaximumFileBytes = other.MaximumFileBytes;
        MaximumDuration = other.MaximumDuration;
        MaximumHydratedSessions = other.MaximumHydratedSessions;
        MaximumConcurrentHydrations = other.MaximumConcurrentHydrations;
        MaximumHydrationBytes = other.MaximumHydrationBytes;
    }
}

public sealed class SessionDiscoveryContext
{
    public SessionDiscoveryContext(
        string? userProfile = null,
        string? applicationData = null,
        Func<string, string?>? getEnvironmentVariable = null,
        SessionDiscoveryLimits? limits = null)
    {
        UserProfile = userProfile ??
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ApplicationData = applicationData ??
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        GetEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        Limits = limits ?? new SessionDiscoveryLimits();
    }

    public string UserProfile { get; }

    public string ApplicationData { get; }

    public Func<string, string?> GetEnvironmentVariable { get; }

    public SessionDiscoveryLimits Limits { get; }

    public void UpdateLimits(SessionDiscoveryLimits limits) =>
        Limits.UpdateFrom(limits);
}

public enum SessionRefreshTrigger
{
    Startup,
    RefreshButton,
    HarnessSelection
}

public enum SessionRefreshMode
{
    CacheAware,
    ForceFull
}

public sealed record SessionRefreshRequest(
    SessionRefreshTrigger Trigger,
    IReadOnlySet<HookProvider>? Providers = null,
    SessionRefreshMode Mode = SessionRefreshMode.CacheAware);

public interface ISessionCatalogSource
{
    string Name { get; }

    HookProvider Provider { get; }

    IReadOnlyList<string> WatchRoots { get; }

    Task<SessionCatalogScanResult> ScanAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A catalog source that can report sessions progressively while it scans, so
/// the UI can insert them as they are discovered instead of all at once when
/// the scan finishes. Each <paramref name="progress"/> report carries the
/// running set of sessions found so far (a growing snapshot); the returned
/// result is the authoritative, complete set.
/// </summary>
public interface IStreamingSessionCatalogSource : ISessionCatalogSource
{
    Task<SessionCatalogScanResult> ScanAsync(
        IProgress<IReadOnlyList<SessionCatalogEntry>>? progress,
        CancellationToken cancellationToken);
}

public interface ISessionMetadataEnricher
{
    string Name { get; }

    Task<IReadOnlyList<SessionCatalogEntry>> EnrichAsync(
        IReadOnlyList<SessionCatalogEntry> sessions,
        CancellationToken cancellationToken);
}

public sealed record SessionDetailHydrationResult(
    SessionCatalogEntry Session,
    IReadOnlyList<string> Warnings);

public interface ISessionDetailHydrator
{
    bool CanHydrate(SessionCatalogEntry session);

    Task<SessionDetailHydrationResult> HydrateAsync(
        SessionCatalogEntry session,
        CancellationToken cancellationToken = default);
}

public sealed record SessionOpenResult(bool Started, string? Error = null);

public interface ISessionOpener
{
    bool CanOpen(SessionCatalogEntry session);

    Task<SessionOpenResult> OpenAsync(
        SessionCatalogEntry session,
        CancellationToken cancellationToken = default);
}
