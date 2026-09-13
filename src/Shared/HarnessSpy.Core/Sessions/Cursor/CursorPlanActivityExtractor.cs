using System.Text.Json;
using System.Text.RegularExpressions;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Core.Sessions.Cursor;

// Extracts plan create/update/reference activities from already-parsed Cursor
// turn events: structured CreatePlan snapshots, explicit plan-file edits, and
// plan paths embedded in ApplyPatch bodies. Direct reads are classified as
// references, never ownership.
public sealed partial class CursorPlanActivityExtractor
{
    private readonly SessionPlanContentNormalizer _normalizer = new();
    private readonly CursorPlanDocumentParser _documentParser = new();

    public IReadOnlyList<SessionPlanActivity> Extract(
        IReadOnlyList<SessionCatalogEntry> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        List<SessionPlanActivity> activities = [];
        foreach (SessionCatalogEntry session in sessions)
        {
            if (session.Provider != HookProvider.Cursor)
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
        if (IsCreatePlan(toolName))
        {
            SessionPlanActivity? created = TryCreatePlanActivity(
                session,
                turn,
                record);
            if (created is not null)
            {
                activities.Add(created);
            }

            return;
        }

        IReadOnlyList<string> planPaths = PlanPaths(record);
        if (planPaths.Count == 0)
        {
            return;
        }

        bool isReference = record.ToolKind == CanonicalToolKind.FileRead;
        SessionPlanActivityKind kind = isReference
            ? SessionPlanActivityKind.Referenced
            : SessionPlanActivityKind.Updated;

        foreach (string path in planPaths)
        {
            activities.Add(new SessionPlanActivity
            {
                Id = $"cursor-plan-activity:{record.Id}:{kind}",
                Kind = kind,
                Provider = HookProvider.Cursor,
                CatalogSessionId = session.CatalogSessionId,
                NativeSessionId = session.NativeSessionId,
                TurnId = turn.Id,
                TurnNumber = turn.Number,
                SourceEventId = record.Id,
                ToolCallId = record.ToolCallId,
                Order = record.Order,
                TimestampUtc = record.TimestampUtc,
                Locator = new SessionPlanLocator(Path: path),
                Content = null,
                IsSuccessful = !record.IsFailure && !record.IsAborted,
                // A plan-file edit whose full result body is not exposed (patch
                // diff or partial replace) still proves an update happened.
                HasOpaqueResult = kind == SessionPlanActivityKind.Updated,
                Evidence = InferenceEvidence.Observed,
                Provenance = record.Provenance
            });
        }
    }

    private SessionPlanActivity? TryCreatePlanActivity(
        SessionCatalogEntry session,
        SessionTurn turn,
        SessionEventRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Text))
        {
            return null;
        }

        string? name = null;
        string? overview = null;
        string? body = null;
        List<CursorPlanTodo> todos = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(record.Text);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            name = String(root, "name");
            overview = String(root, "overview");
            body = String(root, "plan");
            todos = ReadTodos(root);
        }
        catch (JsonException)
        {
            return null;
        }

        string structuredKey = _documentParser.ComputeStructuredKey(
            name,
            overview,
            todos);
        SessionPlanContentSnapshot? snapshot = _normalizer.Snapshot(body);

        return new SessionPlanActivity
        {
            Id = $"cursor-plan-activity:{record.Id}:{SessionPlanActivityKind.Created}",
            Kind = SessionPlanActivityKind.Created,
            Provider = HookProvider.Cursor,
            CatalogSessionId = session.CatalogSessionId,
            NativeSessionId = session.NativeSessionId,
            TurnId = turn.Id,
            TurnNumber = turn.Number,
            SourceEventId = record.Id,
            ToolCallId = record.ToolCallId,
            Order = record.Order,
            TimestampUtc = record.TimestampUtc,
            Locator = new SessionPlanLocator(StructuredKey: structuredKey),
            Content = snapshot,
            IsSuccessful = !record.IsFailure && !record.IsAborted,
            HasOpaqueResult = snapshot is null,
            Evidence = InferenceEvidence.Observed,
            Provenance = record.Provenance
        };
    }

    private static bool IsCreatePlan(string? toolName) =>
        string.Equals(toolName, "CreatePlan", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "UpdatePlan", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<string> PlanPaths(SessionEventRecord record)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in record.TargetPaths)
        {
            if (IsPlanPath(path))
            {
                paths.Add(path);
            }
        }

        if (!string.IsNullOrEmpty(record.Text) &&
            record.Text.Contains(".plan.md", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match match in PlanPathPattern().Matches(record.Text))
            {
                paths.Add(match.Value);
            }
        }

        return [.. paths];
    }

    private static bool IsPlanPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.EndsWith(".plan.md", StringComparison.OrdinalIgnoreCase);

    private static List<CursorPlanTodo> ReadTodos(JsonElement root)
    {
        List<CursorPlanTodo> todos = [];
        if (!root.TryGetProperty("todos", out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return todos;
        }

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            todos.Add(new CursorPlanTodo(
                String(item, "id") ?? string.Empty,
                String(item, "content") ?? string.Empty,
                String(item, "status")));
        }

        return todos;
    }

    private static string? String(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }

    // Matches Windows or POSIX-style absolute paths ending in .plan.md inside an
    // ApplyPatch body or other embedded text.
    [GeneratedRegex(
        @"(?:[A-Za-z]:\\|/)[^""'\r\n]*?\.plan\.md",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlanPathPattern();
}
