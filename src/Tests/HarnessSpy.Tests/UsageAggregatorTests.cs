using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;

namespace HarnessSpy.Tests;

public sealed class UsageAggregatorTests
{
    [Fact]
    public void FinalSessionSnapshotOverridesRequestDeltas()
    {
        UsageAggregator aggregator = new();
        UsageSample[] samples =
        [
            Sample("output_tokens", 20, UsageScope.Request, UsageBehavior.Delta, "r1", "turn-1", 1),
            Sample("output_tokens", 30, UsageScope.Request, UsageBehavior.Delta, "r2", "turn-1", 2),
            Sample("output_tokens", 90, UsageScope.Session, UsageBehavior.FinalSnapshot, "shutdown", "session", 3)
        ];

        Assert.Equal(90, aggregator.Aggregate(samples, static name => name == "output_tokens"));
    }

    [Fact]
    public void TurnSnapshotsAreCombinedAcrossTurns()
    {
        UsageAggregator aggregator = new();
        UsageSample[] samples =
        [
            Sample("input_tokens", 100, UsageScope.Turn, UsageBehavior.CumulativeSnapshot, "a1", "turn-1", 1),
            Sample("input_tokens", 140, UsageScope.Turn, UsageBehavior.CumulativeSnapshot, "a2", "turn-1", 2),
            Sample("input_tokens", 80, UsageScope.Turn, UsageBehavior.CumulativeSnapshot, "b1", "turn-2", 3)
        ];

        Assert.Equal(220, aggregator.Aggregate(samples, static name => name == "input_tokens"));
    }

    [Fact]
    public void AuthoritativeSampleWinsWithinItsBucket()
    {
        UsageAggregator aggregator = new();
        UsageSample transcript = Sample(
            "output_tokens",
            25,
            UsageScope.Turn,
            UsageBehavior.Delta,
            "transcript",
            "turn-1",
            1);
        UsageSample hook = Sample(
            "output_tokens",
            20,
            UsageScope.Turn,
            UsageBehavior.Delta,
            "hook",
            "turn-1",
            2) with
        {
            IsAuthoritative = true
        };

        Assert.Equal(
            20,
            aggregator.Aggregate(
                [transcript, hook],
                static name => name == "output_tokens"));
    }

    [Fact]
    public void PerModelFinalSnapshotsAreSummedWhenNoAggregateExists()
    {
        UsageAggregator aggregator = new();
        UsageSample[] samples =
        [
            Sample("model-a.inputTokens", 40, UsageScope.Session, UsageBehavior.FinalSnapshot, "end", "session", 1),
            Sample("model-b.inputTokens", 60, UsageScope.Session, UsageBehavior.FinalSnapshot, "end", "session", 1)
        ];

        Assert.Equal(
            100,
            aggregator.Aggregate(
                samples,
                static name => name.Contains("input", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AggregateTokenDetailsSupersedePerModelBreakdown()
    {
        UsageAggregator aggregator = new();
        UsageSample[] samples =
        [
            Sample("tokenDetails.input", 100, UsageScope.Session, UsageBehavior.FinalSnapshot, "end", "session", 1),
            Sample("modelMetrics.model-a.usage.inputTokens", 40, UsageScope.Session, UsageBehavior.FinalSnapshot, "end", "session", 1),
            Sample("modelMetrics.model-b.usage.inputTokens", 60, UsageScope.Session, UsageBehavior.FinalSnapshot, "end", "session", 1)
        ];

        Assert.Equal(
            100,
            aggregator.Aggregate(
                samples,
                static name => name.Contains("input", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AgentModelMirrorDoesNotDuplicatePerModelFinalSnapshots()
    {
        UsageAggregator aggregator = new();
        UsageSample[] samples =
        [
            Sample(
                "modelMetrics.gpt.usage.reasoningTokens",
                1028,
                UsageScope.Session,
                UsageBehavior.FinalSnapshot,
                "end",
                "session",
                1),
            Sample(
                "agentMetrics.main.modelMetrics.gpt.usage.reasoningTokens",
                1028,
                UsageScope.Session,
                UsageBehavior.FinalSnapshot,
                "end",
                "session",
                1),
            Sample(
                "modelMetrics.mai.usage.reasoningTokens",
                320,
                UsageScope.Session,
                UsageBehavior.FinalSnapshot,
                "end",
                "session",
                1),
            Sample(
                "agentMetrics.main.modelMetrics.mai.usage.reasoningTokens",
                320,
                UsageScope.Session,
                UsageBehavior.FinalSnapshot,
                "end",
                "session",
                1)
        ];

        Assert.Equal(
            1348,
            aggregator.Aggregate(
                samples,
                static name => name.Contains(
                    "reasoning",
                    StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AgentModelBreakdownsAreSummedWithoutTopLevelModelMetrics()
    {
        UsageAggregator aggregator = new();
        UsageSample[] samples =
        [
            Sample(
                "agentMetrics.first.modelMetrics.gpt.usage.reasoningTokens",
                10,
                UsageScope.Session,
                UsageBehavior.FinalSnapshot,
                "end",
                "session",
                1),
            Sample(
                "agentMetrics.second.modelMetrics.gpt.usage.reasoningTokens",
                20,
                UsageScope.Session,
                UsageBehavior.FinalSnapshot,
                "end",
                "session",
                1)
        ];

        Assert.Equal(
            30,
            aggregator.Aggregate(
                samples,
                static name => name.Contains(
                    "reasoning",
                    StringComparison.OrdinalIgnoreCase)));
    }

    private static UsageSample Sample(
        string name,
        long value,
        UsageScope scope,
        UsageBehavior behavior,
        string source,
        string bucket,
        int second) =>
        new(
            new UsageMeasurement(
                name,
                value,
                "tokens",
                scope,
                behavior,
                source),
            bucket,
            DateTimeOffset.UnixEpoch.AddSeconds(second));
}
