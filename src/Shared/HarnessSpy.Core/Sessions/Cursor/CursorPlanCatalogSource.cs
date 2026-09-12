using System.IO;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Core.Sessions.Cursor;

// Discovers Cursor plan files under %USERPROFILE%\.cursor\plans\*.plan.md. Each
// file becomes an unbound plan artifact whose structured key can later be
// correlated to the session that created it. Workspace-local plan directories
// are capability-gated until an observed isProject:true artifact proves their
// layout.
public sealed class CursorPlanCatalogSource
{
    private readonly SessionDiscoveryContext _context;
    private readonly SessionPlanFileReader _fileReader;
    private readonly SessionPlanContentNormalizer _normalizer;
    private readonly CursorPlanDocumentParser _documentParser;
    private readonly string _plansRoot;

    public CursorPlanCatalogSource(SessionDiscoveryContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _fileReader = new SessionPlanFileReader(context.Limits);
        _normalizer = new SessionPlanContentNormalizer();
        _documentParser = new CursorPlanDocumentParser();
        _plansRoot = Path.GetFullPath(Path.Combine(
            _context.UserProfile,
            ".cursor",
            "plans"));
    }

    public string PlansRoot => _plansRoot;

    public async Task<SessionPlanScanResult> ScanAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_plansRoot) ||
            !IsReadableDirectory(_plansRoot))
        {
            return SessionPlanScanResult.Empty;
        }

        List<string> warnings = [];
        bool isComplete = true;
        List<SessionPlanArtifact> artifacts = [];

        string[] files;
        try
        {
            files = Directory.GetFiles(_plansRoot, "*.plan.md", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return new SessionPlanScanResult(
                SessionPlanCatalogFragment.Empty,
                false,
                [$"Could not enumerate Cursor plans at '{_plansRoot}': {exception.Message}"]);
        }

        int maximumFiles = Math.Max(0, _context.Limits.MaximumFiles);
        foreach (string path in files.OrderBy(
                     static value => value,
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (artifacts.Count >= maximumFiles)
            {
                warnings.Add(
                    $"Cursor plan discovery stopped after {maximumFiles} files.");
                isComplete = false;
                break;
            }

            SessionPlanFileReadResult read = await _fileReader
                .ReadAsync(
                    path,
                    SessionSourceKind.CursorPlanMarkdown,
                    DialectIds.CursorTranscript,
                    cancellationToken)
                .ConfigureAwait(false);
            warnings.AddRange(read.Warnings);
            isComplete &= read.IsComplete;
            if (!read.Exists || read.Content is null || read.Provenance is null)
            {
                continue;
            }

            CursorPlanDocument document = _documentParser.Parse(read.Content);
            warnings.AddRange(document.Warnings);

            string stem = PlanStem(path);
            string title = !string.IsNullOrWhiteSpace(document.Name)
                ? document.Name!
                : stem;
            SessionPlanContentSnapshot? bodySnapshot =
                _normalizer.Snapshot(document.Body);

            Dictionary<string, string?> metadata = new(StringComparer.Ordinal)
            {
                ["cursor.plan.stem"] = stem,
                ["cursor.plan.isProject"] = document.IsProject?.ToString(),
                ["cursor.plan.hasFrontMatter"] =
                    document.HasFrontMatter.ToString()
            };

            artifacts.Add(new SessionPlanArtifact
            {
                CatalogPlanId = SessionPlanIdentity.Create(HookProvider.Cursor, stem),
                Provider = HookProvider.Cursor,
                Surface = HookSurface.CursorIde,
                Title = title,
                Workspace = WorkspaceContext.Unknown,
                PrimaryPath = read.Provenance.Path,
                CurrentMarkdown = read.Content,
                CurrentContentHash = _normalizer.Snapshot(read.Content)?.ContentHash,
                RevisionSnapshot = bodySnapshot,
                CreatedAtUtc = null,
                LastModifiedAtUtc = read.LastWriteTimeUtc,
                StructuredKey = document.StructuredKey,
                Sources = [read.Provenance],
                Metadata = metadata
            });
        }

        return new SessionPlanScanResult(
            new SessionPlanCatalogFragment { Artifacts = artifacts },
            isComplete,
            warnings);
    }

    private static string PlanStem(string path)
    {
        string name = Path.GetFileName(path);
        if (name.EndsWith(".plan.md", StringComparison.OrdinalIgnoreCase))
        {
            return name[..^".plan.md".Length];
        }

        return Path.GetFileNameWithoutExtension(name);
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
}
