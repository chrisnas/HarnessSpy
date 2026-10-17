using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Core.Sessions.Copilot;

// Extracts plan update activities from a built Copilot session: the native
// `plan` tool and writes/edits targeting plan.md. The plan is already bound to
// its session by the session-state directory, so these activities mainly supply
// turn links and partial-history evidence.
public sealed class CopilotPlanActivityExtractor
{
    public IReadOnlyList<SessionPlanActivity> Extract(
        SessionCatalogEntry session,
        string planCatalogId,
        string? planPath)
    {
        ArgumentNullException.ThrowIfNull(session);

        List<SessionPlanActivity> activities = [];
        foreach (SessionEventRecord record in session.SessionEvents)
        {
            AddNativePlanActivity(
                session,
                planCatalogId,
                planPath,
                turn: null,
                record,
                activities);
        }

        foreach (SessionTurn turn in session.Turns)
        {
            foreach (SessionEventRecord record in turn.Events)
            {
                AddNativePlanActivity(
                    session,
                    planCatalogId,
                    planPath,
                    turn,
                    record,
                    activities);
            }
        }

        // Current Copilot versions emit session.plan_changed only after the
        // provider accepted a plan mutation. Prefer that native signal over
        // the corresponding create/edit tool request so one mutation is not
        // counted twice. Older logs fall back to tool evidence below.
        if (activities.Count > 0)
        {
            return activities;
        }

        foreach (SessionTurn turn in session.Turns)
        {
            foreach (SessionEventRecord record in turn.Events)
            {
                if (record.Role != ObservationRole.ToolRequest)
                {
                    continue;
                }

                string? toolName = record.ToolName ?? record.NativeName;
                bool isPlanTool =
                    string.Equals(toolName, "plan", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(toolName, "update_plan", StringComparison.OrdinalIgnoreCase);
                bool isPlanWrite = planPath is not null &&
                    record.TargetPaths.Any(path => string.Equals(
                        path,
                        planPath,
                        StringComparison.OrdinalIgnoreCase));
                if (!isPlanTool && !isPlanWrite)
                {
                    continue;
                }

                activities.Add(new SessionPlanActivity
                {
                    Id =
                        $"copilot-plan-activity:{planCatalogId}:{record.Id}",
                    Kind = SessionPlanActivityKind.Updated,
                    Provider = HookProvider.GitHubCopilot,
                    CatalogSessionId = session.CatalogSessionId,
                    NativeSessionId = session.NativeSessionId,
                    TurnId = turn.Id,
                    TurnNumber = turn.Number,
                    SourceEventId = record.Id,
                    ToolCallId = record.ToolCallId,
                    Order = record.Order,
                    TimestampUtc = record.TimestampUtc,
                    Locator = planPath is not null
                        ? new SessionPlanLocator(Path: planPath)
                        : new SessionPlanLocator(),
                    Content = null,
                    IsSuccessful = !record.IsFailure && !record.IsAborted,
                    HasOpaqueResult = true,
                    EstablishesOwnership = true,
                    Evidence = InferenceEvidence.Observed,
                    Provenance = record.Provenance
                });
            }
        }

        return activities;
    }

    private static void AddNativePlanActivity(
        SessionCatalogEntry session,
        string planCatalogId,
        string? planPath,
        SessionTurn? turn,
        SessionEventRecord record,
        ICollection<SessionPlanActivity> activities)
    {
        if (!string.Equals(
                record.NativeName,
                "session.plan_changed",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SessionPlanActivityKind kind = string.Equals(
            record.Status,
            "create",
            StringComparison.OrdinalIgnoreCase)
                ? SessionPlanActivityKind.Created
                : SessionPlanActivityKind.Updated;
        activities.Add(new SessionPlanActivity
        {
            Id =
                $"copilot-plan-activity:{planCatalogId}:{record.Id}",
            Kind = kind,
            Provider = HookProvider.GitHubCopilot,
            CatalogSessionId = session.CatalogSessionId,
            NativeSessionId = session.NativeSessionId,
            TurnId = record.TurnId ?? turn?.Id,
            TurnNumber = turn?.Number,
            SourceEventId = record.Id,
            Order = record.Order,
            TimestampUtc = record.TimestampUtc,
            Locator = new SessionPlanLocator(Path: planPath),
            IsSuccessful = !record.IsFailure && !record.IsAborted,
            HasOpaqueResult = true,
            EstablishesOwnership = true,
            Evidence = InferenceEvidence.Observed,
            Provenance = record.Provenance
        });
    }
}
