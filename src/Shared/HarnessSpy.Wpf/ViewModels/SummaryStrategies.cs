using HarnessSpy.Core.Models;

namespace HarnessSpy.Wpf.ViewModels;

// Per-harness summary strategy. The shared NodeSummaryBuilder produces the
// primitive rows (counts, durations, KPI chips, entity lists) from provider-
// neutral traits; a strategy selects/labels the meaningful content for its
// harness. Cursor reproduces the existing output exactly; the Claude and
// Copilot strategies exist so their summaries can diverge (e.g. derived vs
// exact turns, native tool grouping) without any provider check in shared code.
internal interface ISummaryStrategy
{
    NodeSummary Build(
        IEnumerable<TreeNodeViewModel> nodes,
        bool isSession,
        int turnCount,
        int abortedTurnCount);
}

internal class SharedTraitSummaryStrategy : ISummaryStrategy
{
    public NodeSummary Build(
        IEnumerable<TreeNodeViewModel> nodes,
        bool isSession,
        int turnCount,
        int abortedTurnCount) =>
        NodeSummaryBuilder.Build(nodes, isSession, turnCount, abortedTurnCount);
}

// The Cursor, Claude and Copilot strategies currently share the trait-based
// builder because it already groups by native tool names and provider-neutral
// roles. They are kept as distinct types so per-harness summary content can be
// specialised later without touching the shared renderer.
internal sealed class CursorSummaryStrategy : SharedTraitSummaryStrategy;

internal sealed class ClaudeSummaryStrategy : SharedTraitSummaryStrategy;

internal sealed class CopilotSummaryStrategy : SharedTraitSummaryStrategy;

internal static class SummaryStrategies
{
    private static readonly CursorSummaryStrategy Cursor = new();
    private static readonly ClaudeSummaryStrategy Claude = new();
    private static readonly CopilotSummaryStrategy Copilot = new();
    private static readonly SharedTraitSummaryStrategy Shared = new();

    // Builds a summary using the strategy for the harness that produced the
    // node's observations, so a session/turn is summarised the way its own
    // harness expects.
    public static NodeSummary Build(
        IEnumerable<TreeNodeViewModel> nodes,
        bool isSession,
        int turnCount,
        int abortedTurnCount)
    {
        List<TreeNodeViewModel> materialized = nodes as List<TreeNodeViewModel> ?? [.. nodes];
        return Resolve(DominantHarness(materialized))
            .Build(materialized, isSession, turnCount, abortedTurnCount);
    }

    private static ISummaryStrategy Resolve(string harnessId) => harnessId switch
    {
        HarnessIds.Cursor => Cursor,
        HarnessIds.ClaudeCode => Claude,
        HarnessIds.GitHubCopilot => Copilot,
        _ => Shared
    };

    private static string DominantHarness(IEnumerable<TreeNodeViewModel> nodes)
    {
        foreach (TreeNodeViewModel node in nodes)
        {
            if (node.Observation is { } observation)
            {
                return HarnessIds.FromProvider(observation.Provider);
            }

            if (node.Children.Count > 0)
            {
                string nested = DominantHarness(node.Children);
                if (nested != HarnessIds.Unknown)
                {
                    return nested;
                }
            }
        }

        return HarnessIds.Unknown;
    }
}
