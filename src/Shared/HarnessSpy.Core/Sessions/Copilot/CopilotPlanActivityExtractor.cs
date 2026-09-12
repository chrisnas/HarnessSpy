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
                    Id = $"copilot-plan-activity:{record.Id}",
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
}
