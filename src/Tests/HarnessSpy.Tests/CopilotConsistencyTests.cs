using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Copilot;
using HarnessSpy.Core.Sources;
using HarnessSpy.Wpf.ViewModels;

namespace HarnessSpy.Tests;

public sealed class CopilotConsistencyTests
{
    [Fact]
    public void CopilotTranscriptLifecycleClearsThePreviousInteraction()
    {
        TranscriptTurnTracker tracker =
            new(DialectIds.CopilotCliTranscript);

        Assert.Equal(
            "transcript-interaction:interaction-1",
            tracker.Observe(TranscriptRowScanner.Read(
                """
                {"type":"user.message","data":{"interactionId":"interaction-1","turnId":"0"}}
                """)));
        Assert.Null(tracker.Observe(TranscriptRowScanner.Read(
            """{"type":"session.shutdown","data":{"shutdownType":"routine"}}""")));
        Assert.Null(tracker.Observe(TranscriptRowScanner.Read(
            """{"type":"model.model_call_failure","data":{"turn":0}}""")));
        Assert.Null(tracker.Observe(TranscriptRowScanner.Read(
            """{"type":"session.resume","data":{"resumeTime":"2026-09-19T09:48:02Z"}}""")));
        Assert.Equal(
            "transcript-interaction:interaction-2",
            tracker.Observe(TranscriptRowScanner.Read(
                """
                {"type":"user.message","data":{"interactionId":"interaction-2","turnId":"0"}}
                """)));
    }

    [Fact]
    public async Task SessionViewerUsesInteractionsAsConversationTurns()
    {
        object[] rows =
        [
            Event(
                "session.start",
                "start",
                new
                {
                    sessionId = "interaction-session",
                    startTime = "2026-09-19T09:00:00Z"
                }),
            Event(
                "user.message",
                "user-1",
                new
                {
                    interactionId = "interaction-1",
                    turnId = "0",
                    content = "first prompt"
                }),
            Event(
                "assistant.turn_start",
                "start-1-0",
                new { interactionId = "interaction-1", turnId = "0" }),
            Event(
                "assistant.message",
                "message-1-0",
                new
                {
                    interactionId = "interaction-1",
                    turnId = "0",
                    messageId = "message-1-0",
                    reasoningOpaque = "encrypted",
                    reasoningText = "readable first thought"
                }),
            Event(
                "assistant.turn_end",
                "end-1-0",
                new { turnId = "0" }),
            Event(
                "assistant.turn_start",
                "start-1-1",
                new { interactionId = "interaction-1", turnId = "1" }),
            Event(
                "assistant.message",
                "message-1-1",
                new
                {
                    interactionId = "interaction-1",
                    turnId = "1",
                    messageId = "message-1-1",
                    reasoningOpaque = "encrypted"
                }),
            Event(
                "assistant.turn_end",
                "end-1-1",
                new { turnId = "1" }),
            Event(
                "user.message",
                "user-2",
                new
                {
                    interactionId = "interaction-2",
                    turnId = "0",
                    content = "second prompt"
                }),
            Event(
                "assistant.turn_start",
                "start-2-0",
                new { interactionId = "interaction-2", turnId = "0" }),
            Event(
                "assistant.message",
                "message-2-0",
                new
                {
                    interactionId = "interaction-2",
                    turnId = "0",
                    messageId = "message-2-0",
                    reasoningOpaque = "encrypted",
                    reasoningText = "readable second thought"
                }),
            Event(
                "assistant.turn_end",
                "end-2-0",
                new { turnId = "0" }),
            Event(
                "session.shutdown",
                "shutdown",
                new { shutdownType = "routine" })
        ];

        SessionCatalogEntry session = await ReadSessionAsync(
            "interaction-session",
            rows);

        Assert.Collection(
            session.Turns,
            first =>
            {
                Assert.Equal("interaction:interaction-1", first.Id);
                Assert.Equal("first prompt", first.Prompt);
                Assert.Equal(
                    2,
                    first.Events.Count(static item =>
                        item.Role == ObservationRole.AgentThought));
                Assert.All(first.Events, item => Assert.Equal(first.Id, item.TurnId));
                Assert.Contains(
                    first.Events,
                    static item =>
                        item.Provenance.NativeTurnId == "1" &&
                        item.Provenance.InteractionId == "interaction-1");
            },
            second =>
            {
                Assert.Equal("interaction:interaction-2", second.Id);
                Assert.Equal("second prompt", second.Prompt);
                Assert.Single(
                    second.Events,
                    static item => item.Role == ObservationRole.AgentThought);
                Assert.All(second.Events, item => Assert.Equal(second.Id, item.TurnId));
            });

        NodeSummary summary = new SessionNodeSummaryBuilder().Build(session);
        Assert.Equal(2, summary.TurnCount);
        Assert.Equal(3, summary.ThoughtCount);
        Assert.Equal(
            "readable first thought".Length +
            "readable second thought".Length,
            summary.ThoughtCharacterCount);
    }

    [Fact]
    public async Task SessionViewerReportsLogicalSuccessfulFileAccesses()
    {
        const string patchPath = @"C:\Repo\summary.md";
        string patch =
            $"*** Begin Patch\n*** Update File: {patchPath}\n@@\n-old\n+new\n*** End Patch\n";
        object[] rows =
        [
            Event(
                "session.start",
                "start",
                new { sessionId = "file-session" }),
            Event(
                "user.message",
                "user",
                new
                {
                    interactionId = "interaction-1",
                    turnId = "0",
                    content = "inspect files"
                }),
            ToolRequest(
                "request-failed",
                "interaction-1",
                "0",
                "failed",
                "view",
                new { path = @"C:\Repo\Missing.cs" }),
            ToolResult(
                "result-failed",
                "0",
                "failed",
                "view",
                success: false),
            ToolRequest(
                "request-alias",
                "interaction-1",
                "1",
                "alias",
                "view",
                new { path = @"C:\Repo\folder\..\Program.cs" }),
            ToolResult(
                "result-alias",
                "1",
                "alias",
                "view",
                success: true),
            ToolRequest(
                "request-canonical",
                "interaction-1",
                "2",
                "canonical",
                "view",
                new { path = @"C:\Repo\Program.cs" }),
            ToolResult(
                "result-canonical",
                "2",
                "canonical",
                "view",
                success: true),
            ToolRequest(
                "request-template",
                "interaction-1",
                "3",
                "template",
                "view",
                new { path = @"C:\Skills\summary-template.md" }),
            ToolResult(
                "result-template",
                "3",
                "template",
                "view",
                success: true),
            ToolRequest(
                "request-patch",
                "interaction-1",
                "4",
                "patch",
                "apply_patch",
                patch),
            ToolResult(
                "result-patch",
                "4",
                "patch",
                "apply_patch",
                success: true),
            Event(
                "session.shutdown",
                "shutdown",
                new { shutdownType = "routine" })
        ];

        SessionCatalogEntry session = await ReadSessionAsync(
            "file-session",
            rows);
        NodeSummary summary = new SessionNodeSummaryBuilder().Build(session);

        Assert.Equal(
            [@"C:\Repo\Program.cs", @"C:\Skills\summary-template.md"],
            summary.ReadFiles.Select(static item => item.FullPath).ToArray());
        Assert.DoesNotContain(
            summary.ReadFiles,
            static item => item.FullPath.EndsWith(
                "Missing.cs",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            patchPath,
            Assert.Single(summary.WrittenFiles).FullPath);
    }

    [Fact]
    public void CopilotTranscriptUsesOneThoughtPerAssistantMessage()
    {
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(
                DialectIds.CopilotCliTranscript);
        TranscriptLine line = new(
            """
            {"type":"assistant.message","id":"message-1","data":{"interactionId":"interaction-1","turnId":"0","messageId":"message-1","reasoningOpaque":"encrypted","encryptedContent":"ciphertext","reasoningText":"readable thought"}}
            """,
            "C:/events.jsonl",
            0,
            1,
            1,
            TranscriptFileRole.Main,
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            DialectIds.CopilotCliTranscript,
            "GitHubCopilot:CopilotCli:session",
            "session",
            TurnHint: "transcript-interaction:interaction-1");

        HookObservation thought = Assert.Single(
            parser.Parse(line),
            static item => item.Interpretation.Role == ObservationRole.AgentThought);
        Assert.Equal("readable thought", thought.Text);
        Assert.Equal(InferenceEvidence.Observed, thought.Interpretation.Evidence);
    }

    [Fact]
    public void CopilotTranscriptPreservesModelAccounting()
    {
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(
                DialectIds.CopilotCliTranscript);
        TranscriptLine line = new(
            """
            {"type":"model.model_call_success","id":"model-1","data":{"kind":"model_call_success","turn":0,"model":"gpt","modelCallDurationMs":412}}
            """,
            "C:/events.jsonl",
            0,
            1,
            1,
            TranscriptFileRole.Main,
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            DialectIds.CopilotCliTranscript,
            "GitHubCopilot:CopilotCli:session",
            "session",
            TurnHint: "transcript-interaction:interaction-1");

        HookObservation accounting = Assert.Single(parser.Parse(line));
        Assert.True(accounting.IsMetadataOnly);
        Assert.Contains(
            accounting.Interpretation.UsageMeasurements,
            static item =>
                item.Name == "modelCallDurationMs" &&
                item.Value == 412 &&
                item.Unit == "ms");
    }

    [Fact]
    public void CopilotSpyExtractsApplyPatchWrittenFile()
    {
        const string path = @"C:\Repo\summary.md";
        string patch =
            $"*** Begin Patch\n*** Update File: {path}\n@@\n-old\n+new\n*** End Patch\n";
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParseHook(
            "userPromptSubmitted",
            new
            {
                sessionId = "session",
                timestamp = 1,
                cwd = @"C:\Repo",
                prompt = "update summary"
            }));
        viewModel.AddObservation(ParseHook(
            "preToolUse",
            new
            {
                sessionId = "session",
                timestamp = 2,
                cwd = @"C:\Repo",
                toolName = "apply_patch",
                toolArgs = patch
            }));
        viewModel.AddObservation(ParseHook(
            "postToolUse",
            new
            {
                sessionId = "session",
                timestamp = 3,
                cwd = @"C:\Repo",
                toolName = "apply_patch",
                toolArgs = patch,
                toolResult = new { resultType = "success" }
            }));

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel turn = Assert.Single(
            session.Children,
            static item => item.Kind == TreeNodeKind.Generation);
        Assert.Equal(
            path,
            Assert.Single(turn.NodeSummary!.WrittenFiles).FullPath);
    }

    [Fact]
    public async Task BothSummariesExposeTheShellCommandText()
    {
        const string command = "dotnet test HarnessSpy.sln";
        object[] rows =
        [
            Event(
                "session.start",
                "start",
                new { sessionId = "command-session" }),
            Event(
                "user.message",
                "user",
                new
                {
                    interactionId = "interaction-1",
                    turnId = "0",
                    content = "test"
                }),
            ToolRequest(
                "request",
                "interaction-1",
                "0",
                "command",
                "powershell",
                new { command, description = "Run tests" }),
            ToolResult(
                "result",
                "0",
                "command",
                "powershell",
                success: true),
            Event(
                "session.shutdown",
                "shutdown",
                new { shutdownType = "routine" })
        ];
        SessionCatalogEntry catalogSession = await ReadSessionAsync(
            "command-session",
            rows);
        NodeSummary catalogSummary =
            new SessionNodeSummaryBuilder().Build(catalogSession);

        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParseHook(
            "userPromptSubmitted",
            new
            {
                sessionId = "command-session",
                timestamp = 1,
                cwd = @"C:\Repo",
                prompt = "test"
            }));
        viewModel.AddObservation(ParseHook(
            "preToolUse",
            new
            {
                sessionId = "command-session",
                timestamp = 2,
                cwd = @"C:\Repo",
                toolName = "powershell",
                toolArgs = new { command, description = "Run tests" }
            }));
        viewModel.AddObservation(ParseHook(
            "postToolUse",
            new
            {
                sessionId = "command-session",
                timestamp = 3,
                cwd = @"C:\Repo",
                toolName = "powershell",
                toolArgs = new { command, description = "Run tests" },
                toolResult = new { resultType = "success" }
            }));
        TreeNodeViewModel hookSession =
            Assert.Single(Assert.Single(viewModel.Roots).Children);

        Assert.Equal([command], catalogSummary.Commands);
        Assert.Equal([command], hookSession.NodeSummary!.Commands);
    }

    [Fact]
    public void CopilotSpyUsesNativeTranscriptLifecycleForSessionDuration()
    {
        DateTimeOffset startedAt = new(
            2026,
            9,
            19,
            9,
            17,
            8,
            TimeSpan.Zero);
        DateTimeOffset endedAt = startedAt.AddMinutes(44).AddSeconds(32);
        ObservationReconciler reconciler = new();
        MainWindowViewModel viewModel = new();
        Apply(
            viewModel,
            reconciler,
            ParseHook(
                "userPromptSubmitted",
                new
                {
                    sessionId = "lifecycle-session",
                    timestamp = startedAt.AddMinutes(4).ToUnixTimeMilliseconds(),
                    cwd = @"C:\Repo",
                    prompt = "inspect"
                },
                startedAt.AddMinutes(4)));

        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(
                DialectIds.CopilotCliTranscript);
        Apply(
            viewModel,
            reconciler,
            Assert.Single(parser.Parse(TranscriptLifecycleLine(
                "session.start",
                "start",
                startedAt))));
        Apply(
            viewModel,
            reconciler,
            Assert.Single(parser.Parse(TranscriptLifecycleLine(
                "session.shutdown",
                "end",
                endedAt))));

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        Assert.Equal(
            endedAt - startedAt,
            session.NodeSummary!.WallTime);
    }

    private static object Event(string type, string id, object data) =>
        new
        {
            type,
            id,
            timestamp = "2026-09-19T09:00:00Z",
            data
        };

    private static object ToolRequest(
        string id,
        string interactionId,
        string turnId,
        string toolCallId,
        string name,
        object arguments) =>
        Event(
            "assistant.message",
            id,
            new
            {
                interactionId,
                turnId,
                messageId = id,
                toolRequests = new[]
                {
                    new { toolCallId, name, arguments }
                }
            });

    private static object ToolResult(
        string id,
        string turnId,
        string toolCallId,
        string toolName,
        bool success) =>
        Event(
            "tool.execution_complete",
            id,
            new
            {
                turnId,
                toolCallId,
                toolName,
                success
            });

    private static async Task<SessionCatalogEntry> ReadSessionAsync(
        string sessionId,
        IReadOnlyList<object> rows)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "HarnessSpy-Copilot-Consistency",
            Guid.NewGuid().ToString("N"));
        try
        {
            string sessionDirectory = Path.Combine(
                root,
                ".copilot",
                "session-state",
                sessionId);
            Directory.CreateDirectory(sessionDirectory);
            await File.WriteAllLinesAsync(
                Path.Combine(sessionDirectory, "events.jsonl"),
                rows.Select(static item => JsonSerializer.Serialize(item)));

            SessionDiscoveryContext context = new(
                root,
                root,
                _ => null,
                new SessionDiscoveryLimits(
                    MaximumDuration: TimeSpan.FromSeconds(5)));
            CopilotCliSessionCatalogSource source = new(context);
            SessionCatalogScanResult result = await source.ScanAsync(
                CancellationToken.None);
            return Assert.Single(result.Sessions);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static HookObservation ParseHook(
        string configuredEventName,
        object payload,
        DateTimeOffset? observedAtUtc = null)
    {
        using JsonDocument payloadDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(payload));
        ObservationEnvelope envelope = new(
            ObservationEnvelope.CurrentIngressVersion,
            Guid.NewGuid(),
            observedAtUtc ?? DateTimeOffset.UtcNow,
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            HookSurface.CopilotCli,
            ObservationSourceKind.Hook,
            configuredEventName,
            configuredEventName,
            "test",
            null,
            "valid",
            payloadDocument.RootElement.Clone());
        string line = JsonSerializer.Serialize(
            envelope,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(HookObservation.TryParse(line, out HookObservation? observation));
        return Assert.IsType<HookObservation>(observation);
    }

    private static TranscriptLine TranscriptLifecycleLine(
        string type,
        string id,
        DateTimeOffset timestamp)
    {
        DateTimeOffset envelopeTimestamp = type == "session.start"
            ? timestamp.AddMilliseconds(64)
            : timestamp;
        return new TranscriptLine(
            JsonSerializer.Serialize(new
            {
                type,
                id,
                timestamp = envelopeTimestamp,
                data = new
                {
                    sessionId = "lifecycle-session",
                    startTime = type == "session.start"
                        ? timestamp
                        : (DateTimeOffset?)null
                }
            }),
            "C:/events.jsonl",
            id == "start" ? 0 : 100,
            id == "start" ? 1 : 2,
            1,
            TranscriptFileRole.Main,
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            DialectIds.CopilotCliTranscript,
            "GitHubCopilot:CopilotCli:lifecycle-session",
            "lifecycle-session",
            ObservedAtUtc: envelopeTimestamp);
    }

    private static void Apply(
        MainWindowViewModel viewModel,
        ObservationReconciler reconciler,
        HookObservation observation)
    {
        foreach (ObservationChange change in reconciler.Reconcile(observation))
        {
            viewModel.ApplyObservationChange(change);
        }
    }
}
