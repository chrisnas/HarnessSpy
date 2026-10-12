using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sessions;

namespace HarnessSpy.Tests;

public sealed class SessionCatalogCompactorTests
{
    [Fact]
    public void BalancedProfileBoundsLargeDisplayFields()
    {
        string large = new('x', 100_000);
        SessionEventRecord record = Event(large);
        SessionCatalogEntry session = Session(record, large);
        SessionCatalogCompactor compactor = new(
            () => SessionCatalogProfile.Balanced);

        SessionCatalogScanResult compacted = compactor.Compact(
            new SessionCatalogScanResult([session], true, []));

        SessionTurn turn = Assert.Single(
            Assert.Single(compacted.Sessions).Turns);
        Assert.True(turn.Prompt.Length < large.Length);
        Assert.True(Assert.Single(turn.Events).Text!.Length < large.Length);
    }

    [Fact]
    public void CompleteProfileKeepsOriginalGraph()
    {
        string large = new('x', 100_000);
        SessionCatalogEntry session = Session(Event(large), large);
        SessionCatalogCompactor compactor = new(
            () => SessionCatalogProfile.Complete);
        SessionCatalogScanResult original =
            new([session], true, []);

        SessionCatalogScanResult compacted = compactor.Compact(original);

        Assert.Same(original, compacted);
    }

    private static SessionCatalogEntry Session(
        SessionEventRecord record,
        string prompt) =>
        new()
        {
            CatalogSessionId = "cursor:s1",
            NativeSessionId = "s1",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Workspace = WorkspaceContext.Unknown,
            Title = "title",
            Turns =
            [
                new SessionTurn(
                    "turn-1",
                    1,
                    prompt,
                    null,
                    null,
                    InferenceEvidence.Observed,
                    [record])
            ]
        };

    private static SessionEventRecord Event(string text) =>
        new()
        {
            Id = "event-1",
            NativeName = "assistant",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Role = ObservationRole.AgentResponse,
            Text = text,
            Provenance = new SessionSourceProvenance(
                SessionSourceKind.CursorTranscriptJsonl,
                "session.jsonl",
                "jsonl",
                text)
        };
}
