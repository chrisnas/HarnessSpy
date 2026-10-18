using HarnessSpy.Core.Sessions.Cursor.Inspection;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;

namespace HarnessSpy.Tests;

public sealed class CursorInspectionSemanticsTests
{
    [Fact]
    public void ParsesBubbleKeyWithColonContainingComposerId()
    {
        CursorBubbleKeyParser parser = new();
        HashSet<string> composers = new(StringComparer.Ordinal)
        {
            "parent:side-chat"
        };

        bool parsed = parser.TryParse(
            "bubbleId:parent:side-chat:bubble-1",
            composers,
            "bubble-1",
            out string composerId,
            out string bubbleId);

        Assert.True(parsed);
        Assert.Equal("parent:side-chat", composerId);
        Assert.Equal("bubble-1", bubbleId);
    }

    [Fact]
    public void ClassifiesComposerAndBubbleRecords()
    {
        CursorRecordClassifier classifier = new();
        CursorRecordClassification composer = classifier.Classify(
            "cursorDiskKV",
            new Dictionary<string, object?>
            {
                ["key"] = "composerData:composer-1",
                ["value"] = """{"composerId":"composer-1"}"""
            });
        CursorRecordClassification bubble = classifier.Classify(
            "cursorDiskKV",
            new Dictionary<string, object?>
            {
                ["key"] = "bubbleId:composer-1:bubble-1",
                ["value"] = """{"bubbleId":"bubble-1","text":"hello"}"""
            },
            new HashSet<string>(StringComparer.Ordinal)
            {
                "composer-1"
            });

        Assert.Equal(CursorRecordKind.ComposerData, composer.Kind);
        Assert.Equal("composer-1", composer.ComposerId);
        Assert.Equal(CursorRecordKind.Bubble, bubble.Kind);
        Assert.Equal("composer-1", bubble.ComposerId);
        Assert.Equal("bubble-1", bubble.BubbleId);
    }

    [Fact]
    public void ExtractsParentBubbleWorkspaceAndSelectedComposerReferences()
    {
        CursorComposerReferenceExtractor extractor = new();
        CursorRecordClassification record = new(
            CursorRecordKind.ComposerData,
            "composerData:child",
            "child",
            null,
            """
            {
              "composerId": "child",
              "selectedComposerId": "child",
              "workspaceIdentifier": {
                "id": "workspace-1",
                "uri": "file:///c:/repo"
              },
              "subagentInfo": {
                "parentComposerId": "parent"
              },
              "conversationHeaders": [
                { "bubbleId": "bubble-1" }
              ]
            }
            """);

        IReadOnlyList<CursorLogicalReference> references =
            extractor.Extract(record);

        Assert.Contains(
            references,
            item => item.Kind == CursorRelationKind.ParentComposer &&
                item.TargetId == "parent");
        Assert.Contains(
            references,
            item => item.Kind == CursorRelationKind.ConversationBubble &&
                item.TargetId == "bubble-1");
        Assert.Contains(
            references,
            item => item.Kind == CursorRelationKind.Workspace &&
                item.TargetId == "workspace-1");
        Assert.Contains(
            references,
            item => item.Kind == CursorRelationKind.SelectedComposer &&
                item.TargetId == "child");
    }

    [Fact]
    public void IndexResolvesConversationBubbleAndBuildsInboundLink()
    {
        CursorRowReference composerRow = Row(
            "cursorDiskKV",
            "composerData:composer-1");
        CursorRowReference bubbleRow = Row(
            "cursorDiskKV",
            "bubbleId:composer-1:bubble-1");
        CursorIndexedRecord composer = new(
            composerRow,
            new CursorRecordClassification(
                CursorRecordKind.ComposerData,
                "composerData:composer-1",
                "composer-1",
                null,
                null),
            [
                new CursorLogicalReference(
                    CursorRelationKind.ConversationBubble,
                    "bubble-1",
                    HarnessSpy.Core.Models.InferenceEvidence.Observed,
                    "header")
            ]);
        CursorIndexedRecord bubble = new(
            bubbleRow,
            new CursorRecordClassification(
                CursorRecordKind.Bubble,
                "bubbleId:composer-1:bubble-1",
                "composer-1",
                "bubble-1",
                null),
            []);

        CursorRelationIndex index = new([composer, bubble]);

        CursorInspectionRelation outbound = Assert.Single(
            index.Outbound(composerRow),
            item => item.Kind == CursorRelationKind.ConversationBubble);
        Assert.Equal(CursorRelationResolution.Resolved, outbound.Resolution);
        Assert.Equal(bubbleRow.StableKey, outbound.Target?.StableKey);
        Assert.Contains(
            index.Inbound(bubbleRow),
            item => item.Source.StableKey == composerRow.StableKey);
    }

    [Fact]
    public void CorrelatesComposerWithWorkspaceManifestAndTranscript()
    {
        CursorRowReference row = Row(
            "cursorDiskKV",
            "composerData:composer-1");
        CursorIndexedRecord record = new(
            row,
            new CursorRecordClassification(
                CursorRecordKind.ComposerData,
                "composerData:composer-1",
                "composer-1",
                null,
                null),
            [
                new CursorLogicalReference(
                    CursorRelationKind.Workspace,
                    "workspace-1",
                    InferenceEvidence.Observed,
                    "workspace")
            ]);
        CursorWorkspaceEvidence workspace = new(
            "workspace-1",
            @"C:\storage\workspace.json",
            @"C:\repo",
            @"C:\storage\state.vscdb");
        SessionCatalogEntry transcript = new()
        {
            CatalogSessionId = "cursor:composer-1",
            NativeSessionId = "composer-1",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Workspace = WorkspaceContext.Unknown,
            Title = "Transcript",
            Files =
            [
                new SessionFileBinding(
                    @"C:\projects\composer-1.jsonl",
                    SessionSourceKind.CursorTranscriptJsonl,
                    "cursor",
                    TranscriptFileRole.Main,
                    DateTimeOffset.UtcNow,
                    10)
            ]
        };

        CursorCrossStoreCorrelationIndex index = new(
            [record],
            [workspace],
            [transcript]);
        IReadOnlyList<CursorInspectionRelation> relations =
            index.ForRow(row);

        Assert.Contains(
            relations,
            item => item.Kind == CursorRelationKind.Workspace &&
                item.Resolution == CursorRelationResolution.Resolved);
        Assert.Contains(
            relations,
            item => item.Kind == CursorRelationKind.Transcript &&
                item.ExternalTarget?.NativeSessionId == "composer-1");
    }

    [Fact]
    public void IndexResolvesParentAndDuplicateComposerRows()
    {
        CursorIndexedRecord parentGlobal = ComposerRecord(
            @"C:\global\state.vscdb",
            "parent",
            []);
        CursorIndexedRecord parentWorkspace = ComposerRecord(
            @"C:\workspace\state.vscdb",
            "parent",
            []);
        CursorIndexedRecord child = ComposerRecord(
            @"C:\global\state.vscdb",
            "child",
            [
                new CursorLogicalReference(
                    CursorRelationKind.ParentComposer,
                    "parent",
                    InferenceEvidence.Observed,
                    "subagentInfo")
            ]);

        CursorRelationIndex index = new(
            [parentGlobal, parentWorkspace, child]);

        Assert.Contains(
            index.Outbound(child.Row),
            item => item.Kind == CursorRelationKind.ParentComposer &&
                item.Resolution == CursorRelationResolution.Ambiguous);
        Assert.Contains(
            index.Outbound(parentGlobal.Row),
            item => item.Kind == CursorRelationKind.DuplicateRecord &&
                item.Target?.StableKey == parentWorkspace.Row.StableKey);
    }

    [Fact]
    public void WorkspacePathDisambiguatesDuplicateTranscriptIds()
    {
        CursorIndexedRecord record = ComposerRecord(
            @"C:\global\state.vscdb",
            "shared-id",
            [
                new CursorLogicalReference(
                    CursorRelationKind.Workspace,
                    @"C:\repo-one",
                    InferenceEvidence.Observed,
                    "workspaceIdentifier")
            ]);
        SessionCatalogEntry first = Transcript(
            "shared-id",
            @"C:\repo-one",
            @"C:\projects\one.jsonl");
        SessionCatalogEntry second = Transcript(
            "shared-id",
            @"C:\repo-two",
            @"C:\projects\two.jsonl");

        CursorCrossStoreCorrelationIndex index = new(
            [record],
            [],
            [first, second]);
        CursorInspectionRelation relation = Assert.Single(
            index.ForRow(record.Row),
            item => item.Kind == CursorRelationKind.Transcript);

        Assert.Equal(
            InferenceEvidence.Corroborated,
            relation.Evidence);
        Assert.Equal(
            CursorRelationResolution.Resolved,
            relation.Resolution);
        Assert.Equal(@"C:\projects\one.jsonl", relation.ExternalTarget?.Path);
    }

    private CursorRowReference Row(string table, string key) =>
        new(
            @"C:\state.vscdb",
            table,
            new Dictionary<string, object?>
            {
                ["key"] = key
            });

    private CursorIndexedRecord ComposerRecord(
        string databasePath,
        string composerId,
        IReadOnlyList<CursorLogicalReference> references) =>
        new(
            new CursorRowReference(
                databasePath,
                "cursorDiskKV",
                new Dictionary<string, object?>
                {
                    ["key"] = $"composerData:{composerId}"
                }),
            new CursorRecordClassification(
                CursorRecordKind.ComposerData,
                $"composerData:{composerId}",
                composerId,
                null,
                null),
            references);

    private SessionCatalogEntry Transcript(
        string nativeId,
        string workspace,
        string path) =>
        new()
        {
            CatalogSessionId = $"cursor:{nativeId}:{workspace}",
            NativeSessionId = nativeId,
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Workspace = WorkspaceContext.FromRoot(workspace),
            Title = nativeId,
            Files =
            [
                new SessionFileBinding(
                    path,
                    SessionSourceKind.CursorTranscriptJsonl,
                    "cursor",
                    TranscriptFileRole.Main,
                    DateTimeOffset.UtcNow,
                    1)
            ]
        };
}
