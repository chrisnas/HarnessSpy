using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Tests;

public sealed class SessionPlanRevisionBuilderTests
{
    private readonly SessionPlanContentNormalizer _normalizer = new();
    private readonly SessionPlanRevisionBuilder _builder = new();

    [Fact]
    public void CurrentOnlyPlanHasNoObservedUpdates()
    {
        SessionPlanRevisionResult result = _builder.Build(
            Snapshot("plan body"),
            DateTimeOffset.UnixEpoch,
            Provenance(),
            []);

        Assert.Equal(0, result.ObservedUpdateCount);
        Assert.Single(result.Revisions);
        Assert.False(result.HasIncompleteRevisionHistory);
    }

    [Fact]
    public void DistinctContentTransitionCountsAsOneUpdate()
    {
        SessionPlanActivity created = Activity(0, "A", SessionPlanActivityKind.Created);

        SessionPlanRevisionResult result = _builder.Build(
            Snapshot("B"),
            DateTimeOffset.UnixEpoch.AddSeconds(10),
            Provenance(),
            [created]);

        Assert.Equal(1, result.ObservedUpdateCount);
        Assert.Equal(2, result.Revisions.Count);
    }

    [Fact]
    public void AdjacentIdenticalWritesAreMergedButRevertsCount()
    {
        SessionPlanActivity[] activities =
        [
            Activity(0, "A", SessionPlanActivityKind.Created),
            Activity(1, "A", SessionPlanActivityKind.Updated),
            Activity(2, "B", SessionPlanActivityKind.Updated),
            Activity(3, "B", SessionPlanActivityKind.Updated)
        ];

        // Current file reverts to A: A, B, A -> two observed updates.
        SessionPlanRevisionResult result = _builder.Build(
            Snapshot("A"),
            DateTimeOffset.UnixEpoch.AddSeconds(100),
            Provenance(),
            activities);

        Assert.Equal(2, result.ObservedUpdateCount);
        Assert.Equal(3, result.Revisions.Count);
    }

    [Fact]
    public void OpaqueEditSetsPartialHistoryWithoutCounting()
    {
        SessionPlanActivity opaque = new()
        {
            Id = "a-opaque",
            Kind = SessionPlanActivityKind.Updated,
            Provider = HookProvider.ClaudeCode,
            Order = 0,
            TimestampUtc = DateTimeOffset.UnixEpoch,
            Content = null,
            IsSuccessful = true,
            HasOpaqueResult = true,
            Provenance = Provenance()
        };

        SessionPlanRevisionResult result = _builder.Build(
            Snapshot("A"),
            DateTimeOffset.UnixEpoch.AddSeconds(5),
            Provenance(),
            [opaque]);

        Assert.Equal(0, result.ObservedUpdateCount);
        Assert.True(result.HasIncompleteRevisionHistory);
    }

    [Fact]
    public void ReferencedActivitiesAreIgnored()
    {
        SessionPlanActivity reference = new()
        {
            Id = "a-ref",
            Kind = SessionPlanActivityKind.Referenced,
            Provider = HookProvider.Cursor,
            Order = 0,
            Content = Snapshot("other"),
            Provenance = Provenance()
        };

        SessionPlanRevisionResult result = _builder.Build(
            Snapshot("A"),
            DateTimeOffset.UnixEpoch,
            Provenance(),
            [reference]);

        Assert.Equal(0, result.ObservedUpdateCount);
        Assert.Single(result.Revisions);
    }

    private SessionPlanActivity Activity(
        int order,
        string content,
        SessionPlanActivityKind kind) =>
        new()
        {
            Id = $"a-{order}",
            Kind = kind,
            Provider = HookProvider.Cursor,
            Order = order,
            TimestampUtc = DateTimeOffset.UnixEpoch.AddSeconds(order),
            Content = Snapshot(content),
            IsSuccessful = true,
            Provenance = Provenance()
        };

    private SessionPlanContentSnapshot? Snapshot(string content) =>
        _normalizer.Snapshot(content);

    private static SessionSourceProvenance Provenance() =>
        new(SessionSourceKind.CursorPlanMarkdown, @"C:\plan.md", "markdown", "raw");
}
