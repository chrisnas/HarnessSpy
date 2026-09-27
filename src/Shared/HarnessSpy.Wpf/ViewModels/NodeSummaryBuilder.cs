using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;
using HarnessSpy.Core.Runtimes.Cursor;
using HarnessSpy.Core.Services;

namespace HarnessSpy.Wpf.ViewModels;

internal static class NodeSummaryBuilder
{
    private const int BadgeToolLimit = 3;
    private static readonly UsageAggregator UsageAggregator = new();
    private static readonly UsageNameClassifier UsageNames = new();
    private static readonly CommandTextExtractor CommandTexts = new();

    public static NodeSummary Build(
        IEnumerable<TreeNodeViewModel> nodes,
        bool isSession,
        int turnCount,
        int abortedTurnCount,
        IEnumerable<TranscriptEvidence>? containerEvidence = null)
    {
        List<TreeNodeViewModel> materialized =
            nodes as List<TreeNodeViewModel> ?? [.. nodes];
        Dictionary<string, CountAccumulator> tools = new(StringComparer.Ordinal);
        Dictionary<string, CountAccumulator> mcp = new(StringComparer.Ordinal);
        Dictionary<string, SubagentAccumulator> subagents = new(StringComparer.Ordinal);
        List<UsageSample> usageSamples = [];
        SortedSet<string> skills = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> slashCommands = new(StringComparer.OrdinalIgnoreCase);
        FileAccessAccumulator fileAccess = new();

        int commands = 0;
        int failures = 0;
        int thoughtCount = 0;
        double thoughtDurationMs = 0;
        int thoughtCharacterCount = 0;
        int compactionCount = 0;
        bool aborted = false;
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;

        Walk(
            materialized,
            tools,
            mcp,
            subagents,
            usageSamples,
            skills,
            slashCommands,
            fileAccess,
            ref commands,
            ref failures,
            ref thoughtCount,
            ref thoughtDurationMs,
            ref thoughtCharacterCount,
            ref compactionCount,
            ref aborted,
            ref start,
            ref end);
        TranscriptEvidence[] scopedEvidence =
            containerEvidence?.ToArray() ?? [];
        foreach (TranscriptEvidence evidence in scopedEvidence)
        {
            AbsorbEvidence(
                evidence.Observation,
                usageSamples,
                skills,
                slashCommands);
            if (isSession)
            {
                AbsorbLifecycleBoundary(
                    evidence.Observation,
                    ref start,
                    ref end);
            }
        }

        if (isSession)
        {
            usageSamples = usageSamples
                .Select(sample =>
                {
                    bool sessionSnapshot =
                        sample.IsAuthoritative &&
                        (UsageNames.IsInputToken(sample.Measurement.Name) ||
                         UsageNames.IsCacheReadToken(sample.Measurement.Name));
                    return sessionSnapshot
                        ? sample with
                        {
                            Measurement = sample.Measurement with
                            {
                                Scope = UsageScope.Session,
                                Behavior = sample.Measurement.Behavior ==
                                    UsageBehavior.FinalSnapshot
                                        ? UsageBehavior.FinalSnapshot
                                        : UsageBehavior.CumulativeSnapshot
                            },
                            BucketId = "hook-session"
                        }
                        : sample;
                })
                .ToList();
        }

        IReadOnlyList<SkillSummaryRow> skillRows =
            BuildSkillRows(materialized, skills, scopedEvidence);

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

        UsageSample[] tokenSamples = usageSamples
            .Where(sample => sample.Measurement.Unit == "tokens")
            .ToArray();
        TokenTotals tokenTotals = new(
            UsageAggregator.Aggregate(tokenSamples, UsageNames.IsInputToken),
            UsageAggregator.Aggregate(tokenSamples, UsageNames.IsOutputToken) ?? 0,
            UsageAggregator.Aggregate(tokenSamples, UsageNames.IsCacheReadToken) ?? 0,
            UsageAggregator.Aggregate(tokenSamples, UsageNames.IsCacheWriteToken) ?? 0,
            UsageAggregator.Aggregate(tokenSamples, UsageNames.IsReasoningToken) ?? 0);
        IReadOnlyList<UsageSummaryRow> accounting = UsageAggregator
            .AggregateByName(usageSamples)
            .Where(value =>
                value.Unit != "tokens" ||
                !UsageNames.IsAnyToken(value.Name))
            .Select(value => new UsageSummaryRow
            {
                Name = value.Name,
                Value = value.Value,
                Unit = value.Unit
            })
            .ToArray();
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
            CompactionCount = compactionCount,
            InputTokens = tokenTotals.Input,
            OutputTokens = tokenTotals.Output,
            CacheReadTokens = tokenTotals.CacheRead,
            CacheWriteTokens = tokenTotals.CacheWrite,
            ReasoningTokens = tokenTotals.Reasoning,
            Tools = toolRows,
            McpCalls = mcpRows,
            Thoughts = thoughtRows,
            Skills = skillRows.Select(row => row.Name).ToArray(),
            SkillDetails = skillRows,
            Commands = BuildCommands(materialized, slashCommands),
            ReadFiles = ToFileRows(fileAccess.Reads),
            WrittenFiles = ToFileRows(fileAccess.Writes),
            DeletedFiles = ToFileRows(fileAccess.Deletes),
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
                fileAccess),
            Accounting = accounting,
            Badge = BuildBadge(
                isSession,
                turnCount,
                abortedTurnCount,
                isAborted,
                toolRows,
                toolCallCount,
                fileAccess.Writes.Count + fileAccess.Deletes.Count,
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
        List<UsageSample> usageSamples,
        SortedSet<string> skills,
        SortedSet<string> slashCommands,
        FileAccessAccumulator fileAccess,
        ref int commands,
        ref int failures,
        ref int thoughtCount,
        ref double thoughtDurationMs,
        ref int thoughtCharacterCount,
        ref int compactionCount,
        ref bool aborted,
        ref DateTimeOffset? start,
        ref DateTimeOffset? end)
    {
        foreach (TreeNodeViewModel node in nodes)
        {
            foreach (TranscriptEvidence evidence in node.Evidence)
            {
                AbsorbEvidence(
                    evidence.Observation,
                    usageSamples,
                    skills,
                    slashCommands);
            }

            HookObservation? observation = node.Observation;
            if (observation is not null)
            {
                HookObservation? identityEvidence = node.Evidence
                    .Select(static evidence => evidence.Observation)
                    .FirstOrDefault(static evidence =>
                        evidence.ToolKind == CanonicalToolKind.Mcp ||
                        evidence.McpServerName is not null);

                // A transcript-only tool request that never matched a hook is
                // still a real call the agent made, so count it once here. Its
                // fragment is excluded from Absorb (ExcludeFromSummary), and the
                // matched-and-nested duplicates are marked secondary, so this is
                // the only place such a call is tallied.
                if (IsCountableTranscriptTool(node, observation))
                {
                    AbsorbTranscriptTool(observation, tools, mcp, fileAccess);
                }

                Absorb(
                    observation,
                    identityEvidence,
                    tools,
                    mcp,
                    subagents,
                    usageSamples,
                    skills,
                    slashCommands,
                    fileAccess,
                    ref commands,
                    ref failures,
                    ref thoughtCount,
                    ref thoughtDurationMs,
                    ref thoughtCharacterCount,
                    ref compactionCount,
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
                    usageSamples,
                    skills,
                    slashCommands,
                    fileAccess,
                    ref commands,
                    ref failures,
                    ref thoughtCount,
                    ref thoughtDurationMs,
                    ref thoughtCharacterCount,
                    ref compactionCount,
                    ref aborted,
                    ref start,
                    ref end);
            }
        }
    }

    private static void Absorb(
        HookObservation observation,
        HookObservation? identityEvidence,
        Dictionary<string, CountAccumulator> tools,
        Dictionary<string, CountAccumulator> mcp,
        Dictionary<string, SubagentAccumulator> subagents,
        List<UsageSample> usageSamples,
        SortedSet<string> skills,
        SortedSet<string> slashCommands,
        FileAccessAccumulator fileAccess,
        ref int commands,
        ref int failures,
        ref int thoughtCount,
        ref double thoughtDurationMs,
        ref int thoughtCharacterCount,
        ref int compactionCount,
        ref bool aborted,
        ref DateTimeOffset? start,
        ref DateTimeOffset? end)
    {
        if (observation.SkillName is string skillName)
        {
            skills.Add(skillName);
        }

        foreach (string mentionedSkill in observation.SkillMentions)
        {
            skills.Add(mentionedSkill);
        }

        foreach (string slashCommand in observation.SlashCommands)
        {
            slashCommands.Add(slashCommand);
        }

        RecordUsage(observation, usageSamples);

        ObservationInterpretation interpretation = observation.Interpretation;
        if (interpretation.ExcludeFromSummary)
        {
            return;
        }

        if (start is null || observation.ObservedAtUtc < start)
        {
            start = observation.ObservedAtUtc;
        }

        if (end is null || observation.ObservedAtUtc > end)
        {
            end = observation.ObservedAtUtc;
        }

        switch (interpretation.Role)
        {
            case ObservationRole.ToolRequest:
                if (IsNativeMcpToolCall(observation) ||
                    identityEvidence?.ToolKind == CanonicalToolKind.Mcp)
                {
                    AddCount(mcp, McpKey(identityEvidence ?? observation));
                }
                else if (!observation.IsMcpPrefixedTool && observation.ToolName is string preTool)
                {
                    AddCount(tools, preTool);
                }

                break;

            case ObservationRole.ToolSuccess:
                if (IsNativeMcpToolCall(observation))
                {
                    AddDuration(mcp, McpKey(observation), observation.DurationMs);
                }
                else if (!observation.IsMcpPrefixedTool && observation.ToolName is string postTool)
                {
                    AddDuration(tools, postTool, observation.DurationMs);
                }

                if (!observation.IsMcpPrefixedTool)
                {
                    fileAccess.Record(observation.ToolKind, observation.TargetFilePaths);
                }

                break;

            case ObservationRole.InnerExecutionStart when
                interpretation.InnerCategory == InnerExecutionCategory.Mcp:
                AddCount(mcp, McpKey(observation));
                break;

            case ObservationRole.InnerExecutionStart when
                interpretation.InnerCategory == InnerExecutionCategory.Shell:
                commands++;
                break;

            case ObservationRole.InnerExecutionEnd when
                interpretation.InnerCategory == InnerExecutionCategory.Mcp:
                AddDuration(mcp, McpKey(observation), observation.DurationMs);
                break;

            case ObservationRole.FileAccess when
                interpretation.InnerCategory == InnerExecutionCategory.FileRead:
                fileAccess.Record(CanonicalToolKind.FileRead, observation.TargetFilePaths);
                break;

            case ObservationRole.FileAccess when
                interpretation.InnerCategory == InnerExecutionCategory.FileEdit:
                fileAccess.Record(CanonicalToolKind.FileEdit, observation.TargetFilePaths);
                break;

            case ObservationRole.SubagentStart:
                GetSubagent(subagents, observation).ApplyStart(observation);
                break;

            case ObservationRole.SubagentStop:
                GetSubagent(subagents, observation).ApplyStop(observation);
                break;

            case ObservationRole.AgentThought:
                thoughtCount++;
                thoughtCharacterCount += observation.Text?.Length ?? 0;
                if (observation.DurationMs is double thoughtMs)
                {
                    thoughtDurationMs += thoughtMs;
                }

                break;

            case ObservationRole.CompactionStart:
                compactionCount++;
                break;

            case ObservationRole.TurnStop when observation.IsAbortedStop:
                aborted = true;
                break;
        }

        if (interpretation.CountsAsFailure)
        {
            failures++;
        }
    }

    private static void AbsorbEvidence(
        HookObservation observation,
        List<UsageSample> usageSamples,
        ISet<string> skills,
        ISet<string> slashCommands)
    {
        RecordUsage(observation, usageSamples);

        if (observation.SkillName is string skillName)
        {
            skills.Add(skillName);
        }

        foreach (string mentionedSkill in observation.SkillMentions)
        {
            skills.Add(mentionedSkill);
        }

        foreach (string slashCommand in observation.SlashCommands)
        {
            slashCommands.Add(slashCommand);
        }
    }

    private static void AbsorbLifecycleBoundary(
        HookObservation observation,
        ref DateTimeOffset? start,
        ref DateTimeOffset? end)
    {
        if (observation.Interpretation.Role == ObservationRole.SessionStart &&
            (start is null || observation.ObservedAtUtc < start))
        {
            start = observation.ObservedAtUtc;
        }

        if (observation.Interpretation.Role == ObservationRole.SessionEnd &&
            (end is null || observation.ObservedAtUtc > end))
        {
            end = observation.ObservedAtUtc;
        }
    }

    private static void RecordUsage(
        HookObservation observation,
        ICollection<UsageSample> samples)
    {
        string bucketId =
            observation.GenerationId ??
            observation.ProviderScopedSessionId;
        foreach (UsageMeasurement measurement in observation.Interpretation.UsageMeasurements)
        {
            samples.Add(new UsageSample(
                measurement,
                bucketId,
                observation.EffectiveTimestamp));
        }

        if (!observation.HasTokenCounts)
        {
            return;
        }

        string source = observation.EventId.ToString("N");
        UsageBehavior snapshotBehavior = observation.IsStop
            ? UsageBehavior.FinalSnapshot
            : UsageBehavior.CumulativeSnapshot;

        AddLegacyUsage(
            samples,
            observation.InputTokens,
            "input_tokens",
            UsageScope.Turn,
            snapshotBehavior,
            source,
            bucketId,
            observation.EffectiveTimestamp);
        AddLegacyUsage(
            samples,
            observation.OutputTokens,
            "output_tokens",
            UsageScope.Turn,
            observation.IsStop ? UsageBehavior.FinalSnapshot : UsageBehavior.Delta,
            source,
            bucketId,
            observation.EffectiveTimestamp);
        AddLegacyUsage(
            samples,
            observation.CacheReadTokens,
            "cache_read_tokens",
            UsageScope.Turn,
            snapshotBehavior,
            source,
            bucketId,
            observation.EffectiveTimestamp);
        AddLegacyUsage(
            samples,
            observation.CacheWriteTokens,
            "cache_write_tokens",
            UsageScope.Turn,
            observation.IsStop ? UsageBehavior.FinalSnapshot : UsageBehavior.Delta,
            source,
            bucketId,
            observation.EffectiveTimestamp);
    }

    private static void AddLegacyUsage(
        ICollection<UsageSample> samples,
        long? value,
        string name,
        UsageScope scope,
        UsageBehavior behavior,
        string source,
        string bucketId,
        DateTimeOffset timestamp)
    {
        if (value is not long count)
        {
            return;
        }

        samples.Add(new UsageSample(
            new UsageMeasurement(
                name,
                count,
                "tokens",
                scope,
                behavior,
                source),
            bucketId,
            timestamp,
            IsAuthoritative: true));
    }

    // A transcript tool node contributes to the summary only when it is the
    // primary representation of its call: transcript-sourced, a tool request,
    // and not a secondary node nested under a matching hook.
    private static bool IsCountableTranscriptTool(
        TreeNodeViewModel node,
        HookObservation observation) =>
        observation.IsTranscriptSourced &&
        !node.IsSecondaryToolNode &&
        observation.Interpretation.Role == ObservationRole.ToolRequest;

    private static void AbsorbTranscriptTool(
        HookObservation observation,
        Dictionary<string, CountAccumulator> tools,
        Dictionary<string, CountAccumulator> mcp,
        FileAccessAccumulator fileAccess)
    {
        if (CursorToolSemantics.IsMcpExecution(
                observation.ToolKind,
                observation.McpToolName ?? observation.ToolName,
                observation.McpServerName))
        {
            AddCount(mcp, McpKey(observation));
            return;
        }

        AddCount(tools, observation.ToolName ?? observation.ToolKind.ToString());
        fileAccess.Record(observation.ToolKind, observation.TargetFilePaths);
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

    // Claude and Copilot have no dedicated before/afterMCPExecution pair like
    // Cursor - their PreToolUse/PostToolUse events are the only signal for an MCP
    // call, so they are counted here (as MCP) instead of as native tools. The MCP
    // kind is provider-neutral: Claude sets it from the "mcp__" prefix and Copilot
    // from its hooks-only heuristics. Cursor also flags the kind on its "MCP:"
    // pre/post events, but those are excluded because its before/afterMCPExecution
    // pair is the canonical MCP signal and already counts the call.
    private static bool IsNativeMcpToolCall(HookObservation observation) =>
        observation.ToolKind == CanonicalToolKind.Mcp &&
        observation.ToolName?.StartsWith("MCP:", StringComparison.Ordinal) != true;

    private static string McpKey(HookObservation observation)
    {
        string? server = observation.McpServerName;
        string? tool =
            observation.McpToolName ??
            StripMcpPrefix(observation.ToolName);

        // Copilot flattens an MCP call as "<server>-<tool>" in a single field.
        // Use that flattened name as the key so a request and its completion
        // agree even before the server is learned from the permission event.
        if (!string.IsNullOrEmpty(server) && tool is not null &&
            tool.StartsWith(server + "-", StringComparison.Ordinal))
        {
            return tool;
        }

        if (!string.IsNullOrEmpty(server) && !string.IsNullOrEmpty(tool))
        {
            return $"{server}/{tool}";
        }

        return tool ?? server ?? "MCP";
    }

    // Claude's native tool_name is "mcp__<server>__<tool>"; Cursor already
    // reports the bare tool name via a separate field, so this is a no-op there.
    private static string? StripMcpPrefix(string? toolName)
    {
        if (toolName is null || !toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return toolName;
        }

        string remainder = toolName[5..];
        int separator = remainder.IndexOf("__", StringComparison.Ordinal);
        return separator > 0 ? remainder[(separator + 2)..] : remainder;
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

    private static IReadOnlyList<SkillSummaryRow> BuildSkillRows(
        IEnumerable<TreeNodeViewModel> nodes,
        IEnumerable<string> knownNames,
        IEnumerable<TranscriptEvidence> containerEvidence)
    {
        Dictionary<string, SortedSet<SkillEvidenceStage>> stages =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in knownNames)
        {
            stages.TryAdd(name, []);
        }

        Visit(nodes);
        foreach (TranscriptEvidence evidence in containerEvidence)
        {
            Record(evidence.Observation);
        }

        return stages
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new SkillSummaryRow
            {
                Name = pair.Key,
                Stages = pair.Value.ToArray()
            })
            .ToArray();

        void Visit(IEnumerable<TreeNodeViewModel> current)
        {
            foreach (TreeNodeViewModel node in current)
            {
                if (node.Observation is HookObservation observation)
                {
                    Record(observation);
                }

                foreach (TranscriptEvidence evidence in node.Evidence)
                {
                    Record(evidence.Observation);
                }

                Visit(node.Children);
            }
        }

        void Record(HookObservation observation)
        {
            if (observation.Interpretation.Skill is SkillEvidence explicitSkill)
            {
                AddStage(explicitSkill.SkillName, explicitSkill.Stage);
                return;
            }

            if (observation.SkillName is not string inferred)
            {
                return;
            }

            if (string.Equals(
                    observation.ToolName,
                    "Skill",
                    StringComparison.OrdinalIgnoreCase))
            {
                AddStage(inferred, SkillEvidenceStage.Invoked);
            }
            else if (observation.ToolKind == CanonicalToolKind.FileRead ||
                observation.Interpretation.Role == ObservationRole.InstructionsLoaded)
            {
                AddStage(inferred, SkillEvidenceStage.Loaded);
            }
        }

        void AddStage(string name, SkillEvidenceStage stage)
        {
            if (!stages.TryGetValue(name, out SortedSet<SkillEvidenceStage>? values))
            {
                values = [];
                stages[name] = values;
            }

            values.Add(stage);
        }
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

        if (tokens.Reasoning > 0)
        {
            parts.Add($"reasoning {HookObservation.FormatTokens(tokens.Reasoning)}");
        }

        return string.Join(" \u00b7 ", parts);
    }

    private static IReadOnlyList<FileAccessRow> ToFileRows(IReadOnlyCollection<string> paths) =>
        paths.Select(static path => new FileAccessRow { FullPath = path }).ToArray();

    private static IReadOnlyList<string> BuildCommands(
        IEnumerable<TreeNodeViewModel> nodes,
        IEnumerable<string> slashCommands)
    {
        List<string> commands = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        Visit(nodes);
        foreach (string slashCommand in slashCommands)
        {
            Add(slashCommand);
        }

        return commands;

        void Visit(IEnumerable<TreeNodeViewModel> current)
        {
            foreach (TreeNodeViewModel node in current)
            {
                if (node.Observation is HookObservation observation &&
                    observation.Interpretation.Role ==
                        ObservationRole.ToolRequest &&
                    observation.ToolKind == CanonicalToolKind.Shell &&
                    (!observation.IsTranscriptSourced ||
                     !node.IsSecondaryToolNode))
                {
                    Add(CommandTexts.Extract(observation.Payload));
                }

                Visit(node.Children);
            }
        }

        void Add(string? command)
        {
            if (!string.IsNullOrWhiteSpace(command) && seen.Add(command))
            {
                commands.Add(command);
            }
        }
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
        FileAccessAccumulator fileAccess)
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

        if (fileAccess.Reads.Count > 0)
        {
            kpis.Add(new KpiItem { Label = "Reads", Value = fileAccess.Reads.Count.ToString() });
        }

        if (fileAccess.Writes.Count > 0)
        {
            kpis.Add(new KpiItem { Label = "Writes", Value = fileAccess.Writes.Count.ToString() });
        }

        if (fileAccess.Deletes.Count > 0)
        {
            kpis.Add(new KpiItem { Label = "Deletes", Value = fileAccess.Deletes.Count.ToString() });
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

    private sealed class FileAccessAccumulator
    {
        private readonly FileAccessPathNormalizer _pathNormalizer = new();
        private readonly SortedSet<string> _reads = new(StringComparer.OrdinalIgnoreCase);
        private readonly SortedSet<string> _writes = new(StringComparer.OrdinalIgnoreCase);
        private readonly SortedSet<string> _deletes = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> Reads => _reads;

        public IReadOnlyCollection<string> Writes => _writes;

        public IReadOnlyCollection<string> Deletes => _deletes;

        public void Record(CanonicalToolKind kind, IEnumerable<string> paths)
        {
            SortedSet<string>? target = kind switch
            {
                CanonicalToolKind.FileRead => _reads,
                CanonicalToolKind.FileWrite or CanonicalToolKind.FileEdit => _writes,
                CanonicalToolKind.FileDelete => _deletes,
                _ => null
            };

            if (target is null)
            {
                return;
            }

            foreach (string path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    target.Add(_pathNormalizer.Normalize(path));
                }
            }
        }
    }

    private sealed class SubagentAccumulator
    {
        public string? Type { get; private set; }

        public double DurationMs { get; private set; }

        public string? Status { get; private set; }

        public string? Task { get; private set; }

        public string? LastMessage { get; private set; }

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
            LastMessage ??= observation.Text;
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
                TaskPreview = Truncate(Task),
                LastMessagePreview = Truncate(LastMessage)
            };
        }
    }

    private readonly record struct TokenTotals(
        long? Input,
        long Output,
        long CacheRead,
        long CacheWrite,
        long Reasoning);

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
