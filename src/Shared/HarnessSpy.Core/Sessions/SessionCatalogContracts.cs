using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions;

public sealed record SessionDiscoveryLimits(
    int MaximumDirectories = 10_000,
    int MaximumFiles = 25_000,
    int MaximumRecordsPerSession = 250_000,
    int MaximumLineBytes = 16 * 1024 * 1024,
    long MaximumFileBytes = 2L * 1024 * 1024 * 1024,
    TimeSpan? MaximumDuration = null)
{
    public TimeSpan EffectiveMaximumDuration => MaximumDuration ?? TimeSpan.FromSeconds(30);
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
}

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

public sealed record SessionOpenResult(bool Started, string? Error = null);

public interface ISessionOpener
{
    bool CanOpen(SessionCatalogEntry session);

    Task<SessionOpenResult> OpenAsync(
        SessionCatalogEntry session,
        CancellationToken cancellationToken = default);
}
