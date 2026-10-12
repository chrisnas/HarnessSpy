using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Core.Services;

public sealed class SessionCatalogCompactor
{
    private const int MaximumEventTextCharacters = 32 * 1024;
    private const int MaximumSystemPromptCharacters = 64 * 1024;
    private const int MaximumMetadataCharacters = 8 * 1024;
    private const string TruncationSuffix =
        "\n\u2026 [truncated; open Raw source for the complete record]";

    private readonly Func<SessionCatalogProfile> _profile;

    public SessionCatalogCompactor(Func<SessionCatalogProfile> profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public SessionCatalogScanResult Compact(
        SessionCatalogScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_profile() == SessionCatalogProfile.Complete)
        {
            return result;
        }

        bool changed = false;
        SessionCatalogEntry[] sessions = new SessionCatalogEntry[
            result.Sessions.Count];
        for (int index = 0; index < result.Sessions.Count; index++)
        {
            SessionCatalogEntry original = result.Sessions[index];
            SessionCatalogEntry compacted = Compact(original);
            sessions[index] = compacted;
            changed |= !ReferenceEquals(original, compacted);
        }

        SessionPlanCatalogFragment planFragment =
            Compact(result.PlanFragment);
        changed |= !ReferenceEquals(
            planFragment,
            result.PlanFragment);
        return changed
            ? result with
            {
                Sessions = sessions,
                PlanFragment = planFragment
            }
            : result;
    }

    private SessionPlanCatalogFragment Compact(
        SessionPlanCatalogFragment fragment)
    {
        if (fragment.IsEmpty)
        {
            return fragment;
        }

        SessionPlanArtifact[] artifacts = fragment.Artifacts
            .Select(Compact)
            .ToArray();
        SessionPlanActivity[] activities = fragment.Activities
            .Select(Compact)
            .ToArray();
        return fragment with
        {
            Artifacts = artifacts,
            Activities = activities
        };
    }

    private SessionPlanArtifact Compact(SessionPlanArtifact artifact) =>
        artifact with
        {
            CurrentMarkdown = null,
            RevisionSnapshot = Compact(artifact.RevisionSnapshot),
            Activities = artifact.Activities
                .Select(Compact)
                .ToArray()
        };

    private SessionPlanActivity Compact(SessionPlanActivity activity) =>
        activity.Content is SessionPlanContentSnapshot content
            ? activity with { Content = Compact(content) }
            : activity;

    private SessionPlanContentSnapshot? Compact(
        SessionPlanContentSnapshot? snapshot) =>
        snapshot is null
            ? null
            : new SessionPlanContentSnapshot(
                string.Empty,
                snapshot.ContentHash);

    private SessionCatalogEntry Compact(SessionCatalogEntry session)
    {
        bool changed = false;
        SessionEventRecord[] sessionEvents = CompactEvents(
            session.SessionEvents,
            ref changed);
        SessionTurn[] turns = new SessionTurn[session.Turns.Count];
        for (int index = 0; index < session.Turns.Count; index++)
        {
            SessionTurn turn = session.Turns[index];
            bool turnChanged = false;
            SessionEventRecord[] events = CompactEvents(
                turn.Events,
                ref turnChanged);
            string prompt = Limit(
                turn.Prompt,
                MaximumEventTextCharacters);
            turnChanged |= !ReferenceEquals(prompt, turn.Prompt);
            turns[index] = turnChanged
                ? turn with
                {
                    Prompt = prompt,
                    Events = events
                }
                : turn;
            changed |= turnChanged;
        }

        IReadOnlyDictionary<string, string?> metadata =
            CompactMetadata(session.Metadata, ref changed);
        return changed
            ? session with
            {
                SessionEvents = sessionEvents,
                Turns = turns,
                Metadata = metadata
            }
            : session;
    }

    private SessionEventRecord[] CompactEvents(
        IReadOnlyList<SessionEventRecord> events,
        ref bool parentChanged)
    {
        SessionEventRecord[] compacted =
            new SessionEventRecord[events.Count];
        for (int index = 0; index < events.Count; index++)
        {
            SessionEventRecord original = events[index];
            string? text = LimitNullable(
                original.Text,
                MaximumEventTextCharacters);
            string? prompt = LimitNullable(
                original.PromptText,
                MaximumEventTextCharacters);
            string? task = LimitNullable(
                original.Task,
                MaximumEventTextCharacters);
            SystemPromptContent? systemPrompt =
                Compact(original.SystemPrompt);
            bool changed =
                !ReferenceEquals(text, original.Text) ||
                !ReferenceEquals(prompt, original.PromptText) ||
                !ReferenceEquals(task, original.Task) ||
                !ReferenceEquals(systemPrompt, original.SystemPrompt);
            compacted[index] = changed
                ? original with
                {
                    Text = text,
                    PromptText = prompt,
                    Task = task,
                    SystemPrompt = systemPrompt
                }
                : original;
            parentChanged |= changed;
        }

        return compacted;
    }

    private SystemPromptContent? Compact(SystemPromptContent? prompt)
    {
        if (prompt is null ||
            prompt.Text.Length <= MaximumSystemPromptCharacters)
        {
            return prompt;
        }

        return prompt with
        {
            Text = Limit(
                prompt.Text,
                MaximumSystemPromptCharacters),
            OriginalCharacterCount = prompt.CharacterCount
        };
    }

    private IReadOnlyDictionary<string, string?> CompactMetadata(
        IReadOnlyDictionary<string, string?> metadata,
        ref bool parentChanged)
    {
        Dictionary<string, string?>? compacted = null;
        foreach ((string key, string? value) in metadata)
        {
            string? limited = LimitNullable(
                value,
                MaximumMetadataCharacters);
            if (ReferenceEquals(limited, value))
            {
                continue;
            }

            compacted ??= new Dictionary<string, string?>(
                metadata,
                StringComparer.Ordinal);
            compacted[key] = limited;
            parentChanged = true;
        }

        return compacted ?? metadata;
    }

    private string Limit(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        int length = Math.Max(
            0,
            maximumCharacters - TruncationSuffix.Length);
        if (length > 0 &&
            length < value.Length &&
            char.IsHighSurrogate(value[length - 1]) &&
            char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        return value[..length] + TruncationSuffix;
    }

    private string? LimitNullable(string? value, int maximumCharacters) =>
        value is null ? null : Limit(value, maximumCharacters);
}
