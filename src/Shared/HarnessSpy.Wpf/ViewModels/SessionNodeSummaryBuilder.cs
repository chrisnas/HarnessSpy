using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;

namespace HarnessSpy.Wpf.ViewModels;

public sealed class SessionNodeSummaryBuilder
{
    public NodeSummary Build(SessionCatalogEntry session)
    {
        IReadOnlyList<SessionEventRecord> events =
            [.. session.Turns.SelectMany(static turn => turn.Events)];
        int abortedTurnCount = session.Turns.Count(
            static turn => turn.Events.Any(static item => item.IsAborted));
        TimeSpan wallTime = CalculateWallTime(
            session.StartedAtUtc,
            session.LastActivityAtUtc);

        // The turn count must match the turn nodes actually shown under the
        // session, which are built one-per-SessionTurn. Counting prompt events
        // instead over-counts providers that emit several prompt records per
        // turn (Cursor) and under-counts turns with no explicit prompt event.
        return Build(
            events,
            isSession: true,
            session.Turns.Count,
            abortedTurnCount,
            wallTime);
    }

    public NodeSummary Build(SessionTurn turn)
    {
        TimeSpan wallTime = CalculateWallTime(
            turn.StartedAtUtc,
            turn.EndedAtUtc);
        return Build(
            turn.Events,
            isSession: false,
            turnCount: 0,
            abortedTurnCount: 0,
            wallTime);
    }

    private NodeSummary Build(
        IReadOnlyList<SessionEventRecord> events,
        bool isSession,
        int turnCount,
        int abortedTurnCount,
        TimeSpan wallTime)
    {
        SessionEventRecord[] toolRequests = events
            .Where(IsToolRequest)
            .ToArray();
        SessionEventRecord[] mcpRequests = toolRequests
            .Where(IsMcp)
            .ToArray();
        SessionEventRecord[] thoughts = events
            .Where(IsThought)
            .ToArray();

        IReadOnlyList<CountedDurationRow> tools = BuildDurationRows(
            toolRequests.Where(item => !IsMcp(item)),
            ToolName);
        IReadOnlyList<CountedDurationRow> mcpCalls = BuildDurationRows(
            mcpRequests,
            McpName);
        IReadOnlyList<CountedDurationRow> thoughtRows = BuildDurationRows(
            thoughts,
            static item => item.Model ?? "Thinking");

        long? inputTokens = AggregateUsage(events, IsInputToken);
        long outputTokens = AggregateUsage(events, IsOutputToken) ?? 0;
        long cacheReadTokens = AggregateUsage(events, IsCacheReadToken) ?? 0;
        long cacheWriteTokens = AggregateUsage(events, IsCacheWriteToken) ?? 0;

        string tokenLine = BuildTokenLine(
            inputTokens,
            outputTokens,
            cacheReadTokens,
            cacheWriteTokens);
        string badge = BuildBadge(
            isSession,
            turnCount,
            toolRequests.Length,
            mcpRequests.Length,
            thoughts.Length);

        List<KpiItem> kpis = [];
        if (isSession)
        {
            kpis.Add(new KpiItem
            {
                Label = "Turns",
                Value = turnCount.ToString(),
                IsWarning = abortedTurnCount > 0
            });
        }

        kpis.Add(new KpiItem
        {
            Label = "Wall time",
            Value = FormatDuration(wallTime)
        });
        kpis.Add(new KpiItem
        {
            Label = "Tool calls",
            Value = toolRequests.Length.ToString()
        });
        kpis.Add(new KpiItem
        {
            Label = "Thinking",
            Value = thoughts.Length.ToString()
        });

        return new NodeSummary
        {
            IsSession = isSession,
            TurnCount = turnCount,
            AbortedTurnCount = abortedTurnCount,
            IsAborted = events.Any(static item => item.IsAborted),
            WallTime = wallTime,
            ToolCallCount = toolRequests.Length,
            McpCallCount = mcpRequests.Length,
            ThoughtCount = thoughts.Length,
            CompactionCount = events.Count(IsCompaction),
            ThoughtDurationMs = thoughts.Sum(static item => item.DurationMs ?? 0),
            ThoughtCharacterCount = thoughts.Sum(
                static item => (item.Text ?? string.Empty).Length),
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheReadTokens,
            CacheWriteTokens = cacheWriteTokens,
            Tools = tools,
            McpCalls = mcpCalls,
            Thoughts = thoughtRows,
            Skills = events
                .Select(static item => item.Skill?.SkillName)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Commands = events
                .Where(static item =>
                    IsToolRequest(item) &&
                    item.ToolKind == CanonicalToolKind.Shell)
                .Select(static item => Preview(item.PromptText ?? item.Text))
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            ReadFiles = BuildFileRows(events, CanonicalToolKind.FileRead),
            WrittenFiles =
            [
                .. BuildFileRows(
                    events,
                    CanonicalToolKind.FileWrite,
                    CanonicalToolKind.FileEdit)
            ],
            DeletedFiles = BuildFileRows(events, CanonicalToolKind.FileDelete),
            Subagents = BuildSubagents(events),
            Kpis = kpis,
            Badge = badge,
            TokenLine = tokenLine
        };
    }

    private static IReadOnlyList<CountedDurationRow> BuildDurationRows(
        IEnumerable<SessionEventRecord> events,
        Func<SessionEventRecord, string> nameSelector)
    {
        List<(string Name, int Count, double Duration)> groups = events
            .GroupBy(nameSelector, StringComparer.OrdinalIgnoreCase)
            .Select(group => (
                group.Key,
                group.Count(),
                group.Sum(static item => item.DurationMs ?? 0)))
            .OrderByDescending(static item => item.Item2)
            .ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        double totalDuration = groups.Sum(static item => item.Duration);

        return groups.Select(item => new CountedDurationRow
        {
            Name = item.Name,
            Count = item.Count,
            DurationMs = item.Duration,
            Share = totalDuration <= 0
                ? 0
                : Math.Clamp(item.Duration / totalDuration * 100, 0, 100)
        }).ToArray();
    }

    private static IReadOnlyList<FileAccessRow> BuildFileRows(
        IEnumerable<SessionEventRecord> events,
        params CanonicalToolKind[] kinds)
    {
        HashSet<CanonicalToolKind> acceptedKinds = [.. kinds];
        return events
            .Where(item => acceptedKinds.Contains(item.ToolKind))
            .SelectMany(static item => item.TargetPaths)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(static path => new FileAccessRow { FullPath = path })
            .ToArray();
    }

    private static IReadOnlyList<SubagentSummary> BuildSubagents(
        IReadOnlyList<SessionEventRecord> events)
    {
        return events
            .Where(static item =>
                item.Role is ObservationRole.SubagentStart or ObservationRole.SubagentStop ||
                item.EventKind is
                    CanonicalEventKind.SubagentStarted or
                    CanonicalEventKind.SubagentCompleted)
            .GroupBy(
                static item => item.AgentId ?? item.Id,
                StringComparer.Ordinal)
            .Select(group =>
            {
                SessionEventRecord first = group.First();
                SessionEventRecord last = group.Last();
                return new SubagentSummary
                {
                    Type = first.AgentType,
                    DurationMs = group.Sum(static item => item.DurationMs ?? 0),
                    Status = last.Status,
                    TaskPreview = Preview(first.Task),
                    LastMessagePreview = Preview(last.Text)
                };
            })
            .ToArray();
    }

    private static long? AggregateUsage(
        IEnumerable<SessionEventRecord> events,
        Func<string, bool> predicate)
    {
        UsageMeasurement[] measurements = events
            .SelectMany(static item => item.UsageMeasurements)
            .Where(item => predicate(item.Name))
            .ToArray();
        if (measurements.Length == 0)
        {
            return null;
        }

        long deltas = measurements
            .Where(static item => item.Behavior == UsageBehavior.Delta)
            .Sum(static item => item.Value);
        long? snapshot = measurements
            .Where(static item => item.Behavior != UsageBehavior.Delta)
            .Select(static item => (long?)item.Value)
            .Max();

        return snapshot ?? deltas;
    }

    private static bool IsInputToken(string name)
    {
        string normalized = NormalizeUsageName(name);
        return normalized.Contains("input", StringComparison.Ordinal) &&
            !normalized.Contains("cache", StringComparison.Ordinal);
    }

    private static bool IsOutputToken(string name)
    {
        string normalized = NormalizeUsageName(name);
        return normalized.Contains("output", StringComparison.Ordinal);
    }

    private static bool IsCacheReadToken(string name)
    {
        string normalized = NormalizeUsageName(name);
        return normalized.Contains("cache", StringComparison.Ordinal) &&
            (normalized.Contains("read", StringComparison.Ordinal) ||
                normalized.Contains("cached", StringComparison.Ordinal));
    }

    private static bool IsCacheWriteToken(string name)
    {
        string normalized = NormalizeUsageName(name);
        return normalized.Contains("cache", StringComparison.Ordinal) &&
            (normalized.Contains("write", StringComparison.Ordinal) ||
                normalized.Contains("creation", StringComparison.Ordinal));
    }

    private static string NormalizeUsageName(string name) =>
        name.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();

    private static bool IsToolRequest(SessionEventRecord item) =>
        item.Role == ObservationRole.ToolRequest ||
        item.EventKind == CanonicalEventKind.ToolRequested;

    private static bool IsMcp(SessionEventRecord item) =>
        item.ToolKind == CanonicalToolKind.Mcp ||
        !string.IsNullOrWhiteSpace(item.McpServerName);

    private static bool IsThought(SessionEventRecord item) =>
        item.Role == ObservationRole.AgentThought ||
        item.EventKind == CanonicalEventKind.AssistantThought;

    private static bool IsCompaction(SessionEventRecord item) =>
        item.Role is ObservationRole.CompactionStart or ObservationRole.CompactionEnd ||
        item.EventKind is
            CanonicalEventKind.CompactionStarted or
            CanonicalEventKind.CompactionCompleted;

    private static string ToolName(SessionEventRecord item) =>
        item.ToolName ?? item.ToolKind.ToString();

    private static string McpName(SessionEventRecord item)
    {
        string? server = item.McpServerName;
        string? tool = item.McpToolName ?? item.ToolName;
        return (server, tool) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{server} / {tool}",
            (_, { Length: > 0 }) => tool,
            ({ Length: > 0 }, _) => server,
            _ => "MCP"
        };
    }

    private static string BuildBadge(
        bool isSession,
        int turnCount,
        int toolCount,
        int mcpCount,
        int thoughtCount)
    {
        List<string> parts = [];
        if (isSession)
        {
            parts.Add($"{turnCount} turn{(turnCount == 1 ? string.Empty : "s")}");
        }

        if (toolCount > 0)
        {
            parts.Add($"{toolCount} tool{(toolCount == 1 ? string.Empty : "s")}");
        }

        if (mcpCount > 0)
        {
            parts.Add($"{mcpCount} MCP");
        }

        if (thoughtCount > 0)
        {
            parts.Add($"{thoughtCount} thought{(thoughtCount == 1 ? string.Empty : "s")}");
        }

        return string.Join(" \u00b7 ", parts);
    }

    private static string BuildTokenLine(
        long? input,
        long output,
        long cacheRead,
        long cacheWrite)
    {
        List<string> parts = [];
        if (input is long inputValue)
        {
            parts.Add($"input {inputValue:N0}");
        }

        if (output > 0)
        {
            parts.Add($"output {output:N0}");
        }

        if (cacheRead > 0)
        {
            parts.Add($"cache read {cacheRead:N0}");
        }

        if (cacheWrite > 0)
        {
            parts.Add($"cache write {cacheWrite:N0}");
        }

        return string.Join(" \u00b7 ", parts);
    }

    private static TimeSpan CalculateWallTime(
        DateTimeOffset? start,
        DateTimeOffset? end)
    {
        if (start is null || end is null || end < start)
        {
            return TimeSpan.Zero;
        }

        return end.Value - start.Value;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "\u2014";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return $"{duration.TotalSeconds:0.#}s";
    }

    private static string? Preview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string oneLine = value.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 120
            ? oneLine
            : oneLine[..120].TrimEnd() + "\u2026";
    }
}
