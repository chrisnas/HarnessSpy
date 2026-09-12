using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Core.Sessions.Claude;

// Extracts plan create/update activities from already-parsed Claude turn events:
// Write/Edit into a plans directory and ExitPlanMode (whose input carries the
// full plan body). plan_mode attachments are captured separately during the
// transcript scan because they are not projected as tool events.
public sealed class ClaudePlanActivityExtractor
{
    private readonly SessionPlanContentNormalizer _normalizer = new();

    public IReadOnlyList<SessionPlanActivity> Extract(
        IReadOnlyList<SessionCatalogEntry> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        List<SessionPlanActivity> activities = [];
        foreach (SessionCatalogEntry session in sessions)
        {
            if (session.Provider != HookProvider.ClaudeCode)
            {
                continue;
            }

            foreach (SessionTurn turn in session.Turns)
            {
                foreach (SessionEventRecord record in turn.Events)
                {
                    ExtractFromEvent(session, turn, record, activities);
                }
            }
        }

        return activities;
    }

    private void ExtractFromEvent(
        SessionCatalogEntry session,
        SessionTurn turn,
        SessionEventRecord record,
        List<SessionPlanActivity> activities)
    {
        if (record.Role != ObservationRole.ToolRequest)
        {
            return;
        }

        string? toolName = record.ToolName ?? record.NativeName;
        bool isSuccessful = !record.IsFailure && !record.IsAborted;

        if (string.Equals(toolName, "ExitPlanMode", StringComparison.OrdinalIgnoreCase))
        {
            SessionPlanContentSnapshot? snapshot = ExitPlanContent(record);
            activities.Add(new SessionPlanActivity
            {
                Id = $"claude-plan-activity:{record.Id}:exitplanmode",
                Kind = SessionPlanActivityKind.Updated,
                Provider = HookProvider.ClaudeCode,
                CatalogSessionId = session.CatalogSessionId,
                NativeSessionId = session.NativeSessionId,
                TurnId = turn.Id,
                TurnNumber = turn.Number,
                SourceEventId = record.Id,
                ToolCallId = record.ToolCallId,
                Order = record.Order,
                TimestampUtc = record.TimestampUtc,
                // No path: bound by session scope to the single plan the session
                // owns. Content supplies a revision body when available.
                Locator = new SessionPlanLocator(),
                Content = snapshot,
                IsSuccessful = isSuccessful,
                HasOpaqueResult = snapshot is null,
                EstablishesOwnership = true,
                Evidence = InferenceEvidence.Observed,
                Provenance = record.Provenance
            });
            return;
        }

        if (!IsWriteLikeTool(toolName))
        {
            return;
        }

        foreach (string path in record.TargetPaths.Where(IsClaudePlanPath))
        {
            SessionPlanContentSnapshot? snapshot =
                string.Equals(toolName, "Write", StringComparison.OrdinalIgnoreCase)
                    ? WriteContent(record)
                    : null;
            activities.Add(new SessionPlanActivity
            {
                Id = $"claude-plan-activity:{record.Id}:{SessionPlanActivityKind.Updated}",
                Kind = SessionPlanActivityKind.Updated,
                Provider = HookProvider.ClaudeCode,
                CatalogSessionId = session.CatalogSessionId,
                NativeSessionId = session.NativeSessionId,
                TurnId = turn.Id,
                TurnNumber = turn.Number,
                SourceEventId = record.Id,
                ToolCallId = record.ToolCallId,
                Order = record.Order,
                TimestampUtc = record.TimestampUtc,
                Locator = new SessionPlanLocator(Path: path),
                Content = snapshot,
                IsSuccessful = isSuccessful,
                HasOpaqueResult = snapshot is null,
                EstablishesOwnership = true,
                Evidence = InferenceEvidence.Observed,
                Provenance = record.Provenance
            });
        }
    }

    private static bool IsWriteLikeTool(string? toolName) =>
        toolName is not null &&
        (toolName.Equals("Write", StringComparison.OrdinalIgnoreCase) ||
            toolName.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
            toolName.Equals("MultiEdit", StringComparison.OrdinalIgnoreCase) ||
            toolName.Equals("NotebookEdit", StringComparison.OrdinalIgnoreCase));

    // Only paths inside a "plans" directory are treated as plan files so ordinary
    // markdown writes are never misclassified as plan activity.
    private static bool IsClaudePlanPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (string segment in path.Split(
                     ['\\', '/'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Equals("plans", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private SessionPlanContentSnapshot? WriteContent(SessionEventRecord record) =>
        ReadStringField(record.Text, "content");

    private SessionPlanContentSnapshot? ExitPlanContent(SessionEventRecord record) =>
        ReadStringField(record.Text, "plan");

    private SessionPlanContentSnapshot? ReadStringField(string? rawInput, string field)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(rawInput);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(field, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return _normalizer.Snapshot(value.GetString());
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
