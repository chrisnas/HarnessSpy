using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes.Cursor;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Cursor;
using HarnessSpy.Wpf.ViewModels;

namespace HarnessSpy.Tests;

// Locks the Cursor parity contract shared by CursorSpy and SessionViewer: a
// logical tool call is counted once regardless of how many sources observed it,
// dynamic-tool discovery is separated from MCP executions, and a reconciled
// event keeps every source's provenance plus the transcript's parallel grouping.
public sealed class CursorParityReconciliationTests
{
    [Theory]
    [InlineData("GetDynamicTools", true)]
    [InlineData("get_mcp_tools", true)]
    [InlineData("CallDynamicTool", false)]
    [InlineData("Shell", false)]
    public void DynamicToolDiscoveryIsRecognized(string nativeName, bool expected)
    {
        Assert.Equal(expected, CursorToolSemantics.IsDynamicToolDiscovery(nativeName));
    }

    [Theory]
    [InlineData("run_terminal_command_v2", CanonicalToolKind.Shell)]
    [InlineData("ripgrep_raw_search", CanonicalToolKind.TextSearch)]
    [InlineData("glob_file_search", CanonicalToolKind.FileSearch)]
    [InlineData("read_file_v2", CanonicalToolKind.FileRead)]
    [InlineData("edit_file_v2", CanonicalToolKind.FileEdit)]
    [InlineData("Shell", CanonicalToolKind.Shell)]
    [InlineData("Grep", CanonicalToolKind.TextSearch)]
    public void DesktopAliasesResolveToTranscriptKind(
        string nativeName,
        CanonicalToolKind expected)
    {
        Assert.Equal(expected, CursorToolSemantics.CanonicalKind(nativeName));
    }

    [Fact]
    public void DiscoveryIsNeverAnMcpExecution()
    {
        Assert.False(CursorToolSemantics.IsMcpExecution(
            CanonicalToolKind.Mcp,
            "GetDynamicTools",
            "user-dotnet-dstrings"));
        Assert.True(CursorToolSemantics.IsMcpExecution(
            CanonicalToolKind.Mcp,
            "CallDynamicTool",
            "user-dotnet-dstrings"));
    }

    [Theory]
    // Transcript name and Desktop alias share a correlation bucket.
    [InlineData("Shell", CanonicalToolKind.Shell, "run_terminal_command_v2", CanonicalToolKind.Shell)]
    [InlineData("Grep", CanonicalToolKind.TextSearch, "ripgrep_raw_search", CanonicalToolKind.TextSearch)]
    [InlineData("Write", CanonicalToolKind.FileWrite, "edit_file_v2", CanonicalToolKind.FileEdit)]
    public void AliasedToolsShareCorrelationKey(
        string transcriptName,
        CanonicalToolKind transcriptKind,
        string desktopName,
        CanonicalToolKind desktopKind)
    {
        string transcriptKey = CursorToolSemantics.CorrelationKey(
            transcriptKind, transcriptName, null, null);
        string desktopKey = CursorToolSemantics.CorrelationKey(
            desktopKind, desktopName, null, null);
        Assert.Equal(transcriptKey, desktopKey);
    }

    [Fact]
    public void McpExecutionsShareCorrelationKeyAcrossDialects()
    {
        string transcript = CursorToolSemantics.CorrelationKey(
            CanonicalToolKind.Mcp,
            "CallDynamicTool",
            "user-dotnet-dstrings",
            "get_duplicated_strings");
        string desktop = CursorToolSemantics.CorrelationKey(
            CanonicalToolKind.Mcp,
            "mcp-dotnet-dstrings-get_duplicated_strings",
            "dotnet-dstrings",
            null);
        Assert.Equal(transcript, desktop);
    }

    [Fact]
    public void ReconcilerCollapsesDualSourceToolsIntoCanonicalEvents()
    {
        SessionTurn turn = new(
            "turn-1",
            1,
            "inspect",
            null,
            null,
            InferenceEvidence.Derived,
            [
                // Transcript view: three parallel file tools, discovery, MCP.
                TranscriptTool(1, "Shell", CanonicalToolKind.Shell, groupId: "g-file"),
                TranscriptTool(2, "Grep", CanonicalToolKind.TextSearch, groupId: "g-file"),
                TranscriptTool(3, "Write", CanonicalToolKind.FileWrite, groupId: "g-file"),
                TranscriptTool(4, "GetDynamicTools", CanonicalToolKind.Unknown),
                TranscriptTool(
                    5,
                    "CallDynamicTool",
                    CanonicalToolKind.Mcp,
                    server: "user-dotnet-dstrings",
                    mcpTool: "get_duplicated_strings"),
                // Desktop view: the same five calls under Desktop names.
                DesktopTool(1, "run_terminal_command_v2", CanonicalToolKind.Shell),
                DesktopTool(2, "ripgrep_raw_search", CanonicalToolKind.TextSearch),
                DesktopTool(3, "edit_file_v2", CanonicalToolKind.FileEdit),
                DesktopTool(4, "get_mcp_tools", CanonicalToolKind.Unknown, server: "user-dotnet-dstrings"),
                DesktopTool(
                    5,
                    "mcp-dotnet-dstrings-get_duplicated_strings",
                    CanonicalToolKind.Mcp,
                    server: "dotnet-dstrings",
                    mcpTool: "get_duplicated_strings")
            ]);
        SessionCatalogEntry entry = new()
        {
            CatalogSessionId = "cursor:x",
            NativeSessionId = "x",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Workspace = WorkspaceContext.Unknown,
            Title = "inspect",
            Turns = [turn]
        };

        SessionCatalogEntry reconciled =
            new CursorSessionEventReconciler().Reconcile(entry);
        SessionEventRecord[] toolRequests = reconciled.Turns.Single().Events
            .Where(item => item.Role == ObservationRole.ToolRequest)
            .ToArray();

        // Ten source rows collapse to five logical calls.
        Assert.Equal(5, toolRequests.Length);

        // Each canonical call keeps both origins' provenance.
        Assert.All(toolRequests, item =>
        {
            SessionSourceKind[] kinds =
            [
                item.Provenance.SourceKind,
                .. item.SupplementalProvenance.Select(source => source.SourceKind)
            ];
            Assert.Contains(SessionSourceKind.CursorTranscriptJsonl, kinds);
            Assert.Contains(SessionSourceKind.CursorDesktopSqlite, kinds);
        });

        // The transcript parallel group survives on the merged calls.
        string[] fileGroups = toolRequests
            .Where(item => item.ToolName is "Shell" or "Grep" or "Write")
            .Select(item => item.ParallelGroupId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .Cast<string>()
            .ToArray();
        Assert.Single(fileGroups);
        Assert.Equal(
            3,
            toolRequests.Count(item => item.ParallelGroupId == fileGroups[0]));

        NodeSummary summary = new SessionNodeSummaryBuilder().Build(reconciled);
        Assert.Equal(5, summary.ToolCallCount);
        Assert.Equal(1, summary.McpCallCount);
        Assert.Equal(
            "user-dotnet-dstrings / get_duplicated_strings",
            Assert.Single(summary.McpCalls).Name);
        // Discovery is a tool, never an MCP execution.
        Assert.Contains(summary.Tools, row => row.Name == "GetDynamicTools");
        Assert.DoesNotContain(summary.McpCalls, row => row.Name == "GetDynamicTools");
    }

    [Fact]
    public void ReconcilerPairsCorrelationBucketsInFifoOrder()
    {
        SessionEventRecord firstTranscript = TranscriptTool(
            1,
            "Shell",
            CanonicalToolKind.Shell,
            groupId: "wave-1");
        SessionEventRecord secondTranscript = TranscriptTool(
            2,
            "Shell",
            CanonicalToolKind.Shell,
            groupId: "wave-2");
        SessionEventRecord firstDesktop = DesktopTool(
            1,
            "run_terminal_command_v2",
            CanonicalToolKind.Shell) with
        {
            DurationMs = 10
        };
        SessionEventRecord secondDesktop = DesktopTool(
            2,
            "run_terminal_command_v2",
            CanonicalToolKind.Shell) with
        {
            DurationMs = 20
        };
        SessionCatalogEntry entry = Entry(
            [
                firstTranscript,
                secondTranscript,
                firstDesktop,
                secondDesktop
            ]);

        SessionEventRecord[] requests =
            new CursorSessionEventReconciler()
                .Reconcile(entry)
                .Turns
                .Single()
                .Events
                .Where(item => item.Role == ObservationRole.ToolRequest)
                .ToArray();

        Assert.Collection(
            requests,
            item =>
            {
                Assert.Equal("wave-1", item.ParallelGroupId);
                Assert.Equal(10, item.DurationMs);
            },
            item =>
            {
                Assert.Equal("wave-2", item.ParallelGroupId);
                Assert.Equal(20, item.DurationMs);
            });
    }

    [Fact]
    public void ReconcilerHandlesLargePayloadPairingBatch()
    {
        const int pairCount = 100;
        string raw = new('x', 64 * 1024);
        List<SessionEventRecord> events = new(pairCount * 2);
        for (int index = 0; index < pairCount; index++)
        {
            events.Add(TranscriptTool(
                index + 1,
                "Shell",
                CanonicalToolKind.Shell,
                groupId: $"wave-{index}") with
            {
                Provenance = new SessionSourceProvenance(
                    SessionSourceKind.CursorTranscriptJsonl,
                    "C:/transcript.jsonl",
                    "cursor-transcript-jsonl",
                    raw)
            });
        }

        for (int index = 0; index < pairCount; index++)
        {
            events.Add(DesktopTool(
                index + 1,
                "run_terminal_command_v2",
                CanonicalToolKind.Shell) with
            {
                Provenance = new SessionSourceProvenance(
                    SessionSourceKind.CursorDesktopSqlite,
                    "C:/state.vscdb",
                    "cursor-desktop-composer-json",
                    raw)
            });
        }

        SessionEventRecord[] requests =
            new CursorSessionEventReconciler()
                .Reconcile(Entry(events))
                .Turns
                .Single()
                .Events
                .Where(item => item.Role == ObservationRole.ToolRequest)
                .ToArray();

        Assert.Equal(pairCount, requests.Length);
        Assert.Equal(
            Enumerable.Range(0, pairCount)
                .Select(index => $"wave-{index}"),
            requests.Select(item => item.ParallelGroupId));
    }

    [Fact]
    public void ReconcilerDropsWhitespaceEquivalentTranscriptResponse()
    {
        SessionCatalogEntry entry = Entry(
            [
                ResponseEvent(
                    SessionSourceKind.CursorTranscriptJsonl,
                    1,
                    "  completed\r\n\t successfully "),
                ResponseEvent(
                    SessionSourceKind.CursorDesktopSqlite,
                    2,
                    "completed successfully")
            ]);

        SessionEventRecord response = Assert.Single(
            new CursorSessionEventReconciler()
                .Reconcile(entry)
                .Turns
                .Single()
                .Events,
            item => item.Role == ObservationRole.AgentResponse);

        Assert.Equal(
            SessionSourceKind.CursorDesktopSqlite,
            response.Provenance.SourceKind);
    }

    private static SessionCatalogEntry Entry(
        IReadOnlyList<SessionEventRecord> events) =>
        new()
        {
            CatalogSessionId = "cursor:x",
            NativeSessionId = "x",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Workspace = WorkspaceContext.Unknown,
            Title = "inspect",
            Turns =
            [
                new SessionTurn(
                    "turn-1",
                    1,
                    "inspect",
                    null,
                    null,
                    InferenceEvidence.Derived,
                    events)
            ]
        };

    private static SessionEventRecord ResponseEvent(
        SessionSourceKind sourceKind,
        int order,
        string text) =>
        new()
        {
            Id = $"{sourceKind}:response:{order}",
            NativeName = "assistant",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Role = ObservationRole.AgentResponse,
            EventKind = CanonicalEventKind.AssistantMessage,
            Direction = ObservationDirection.Output,
            Order = order,
            TurnId = "turn-1",
            TimestampUtc = sourceKind == SessionSourceKind.CursorDesktopSqlite
                ? DesktopTimestamp(order)
                : null,
            Text = text,
            Provenance = new SessionSourceProvenance(
                sourceKind,
                sourceKind == SessionSourceKind.CursorTranscriptJsonl
                    ? "C:/transcript.jsonl"
                    : "C:/state.vscdb",
                "json",
                text)
        };

    private static SessionEventRecord TranscriptTool(
        int order,
        string nativeName,
        CanonicalToolKind kind,
        string? server = null,
        string? mcpTool = null,
        string? groupId = null) =>
        ToolEvent(
            SessionSourceKind.CursorTranscriptJsonl,
            order,
            nativeName,
            kind,
            server,
            mcpTool,
            groupId,
            timestamp: null);

    private static SessionEventRecord DesktopTool(
        int order,
        string nativeName,
        CanonicalToolKind kind,
        string? server = null,
        string? mcpTool = null) =>
        ToolEvent(
            SessionSourceKind.CursorDesktopSqlite,
            order,
            nativeName,
            kind,
            server,
            mcpTool,
            groupId: null,
            timestamp: DesktopTimestamp(order));

    private static DateTimeOffset DesktopTimestamp(int order) =>
        new DateTimeOffset(
            2026,
            9,
            20,
            8,
            40,
            0,
            TimeSpan.Zero).AddSeconds(order);

    private static SessionEventRecord ToolEvent(
        SessionSourceKind sourceKind,
        int order,
        string nativeName,
        CanonicalToolKind kind,
        string? server,
        string? mcpTool,
        string? groupId,
        DateTimeOffset? timestamp) =>
        new()
        {
            Id = $"{sourceKind}:{order}:{Guid.NewGuid():N}",
            NativeName = nativeName,
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Role = ObservationRole.ToolRequest,
            EventKind = CanonicalEventKind.ToolRequested,
            ToolKind = kind,
            Direction = ObservationDirection.Input,
            Order = order,
            TurnId = "turn-1",
            TimestampUtc = timestamp,
            ToolName = nativeName,
            McpServerName = server,
            McpToolName = mcpTool,
            AssistantStepId = groupId,
            ParallelGroupId = groupId,
            IsParallelCandidate = groupId is not null,
            Provenance = new SessionSourceProvenance(
                sourceKind,
                sourceKind == SessionSourceKind.CursorTranscriptJsonl
                    ? "C:/transcript.jsonl"
                    : "C:/state.vscdb",
                sourceKind == SessionSourceKind.CursorTranscriptJsonl
                    ? "cursor-transcript-jsonl"
                    : "cursor-desktop-composer-json",
                string.Empty)
        };
}
