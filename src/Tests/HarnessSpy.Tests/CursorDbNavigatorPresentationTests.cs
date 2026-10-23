using HarnessSpy.Core.Sessions.Cursor.Inspection;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Wpf.ViewModels;
using Microsoft.Data.Sqlite;
using System.Windows.Threading;

namespace HarnessSpy.Tests;

public sealed class CursorDbNavigatorPresentationTests
{
    [Fact]
    public void RowViewModelFormatsJsonAndPhysicalIdentity()
    {
        Dictionary<string, object?> cells = new()
        {
            ["key"] = "composerData:composer-1",
            ["value"] = """{"composerId":"composer-1","name":"Test"}"""
        };
        CursorRowReference reference = new(
            @"C:\Cursor\User\globalStorage\state.vscdb",
            "cursorDiskKV",
            new Dictionary<string, object?>
            {
                ["key"] = "composerData:composer-1"
            });
        CursorRecordClassification classification = new(
            CursorRecordKind.ComposerData,
            "composerData:composer-1",
            "composer-1",
            null,
            (string)cells["value"]!);

        CursorRowViewModel row = new(
            new object(),
            reference,
            cells,
            classification,
            42,
            "Test");

        Assert.Equal("composerData:composer-1", row.KeyText);
        Assert.Equal("ComposerData", row.RecordKindText);
        Assert.DoesNotContain(Environment.NewLine, row.RawText);
        Assert.Contains(Environment.NewLine, row.JsonText);
        Assert.Contains("key=composerData:composer-1", row.LocatorText);
    }

    [Fact]
    public void RelationViewModelShowsInboundSource()
    {
        CursorRowReference source = Row("composerData:composer-1");
        CursorRowReference target = Row("bubbleId:composer-1:bubble-1");
        CursorInspectionRelation relation = new(
            CursorRelationKind.ConversationBubble,
            source,
            target,
            null,
            "bubble-1",
            HarnessSpy.Core.Models.InferenceEvidence.Observed,
            CursorRelationResolution.Resolved,
            "header");

        CursorRelationViewModel outbound = new(relation);
        CursorRelationViewModel inbound = new(relation, inbound: true);

        Assert.Contains("bubbleId:composer-1:bubble-1", outbound.TargetText);
        Assert.Contains("composerData:composer-1", inbound.TargetText);
    }

    [Fact]
    public async Task ViewModelBrowsesAndNavigatesSyntheticCursorDatabase()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"HarnessSpy-CursorDbNavigator-{Guid.NewGuid():N}");
        string databasePath = Path.Combine(
            root,
            "Cursor",
            "User",
            "globalStorage",
            "state.vscdb");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using (SqliteConnection connection = new(
                         $"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE cursorDiskKV (
                    key TEXT NOT NULL,
                    value TEXT NOT NULL
                );
                INSERT INTO cursorDiskKV (key, value) VALUES
                (
                    'composerData:composer-1',
                    '{"composerId":"composer-1","name":"Session 1","workspaceIdentifier":{"id":"workspace-one","uri":{"fsPath":"C:\\repo"}},"conversationHeaders":[{"bubbleId":"bubble-2","type":1},{"bubbleId":"bubble-1","type":2}]}'
                ),
                (
                    'bubbleId:composer-1:bubble-1',
                    '{"bubbleId":"bubble-1","type":2,"text":"done","requestId":"request-1"}'
                ),
                (
                    'bubbleId:composer-1:bubble-2',
                    '{"bubbleId":"bubble-2","type":1,"text":"hello","requestId":"request-1"}'
                ),
                (
                    'composerData:composer-2',
                    '{"composerId":"composer-2","name":"Session 2","workspaceIdentifier":{"id":"workspace-one","uri":{"fsPath":"C:\\repo"}},"conversationHeaders":[{"bubbleId":"bubble-3","type":1}]}'
                ),
                (
                    'bubbleId:composer-2:bubble-3',
                    '{"bubbleId":"bubble-3","type":1,"text":"second session","requestId":"request-2"}'
                );
                """;
            await command.ExecuteNonQueryAsync();
        }
        string workspaceDirectory = Path.Combine(
            root,
            "Cursor",
            "User",
            "workspaceStorage",
            "workspace-one");
        Directory.CreateDirectory(workspaceDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(workspaceDirectory, "workspace.json"),
            """{"folder":"file:///c:/repo"}""");
        string transcriptDirectory = Path.Combine(
            root,
            ".cursor",
            "projects",
            "c-repo",
            "agent-transcripts",
            "composer-1");
        Directory.CreateDirectory(transcriptDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(transcriptDirectory, "composer-1.jsonl"),
            """{"type":"user","text":"hello"}""" + Environment.NewLine);

        CursorDbNavigatorViewModel viewModel = new(
            new SessionDiscoveryContext(
                userProfile: root,
                applicationData: root),
            new CursorDbNavigatorSettings
            {
                PageSize = 50
            },
            _ => { },
            Dispatcher.CurrentDispatcher);
        try
        {
            await viewModel.InitializeAsync();

            Assert.Empty(viewModel.SchemaRoots);
            CursorReconstructionItemViewModel drive =
                Assert.Single(viewModel.Reconstruction.Roots);
            Assert.Equal(CursorReconstructionItemKind.Folder, drive.Kind);
            CursorReconstructionItemViewModel workspaceNode =
                Assert.Single(drive.Children);
            Assert.Equal(
                CursorReconstructionItemKind.Workspace,
                workspaceNode.Kind);
            Assert.Equal("repo", workspaceNode.Header);
            CursorReconstructionItemViewModel firstSession =
                Assert.IsType<CursorReconstructionItemViewModel>(
                    viewModel.Reconstruction.FindSession("composer-1"));
            CursorReconstructionItemViewModel secondSession =
                Assert.IsType<CursorReconstructionItemViewModel>(
                    viewModel.Reconstruction.FindSession("composer-2"));
            Assert.Equal(
                "Details load on selection",
                firstSession.Summary);
            secondSession.IsExpanded = true;

            await viewModel.SelectReconstructionItemAsync(firstSession);
            Assert.Same(
                drive,
                Assert.Single(viewModel.Reconstruction.Roots));
            Assert.Same(workspaceNode, Assert.Single(drive.Children));
            Assert.Same(firstSession, viewModel.Reconstruction.SelectedItem);
            Assert.Same(
                firstSession,
                viewModel.Reconstruction.FindSession("composer-1"));
            Assert.Same(
                secondSession,
                viewModel.Reconstruction.FindSession("composer-2"));
            Assert.True(secondSession.IsExpanded);
            Assert.Contains("2 bubbles, 1 turn", firstSession.Summary);
            Assert.Empty(viewModel.SchemaRoots);
            secondSession = Assert.IsType<CursorReconstructionItemViewModel>(
                viewModel.Reconstruction.FindSession("composer-2"));
            Assert.Equal(
                "Details load on selection",
                secondSession.Summary);

            await viewModel.EnsurePhysicalStorageLoadedAsync();
            Assert.Single(viewModel.SchemaRoots);
            Assert.Equal(5, viewModel.Rows.Count);
            CursorRowViewModel composer = Assert.Single(
                viewModel.Rows,
                row => row.KeyText == "composerData:composer-1");
            await viewModel.SelectRowAsync(composer);
            CursorRelationViewModel bubble = Assert.Single(
                viewModel.OutboundRelations,
                relation =>
                    relation.KindText == "ConversationBubble" &&
                    relation.Relation.LogicalTarget == "bubble-1" &&
                    relation.StatusText == "Resolved");
            CursorRelationViewModel workspace = Assert.Single(
                viewModel.Correlations,
                relation =>
                    relation.KindText == "Workspace" &&
                    relation.Relation.LogicalTarget == "workspace-one" &&
                    relation.StatusText == "Resolved");
            CursorRelationViewModel transcript = Assert.Single(
                viewModel.Correlations,
                relation =>
                    relation.KindText == "Transcript" &&
                    relation.StatusText == "Resolved");

            await viewModel.NavigateToRelationAsync(workspace);
            Assert.Contains(
                "Workspace manifest",
                viewModel.SelectedRowTitle);
            Assert.Contains(
                "file:///c:/repo",
                viewModel.SelectedRawText);
            await viewModel.NavigateBackAsync();
            await viewModel.NavigateToRelationAsync(transcript);
            Assert.Contains("Transcript", viewModel.SelectedRowTitle);
            Assert.Contains("\"hello\"", viewModel.SelectedRawText);
            await viewModel.NavigateBackAsync();
            await viewModel.NavigateToRelationAsync(bubble);

            Assert.NotNull(viewModel.SelectedRow);
            Assert.Equal(
                "bubbleId:composer-1:bubble-1",
                viewModel.SelectedRow!.KeyText);
            Assert.True(viewModel.CanNavigateBack);

            Assert.NotEmpty(viewModel.Reconstruction.Roots);
            Assert.Single(viewModel.Reconstruction.WorkspaceStage);
            Assert.Contains(
                viewModel.Reconstruction.ComposerStage,
                item => item.Kind == CursorReconstructionItemKind.Session);
            Assert.Contains(
                viewModel.Reconstruction.ComposerStage,
                item =>
                    item.Kind ==
                    CursorReconstructionItemKind.ComposerRecord);
            Assert.Equal(2, viewModel.Reconstruction.BubbleStage.Count);
            Assert.Equal(
                "bubble-2",
                viewModel.Reconstruction.BubbleStage[0]
                    .Bubble?.Record.Classification.BubbleId);
            Assert.Equal(
                "bubble-1",
                viewModel.Reconstruction.BubbleStage[1]
                    .Bubble?.Record.Classification.BubbleId);
            CursorReconstructionItemViewModel reconstructedBubble =
                viewModel.Reconstruction.BubbleStage[1];
            CursorReconstructionItemViewModel reconstructedTurn =
                Assert.Single(
                    viewModel.Reconstruction.OutputStage,
                    item => item.Kind == CursorReconstructionItemKind.Turn);
            Assert.Equal(2, reconstructedTurn.Relations.Count);
            Assert.Contains(
                viewModel.Reconstruction.OutputStage,
                item =>
                    item.Kind == CursorReconstructionItemKind.Relation &&
                    item.Header.StartsWith(
                        "Prompt source:",
                        StringComparison.Ordinal));
            Assert.Contains(
                viewModel.Reconstruction.OutputStage,
                item => item.Kind == CursorReconstructionItemKind.Event);

            await viewModel.SelectReconstructionItemAsync(reconstructedTurn);
            Assert.Contains(
                viewModel.Reconstruction.EvidenceFields,
                field =>
                    field.Name == "Prompt" &&
                    field.Value == "hello");
            Assert.Contains(
                viewModel.Reconstruction.EvidenceFields,
                field =>
                    field.Name == "Prompt source bubble" &&
                    field.Value == "bubble-2");
            Assert.All(
                viewModel.Reconstruction.BubbleStage,
                item => Assert.True(item.IsRelationHighlighted));
            Assert.True(viewModel.Reconstruction.CanGoToSourceBubble);
            await viewModel.GoToReconstructionSourceBubbleAsync();
            Assert.Equal(
                "bubble-2",
                viewModel.Reconstruction.SelectedItem
                    ?.Bubble?.Record.Classification.BubbleId);

            await viewModel.SelectReconstructionItemAsync(
                reconstructedBubble);
            Assert.Contains(
                "conversationHeaders",
                viewModel.Reconstruction.SelectedRule);
            Assert.Contains(
                "bubble-1",
                viewModel.Reconstruction.RawSource);
            Assert.Contains(
                Environment.NewLine,
                viewModel.Reconstruction.JsonSource);
            Assert.Contains(
                "\"bubbleId\": \"bubble-1\"",
                viewModel.Reconstruction.JsonSource);
            Assert.True(viewModel.Reconstruction.CanOpenPhysicalSource);

            await viewModel.OpenReconstructionPhysicalSourceAsync();
            Assert.Equal(1, viewModel.SelectedMainTabIndex);
            Assert.Equal(
                "bubbleId:composer-1:bubble-1",
                viewModel.SelectedRow?.KeyText);
            viewModel.SelectedMainTabIndex = 0;
            Assert.Equal(2, viewModel.Reconstruction.BubbleStage.Count);
            Assert.Contains(
                viewModel.Reconstruction.OutputStage,
                item => item.Kind == CursorReconstructionItemKind.Turn);

            await viewModel.RefreshAsync();
            CursorReconstructionItemViewModel pendingSecond =
                Assert.IsType<CursorReconstructionItemViewModel>(
                    viewModel.Reconstruction.FindSession("composer-2"));
            Task discover = viewModel.DiscoverStoresAsync();
            Task selectWhileBusy =
                viewModel.SelectReconstructionItemAsync(pendingSecond);
            await Task.WhenAll(discover, selectWhileBusy);
            Assert.Equal(
                "composer-2",
                viewModel.Reconstruction.SelectedItem
                    ?.Session?.Session.NativeSessionId);
            Assert.Contains(
                "1 bubbles, 1 turns",
                viewModel.Reconstruction.SelectedItem?.Summary);

            firstSession =
                Assert.IsType<CursorReconstructionItemViewModel>(
                    viewModel.Reconstruction.FindSession("composer-1"));
            secondSession =
                Assert.IsType<CursorReconstructionItemViewModel>(
                    viewModel.Reconstruction.FindSession("composer-2"));
            Task selectSecond =
                viewModel.SelectReconstructionItemAsync(secondSession);
            Task selectFirst =
                viewModel.SelectReconstructionItemAsync(firstSession);

            await Task.WhenAll(selectSecond, selectFirst);

            Assert.Equal(
                firstSession.Session?.Session.NativeSessionId,
                viewModel.Reconstruction.SelectedItem
                    ?.Session?.Session.NativeSessionId);
        }
        finally
        {
            viewModel.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private CursorRowReference Row(string key) =>
        new(
            @"C:\state.vscdb",
            "cursorDiskKV",
            new Dictionary<string, object?>
            {
                ["key"] = key
            });
}
