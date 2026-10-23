using System.Text.Json;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sessions;

namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public sealed record CursorReconstructionHydration(
    IReadOnlyList<SessionTurn> Turns,
    string? Model,
    string? Mode,
    IReadOnlyList<string> Warnings);

public sealed class CursorReconstructionSessionHydrator
{
    private readonly SessionDiscoveryContext _context;
    private readonly SessionRawSourceResolver _rawSourceResolver = new();
    private readonly CursorJsonReader _json = new();
    private readonly CursorDesktopBubbleProjector _projector;

    public CursorReconstructionSessionHydrator(
        SessionDiscoveryContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _projector = new CursorDesktopBubbleProjector(
            _json,
            new CursorIdentityBuilder());
    }

    public async Task<CursorReconstructionHydration> HydrateAsync(
        CursorReconstructedSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        List<string> warnings = [];
        CursorIndexedRecord? composer = PreferredComposerRecord(session);
        if (composer is null)
        {
            warnings.Add(
                $"No composer row was found for '{session.Session.NativeSessionId}'.");
            return new CursorReconstructionHydration([], null, null, warnings);
        }

        CursorSqliteJsonRecord? composerRecord = await LoadRecordAsync(
            composer,
            warnings,
            cancellationToken).ConfigureAwait(false);
        if (composerRecord is null)
        {
            return new CursorReconstructionHydration([], null, null, warnings);
        }

        JsonElement? composerElement = FindComposer(
            composerRecord.Json,
            session.Session.NativeSessionId);
        if (composerElement is null)
        {
            warnings.Add(
                $"Composer payload '{composerRecord.Key}' does not contain " +
                $"'{session.Session.NativeSessionId}'.");
            return new CursorReconstructionHydration([], null, null, warnings);
        }

        Dictionary<string, CursorSqliteJsonRecord> bubbles =
            new(StringComparer.Ordinal);
        foreach (CursorReconstructionBubble bubble in session.Bubbles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CursorSqliteJsonRecord? record = await LoadRecordAsync(
                bubble.Record,
                warnings,
                cancellationToken).ConfigureAwait(false);
            if (record is not null &&
                bubble.Record.Classification.BubbleId is string bubbleId)
            {
                bubbles[bubbleId] = record;
            }
        }

        CursorDiscoveryGuard guard = new(_context.Limits);
        CursorWarningCollector projectionWarnings = new();
        CursorBubbleProjection projection = _projector.Project(
            composer.Row.DatabasePath,
            session.Session.NativeSessionId,
            composerRecord,
            composerElement.Value,
            bubbles,
            _context.Limits.MaximumRecordsPerSession,
            guard,
            projectionWarnings,
            cancellationToken);
        warnings.AddRange(projectionWarnings.Snapshot());
        return new CursorReconstructionHydration(
            projection.Turns,
            projection.Model,
            projection.Mode,
            warnings);
    }

    private CursorIndexedRecord? PreferredComposerRecord(
        CursorReconstructedSession session) =>
        session.ComposerRecords.FirstOrDefault(record =>
            record.Classification.Kind == CursorRecordKind.ComposerData &&
            record.Classification.StorageKey?.StartsWith(
                "composerData:",
                StringComparison.Ordinal) == true) ??
        session.ComposerRecords.FirstOrDefault(record =>
            record.Classification.Kind == CursorRecordKind.ComposerData) ??
        session.ComposerRecords.FirstOrDefault();

    private async Task<CursorSqliteJsonRecord?> LoadRecordAsync(
        CursorIndexedRecord record,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        SessionSourceProvenance? provenance = Provenance(record);
        if (provenance is null)
        {
            warnings.Add(
                $"Row '{record.Row.DisplayText}' has no exact key.");
            return null;
        }

        SessionRawSourceContent source = await _rawSourceResolver.LoadAsync(
            provenance,
            Math.Clamp(
                _context.Limits.MaximumLineBytes,
                1024,
                16 * 1024 * 1024),
            cancellationToken).ConfigureAwait(false);
        if (source.IsTruncated)
        {
            warnings.Add(
                $"Row '{record.Row.DisplayText}' exceeded the raw-value limit.");
            return null;
        }

        if (!_json.TryParse(
                source.Content,
                out JsonDocument? document,
                out string? error) ||
            document is null)
        {
            warnings.Add(
                $"Row '{record.Row.DisplayText}' contains malformed JSON: {error}");
            return null;
        }

        using (document)
        {
            return new CursorSqliteJsonRecord(
                record.Row.TableName,
                record.Classification.StorageKey ??
                    provenance.DatabaseKey ??
                    record.Row.DisplayText,
                source.Content,
                document.RootElement.Clone());
        }
    }

    private JsonElement? FindComposer(JsonElement root, string composerId)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            string.Equals(
                _json.String(root, "composerId", "composer_id", "id"),
                composerId,
                StringComparison.Ordinal))
        {
            return root.Clone();
        }

        IEnumerable<JsonElement> candidates =
            root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : ComposerContainers(root);
        foreach (JsonElement candidate in candidates)
        {
            if (candidate.ValueKind == JsonValueKind.Object &&
                string.Equals(
                    _json.String(
                        candidate,
                        "composerId",
                        "composer_id",
                        "id"),
                    composerId,
                    StringComparison.Ordinal))
            {
                return candidate.Clone();
            }
        }

        return root.ValueKind == JsonValueKind.Object
            ? root.Clone()
            : null;
    }

    private IEnumerable<JsonElement> ComposerContainers(JsonElement root)
    {
        foreach (string name in new[]
                 {
                     "allComposers", "composerHeaders", "headers"
                 })
        {
            if (_json.TryGetProperty(
                    root,
                    name,
                    out JsonElement items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in items.EnumerateArray())
                {
                    yield return item;
                }
            }
        }
    }

    private SessionSourceProvenance? Provenance(CursorIndexedRecord record)
    {
        string? databaseKey = DatabaseKey(record);
        if (string.IsNullOrWhiteSpace(databaseKey))
        {
            return null;
        }

        return new SessionSourceProvenance(
            SessionSourceKind.CursorDesktopSqlite,
            record.Row.DatabasePath,
            "cursor-sqlite-reconstruction",
            DatabaseKey: databaseKey,
            RawSource: new SessionRawSourceReference(
                SessionRawSourceKind.SqliteValue,
                record.Row.DatabasePath,
                DatabaseTable: record.Row.TableName,
                DatabaseKey: databaseKey));
    }

    private string? DatabaseKey(CursorIndexedRecord record)
    {
        foreach (string columnName in new[] { "key", "composerId", "id" })
        {
            KeyValuePair<string, object?> match =
                record.Row.KeyValues.FirstOrDefault(item =>
                    string.Equals(
                        item.Key,
                        columnName,
                        StringComparison.OrdinalIgnoreCase));
            if (match.Value is not null)
            {
                return Convert.ToString(match.Value);
            }
        }

        string? storageKey = record.Classification.StorageKey;
        const string composerHeaderPrefix = "composerHeaders:";
        return string.Equals(
                   record.Row.TableName,
                   "composerHeaders",
                   StringComparison.OrdinalIgnoreCase) &&
               storageKey?.StartsWith(
                   composerHeaderPrefix,
                   StringComparison.Ordinal) == true
            ? storageKey[composerHeaderPrefix.Length..]
            : storageKey;
    }
}
