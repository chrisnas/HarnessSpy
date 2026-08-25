using CursorSpy.App.Models;

namespace CursorSpy.App.ViewModels;

internal static class NodeSummaryBuilder
{
    private const int BadgeToolLimit = 3;

    public static NodeSummary Build(
        IEnumerable<TreeNodeViewModel> nodes,
        bool isSession,
        int turnCount,
        int abortedTurnCount)
    {
        Dictionary<string, CountAccumulator> tools = new(StringComparer.Ordinal);
        Dictionary<string, CountAccumulator> mcp = new(StringComparer.Ordinal);
        Dictionary<string, SubagentAccumulator> subagents = new(StringComparer.Ordinal);
        Dictionary<string, TokenSnapshot> tokensByGeneration = new(StringComparer.Ordinal);
        SortedSet<string> skills = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> writtenFiles = new(StringComparer.OrdinalIgnoreCase);

        int edits = 0;
        int commands = 0;
        int failures = 0;
        int thoughtCount = 0;
        double thoughtDurationMs = 0;
        int thoughtCharacterCount = 0;
        bool aborted = false;
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;

        Walk(
            nodes,
            tools,
            mcp,
            subagents,
            tokensByGeneration,
            skills,
            writtenFiles,
            ref edits,
            ref commands,
            ref failures,
            ref thoughtCount,
            ref thoughtDurationMs,
            ref thoughtCharacterCount,
            ref aborted,
            ref start,
            ref end);

        IReadOnlyList<CountedDurationRow> toolRows = ToRows(tools);
        IReadOnlyList<CountedDurationRow> mcpRows = ToRows(mcp);
        IReadOnlyList<CountedDurationRow> thoughtRows = thoughtCount == 0
            ? []
            : [new CountedDurationRow
            {
                Name = HookObservation.FormatTokens(thoughtCharacterCount),
                Count = thoughtCount,
                DurationMs = thoughtDurationMs,
                Share = 100
            }];
        IReadOnlyList<SubagentSummary> subagentRows = subagents.Values
            .Select(static item => item.ToSummary())
            .OrderBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int toolCallCount = toolRows.Sum(row => row.Count);
        int mcpCallCount = mcpRows.Sum(row => row.Count);
        TimeSpan wallTime = start is not null && end is not null && end > start
            ? end.Value - start.Value
            : TimeSpan.Zero;

        TokenTotals tokenTotals = SumTokens(tokensByGeneration);
        string tokenLine = BuildTokenLine(tokenTotals);
        bool isAborted = isSession ? abortedTurnCount > 0 : aborted;

        return new NodeSummary
        {
            IsSession = isSession,
            TurnCount = turnCount,
            AbortedTurnCount = abortedTurnCount,
            IsAborted = isAborted,
            WallTime = wallTime,
            ToolCallCount = toolCallCount,
            McpCallCount = mcpCallCount,
            ThoughtCount = thoughtCount,
            ThoughtDurationMs = thoughtDurationMs,
            ThoughtCharacterCount = thoughtCharacterCount,
            InputTokens = tokenTotals.Input,
            OutputTokens = tokenTotals.Output,
            CacheReadTokens = tokenTotals.CacheRead,
            CacheWriteTokens = tokenTotals.CacheWrite,
            Tools = toolRows,
            McpCalls = mcpRows,
            Thoughts = thoughtRows,
            Skills = skills.ToArray(),
            WrittenFiles = writtenFiles.ToArray(),
            Subagents = subagentRows,
            Kpis = BuildKpis(
                isSession,
                turnCount,
                abortedTurnCount,
                isAborted,
                toolCallCount,
                mcpCallCount,
                thoughtCount,
                thoughtDurationMs,
                thoughtCharacterCount,
                wallTime,
                tokenLine,
                writtenFiles.Count),
            Badge = BuildBadge(
                isSession,
                turnCount,
                abortedTurnCount,
                isAborted,
                toolRows,
                toolCallCount,
                edits,
                commands,
                failures,
                wallTime,
                tokenTotals.Output),
            TokenLine = tokenLine
        };
    }

    private static void Walk(
        IEnumerable<TreeNodeViewModel> nodes,
        Dictionary<string, CountAccumulator> tools,
        Dictionary<string, CountAccumulator> mcp,
        Dictionary<string, SubagentAccumulator> subagents,
        Dictionary<string, TokenSnapshot> tokensByGeneration,
        SortedSet<string> skills,
        SortedSet<string> writtenFiles,
        ref int edits,
        ref int commands,
        ref int failures,
        ref int thoughtCount,
        ref double thoughtDurationMs,
        ref int thoughtCharacterCount,
        ref bool aborted,
        ref DateTimeOffset? start,
        ref DateTimeOffset? end)
    {
        foreach (TreeNodeViewModel node in nodes)
        {
            HookObservation? observation = node.Observation;
            if (observation is not null)
            {
                Absorb(
                    observation,
                    tools,
                    mcp,
                    subagents,
                    tokensByGeneration,
                    skills,
                    writtenFiles,
                    ref edits,
                    ref commands,
                    ref failures,
                    ref thoughtCount,
                    ref thoughtDurationMs,
                    ref thoughtCharacterCount,
                    ref aborted,
                    ref start,
                    ref end);
            }

            if (node.Children.Count > 0)
            {
                Walk(
                    node.Children,
                    tools,
                    mcp,
                    subagents,
                    tokensByGeneration,
                    skills,
                    writtenFiles,
                    ref edits,
                    ref commands,
                    ref failures,
                    ref thoughtCount,
                    ref thoughtDurationMs,
                    ref thoughtCharacterCount,
                    ref aborted,
                    ref start,
                    ref end);
            }
        }
    }

    private static void Absorb(
        HookObservation observation,
        Dictionary<string, CountAccumulator> tools,
        Dictionary<string, CountAccumulator> mcp,
        Dictionary<string, SubagentAccumulator> subagents,
        Dictionary<string, TokenSnapshot> tokensByGeneration,
        SortedSet<string> skills,
        SortedSet<string> writtenFiles,
        ref int edits,
        ref int commands,
        ref int failures,
        ref int thoughtCount,
        ref double thoughtDurationMs,
        ref int thoughtCharacterCount,
        ref bool aborted,
        ref DateTimeOffset? start,
        ref DateTimeOffset? end)
    {
        if (start is null || observation.ObservedAtUtc < start)
        {
            start = observation.ObservedAtUtc;
        }

        if (end is null || observation.ObservedAtUtc > end)
        {
            end = observation.ObservedAtUtc;
        }

        if (observation.SkillName is string skillName)
        {
            skills.Add(skillName);
        }

        RecordTokens(observation, tokensByGeneration);

        switch (observation.HookEventName)
        {
            case "preToolUse":
                if (!observation.IsMcpPrefixedTool && observation.ToolName is string preTool)
                {
                    AddCount(tools, preTool);
                }

                break;

            case "postToolUse":
                if (!observation.IsMcpPrefixedTool && observation.ToolName is string postTool)
                {
                    AddDuration(tools, postTool, observation.DurationMs);
                }

                break;

            case "postToolUseFailure":
                failures++;
                break;

            case "beforeMCPExecution":
                AddCount(mcp, McpKey(observation));
                break;

            case "afterMCPExecution":
                AddDuration(mcp, McpKey(observation), observation.DurationMs);
                break;

            case "afterFileEdit":
                edits++;
                if (observation.TargetFilePath is string editedFile)
                {
                    writtenFiles.Add(editedFile);
                }

                break;

            case "beforeShellExecution":
                commands++;
                break;

            case "subagentStart":
                GetSubagent(subagents, observation).ApplyStart(observation);
                break;

            case "subagentStop":
                GetSubagent(subagents, observation).ApplyStop(observation);
                break;

            case "afterAgentThought":
                thoughtCount++;
                thoughtCharacterCount += observation.Text?.Length ?? 0;
                if (observation.DurationMs is double thoughtMs)
                {
                    thoughtDurationMs += thoughtMs;
                }

                break;

            case "stop" when observation.IsAbortedStop:
                aborted = true;
                break;
        }
    }

    private static void RecordTokens(HookObservation observation, Dictionary<string, TokenSnapshot> tokensByGeneration)
    {
        if (!observation.HasTokenCounts)
        {
            return;
        }

        string key = observation.GenerationId ?? observation.EventId.ToString("N");
        TokenSnapshot snapshot = new(
            observation.InputTokens,
            observation.OutputTokens ?? 0,
            observation.CacheReadTokens ?? 0,
            observation.CacheWriteTokens ?? 0,
            observation.ObservedAtUtc);

        if (observation.IsStop)
        {
            tokensByGeneration[key] = snapshot;
            return;
        }

        if (StringComparer.Ordinal.Equals(observation.HookEventName, "afterAgentResponse") &&
            !tokensByGeneration.ContainsKey(key))
        {
            tokensByGeneration[key] = snapshot;
        }
    }

    private static void AddCount(Dictionary<string, CountAccumulator> map, string name)
    {
        if (!map.TryGetValue(name, out CountAccumulator? item))
        {
            item = new CountAccumulator();
            map[name] = item;
        }

        item.Count++;
    }

    private static void AddDuration(Dictionary<string, CountAccumulator> map, string name, double? durationMs)
    {
        if (!map.TryGetValue(name, out CountAccumulator? item))
        {
            item = new CountAccumulator { Count = 1 };
            map[name] = item;
        }
        else if (item.Count == 0)
        {
            item.Count = 1;
        }

        if (durationMs is double ms)
        {
            item.DurationMs += ms;
        }
    }

    private static string McpKey(HookObservation observation)
    {
        string? server = observation.McpServerName;
        string? tool = observation.ToolName;
        if (!string.IsNullOrEmpty(server) && !string.IsNullOrEmpty(tool))
        {
            return $"{server}/{tool}";
        }

        return tool ?? server ?? "MCP";
    }

    private static SubagentAccumulator GetSubagent(
        Dictionary<string, SubagentAccumulator> subagents,
        HookObservation observation)
    {
        string key = observation.SubagentId ?? observation.EventId.ToString("N");
        if (!subagents.TryGetValue(key, out SubagentAccumulator? item))
        {
            item = new SubagentAccumulator();
            subagents[key] = item;
        }

        return item;
    }

    private static IReadOnlyList<CountedDurationRow> ToRows(Dictionary<string, CountAccumulator> map)
    {
        double totalMs = map.Values.Sum(item => item.DurationMs);
        return map
            .Select(pair => new CountedDurationRow
            {
                Name = pair.Key,
                Count = pair.Value.Count,
                DurationMs = pair.Value.DurationMs,
                Share = totalMs > 0 ? pair.Value.DurationMs / totalMs * 100 : 0
            })
            .OrderByDescending(row => row.DurationMs)
            .ThenByDescending(row => row.Count)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static TokenTotals SumTokens(Dictionary<string, TokenSnapshot> tokensByGeneration)
    {
        if (tokensByGeneration.Count == 0)
        {
            return default;
        }

        TokenSnapshot last = tokensByGeneration.Values.OrderBy(item => item.At).Last();
        return new TokenTotals(
            last.Input,
            tokensByGeneration.Values.Sum(item => item.Output),
            last.CacheRead,
            tokensByGeneration.Values.Sum(item => item.CacheWrite));
    }

    private static string BuildTokenLine(TokenTotals tokens)
    {
        List<string> parts = [];
        if (tokens.Input is long input)
        {
            parts.Add($"in {HookObservation.FormatTokens(input)}");
        }

        if (tokens.Output > 0)
        {
            parts.Add($"out {HookObservation.FormatTokens(tokens.Output)}");
        }

        if (tokens.CacheRead > 0)
        {
            parts.Add($"cache r {HookObservation.FormatTokens(tokens.CacheRead)}");
        }

        if (tokens.CacheWrite > 0)
        {
            parts.Add($"cache w {HookObservation.FormatTokens(tokens.CacheWrite)}");
        }

        return string.Join(" \u00b7 ", parts);
    }

    private static IReadOnlyList<KpiItem> BuildKpis(
        bool isSession,
        int turnCount,
        int abortedTurnCount,
        bool isAborted,
        int toolCallCount,
        int mcpCallCount,
        int thoughtCount,
        double thoughtDurationMs,
        int thoughtCharacterCount,
        TimeSpan wallTime,
        string tokenLine,
        int writtenFileCount)
    {
        List<KpiItem> kpis = [];

        if (isSession)
        {
            kpis.Add(new KpiItem { Label = "Turns", Value = turnCount.ToString() });
        }

        if (wallTime > TimeSpan.Zero)
        {
            kpis.Add(new KpiItem { Label = "Duration", Value = HookObservation.FormatDuration(wallTime) });
        }

        if (thoughtCount > 0)
        {
            string chars = HookObservation.FormatTokens(thoughtCharacterCount);
            string thoughtsValue = thoughtDurationMs > 0
                ? $"{chars} \u00d7{thoughtCount} \u00b7 {HookObservation.FormatDuration(TimeSpan.FromMilliseconds(thoughtDurationMs))}"
                : $"{chars} \u00d7{thoughtCount}";
            kpis.Add(new KpiItem { Label = "Thoughts", Value = thoughtsValue });
        }

        if (mcpCallCount > 0)
        {
            kpis.Add(new KpiItem { Label = "MCP", Value = mcpCallCount.ToString() });
        }

        kpis.Add(new KpiItem { Label = "Tools", Value = toolCallCount.ToString() });

        if (writtenFileCount > 0)
        {
            kpis.Add(new KpiItem { Label = "Files", Value = writtenFileCount.ToString() });
        }

        if (!string.IsNullOrEmpty(tokenLine))
        {
            kpis.Add(new KpiItem { Label = "Tokens", Value = tokenLine });
        }

        if (isSession && abortedTurnCount > 0)
        {
            kpis.Add(new KpiItem
            {
                Label = "Aborted",
                Value = abortedTurnCount.ToString(),
                IsWarning = true
            });
        }
        else if (!isSession && isAborted)
        {
            kpis.Add(new KpiItem { Label = "Status", Value = "aborted", IsWarning = true });
        }

        return kpis;
    }

    private static string BuildBadge(
        bool isSession,
        int turnCount,
        int abortedTurnCount,
        bool isAborted,
        IReadOnlyList<CountedDurationRow> tools,
        int toolCallCount,
        int edits,
        int commands,
        int failures,
        TimeSpan wallTime,
        long outputTokens)
    {
        List<string> parts = [];

        if (isSession)
        {
            if (turnCount > 0)
            {
                parts.Add(turnCount == 1 ? "1 turn" : $"{turnCount} turns");
            }
            if (abortedTurnCount > 0)
            {
                parts.Add(abortedTurnCount == 1 ? "1 aborted" : $"{abortedTurnCount} aborted");
            }

            if (toolCallCount > 0)
            {
                parts.Add(toolCallCount == 1 ? "1 tool" : $"{toolCallCount} tools");
            }

            if (outputTokens > 0)
            {
                parts.Add($"{HookObservation.FormatTokens(outputTokens)} out");
            }
            else if (wallTime > TimeSpan.Zero)
            {
                parts.Add(HookObservation.FormatDuration(wallTime));
            }

            return string.Join(" \u00b7 ", parts);
        }

        if (isAborted)
        {
            parts.Add("aborted");
        }

        foreach (CountedDurationRow row in tools.Take(BadgeToolLimit))
        {
            parts.Add($"{row.Name}\u00d7{row.Count}");
        }

        if (edits > 0)
        {
            parts.Add(edits == 1 ? "1 edit" : $"{edits} edits");
        }

        if (commands > 0)
        {
            parts.Add(commands == 1 ? "1 cmd" : $"{commands} cmds");
        }

        if (wallTime > TimeSpan.Zero)
        {
            parts.Add(HookObservation.FormatDuration(wallTime));
        }

        if (failures > 0)
        {
            parts.Add(failures == 1 ? "failed" : $"{failures} failures");
        }

        return string.Join(" \u00b7 ", parts);
    }

    private sealed class CountAccumulator
    {
        public int Count { get; set; }

        public double DurationMs { get; set; }
    }

    private sealed class SubagentAccumulator
    {
        public string Type { get; private set; } = "subagent";

        public double DurationMs { get; private set; }

        public string? Status { get; private set; }

        public string? Task { get; private set; }

        public void ApplyStart(HookObservation observation)
        {
            Type = observation.SubagentType ?? Type;
            Task ??= observation.Task;
        }

        public void ApplyStop(HookObservation observation)
        {
            Type = observation.SubagentType ?? Type;
            Status = observation.Status;
            Task ??= observation.Task;
            if (observation.DurationMs is double ms)
            {
                DurationMs = ms;
            }
        }

        public SubagentSummary ToSummary()
        {
            return new SubagentSummary
            {
                Type = Type,
                DurationMs = DurationMs,
                Status = Status,
                TaskPreview = Truncate(Task)
            };
        }
    }

    private readonly record struct TokenSnapshot(long? Input, long Output, long CacheRead, long CacheWrite, DateTimeOffset At);

    private readonly record struct TokenTotals(long? Input, long Output, long CacheRead, long CacheWrite);

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string oneLine = text.ReplaceLineEndings(" ").Trim();
        const int maxLength = 80;
        return oneLine.Length <= maxLength
            ? oneLine
            : oneLine[..maxLength].TrimEnd() + "\u2026";
    }
}
