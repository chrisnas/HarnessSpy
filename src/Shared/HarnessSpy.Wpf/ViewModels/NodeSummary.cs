using HarnessSpy.Core.Models;

namespace HarnessSpy.Wpf.ViewModels;

public sealed class NodeSummary
{
    public static NodeSummary CreateEmpty(bool isSession)
    {
        return new NodeSummary
        {
            IsSession = isSession,
            Tools = [],
            McpCalls = [],
            Thoughts = [],
            Skills = [],
            WrittenFiles = [],
            Subagents = [],
            Kpis = []
        };
    }

    public required bool IsSession { get; init; }

    public int TurnCount { get; init; }

    public int AbortedTurnCount { get; init; }

    public bool IsAborted { get; init; }

    public TimeSpan WallTime { get; init; }

    public int ToolCallCount { get; init; }

    public int McpCallCount { get; init; }

    public int ThoughtCount { get; init; }

    public double ThoughtDurationMs { get; init; }

    public int ThoughtCharacterCount { get; init; }

    public long? InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long CacheReadTokens { get; init; }

    public long CacheWriteTokens { get; init; }

    public required IReadOnlyList<CountedDurationRow> Tools { get; init; }

    public required IReadOnlyList<CountedDurationRow> McpCalls { get; init; }

    public required IReadOnlyList<CountedDurationRow> Thoughts { get; init; }

    public required IReadOnlyList<string> Skills { get; init; }

    public required IReadOnlyList<string> WrittenFiles { get; init; }

    public required IReadOnlyList<SubagentSummary> Subagents { get; init; }

    public required IReadOnlyList<KpiItem> Kpis { get; init; }

    public string Badge { get; init; } = string.Empty;

    public string TokenLine { get; init; } = string.Empty;

    public bool HasTools => Tools.Count > 0;

    public bool HasMcp => McpCalls.Count > 0;

    public bool HasThoughts => ThoughtCount > 0;

    public bool HasSkills => Skills.Count > 0;

    public bool HasWrittenFiles => WrittenFiles.Count > 0;

    public bool HasSubagents => Subagents.Count > 0;

    public bool HasTokens =>
        InputTokens is not null ||
        OutputTokens > 0 ||
        CacheReadTokens > 0 ||
        CacheWriteTokens > 0;
}

public sealed class CountedDurationRow
{
    public required string Name { get; init; }

    public int Count { get; init; }

    public double DurationMs { get; init; }

    public double Share { get; init; }

    public string CountText => $"\u00d7{Count}";

    public string DurationText =>
        DurationMs > 0
            ? HookObservation.FormatDuration(TimeSpan.FromMilliseconds(DurationMs))
            : "\u2014";
}

public sealed class SubagentSummary
{
    public required string Type { get; init; }

    public double DurationMs { get; init; }

    public string? Status { get; init; }

    public string? TaskPreview { get; init; }

    public string Header
    {
        get
        {
            List<string> parts = [Type];
            if (DurationMs > 0)
            {
                parts.Add(HookObservation.FormatDuration(TimeSpan.FromMilliseconds(DurationMs)));
            }

            if (!string.IsNullOrEmpty(Status))
            {
                parts.Add(Status);
            }

            return string.Join(" \u00b7 ", parts);
        }
    }
}

public sealed class KpiItem
{
    public required string Label { get; init; }

    public required string Value { get; init; }

    public bool IsWarning { get; init; }
}
