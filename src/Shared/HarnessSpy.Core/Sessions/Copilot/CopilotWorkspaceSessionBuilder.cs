using System.Collections.ObjectModel;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Copilot;

// Builds a lightweight catalog entry when Copilot retained workspace or plan
// state after its event log disappeared. Empty startup shells are ignored.
internal sealed class CopilotWorkspaceSessionBuilder
{
    private static readonly TimeSpan MinimumMeaningfulLifetime =
        TimeSpan.FromSeconds(1);

    private readonly WorkspaceNormalizer _workspaceNormalizer;

    public CopilotWorkspaceSessionBuilder(
        WorkspaceNormalizer workspaceNormalizer)
    {
        _workspaceNormalizer = workspaceNormalizer ??
            throw new ArgumentNullException(nameof(workspaceNormalizer));
    }

    public SessionCatalogEntry? Build(
        CopilotSessionFileSource source,
        CopilotWorkspaceSnapshot workspace)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(workspace);

        if (!ShouldMaterialize(source, workspace))
        {
            return null;
        }

        Dictionary<string, string?> metadata = new(
            workspace.Metadata,
            StringComparer.Ordinal)
        {
            ["source.layout"] = source.Layout == CopilotSessionLayout.Current
                ? "current"
                : "legacy",
            ["source.events"] = source.EventsPath,
            ["source.workspace"] = source.WorkspacePath,
            ["source.plan"] = source.PlanPath,
            ["copilot.metadataOnly"] = "true"
        };

        DateTimeOffset? lastActivity =
            workspace.UpdatedAtUtc ??
            PlanLastWriteTime(source.PlanPath) ??
            workspace.CreatedAtUtc;

        return new SessionCatalogEntry
        {
            CatalogSessionId =
                $"{HookProvider.GitHubCopilot}:{HookSurface.CopilotCli}:" +
                source.NativeSessionId,
            NativeSessionId = source.NativeSessionId,
            Provider = HookProvider.GitHubCopilot,
            Surface = HookSurface.CopilotCli,
            Workspace = _workspaceNormalizer.FromPath(
                workspace.GitRoot ?? workspace.Cwd),
            Title =
                workspace.Name ??
                workspace.Summary ??
                source.NativeSessionId,
            StartedAtUtc = workspace.CreatedAtUtc,
            LastActivityAtUtc = lastActivity,
            LifecycleState = SessionLifecycleState.Closed,
            LifecycleEvidence = InferenceEvidence.Unavailable,
            Files = workspace.File is null ? [] : [workspace.File],
            Turns = [],
            Metadata = new ReadOnlyDictionary<string, string?>(metadata),
            Sources = workspace.Provenance is null ? [] : [workspace.Provenance]
        };
    }

    private static bool ShouldMaterialize(
        CopilotSessionFileSource source,
        CopilotWorkspaceSnapshot workspace)
    {
        if (!string.IsNullOrWhiteSpace(source.PlanPath))
        {
            return true;
        }

        if (workspace.Provenance is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(workspace.Name) ||
            !string.IsNullOrWhiteSpace(workspace.Summary) ||
            !string.IsNullOrWhiteSpace(workspace.MissionControlTaskId) ||
            !string.IsNullOrWhiteSpace(workspace.MissionControlSessionId) ||
            workspace.UserNamed == true ||
            workspace.RemoteSteerable == true)
        {
            return true;
        }

        return workspace.CreatedAtUtc is DateTimeOffset created &&
            workspace.UpdatedAtUtc is DateTimeOffset updated &&
            updated - created >= MinimumMeaningfulLifetime;
    }

    private static DateTimeOffset? PlanLastWriteTime(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            FileInfo file = new(path);
            return file.Exists
                ? new DateTimeOffset(
                    DateTime.SpecifyKind(
                        file.LastWriteTimeUtc,
                        DateTimeKind.Utc))
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            System.Security.SecurityException)
        {
            return null;
        }
    }
}
