using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Core.Sessions.Copilot;

/// <summary>
/// Discovers and reads the provider-owned GitHub Copilot CLI session files.
/// The source is strictly read-only and never follows file-system links.
/// </summary>
public sealed class CopilotCliSessionCatalogSource : ISessionCatalogSource
{
    private const string SessionStateDirectoryName = "session-state";
    private const string EventsFileName = "events.jsonl";
    private const string WorkspaceFileName = "workspace.yaml";
    private const string PlanFileName = "plan.md";

    private readonly SessionDiscoveryContext _context;
    private readonly CopilotCliSessionReader _reader;
    private readonly CopilotFileSystemGuard _fileSystemGuard;
    private readonly string _sessionStateRoot;
    private readonly IReadOnlyList<string> _watchRoots;

    public CopilotCliSessionCatalogSource()
        : this(new SessionDiscoveryContext())
    {
    }

    public CopilotCliSessionCatalogSource(SessionDiscoveryContext context)
        : this(context, new WorkspaceNormalizer())
    {
    }

    public CopilotCliSessionCatalogSource(
        SessionDiscoveryContext context,
        WorkspaceNormalizer workspaceNormalizer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workspaceNormalizer);

        _context = context;
        _fileSystemGuard = new CopilotFileSystemGuard();

        CopilotHomeResolver homeResolver = new(context);
        string copilotHome = homeResolver.Resolve();
        _sessionStateRoot = Path.Combine(copilotHome, SessionStateDirectoryName);
        _watchRoots = [_sessionStateRoot];
        _reader = new CopilotCliSessionReader(
            context,
            workspaceNormalizer,
            _fileSystemGuard);
    }

    public string Name => "GitHub Copilot CLI";

    public HookProvider Provider => HookProvider.GitHubCopilot;

    public IReadOnlyList<string> WatchRoots => _watchRoots;

    public async Task<SessionCatalogScanResult> ScanAsync(
        CancellationToken cancellationToken)
    {
        List<SessionCatalogEntry> sessions = [];
        List<SessionPlanArtifact> planArtifacts = [];
        List<SessionPlanActivity> planActivities = [];
        CopilotWarningCollector warnings = new();
        bool isComplete = true;

        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan maximumDuration = _context.Limits.EffectiveMaximumDuration;
        if (maximumDuration != Timeout.InfiniteTimeSpan &&
            maximumDuration <= TimeSpan.Zero)
        {
            return new SessionCatalogScanResult(
                [],
                false,
                ["Copilot session discovery time limit was reached before scanning."]);
        }

        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (maximumDuration != Timeout.InfiniteTimeSpan)
        {
            if (maximumDuration <= TimeSpan.FromMilliseconds(uint.MaxValue - 1L))
            {
                timeoutSource.CancelAfter(maximumDuration);
            }
        }

        CancellationToken scanToken = timeoutSource.Token;

        try
        {
            CopilotSourceDiscoveryResult discovery = DiscoverSources(scanToken);
            warnings.AddRange(discovery.Warnings);
            isComplete &= discovery.IsComplete;

            foreach (CopilotSessionFileSource source in discovery.Sources)
            {
                scanToken.ThrowIfCancellationRequested();

                try
                {
                    CopilotSessionReadResult result =
                        await _reader.ReadAsync(source, scanToken).ConfigureAwait(false);

                    warnings.AddRange(result.Warnings);
                    isComplete &= result.IsComplete;
                    if (result.Session is not null)
                    {
                        sessions.Add(result.Session);
                    }

                    planArtifacts.AddRange(result.PlanFragment.Artifacts);
                    planActivities.AddRange(result.PlanFragment.Activities);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    warnings.Add(
                        $"Copilot session discovery exceeded its " +
                        $"{maximumDuration.TotalSeconds:0.###}-second time limit.");
                    isComplete = false;
                    break;
                }
                catch (Exception exception) when (
                    exception is IOException or
                    UnauthorizedAccessException or
                    JsonException or
                    InvalidDataException or
                    NotSupportedException or
                    System.Security.SecurityException)
                {
                    warnings.Add(
                        $"Could not read Copilot session '{source.NativeSessionId}' " +
                        $"from '{source.EventsPath}': {exception.Message}");
                    isComplete = false;
                }
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            warnings.Add(
                $"Copilot session discovery exceeded its " +
                $"{maximumDuration.TotalSeconds:0.###}-second time limit.");
            isComplete = false;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            System.Security.SecurityException)
        {
            warnings.Add(
                $"Could not enumerate Copilot session state at " +
                $"'{_sessionStateRoot}': {exception.Message}");
            isComplete = false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        SessionCatalogEntry[] orderedSessions = sessions
            .OrderByDescending(static session => session.LastActivityAtUtc)
            .ThenBy(static session => session.NativeSessionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SessionCatalogScanResult(
            orderedSessions,
            isComplete,
            warnings.Snapshot().Distinct(StringComparer.Ordinal).ToArray())
        {
            PlanFragment = new SessionPlanCatalogFragment
            {
                Artifacts = planArtifacts,
                Activities = planActivities
            }
        };
    }

    private CopilotSourceDiscoveryResult DiscoverSources(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_sessionStateRoot))
        {
            return CopilotSourceDiscoveryResult.Empty;
        }

        DirectoryInfo stateDirectory = new(_sessionStateRoot);
        if (!_fileSystemGuard.IsSafeDirectory(stateDirectory, out string? rootReason))
        {
            return new CopilotSourceDiscoveryResult(
                [],
                false,
                [$"Skipped Copilot session root '{_sessionStateRoot}': {rootReason}"]);
        }

        List<DirectoryInfo> sessionDirectories = [];
        List<FileInfo> legacyCandidates = [];
        List<string> warnings = [];
        bool isComplete = true;
        int maximumDirectories = Math.Max(
            0,
            _context.Limits.MaximumDirectories);
        if (maximumDirectories == 0)
        {
            return new CopilotSourceDiscoveryResult(
                [],
                false,
                [
                    $"Copilot session discovery directory limit " +
                    $"{_context.Limits.MaximumDirectories} was reached at " +
                    $"'{_sessionStateRoot}'."
                ]);
        }

        int directoryCount = 1;
        int topLevelFileCount = 0;

        IEnumerable<FileSystemInfo> entries = stateDirectory
            .EnumerateFileSystemInfos(
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = 0
                });

        foreach (FileSystemInfo entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                if (directoryCount >= maximumDirectories)
                {
                    warnings.Add(
                        $"Copilot session discovery stopped after " +
                        $"{_context.Limits.MaximumDirectories} directories.");
                    isComplete = false;
                    break;
                }

                directoryCount++;
                DirectoryInfo directory = (DirectoryInfo)entry;
                if (!_fileSystemGuard.IsSafeDirectory(directory, out string? reason))
                {
                    warnings.Add(
                        $"Skipped Copilot session directory '{directory.FullName}': {reason}");
                    isComplete = false;
                    continue;
                }

                sessionDirectories.Add(directory);
                continue;
            }

            if (topLevelFileCount >= Math.Max(0, _context.Limits.MaximumFiles))
            {
                warnings.Add(
                    $"Copilot session discovery stopped after " +
                    $"{_context.Limits.MaximumFiles} top-level files.");
                isComplete = false;
                break;
            }

            topLevelFileCount++;
            if (entry is FileInfo file &&
                file.Extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                legacyCandidates.Add(file);
            }
        }

        sessionDirectories.Sort(
            static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(
                left.Name,
                right.Name));
        legacyCandidates.Sort(
            static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(
                left.Name,
                right.Name));

        List<CopilotSessionFileSource> sources = [];
        HashSet<string> currentSessionIds = new(StringComparer.OrdinalIgnoreCase);
        int sourceFileCount = 0;
        int maximumFiles = Math.Max(0, _context.Limits.MaximumFiles);

        foreach (DirectoryInfo directory in sessionDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(directory.Name))
            {
                warnings.Add(
                    $"Skipped Copilot session directory with an empty identifier: " +
                    $"'{directory.FullName}'.");
                isComplete = false;
                continue;
            }

            FileInfo eventsFile = new(Path.Combine(directory.FullName, EventsFileName));
            if (!eventsFile.Exists)
            {
                continue;
            }

            if (sourceFileCount >= maximumFiles)
            {
                warnings.Add(
                    $"Copilot session discovery stopped after {maximumFiles} source files.");
                isComplete = false;
                break;
            }

            if (!_fileSystemGuard.IsSafeFile(eventsFile, out string? eventsReason))
            {
                warnings.Add(
                    $"Skipped Copilot event log '{eventsFile.FullName}': {eventsReason}");
                isComplete = false;
                continue;
            }

            sourceFileCount++;
            string? workspacePath = null;
            FileInfo workspaceFile =
                new(Path.Combine(directory.FullName, WorkspaceFileName));
            if (workspaceFile.Exists)
            {
                if (sourceFileCount >= maximumFiles)
                {
                    warnings.Add(
                        $"Skipped Copilot workspace metadata '{workspaceFile.FullName}' " +
                        $"because the {maximumFiles}-file limit was reached.");
                    isComplete = false;
                }
                else if (_fileSystemGuard.IsSafeFile(
                    workspaceFile,
                    out string? workspaceReason))
                {
                    sourceFileCount++;
                    workspacePath = workspaceFile.FullName;
                }
                else
                {
                    warnings.Add(
                        $"Skipped Copilot workspace metadata " +
                        $"'{workspaceFile.FullName}': {workspaceReason}");
                    isComplete = false;
                }
            }

            string? planPath = null;
            FileInfo planFile = new(Path.Combine(directory.FullName, PlanFileName));
            if (planFile.Exists)
            {
                if (sourceFileCount >= maximumFiles)
                {
                    warnings.Add(
                        $"Skipped Copilot plan '{planFile.FullName}' because the " +
                        $"{maximumFiles}-file limit was reached.");
                    isComplete = false;
                }
                else if (_fileSystemGuard.IsSafeFile(planFile, out string? planReason))
                {
                    sourceFileCount++;
                    planPath = planFile.FullName;
                }
                else
                {
                    warnings.Add(
                        $"Skipped Copilot plan '{planFile.FullName}': {planReason}");
                    isComplete = false;
                }
            }

            currentSessionIds.Add(directory.Name);
            sources.Add(new CopilotSessionFileSource(
                directory.Name,
                eventsFile.FullName,
                workspacePath,
                CopilotSessionLayout.Current,
                planPath));
        }

        foreach (FileInfo legacyFile in legacyCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string nativeSessionId = Path.GetFileNameWithoutExtension(legacyFile.Name);
            if (string.IsNullOrWhiteSpace(nativeSessionId) ||
                currentSessionIds.Contains(nativeSessionId))
            {
                continue;
            }

            if (sourceFileCount >= maximumFiles)
            {
                warnings.Add(
                    $"Copilot session discovery stopped after {maximumFiles} source files.");
                isComplete = false;
                break;
            }

            if (!_fileSystemGuard.IsSafeFile(legacyFile, out string? reason))
            {
                warnings.Add(
                    $"Skipped legacy Copilot event log '{legacyFile.FullName}': {reason}");
                isComplete = false;
                continue;
            }

            sourceFileCount++;
            sources.Add(new CopilotSessionFileSource(
                nativeSessionId,
                legacyFile.FullName,
                null,
                CopilotSessionLayout.Legacy));
        }

        return new CopilotSourceDiscoveryResult(sources, isComplete, warnings);
    }
}
