using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Cursor;

public sealed class CursorTranscriptSessionCatalogSource : ISessionCatalogSource
{
    private readonly SessionDiscoveryContext _context;
    private readonly CursorFileSystemInspector _fileSystem;
    private readonly CursorJsonReader _json;
    private readonly CursorIdentityBuilder _identity;
    private readonly CursorWorkspaceResolver _workspaceResolver;
    private readonly CursorTranscriptFileReader _fileReader;
    private readonly CursorTranscriptParser _parser;
    private readonly string _projectsRoot;

    public CursorTranscriptSessionCatalogSource()
        : this(new SessionDiscoveryContext())
    {
    }

    public CursorTranscriptSessionCatalogSource(SessionDiscoveryContext context)
        : this(context, new WorkspaceNormalizer())
    {
    }

    public CursorTranscriptSessionCatalogSource(
        SessionDiscoveryContext context,
        WorkspaceNormalizer workspaceNormalizer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workspaceNormalizer);

        _context = context;
        _fileSystem = new CursorFileSystemInspector();
        _json = new CursorJsonReader();
        _identity = new CursorIdentityBuilder();
        _workspaceResolver = new CursorWorkspaceResolver(
            workspaceNormalizer,
            _json);
        _fileReader = new CursorTranscriptFileReader(_context.Limits, _fileSystem);
        _parser = new CursorTranscriptParser(_json, _identity);
        _projectsRoot = Path.GetFullPath(Path.Combine(
            _context.UserProfile,
            ".cursor",
            "projects"));
    }

    public string Name => "Cursor transcript sessions";

    public HookProvider Provider => HookProvider.Cursor;

    public IReadOnlyList<string> WatchRoots => [_projectsRoot];

    public async Task<SessionCatalogScanResult> ScanAsync(
        CancellationToken cancellationToken)
    {
        CursorWarningCollector warnings = new();
        CursorDiscoveryGuard guard = new(_context.Limits);
        IReadOnlyList<CursorTranscriptSourceFile> discovered = Discover(
            guard,
            warnings,
            cancellationToken);

        List<CursorParsedTranscript> parsed = [];
        foreach (CursorTranscriptSourceFile source in discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (guard.DeadlineExceeded)
            {
                guard.MarkIncomplete();
                warnings.Add(
                    "Cursor transcript discovery stopped after reaching its time limit.");
                break;
            }

            CursorTranscriptReadResult read = await _fileReader.ReadAsync(
                source.Path,
                guard,
                cancellationToken).ConfigureAwait(false);
            warnings.AddRange(read.Warnings);
            if (!read.IsComplete)
            {
                guard.MarkIncomplete();
            }

            parsed.Add(_parser.Parse(
                source,
                read.Rows,
                cancellationToken));
        }

        IReadOnlyList<SessionCatalogEntry> sessions = BuildCatalog(
            parsed,
            warnings);
        return new SessionCatalogScanResult(
            sessions,
            guard.IsComplete,
            warnings.Snapshot());
    }

    private IReadOnlyList<CursorTranscriptSourceFile> Discover(
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_projectsRoot))
        {
            return [];
        }

        if (!_fileSystem.IsReadableDirectory(_projectsRoot))
        {
            warnings.Add(
                $"Cursor projects root '{_projectsRoot}' is a symbolic link or is unreadable.");
            guard.MarkIncomplete();
            return [];
        }

        if (!guard.TryVisitDirectory(_projectsRoot, warnings))
        {
            return [];
        }

        Dictionary<string, CursorTranscriptSourceFile> mainByProjectAndId =
            new(StringComparer.OrdinalIgnoreCase);
        List<CursorTranscriptSourceFile> children = [];
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

        foreach (DirectoryInfo project in EnumerateDirectories(
            _projectsRoot,
            warnings,
            guard))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (guard.IsExhausted)
            {
                break;
            }

            if (!guard.TryVisitDirectory(project.FullName, warnings))
            {
                break;
            }

            if (!_fileSystem.IsReadableDirectory(project.FullName))
            {
                continue;
            }

            string transcriptRoot = Path.Combine(
                project.FullName,
                "agent-transcripts");
            if (!Directory.Exists(transcriptRoot) ||
                !_fileSystem.IsReadableDirectory(transcriptRoot))
            {
                continue;
            }

            if (!guard.TryVisitDirectory(transcriptRoot, warnings))
            {
                break;
            }

            foreach (FileInfo flat in EnumerateJsonLines(
                transcriptRoot,
                warnings,
                guard))
            {
                if (guard.IsExhausted)
                {
                    break;
                }

                AddMain(
                    new CursorTranscriptSourceFile(
                        flat.FullName,
                        project.Name,
                        Path.GetFileNameWithoutExtension(flat.Name),
                        CursorTranscriptLayout.LegacyFlat),
                    mainByProjectAndId,
                    paths,
                    guard,
                    warnings);
            }

            foreach (DirectoryInfo sessionDirectory in EnumerateDirectories(
                transcriptRoot,
                warnings,
                guard))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (guard.IsExhausted)
                {
                    break;
                }

                if (!guard.TryVisitDirectory(sessionDirectory.FullName, warnings))
                {
                    break;
                }

                if (!_fileSystem.IsReadableDirectory(sessionDirectory.FullName))
                {
                    continue;
                }

                FileInfo? main = EnumerateJsonLines(
                    sessionDirectory.FullName,
                    warnings,
                    guard)
                    .FirstOrDefault(file => string.Equals(
                        Path.GetFileNameWithoutExtension(file.Name),
                        sessionDirectory.Name,
                        StringComparison.OrdinalIgnoreCase));
                if (main is not null)
                {
                    AddMain(
                        new CursorTranscriptSourceFile(
                            main.FullName,
                            project.Name,
                            sessionDirectory.Name,
                            CursorTranscriptLayout.NestedMain),
                        mainByProjectAndId,
                        paths,
                        guard,
                        warnings);
                }

                string subagents = Path.Combine(
                    sessionDirectory.FullName,
                    "subagents");
                if (!Directory.Exists(subagents) ||
                    !_fileSystem.IsReadableDirectory(subagents))
                {
                    continue;
                }

                if (!guard.TryVisitDirectory(subagents, warnings))
                {
                    break;
                }

                foreach (FileInfo child in EnumerateJsonLines(
                    subagents,
                    warnings,
                    guard))
                {
                    if (guard.IsExhausted)
                    {
                        break;
                    }

                    string fullPath = Path.GetFullPath(child.FullName);
                    if (!paths.Add(fullPath) ||
                        !guard.TryVisitFile(fullPath, warnings))
                    {
                        continue;
                    }

                    children.Add(new CursorTranscriptSourceFile(
                        fullPath,
                        project.Name,
                        Path.GetFileNameWithoutExtension(child.Name),
                        CursorTranscriptLayout.NestedChild,
                        sessionDirectory.Name));
                }
            }
        }

        return mainByProjectAndId.Values
            .Concat(children)
            .OrderBy(source => source.ProjectKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void AddMain(
        CursorTranscriptSourceFile candidate,
        Dictionary<string, CursorTranscriptSourceFile> mainByProjectAndId,
        HashSet<string> paths,
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings)
    {
        if (string.IsNullOrWhiteSpace(candidate.NativeSessionId))
        {
            return;
        }

        string path = Path.GetFullPath(candidate.Path);
        if (!paths.Add(path))
        {
            return;
        }

        string key = SessionKey(candidate.ProjectKey, candidate.NativeSessionId);
        if (mainByProjectAndId.TryGetValue(
            key,
            out CursorTranscriptSourceFile? existing))
        {
            if (existing.Layout == CursorTranscriptLayout.NestedMain ||
                candidate.Layout != CursorTranscriptLayout.NestedMain)
            {
                return;
            }

            paths.Remove(existing.Path);
        }

        if (!guard.TryVisitFile(path, warnings))
        {
            return;
        }

        mainByProjectAndId[key] = candidate with { Path = path };
    }

    private IReadOnlyList<SessionCatalogEntry> BuildCatalog(
        IReadOnlyList<CursorParsedTranscript> parsed,
        CursorWarningCollector warnings)
    {
        Dictionary<string, CursorParsedTranscript> mains =
            parsed
                .Where(item => !item.Source.IsChild)
                .ToDictionary(
                    item => SessionKey(
                        item.Source.ProjectKey,
                        item.Source.NativeSessionId),
                    StringComparer.OrdinalIgnoreCase);
        ILookup<string, CursorParsedTranscript> children = parsed
            .Where(item => item.Source.IsChild)
            .ToLookup(
                item => SessionKey(
                    item.Source.ProjectKey,
                    item.Source.ParentSessionId ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

        HashSet<string> allParentKeys = new(
            mains.Keys,
            StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, CursorParsedTranscript> group in children)
        {
            allParentKeys.Add(group.Key);
        }

        Dictionary<string, int> nativeIdCounts = allParentKeys
            .Select(SplitSessionKey)
            .GroupBy(
                item => item.NativeId,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.OrdinalIgnoreCase);

        List<SessionCatalogEntry> sessions = [];
        foreach (string parentKey in allParentKeys.Order(
            StringComparer.OrdinalIgnoreCase))
        {
            (string projectKey, string nativeId) = SplitSessionKey(parentKey);
            mains.TryGetValue(parentKey, out CursorParsedTranscript? main);
            CursorParsedTranscript[] childItems = children[parentKey]
                .OrderBy(item => item.Source.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (string.IsNullOrWhiteSpace(nativeId))
            {
                warnings.Add(
                    $"Skipped Cursor child transcript group with no parent id in '{projectKey}'.");
                continue;
            }

            SessionCatalogEntry entry = CreateEntry(
                projectKey,
                nativeId,
                nativeIdCounts[nativeId] > 1,
                main,
                childItems);
            sessions.Add(entry);
        }

        return sessions
            .OrderBy(
                session => session.Workspace.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(session => session.LastActivityAtUtc)
            .ThenBy(session => session.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private SessionCatalogEntry CreateEntry(
        string projectKey,
        string nativeId,
        bool scopeIdentity,
        CursorParsedTranscript? main,
        IReadOnlyList<CursorParsedTranscript> children)
    {
        List<SessionFileBinding> files = [];
        List<SessionSourceProvenance> sources = [];
        List<SessionTurn> turns = [];
        List<JsonElement> workspaceRows = [];
        string? model = main?.Model;
        string? mode = main?.Mode;
        bool hasTerminalRecord = main?.HasTerminalRecord == true;
        DateTimeOffset? fileLastWrite = null;

        if (main is not null)
        {
            AddTranscript(
                main,
                nativeId,
                files,
                sources,
                turns,
                workspaceRows,
                ref fileLastWrite);
        }

        int nextTurnNumber = turns.Count + 1;
        foreach (CursorParsedTranscript child in children)
        {
            List<SessionTurn> childTurns = [];
            AddTranscript(
                child,
                nativeId,
                files,
                sources,
                childTurns,
                workspaceRows,
                ref fileLastWrite);
            foreach (SessionTurn turn in childTurns)
            {
                turns.Add(turn with { Number = nextTurnNumber++ });
            }

            model ??= child.Model;
            mode ??= child.Mode;
        }

        WorkspaceContext workspace = _workspaceResolver.ResolveTranscriptWorkspace(
            projectKey,
            workspaceRows);
        string? firstPrompt = turns
            .Select(turn => turn.Prompt)
            .FirstOrDefault(prompt => !string.IsNullOrWhiteSpace(prompt));
        string title = _json.Preview(firstPrompt);
        if (title.Length == 0)
        {
            title = nativeId;
        }

        DateTimeOffset? startedAt = turns
            .Select(turn => turn.StartedAtUtc)
            .Where(value => value is not null)
            .Min();
        DateTimeOffset? lastEvent = turns
            .SelectMany(turn => turn.Events)
            .Select(item => item.TimestampUtc)
            .Where(value => value is not null)
            .Max();

        Dictionary<string, string?> metadata = new(StringComparer.Ordinal)
        {
            ["cursor.source"] = "agent-transcript",
            ["cursor.projectKey"] = projectKey,
            ["cursor.layout"] = main?.Source.Layout.ToString() ?? "child-only-recovery",
            ["cursor.childTranscriptCount"] = children.Count.ToString(),
            ["cursor.timestampProvenance"] =
                lastEvent is null && fileLastWrite is not null
                    ? "derived-file-last-write-time"
                    : null
        };

        return new SessionCatalogEntry
        {
            CatalogSessionId = _identity.CatalogId(
                nativeId,
                projectKey,
                scopeIdentity),
            NativeSessionId = nativeId,
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Workspace = workspace,
            Title = title,
            StartedAtUtc = startedAt,
            LastActivityAtUtc = lastEvent ?? fileLastWrite,
            Model = model,
            Mode = mode,
            LifecycleState = SessionLifecycleState.Closed,
            LifecycleEvidence = hasTerminalRecord
                ? InferenceEvidence.Observed
                : InferenceEvidence.Unavailable,
            Files = files,
            Turns = turns,
            Metadata = metadata,
            Sources = sources
        };
    }

    private void AddTranscript(
        CursorParsedTranscript transcript,
        string parentNativeId,
        List<SessionFileBinding> files,
        List<SessionSourceProvenance> sources,
        List<SessionTurn> turns,
        List<JsonElement> workspaceRows,
        ref DateTimeOffset? latestFileWrite)
    {
        if (_fileSystem.TryGetFileInfo(
            transcript.Source.Path,
            out long length,
            out DateTimeOffset lastWrite,
            out _))
        {
            latestFileWrite = latestFileWrite is null || lastWrite > latestFileWrite
                ? lastWrite
                : latestFileWrite;
            files.Add(new SessionFileBinding(
                transcript.Source.Path,
                SessionSourceKind.CursorTranscriptJsonl,
                DialectIds.CursorTranscript,
                transcript.Source.IsChild
                    ? TranscriptFileRole.Subagent
                    : TranscriptFileRole.Main,
                lastWrite,
                length,
                transcript.Source.IsChild
                    ? transcript.Source.NativeSessionId
                    : null,
                transcript.Source.IsChild ? parentNativeId : null));
        }

        sources.AddRange(transcript.Provenance);
        turns.AddRange(transcript.Turns);
        workspaceRows.AddRange(transcript.Rows);
    }

    private IReadOnlyList<DirectoryInfo> EnumerateDirectories(
        string path,
        CursorWarningCollector warnings,
        CursorDiscoveryGuard guard)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                .Where(directory => !directory.Attributes.HasFlag(
                    FileAttributes.ReparsePoint))
                .OrderBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (_fileSystem.IsRecoverable(exception))
        {
            guard.MarkIncomplete();
            warnings.Add($"Could not enumerate Cursor directory '{path}': {exception.Message}");
            return [];
        }
    }

    private IReadOnlyList<FileInfo> EnumerateJsonLines(
        string path,
        CursorWarningCollector warnings,
        CursorDiscoveryGuard guard)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(file =>
                    string.Equals(
                        file.Extension,
                        ".jsonl",
                        StringComparison.OrdinalIgnoreCase) &&
                    !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (_fileSystem.IsRecoverable(exception))
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Could not enumerate Cursor transcript directory '{path}': " +
                exception.Message);
            return [];
        }
    }

    private string SessionKey(string projectKey, string nativeId) =>
        projectKey + '\0' + nativeId;

    private (string ProjectKey, string NativeId) SplitSessionKey(string key)
    {
        int separator = key.IndexOf('\0');
        return separator < 0
            ? (string.Empty, key)
            : (key[..separator], key[(separator + 1)..]);
    }
}
