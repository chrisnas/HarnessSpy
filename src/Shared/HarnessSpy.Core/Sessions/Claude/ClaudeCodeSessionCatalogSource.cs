using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Core.Sessions.Claude;

public sealed class ClaudeCodeSessionCatalogSource : ISessionCatalogSource
{
    private readonly SessionDiscoveryContext _context;
    private readonly ClaudeTranscriptFileReader _transcriptReader;
    private readonly ClaudeSessionIndexReader _indexReader;
    private readonly ClaudeJsonFileReader _jsonReader;
    private readonly WorkspaceNormalizer _workspaceNormalizer;
    private readonly SessionPlanFileReader _planFileReader;
    private readonly SessionPlanContentNormalizer _planNormalizer = new();
    private readonly ClaudePlanActivityExtractor _planActivityExtractor = new();
    private readonly string _configRoot;
    private readonly string _projectsRoot;
    private readonly string _plansRoot;

    public ClaudeCodeSessionCatalogSource()
        : this(new SessionDiscoveryContext(), new WorkspaceNormalizer())
    {
    }

    public ClaudeCodeSessionCatalogSource(SessionDiscoveryContext context)
        : this(context, new WorkspaceNormalizer())
    {
    }

    public ClaudeCodeSessionCatalogSource(
        SessionDiscoveryContext context,
        WorkspaceNormalizer workspaceNormalizer)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _workspaceNormalizer = workspaceNormalizer ??
            throw new ArgumentNullException(nameof(workspaceNormalizer));
        _transcriptReader = new ClaudeTranscriptFileReader(_context.Limits);
        _indexReader = new ClaudeSessionIndexReader(_context.Limits);
        _jsonReader = new ClaudeJsonFileReader(_context.Limits);
        _planFileReader = new SessionPlanFileReader(_context.Limits);
        _configRoot = ResolveConfigRoot(_context);
        _projectsRoot = Path.Combine(_configRoot, "projects");
        _plansRoot = Path.Combine(_configRoot, "plans");
    }

    public string Name => "Claude Code";

    public HookProvider Provider => HookProvider.ClaudeCode;

    public IReadOnlyList<string> WatchRoots => [_projectsRoot, _plansRoot];

    public async Task<SessionCatalogScanResult> ScanAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_projectsRoot))
        {
            return SessionCatalogScanResult.Empty;
        }

        ClaudeScanBudget budget = new(_context.Limits);
        if (IsReparsePoint(_projectsRoot, budget))
        {
            budget.MarkIncomplete(
                $"Skipped unsafe Claude projects root '{_projectsRoot}'.");
            return new SessionCatalogScanResult([], false, budget.Warnings);
        }

        ClaudeSessionCatalogBuilder catalog = new(_workspaceNormalizer);
        ClaudeTranscriptIdentityResolver identityResolver = new();
        Dictionary<string, int> recordCounts = new(StringComparer.Ordinal);

        if (!budget.TryTakeDirectory(_projectsRoot))
        {
            budget.AddWarning(
                $"Claude directory limit reached before scanning '{_projectsRoot}'.");
            return new SessionCatalogScanResult([], false, budget.Warnings);
        }

        IReadOnlyList<string> projectDirectories = GetDirectories(
            _projectsRoot,
            budget,
            cancellationToken);
        foreach (string projectDirectory in projectDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.DeadlineExceeded())
            {
                budget.MarkDeadline(_projectsRoot);
                break;
            }

            if (!budget.TryTakeDirectory(projectDirectory))
            {
                budget.AddWarning(
                    $"Claude directory limit reached while scanning '{_projectsRoot}'.");
                break;
            }

            if (IsReparsePoint(projectDirectory, budget))
            {
                budget.MarkIncomplete(
                    $"Skipped linked Claude project directory '{projectDirectory}'.");
                continue;
            }

            ClaudeProjectLocation project = new(projectDirectory);
            ClaudeSessionIndex? index = await ReadIndexAsync(
                project,
                budget,
                cancellationToken).ConfigureAwait(false);
            if (index is not null)
            {
                foreach (ClaudeSessionIndexEntry entry in index.Entries)
                {
                    if (!entry.IsSidechain)
                    {
                        catalog.GetOrCreate(entry.SessionId).AddIndex(project, index, entry);
                    }
                }
            }

            IReadOnlyList<ClaudeMainFileCandidate> mainFiles = DiscoverMainFiles(
                project,
                budget,
                cancellationToken);
            foreach (ClaudeMainFileCandidate candidate in mainFiles
                         .OrderByDescending(static file => file.IsRecovery)
                         .ThenBy(static file => file.Path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int remaining = RemainingRecords(recordCounts, candidate.SessionId);
                ClaudeTranscriptReadResult read = await _transcriptReader.ReadAsync(
                    candidate.Path,
                    remaining,
                    budget.DeadlineExceeded,
                    cancellationToken).ConfigureAwait(false);
                budget.Add(read);

                string resolvedSessionId = read.Rows.Count > 0
                    ? identityResolver.ResolveMainSessionId(
                        read.Rows,
                        candidate.SessionId,
                        budget.MutableWarnings)
                    : candidate.SessionId;
                AddRecordCount(recordCounts, resolvedSessionId, read.RecordsRead);

                ClaudeTranscriptFile file = new()
                {
                    Path = candidate.Path,
                    Role = TranscriptFileRole.Main,
                    LastWriteTimeUtc = candidate.LastWriteTimeUtc,
                    Length = candidate.Length,
                    IsRecovery = candidate.IsRecovery
                };
                catalog.GetOrCreate(resolvedSessionId).AddTranscript(
                    project,
                    file,
                    read.Rows);
            }

            await ReadSubagentsAsync(
                project,
                catalog,
                identityResolver,
                recordCounts,
                budget,
                cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<SessionCatalogEntry> sessions = catalog.Build();
        List<Plans.SessionPlanActivity> planActivities =
        [
            .. catalog.BuildPlanActivities(),
            .. _planActivityExtractor.Extract(sessions)
        ];
        SessionPlanScanResult planScan = await ScanPlansAsync(cancellationToken)
            .ConfigureAwait(false);
        SessionPlanCatalogFragment planFragment = new()
        {
            Artifacts = planScan.Fragment.Artifacts,
            Activities = planActivities
        };
        string[] combinedWarnings = budget.Warnings
            .Concat(planScan.Warnings)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new SessionCatalogScanResult(
            sessions,
            budget.IsComplete && planScan.IsComplete,
            combinedWarnings)
        {
            PlanFragment = planFragment
        };

        int RemainingRecords(
            IReadOnlyDictionary<string, int> counts,
            string sessionId)
        {
            int current = counts.GetValueOrDefault(sessionId);
            return Math.Max(0, _context.Limits.MaximumRecordsPerSession - current);
        }

        static void AddRecordCount(
            IDictionary<string, int> counts,
            string sessionId,
            int count)
        {
            counts.TryGetValue(sessionId, out int current);
            counts[sessionId] = current + count;
        }
    }

    // Scans {config}/plans/*.md into unbound plan artifacts. Standalone plans
    // are returned even when their originating session transcript is gone.
    private async Task<SessionPlanScanResult> ScanPlansAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_plansRoot) || !IsReadableDirectory(_plansRoot))
        {
            return SessionPlanScanResult.Empty;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(_plansRoot, "*.md", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return new SessionPlanScanResult(
                SessionPlanCatalogFragment.Empty,
                false,
                [$"Could not enumerate Claude plans at '{_plansRoot}': {exception.Message}"]);
        }

        List<string> warnings = [];
        bool isComplete = true;
        List<SessionPlanArtifact> artifacts = [];
        int maximumFiles = Math.Max(0, _context.Limits.MaximumFiles);

        foreach (string path in files.OrderBy(
                     static value => value,
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (artifacts.Count >= maximumFiles)
            {
                warnings.Add($"Claude plan discovery stopped after {maximumFiles} files.");
                isComplete = false;
                break;
            }

            SessionPlanFileReadResult read = await _planFileReader
                .ReadAsync(
                    path,
                    SessionSourceKind.ClaudePlanMarkdown,
                    DialectIds.ClaudeTranscript,
                    cancellationToken)
                .ConfigureAwait(false);
            warnings.AddRange(read.Warnings);
            isComplete &= read.IsComplete;
            if (!read.Exists || read.Content is null || read.Provenance is null)
            {
                continue;
            }

            string stem = Path.GetFileNameWithoutExtension(path);
            SessionPlanContentSnapshot? snapshot = _planNormalizer.Snapshot(read.Content);
            artifacts.Add(new SessionPlanArtifact
            {
                CatalogPlanId = SessionPlanIdentity.Create(HookProvider.ClaudeCode, stem),
                Provider = HookProvider.ClaudeCode,
                Surface = HookSurface.ClaudeCode,
                Title = FirstHeading(read.Content) ?? stem,
                Workspace = WorkspaceContext.Unknown,
                PrimaryPath = read.Provenance.Path,
                CurrentMarkdown = read.Content,
                CurrentContentHash = snapshot?.ContentHash,
                RevisionSnapshot = snapshot,
                LastModifiedAtUtc = read.LastWriteTimeUtc,
                Sources = [read.Provenance],
                Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["claude.plan.slug"] = stem
                }
            });
        }

        return new SessionPlanScanResult(
            new SessionPlanCatalogFragment { Artifacts = artifacts },
            isComplete,
            warnings);
    }

    private static string? FirstHeading(string content)
    {
        foreach (string line in content.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                return trimmed[2..].Trim();
            }
        }

        return null;
    }

    private static bool IsReadableDirectory(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) &&
                !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    private async Task<ClaudeSessionIndex?> ReadIndexAsync(
        ClaudeProjectLocation project,
        ClaudeScanBudget budget,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(project.DirectoryPath, "sessions-index.json");
        if (!File.Exists(path))
        {
            return null;
        }

        if (!budget.TryTakeFile(path))
        {
            return null;
        }

        ClaudeSessionIndexReadResult read = await _indexReader.ReadAsync(
            path,
            budget.DeadlineExceeded,
            cancellationToken).ConfigureAwait(false);
        budget.Add(read);
        return read.Index;
    }

    private IReadOnlyList<ClaudeMainFileCandidate> DiscoverMainFiles(
        ClaudeProjectLocation project,
        ClaudeScanBudget budget,
        CancellationToken cancellationToken)
    {
        List<ClaudeMainFileCandidate> files = [];
        foreach (string path in GetFiles(project.DirectoryPath, budget, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryClassifyMainFile(path, out string? sessionId, out bool isRecovery))
            {
                continue;
            }

            if (!budget.TryTakeFile(path))
            {
                break;
            }

            if (TryFileDetails(path, budget, out DateTimeOffset lastWrite, out long length))
            {
                files.Add(new ClaudeMainFileCandidate(
                    path,
                    sessionId,
                    isRecovery,
                    lastWrite,
                    length));
            }
        }

        return files;
    }

    private async Task ReadSubagentsAsync(
        ClaudeProjectLocation project,
        ClaudeSessionCatalogBuilder catalog,
        ClaudeTranscriptIdentityResolver identityResolver,
        Dictionary<string, int> recordCounts,
        ClaudeScanBudget budget,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> sessionDirectories = GetDirectories(
            project.DirectoryPath,
            budget,
            cancellationToken);
        foreach (string sessionDirectory in sessionDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!budget.TryTakeDirectory(sessionDirectory))
            {
                break;
            }

            if (IsReparsePoint(sessionDirectory, budget))
            {
                continue;
            }

            string subagentsRoot = Path.Combine(sessionDirectory, "subagents");
            if (!Directory.Exists(subagentsRoot))
            {
                continue;
            }

            if (!budget.TryTakeDirectory(subagentsRoot))
            {
                break;
            }

            string parentSessionId = Path.GetFileName(
                sessionDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
            Queue<string> directories = new();
            directories.Enqueue(subagentsRoot);

            while (directories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget.DeadlineExceeded())
                {
                    budget.MarkDeadline(subagentsRoot);
                    return;
                }

                string current = directories.Dequeue();
                foreach (string child in GetDirectories(current, budget, cancellationToken))
                {
                    if (!budget.TryTakeDirectory(child))
                    {
                        return;
                    }

                    if (!IsReparsePoint(child, budget))
                    {
                        directories.Enqueue(child);
                    }
                }

                foreach (string path in GetFiles(current, budget, cancellationToken))
                {
                    if (!IsSubagentTranscript(path) && !IsSubagentMetadata(path))
                    {
                        continue;
                    }

                    if (!budget.TryTakeFile(path))
                    {
                        return;
                    }

                    if (!TryFileDetails(
                            path,
                            budget,
                            out DateTimeOffset lastWrite,
                            out long length))
                    {
                        continue;
                    }

                    string? filenameAgentId = AgentIdFromFileName(path);
                    if (IsSubagentMetadata(path))
                    {
                        ClaudeJsonFileReadResult metadata = await _jsonReader.ReadAsync(
                            path,
                            budget.DeadlineExceeded,
                            cancellationToken).ConfigureAwait(false);
                        budget.Add(metadata);
                        if (metadata.RawContent is string raw &&
                            metadata.Json is JsonElement json &&
                            json.ValueKind == JsonValueKind.Object)
                        {
                            string? metadataAgentId =
                                JsonString(json, "agentId", "agent_id") ??
                                filenameAgentId;
                            ClaudeTranscriptFile metadataFile = new()
                            {
                                Path = path,
                                Role = TranscriptFileRole.Subagent,
                                LastWriteTimeUtc = lastWrite,
                                Length = length,
                                AgentId = metadataAgentId,
                                ParentSessionId = parentSessionId
                            };
                            catalog.GetOrCreate(parentSessionId).AddAgentMetadata(
                                project,
                                metadataFile,
                                raw,
                                json);
                        }

                        continue;
                    }

                    int remaining = Math.Max(
                        0,
                        _context.Limits.MaximumRecordsPerSession -
                        recordCounts.GetValueOrDefault(parentSessionId));
                    ClaudeTranscriptReadResult read = await _transcriptReader.ReadAsync(
                        path,
                        remaining,
                        budget.DeadlineExceeded,
                        cancellationToken).ConfigureAwait(false);
                    budget.Add(read);

                    string resolvedParent = read.Rows.Count > 0
                        ? identityResolver.ResolveParentSessionId(read.Rows, parentSessionId)
                        : parentSessionId;
                    string? agentId = identityResolver.ResolveAgentId(
                        read.Rows,
                        filenameAgentId);
                    recordCounts[resolvedParent] =
                        recordCounts.GetValueOrDefault(resolvedParent) +
                        read.RecordsRead;

                    ClaudeTranscriptFile transcriptFile = new()
                    {
                        Path = path,
                        Role = TranscriptFileRole.Subagent,
                        LastWriteTimeUtc = lastWrite,
                        Length = length,
                        AgentId = agentId,
                        ParentSessionId = resolvedParent
                    };
                    catalog.GetOrCreate(resolvedParent).AddTranscript(
                        project,
                        transcriptFile,
                        read.Rows);
                }
            }
        }
    }

    private static IReadOnlyList<string> GetDirectories(
        string path,
        ClaudeScanBudget budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (budget.DeadlineExceeded())
        {
            budget.MarkDeadline(path);
            return [];
        }

        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception exception) when (IsRecoverableIoException(exception))
        {
            budget.MarkIncomplete(
                $"Could not enumerate Claude directories below '{path}': {exception.Message}");
            return [];
        }
    }

    private static IReadOnlyList<string> GetFiles(
        string path,
        ClaudeScanBudget budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (budget.DeadlineExceeded())
        {
            budget.MarkDeadline(path);
            return [];
        }

        try
        {
            return Directory.GetFiles(path);
        }
        catch (Exception exception) when (IsRecoverableIoException(exception))
        {
            budget.MarkIncomplete(
                $"Could not enumerate Claude files in '{path}': {exception.Message}");
            return [];
        }
    }

    private static bool TryClassifyMainFile(
        string path,
        out string sessionId,
        out bool isRecovery)
    {
        string name = Path.GetFileName(path);
        const string supersededMarker = ".jsonl.superseded-";
        int superseded = name.IndexOf(
            supersededMarker,
            StringComparison.OrdinalIgnoreCase);
        if (superseded > 0)
        {
            sessionId = name[..superseded];
            isRecovery = true;
            return true;
        }

        if (!name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            sessionId = string.Empty;
            isRecovery = false;
            return false;
        }

        string stem = name[..^".jsonl".Length];
        const string orphanedMarker = ".orphaned-";
        int orphaned = stem.IndexOf(
            orphanedMarker,
            StringComparison.OrdinalIgnoreCase);
        if (orphaned > 0)
        {
            sessionId = stem[..orphaned];
            isRecovery = true;
            return true;
        }

        sessionId = stem;
        isRecovery = false;
        return sessionId.Length > 0;
    }

    private static bool IsSubagentTranscript(string path) =>
        path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);

    private static bool IsSubagentMetadata(string path) =>
        path.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase);

    private static string? AgentIdFromFileName(string path)
    {
        string name = Path.GetFileName(path);
        if (name.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".meta.json".Length];
        }
        else if (name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".jsonl".Length];
        }

        return name.StartsWith("agent-", StringComparison.OrdinalIgnoreCase) &&
            name.Length > "agent-".Length
                ? name["agent-".Length..]
                : null;
    }

    private static bool TryFileDetails(
        string path,
        ClaudeScanBudget budget,
        out DateTimeOffset lastWriteTimeUtc,
        out long length)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists)
            {
                lastWriteTimeUtc = default;
                length = 0;
                budget.MarkIncomplete(
                    $"Claude session file disappeared during discovery: '{path}'.");
                return false;
            }

            lastWriteTimeUtc = file.LastWriteTimeUtc;
            length = file.Length;
            return true;
        }
        catch (Exception exception) when (IsRecoverableIoException(exception))
        {
            lastWriteTimeUtc = default;
            length = 0;
            budget.MarkIncomplete(
                $"Could not inspect Claude session file '{path}': {exception.Message}");
            return false;
        }
    }

    private static bool IsReparsePoint(string path, ClaudeScanBudget budget)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (IsRecoverableIoException(exception))
        {
            budget.MarkIncomplete(
                $"Could not inspect Claude directory '{path}': {exception.Message}");
            return true;
        }
    }

    private static string? JsonString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static string ResolveConfigRoot(SessionDiscoveryContext context)
    {
        string? configured = context.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        string value = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(context.UserProfile, ".claude")
            : Environment.ExpandEnvironmentVariables(configured.Trim());
        if (value == "~")
        {
            value = context.UserProfile;
        }
        else if (value.StartsWith("~/", StringComparison.Ordinal) ||
                 value.StartsWith("~\\", StringComparison.Ordinal))
        {
            value = Path.Combine(context.UserProfile, value[2..]);
        }

        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
            PathTooLongException or System.Security.SecurityException)
        {
            return value;
        }
    }

    private static bool IsRecoverableIoException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
        NotSupportedException or System.Security.SecurityException;
}

internal sealed class ClaudeMainFileCandidate
{
    public ClaudeMainFileCandidate(
        string path,
        string sessionId,
        bool isRecovery,
        DateTimeOffset lastWriteTimeUtc,
        long length)
    {
        Path = path;
        SessionId = sessionId;
        IsRecovery = isRecovery;
        LastWriteTimeUtc = lastWriteTimeUtc;
        Length = length;
    }

    public string Path { get; }

    public string SessionId { get; }

    public bool IsRecovery { get; }

    public DateTimeOffset LastWriteTimeUtc { get; }

    public long Length { get; }
}

internal sealed class ClaudeScanBudget
{
    private readonly SessionDiscoveryLimits _limits;
    private readonly long _startedTimestamp;
    private readonly List<string> _warnings = [];
    private int _directoryCount;
    private int _fileCount;
    private bool _deadlineReported;

    public ClaudeScanBudget(SessionDiscoveryLimits limits)
    {
        _limits = limits;
        _startedTimestamp = Stopwatch.GetTimestamp();
    }

    public bool IsComplete { get; private set; } = true;

    public IReadOnlyList<string> Warnings => _warnings;

    public IList<string> MutableWarnings => _warnings;

    public bool TryTakeDirectory(string path)
    {
        if (_directoryCount >= Math.Max(0, _limits.MaximumDirectories))
        {
            MarkIncomplete(
                $"Claude directory limit {_limits.MaximumDirectories} reached at '{path}'.");
            return false;
        }

        _directoryCount++;
        return true;
    }

    public bool TryTakeFile(string path)
    {
        if (_fileCount >= Math.Max(0, _limits.MaximumFiles))
        {
            MarkIncomplete($"Claude file limit {_limits.MaximumFiles} reached at '{path}'.");
            return false;
        }

        _fileCount++;
        return true;
    }

    public bool DeadlineExceeded()
    {
        TimeSpan maximumDuration = _limits.EffectiveMaximumDuration;
        return maximumDuration != Timeout.InfiniteTimeSpan &&
            Stopwatch.GetElapsedTime(_startedTimestamp) >= maximumDuration;
    }

    public void MarkDeadline(string path)
    {
        IsComplete = false;
        if (_deadlineReported)
        {
            return;
        }

        _deadlineReported = true;
        _warnings.Add($"Claude session scan deadline reached at '{path}'.");
    }

    public void MarkIncomplete(string warning)
    {
        IsComplete = false;
        AddWarning(warning);
    }

    public void AddWarning(string warning)
    {
        if (!_warnings.Contains(warning, StringComparer.Ordinal))
        {
            _warnings.Add(warning);
        }
    }

    public void Add(ClaudeTranscriptReadResult result)
    {
        if (!result.IsComplete)
        {
            IsComplete = false;
        }

        foreach (string warning in result.Warnings)
        {
            AddWarning(warning);
        }
    }

    public void Add(ClaudeSessionIndexReadResult result)
    {
        if (!result.IsComplete)
        {
            IsComplete = false;
        }

        foreach (string warning in result.Warnings)
        {
            AddWarning(warning);
        }
    }

    public void Add(ClaudeJsonFileReadResult result)
    {
        if (!result.IsComplete)
        {
            IsComplete = false;
        }

        foreach (string warning in result.Warnings)
        {
            AddWarning(warning);
        }
    }
}
