using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Core.Sessions.Copilot;

internal sealed class CopilotCliSessionReader
{
    private readonly SessionDiscoveryContext _context;
    private readonly WorkspaceNormalizer _workspaceNormalizer;
    private readonly CopilotFileSystemGuard _fileSystemGuard;
    private readonly CopilotWorkspaceReader _workspaceReader;
    private readonly SessionPlanFileReader _planFileReader;
    private readonly SessionPlanContentNormalizer _planNormalizer = new();
    private readonly CopilotPlanActivityExtractor _planActivityExtractor = new();

    public CopilotCliSessionReader(
        SessionDiscoveryContext context,
        WorkspaceNormalizer workspaceNormalizer,
        CopilotFileSystemGuard fileSystemGuard)
    {
        _context = context;
        _workspaceNormalizer = workspaceNormalizer;
        _fileSystemGuard = fileSystemGuard;
        _workspaceReader = new CopilotWorkspaceReader(context, fileSystemGuard);
        _planFileReader = new SessionPlanFileReader(context.Limits);
    }

    public async Task<CopilotSessionReadResult> ReadAsync(
        CopilotSessionFileSource source,
        CancellationToken cancellationToken)
    {
        FileInfo eventsFile = new(source.EventsPath);
        if (!_fileSystemGuard.IsSafeFile(eventsFile, out string? reason))
        {
            return new CopilotSessionReadResult(
                null,
                false,
                [$"Skipped Copilot event log '{source.EventsPath}': {reason}"]);
        }

        long maximumFileBytes = Math.Max(0, _context.Limits.MaximumFileBytes);
        long snapshotLength = eventsFile.Length;
        if (snapshotLength > maximumFileBytes)
        {
            return new CopilotSessionReadResult(
                null,
                false,
                [
                    $"Skipped Copilot event log '{source.EventsPath}' because its " +
                    $"{snapshotLength} bytes exceed the " +
                    $"{maximumFileBytes}-byte limit."
                ]);
        }

        CopilotWorkspaceReadResult workspaceResult =
            await _workspaceReader.ReadAsync(
                source.WorkspacePath,
                cancellationToken).ConfigureAwait(false);

        CopilotWarningCollector warnings = new();
        warnings.AddRange(workspaceResult.Warnings);
        bool isComplete = workspaceResult.IsComplete;

        SessionFileBinding eventsBinding = new(
            eventsFile.FullName,
            SessionSourceKind.CopilotEventsJsonl,
            DialectIds.CopilotCliTranscript,
            TranscriptFileRole.Main,
            new DateTimeOffset(
                DateTime.SpecifyKind(eventsFile.LastWriteTimeUtc, DateTimeKind.Utc)),
            snapshotLength);

        CopilotSessionProjector projector = new(
            source,
            workspaceResult.Snapshot,
            eventsBinding,
            _workspaceNormalizer);

        int lineNumber = 0;
        int recordCount = 0;
        int malformedLineCount = 0;
        int maximumRecords = Math.Max(0, _context.Limits.MaximumRecordsPerSession);

        FileStream stream = new(
            eventsFile.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!_fileSystemGuard.IsSafeFile(eventsFile, out string? postOpenReason))
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return new CopilotSessionReadResult(
                null,
                false,
                [
                    $"Skipped Copilot event log '{eventsFile.FullName}' after it " +
                    $"changed while being opened: {postOpenReason}"
                ]);
        }

        await using CopilotBoundedLineReader lineReader = new(
            stream,
            _context.Limits.MaximumLineBytes,
            snapshotLength);

        while (await lineReader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            is CopilotBoundedLine line)
        {
            lineNumber++;
            if (line.IsTooLong)
            {
                if (recordCount >= maximumRecords)
                {
                    isComplete = false;
                    warnings.Add(
                        $"Stopped reading Copilot event log " +
                        $"'{eventsFile.FullName}' after {maximumRecords} records.");
                    break;
                }

                recordCount++;
                malformedLineCount++;
                isComplete = false;
                warnings.Add(
                    $"Skipped line {lineNumber} in Copilot event log " +
                    $"'{eventsFile.FullName}' because it exceeds " +
                    $"{_context.Limits.MaximumLineBytes} bytes.");
                continue;
            }

            string rawContent = line.Content ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                continue;
            }

            if (recordCount >= maximumRecords)
            {
                isComplete = false;
                warnings.Add(
                    $"Stopped reading Copilot event log '{eventsFile.FullName}' " +
                    $"after {maximumRecords} records.");
                break;
            }

            recordCount++;
            if (line.HasInvalidEncoding)
            {
                isComplete = false;
                warnings.Add(
                    $"Line {lineNumber} in Copilot event log " +
                    $"'{eventsFile.FullName}' contains invalid UTF-8.");
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    rawContent,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                        MaxDepth = 128
                    });

                projector.Accept(
                    document.RootElement,
                    rawContent,
                    lineNumber,
                    line.ByteOffset);
            }
            catch (JsonException exception)
            {
                malformedLineCount++;
                isComplete = false;
                projector.AddUnparsedSource(
                    rawContent,
                    lineNumber,
                    line.ByteOffset);

                string tailDescription = line.IsTerminated
                    ? "malformed JSON"
                    : "an incomplete active-tail record";
                warnings.Add(
                    $"Skipped {tailDescription} on line {lineNumber} in " +
                    $"Copilot event log '{eventsFile.FullName}': " +
                    exception.Message);
            }
        }

        if (lineReader.WasTruncated)
        {
            isComplete = false;
            warnings.Add(
                $"Copilot event log '{eventsFile.FullName}' was truncated while " +
                "its read-only snapshot was being read.");
        }

        warnings.AddRange(projector.Warnings);
        isComplete &= projector.IsComplete;

        SessionCatalogEntry session = projector.Build(
            recordCount,
            malformedLineCount);

        SessionPlanCatalogFragment planFragment = await BuildPlanFragmentAsync(
            source,
            session,
            warnings,
            cancellationToken).ConfigureAwait(false);

        return new CopilotSessionReadResult(
            session,
            isComplete,
            warnings.Snapshot(),
            planFragment);
    }

    // Reads the session-local plan.md (when present) into an artifact bound to
    // this session by its containing directory, and extracts plan tool activity
    // from the session's events.
    private async Task<SessionPlanCatalogFragment> BuildPlanFragmentAsync(
        CopilotSessionFileSource source,
        SessionCatalogEntry session,
        CopilotWarningCollector warnings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.PlanPath))
        {
            return SessionPlanCatalogFragment.Empty;
        }

        SessionPlanFileReadResult read = await _planFileReader
            .ReadAsync(
                source.PlanPath,
                SessionSourceKind.CopilotPlanMarkdown,
                DialectIds.CopilotCliTranscript,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (string warning in read.Warnings)
        {
            warnings.Add(warning);
        }

        if (!read.Exists || read.Content is null || read.Provenance is null)
        {
            return SessionPlanCatalogFragment.Empty;
        }

        string planCatalogId = SessionPlanIdentity.Create(
            HookProvider.GitHubCopilot,
            source.NativeSessionId);
        SessionPlanContentSnapshot? snapshot = _planNormalizer.Snapshot(read.Content);
        SessionPlanArtifact artifact = new()
        {
            CatalogPlanId = planCatalogId,
            Provider = HookProvider.GitHubCopilot,
            Surface = HookSurface.CopilotCli,
            Title = FirstHeading(read.Content) ?? $"{session.Title} plan",
            Workspace = session.Workspace,
            PrimaryPath = read.Provenance.Path,
            CurrentMarkdown = read.Content,
            CurrentContentHash = snapshot?.ContentHash,
            RevisionSnapshot = snapshot,
            LastModifiedAtUtc = read.LastWriteTimeUtc,
            SuggestedCatalogSessionId = session.CatalogSessionId,
            SuggestedBindingReason = SessionPlanBindingReason.CopilotSessionDirectory,
            SuggestedBindingEvidence = InferenceEvidence.Observed,
            Sources = [read.Provenance],
            Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["copilot.plan.sessionId"] = source.NativeSessionId
            }
        };

        IReadOnlyList<SessionPlanActivity> activities =
            _planActivityExtractor.Extract(session, planCatalogId, read.Provenance.Path);

        return new SessionPlanCatalogFragment
        {
            Artifacts = [artifact],
            Activities = activities
        };
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
}
