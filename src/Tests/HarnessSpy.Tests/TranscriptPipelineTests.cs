using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sources;

namespace HarnessSpy.Tests;

// Tailer, reconciler, capture store, and end-to-end coordinator behaviour.
public sealed class TranscriptPipelineTests
{
    [Fact]
    public void TailerReadsOnlyCompleteLinesAndTracksByteOffset()
    {
        string path = TempFile();
        File.WriteAllText(path, "{\"a\":1}\n{\"b\":2}\n{\"partial\":");

        TranscriptReadCursor cursor = new(path);
        IReadOnlyList<TranscriptRawLine> first = JsonLineFileTailer.ReadNewLines(cursor);

        Assert.Equal(2, first.Count);
        Assert.Equal("{\"a\":1}", first[0].Raw);
        Assert.Equal(0, first[0].ByteOffset);
        Assert.Equal(1, first[0].LineNumber);

        // The incomplete trailing row is not consumed; a re-read yields nothing
        // until it is completed.
        Assert.Empty(JsonLineFileTailer.ReadNewLines(cursor));

        File.AppendAllText(path, "3}\n{\"c\":4}\n");
        IReadOnlyList<TranscriptRawLine> second = JsonLineFileTailer.ReadNewLines(cursor);
        Assert.Equal(2, second.Count);
        Assert.Equal("{\"partial\":3}", second[0].Raw);
        Assert.Equal("{\"c\":4}", second[1].Raw);
    }

    [Fact]
    public void TailerStartsNewGenerationOnTruncation()
    {
        string path = TempFile();
        File.WriteAllText(path, "{\"a\":1}\n{\"b\":2}\n");

        TranscriptReadCursor cursor = new(path);
        JsonLineFileTailer.ReadNewLines(cursor);
        Assert.Equal(1, cursor.FileGeneration);

        // Replace with a shorter file: the tailer resets to a new generation
        // and re-reads from zero.
        File.WriteAllText(path, "{\"z\":9}\n");
        IReadOnlyList<TranscriptRawLine> lines = JsonLineFileTailer.ReadNewLines(cursor);
        Assert.Equal(2, cursor.FileGeneration);
        Assert.Equal("{\"z\":9}", Assert.Single(lines).Raw);
    }

    [Fact]
    public void CaptureStoreIsIdempotentAcrossRebackfill()
    {
        string payloadsDir = TempDir();
        TranscriptRawLine row = new("{\"type\":\"assistant\"}", 16616, 15, 1);

        // First run captures the row; a re-backfill (or a new store instance
        // reading the existing file) must not append it again.
        TranscriptCaptureStore first = new(payloadsDir);
        Assert.True(first.AppendSourceRow("Claude:Claude:s1", "main", row, "C:/t.jsonl", "claude-transcript-jsonl", TranscriptFileRole.Main, TranscriptCompleteness.Complete));
        Assert.False(first.AppendSourceRow("Claude:Claude:s1", "main", row, "C:/t.jsonl", "claude-transcript-jsonl", TranscriptFileRole.Main, TranscriptCompleteness.Complete));

        TranscriptCaptureStore afterRestart = new(payloadsDir);
        Assert.False(afterRestart.AppendSourceRow("Claude:Claude:s1", "main", row, "C:/t.jsonl", "claude-transcript-jsonl", TranscriptFileRole.Main, TranscriptCompleteness.Complete));

        string sidecar = first.SidecarDirectory("Claude:Claude:s1");
        string sourceFile = Path.Combine(sidecar, "source-main.jsonl");
        Assert.Single(File.ReadAllLines(sourceFile));
    }

    [Fact]
    public void ReconcilerAttachesTranscriptToolToHookByToolUseId()
    {
        ObservationReconciler reconciler = new();

        HookObservation preToolUse = ClaudeHook(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_use_id":"t1"}""");
        IReadOnlyList<ObservationChange> hookChanges = reconciler.Reconcile(preToolUse);
        Assert.Equal(ObservationChangeKind.Add, Assert.Single(hookChanges).Kind);

        HookObservation transcriptTool = TranscriptToolUse("s1", "t1", "Bash");
        ObservationChange change = Assert.Single(reconciler.Reconcile(transcriptTool));
        Assert.Equal(ObservationChangeKind.AttachEvidence, change.Kind);
        Assert.Equal(preToolUse.EventId, change.TargetEventId);
    }

    [Fact]
    public void ReconcilerAddsTranscriptOnlyThinkingAsNode()
    {
        ObservationReconciler reconciler = new();
        HookObservation thinking = TranscriptThinking("s1");
        ObservationChange change = Assert.Single(reconciler.Reconcile(thinking));
        Assert.Equal(ObservationChangeKind.Add, change.Kind);
    }

    [Fact]
    public void ReconcilerAttachesCopilotLifecycleThroughLearnedToolCallId()
    {
        ObservationReconciler reconciler = new();
        HookObservation hook = CopilotHook(
            "preToolUse",
            """{"sessionId":"c1","toolName":"view","toolArgs":{"path":"C:\\Repo\\Program.cs"}}""");
        Assert.Equal(
            ObservationChangeKind.Add,
            Assert.Single(reconciler.Reconcile(hook)).Kind);

        HookObservation request = Assert.Single(CopilotTranscript(
            """{"type":"assistant.message","id":"m1","data":{"interactionId":"i1","turnId":"3","toolRequests":[{"toolCallId":"call_1","name":"view","arguments":{"path":"C:\\Repo\\Program.cs"}}]}}"""));
        ToolCorrelationSignatureBuilder signatureBuilder = new();
        Assert.Equal(signatureBuilder.Build(hook), signatureBuilder.Build(request));
        ObservationChange requestChange = Assert.Single(reconciler.Reconcile(request));
        Assert.Equal(ObservationChangeKind.AttachEvidence, requestChange.Kind);
        Assert.Equal(hook.EventId, requestChange.TargetEventId);

        HookObservation execution = Assert.Single(CopilotTranscript(
            """{"type":"tool.execution_start","id":"e1","data":{"interactionId":"i1","turnId":"3","toolCallId":"call_1","toolName":"view"}}"""));
        ObservationChange executionChange = Assert.Single(reconciler.Reconcile(execution));
        Assert.Equal(ObservationChangeKind.AttachEvidence, executionChange.Kind);
        Assert.Equal(hook.EventId, executionChange.TargetEventId);

        HookObservation permission = Assert.Single(CopilotTranscript(
            """{"type":"permission.requested","id":"p1","data":{"interactionId":"i1","turnId":"3","permissionRequest":{"kind":"read","toolCallId":"call_1"}}}"""));
        ObservationChange permissionChange = Assert.Single(reconciler.Reconcile(permission));
        Assert.Equal(ObservationChangeKind.AttachEvidence, permissionChange.Kind);
        Assert.Equal(hook.EventId, permissionChange.TargetEventId);
    }

    [Fact]
    public void ReconcilerPromotesTranscriptFirstCopilotToolBySignature()
    {
        ObservationReconciler reconciler = new();
        HookObservation request = Assert.Single(CopilotTranscript(
            """{"type":"assistant.message","id":"m1","data":{"interactionId":"i1","turnId":"0","toolRequests":[{"toolCallId":"call_1","name":"powershell","arguments":{"command":"dotnet test"}}]}}"""));
        Assert.Equal(
            ObservationChangeKind.Add,
            Assert.Single(reconciler.Reconcile(request)).Kind);

        HookObservation execution = Assert.Single(CopilotTranscript(
            """{"type":"tool.execution_start","id":"e1","data":{"interactionId":"i1","turnId":"0","toolCallId":"call_1","toolName":"powershell"}}"""));
        ObservationChange pendingExecution = Assert.Single(reconciler.Reconcile(execution));
        Assert.Equal(ObservationChangeKind.AttachEvidence, pendingExecution.Kind);
        Assert.Equal(request.EventId, pendingExecution.TargetEventId);

        HookObservation hook = CopilotHook(
            "preToolUse",
            """{"sessionId":"c1","toolName":"powershell","toolArgs":{"command":"dotnet test"}}""");
        ObservationChange promoted = Assert.Single(reconciler.Reconcile(hook));
        Assert.Equal(ObservationChangeKind.PromotePrimary, promoted.Kind);
        Assert.Equal(request.EventId, promoted.TargetEventId);

        HookObservation completed = Assert.Single(CopilotTranscript(
            """{"type":"tool.execution_complete","id":"e2","data":{"interactionId":"i1","turnId":"0","toolCallId":"call_1","toolName":"powershell","success":true}}"""));
        ObservationChange completionChange = Assert.Single(reconciler.Reconcile(completed));
        Assert.Equal(ObservationChangeKind.AttachEvidence, completionChange.Kind);
        Assert.Equal(hook.EventId, completionChange.TargetEventId);
    }

    [Fact]
    public void ReconcilerMatchesParallelCopilotShellToolsByCommand()
    {
        ObservationReconciler reconciler = new();
        HookObservation firstHook = CopilotHook(
            "preToolUse",
            """{"sessionId":"c1","toolName":"powershell","toolArgs":{"command":"dotnet test A"}}""");
        HookObservation secondHook = CopilotHook(
            "preToolUse",
            """{"sessionId":"c1","toolName":"powershell","toolArgs":{"command":"dotnet test B"}}""");
        reconciler.Reconcile(firstHook);
        reconciler.Reconcile(secondHook);

        HookObservation secondRequest = Assert.Single(CopilotTranscript(
            """{"type":"assistant.message","id":"m2","data":{"interactionId":"i1","turnId":"0","toolRequests":[{"toolCallId":"call_b","name":"powershell","arguments":{"command":"dotnet test B"}}]}}"""));
        HookObservation firstRequest = Assert.Single(CopilotTranscript(
            """{"type":"assistant.message","id":"m1","data":{"interactionId":"i1","turnId":"0","toolRequests":[{"toolCallId":"call_a","name":"powershell","arguments":{"command":"dotnet test A"}}]}}"""));

        Assert.Equal(
            secondHook.EventId,
            Assert.Single(reconciler.Reconcile(secondRequest)).TargetEventId);
        Assert.Equal(
            firstHook.EventId,
            Assert.Single(reconciler.Reconcile(firstRequest)).TargetEventId);
    }

    [Fact]
    public void SignatureBuilderNormalizesCopilotObjectAndStringArguments()
    {
        ToolCorrelationSignatureBuilder builder = new();

        HookObservation globHook = CopilotHook(
            "preToolUse",
            """{"sessionId":"c1","toolName":"glob","toolArgs":{"pattern":"**/*","paths":["C:\\Repo"],"head_limit":100}}""");
        HookObservation globTranscript = Assert.Single(CopilotTranscript(
            """{"type":"assistant.message","id":"m1","data":{"toolRequests":[{"toolCallId":"call_g","name":"glob","arguments":{"head_limit":100,"paths":["C:\\Repo"],"pattern":"**/*"}}]}}"""));
        Assert.Equal(builder.Build(globHook), builder.Build(globTranscript));

        HookObservation patchHook = CopilotHook(
            "preToolUse",
            """{"sessionId":"c1","toolName":"apply_patch","toolArgs":"*** Begin Patch\n*** End Patch\n"}""");
        HookObservation patchTranscript = Assert.Single(CopilotTranscript(
            """{"type":"assistant.message","id":"m2","data":{"toolRequests":[{"toolCallId":"call_p","name":"apply_patch","arguments":"*** Begin Patch\r\n*** End Patch\r\n"}]}}"""));
        Assert.Equal(builder.Build(patchHook), builder.Build(patchTranscript));
    }

    [Fact]
    public void ReconcilerDoesNotMatchRepeatedToolToStalePriorTurn()
    {
        ObservationReconciler reconciler = new();
        HookObservation oldHook = CopilotHook(
            "preToolUse",
            """{"sessionId":"c1","timestamp":1789809677000,"toolName":"powershell","toolArgs":{"command":"dotnet test"}}""");
        HookObservation currentHook = CopilotHook(
            "preToolUse",
            """{"sessionId":"c1","timestamp":1789811546400,"toolName":"powershell","toolArgs":{"command":"dotnet test"}}""");
        reconciler.Reconcile(oldHook);
        reconciler.Reconcile(currentHook);

        HookObservation currentRequest = Assert.Single(CopilotTranscript(
            """{"type":"assistant.message","id":"m1","timestamp":"2026-09-19T09:52:27Z","data":{"interactionId":"i2","turnId":"0","toolRequests":[{"toolCallId":"call_1","name":"powershell","arguments":{"command":"dotnet test"}}]}}"""));
        ObservationChange matched = Assert.Single(reconciler.Reconcile(currentRequest));

        Assert.Equal(ObservationChangeKind.AttachEvidence, matched.Kind);
        Assert.Equal(currentHook.EventId, matched.TargetEventId);
        Assert.NotEqual(oldHook.EventId, matched.TargetEventId);
    }

    [Fact]
    public void ReconcilerClearsClosedCopilotTurnBeforeTranscriptFirstRepeat()
    {
        ObservationReconciler reconciler = new();
        HookObservation oldHook = CopilotHook(
            "preToolUse",
            """{"sessionId":"c1","timestamp":1789811540000,"toolName":"powershell","toolArgs":{"command":"dotnet test"}}""");
        reconciler.Reconcile(oldHook);
        reconciler.Reconcile(CopilotHook(
            "agentStop",
            """{"sessionId":"c1","timestamp":1789811545000,"stopReason":"end_turn"}"""));

        HookObservation request = Assert.Single(CopilotTranscript(
            """{"type":"assistant.message","id":"m1","timestamp":"2026-09-19T09:52:27Z","data":{"interactionId":"i2","turnId":"0","toolRequests":[{"toolCallId":"call_2","name":"powershell","arguments":{"command":"dotnet test"}}]}}"""));
        ObservationChange added = Assert.Single(reconciler.Reconcile(request));
        Assert.Equal(ObservationChangeKind.Add, added.Kind);
        Assert.Null(added.TargetEventId);

        HookObservation currentHook = CopilotHook(
            "preToolUse",
            """{"sessionId":"c1","timestamp":1789811547500,"toolName":"powershell","toolArgs":{"command":"dotnet test"}}""");
        ObservationChange promoted = Assert.Single(reconciler.Reconcile(currentHook));
        Assert.Equal(ObservationChangeKind.PromotePrimary, promoted.Kind);
        Assert.Equal(request.EventId, promoted.TargetEventId);
    }

    [Fact]
    public void ReconcilerKeepsOrderedFallbackForTimestampLessCursorTranscript()
    {
        ObservationReconciler reconciler = new();
        HookObservation hook = CursorPreToolHook("c1", "g1", "Read", "README.md");
        reconciler.Reconcile(hook);

        HookObservation transcript = CursorTranscriptTool("c1", "Read", "README.md");
        ObservationChange matched = Assert.Single(reconciler.Reconcile(transcript));

        Assert.Equal(ObservationChangeKind.AttachEvidence, matched.Kind);
        Assert.Equal(hook.EventId, matched.TargetEventId);
    }

    [Fact]
    public void ReconcilerDeduplicatesReplayedTranscriptRow()
    {
        ObservationReconciler reconciler = new();
        HookObservation thinking = TranscriptThinking("s1");
        Assert.Single(reconciler.Reconcile(thinking));
        // The same row (same provenance dedupe key) never projects twice.
        Assert.Empty(reconciler.Reconcile(thinking));
    }

    [Fact]
    public async Task CoordinatorDiscoversBackfillsAndDurablyCapturesTranscript()
    {
        string payloadsDir = TempDir();
        string transcriptPath = Path.Combine(TempDir(), "778.jsonl");
        File.Copy(Fixture("Cursor", "transcript-sample.jsonl"), transcriptPath);

        TranscriptCaptureStore capture = new(payloadsDir);
        TranscriptBindingJournal journal = new(capture);
        TranscriptSessionRegistry registry = new();
        List<ObservationChange> changes = [];
        object gate = new();

        await using ObservationIngestionCoordinator coordinator = new(
            registry,
            capture,
            journal,
            (change, _) =>
            {
                lock (gate)
                {
                    changes.Add(change);
                }

                return Task.CompletedTask;
            },
            enableTranscripts: true,
            pollInterval: TimeSpan.FromMilliseconds(50));

        using CancellationTokenSource cts = new();
        coordinator.Start(cts.Token);

        HookObservation hook = CursorHookWithTranscript("c1", transcriptPath);
        await coordinator.IngestHookAsync(hook, cts.Token);

        await WaitUntil(() =>
        {
            lock (gate)
            {
                return changes.Any(c => c.Observation.IsTranscriptSourced);
            }
        });

        lock (gate)
        {
            Assert.Contains(changes, c => !c.Observation.IsTranscriptSourced); // the hook itself
            Assert.Contains(changes, c => c.Observation.IsTranscriptSourced);  // transcript fragments
        }

        // The raw rows are durably captured beside the payloads so replay
        // survives provider cleanup.
        string sidecar = capture.SidecarDirectory(hook.ProviderScopedSessionId);
        Assert.True(Directory.Exists(sidecar));
        Assert.NotEmpty(Directory.GetFiles(sidecar, "source-*.jsonl"));
    }

    [Fact]
    public async Task CapturedSidecarIsReloadedByReplayLoader()
    {
        string payloadsDir = TempDir();
        string transcriptPath = Path.Combine(TempDir(), "claude.jsonl");
        File.Copy(Fixture("ClaudeCode", "transcript-main-sample.jsonl"), transcriptPath);

        // Capture the transcript live via the coordinator (writes the sidecar).
        TranscriptCaptureStore capture = new(payloadsDir);
        TranscriptBindingJournal journal = new(capture);
        TranscriptSessionRegistry registry = new();
        int captured = 0;
        object gate = new();

        await using (ObservationIngestionCoordinator coordinator = new(
            registry,
            capture,
            journal,
            (change, _) =>
            {
                lock (gate)
                {
                    if (change.Observation.IsTranscriptSourced)
                    {
                        captured++;
                    }
                }

                return Task.CompletedTask;
            },
            enableTranscripts: true,
            pollInterval: TimeSpan.FromMilliseconds(50)))
        {
            using CancellationTokenSource cts = new();
            coordinator.Start(cts.Token);
            await coordinator.IngestHookAsync(ClaudeHookWithTranscript("9303", transcriptPath), cts.Token);
            await WaitUntil(() => { lock (gate) { return captured > 0; } });
        }

        // A fresh process/session has no live tailer, so it must rehydrate from
        // the durable sidecar rather than the (possibly deleted) original file.
        File.Delete(transcriptPath);
        IReadOnlyList<HookObservation> reloaded = new TranscriptReplayLoader().Load(payloadsDir);

        Assert.NotEmpty(reloaded);
        Assert.Contains(reloaded, o => o.Interpretation.Role == ObservationRole.AgentThought);
        Assert.Contains(reloaded, o => o.ToolName == "Bash" && o.ToolUseId == "t1");
    }

    [Fact]
    public async Task CopilotBackfillAndReplayPreserveDerivedInteractionTurn()
    {
        string payloadsDir = TempDir();
        string transcriptPath = Path.Combine(TempDir(), "events.jsonl");
        File.Copy(Fixture("CopilotCli", "transcript-events-sample.jsonl"), transcriptPath);

        TranscriptCaptureStore capture = new(payloadsDir);
        TranscriptBindingJournal journal = new(capture);
        TranscriptSessionRegistry registry = new();
        List<HookObservation> captured = [];
        object gate = new();

        await using (ObservationIngestionCoordinator coordinator = new(
            registry,
            capture,
            journal,
            (change, _) =>
            {
                lock (gate)
                {
                    if (change.Observation.IsTranscriptSourced)
                    {
                        captured.Add(change.Observation);
                    }
                }

                return Task.CompletedTask;
            },
            enableTranscripts: true,
            pollInterval: TimeSpan.FromMilliseconds(50)))
        {
            using CancellationTokenSource cts = new();
            coordinator.Start(cts.Token);
            await coordinator.IngestHookAsync(
                CopilotHookWithTranscript("c1", transcriptPath),
                cts.Token);
            await WaitUntil(() =>
            {
                lock (gate)
                {
                    return captured.Any(o =>
                        o.Interpretation.Role == ObservationRole.AgentResponse);
                }
            });
        }

        lock (gate)
        {
            Assert.NotEmpty(captured);
            Assert.All(captured, observation =>
                Assert.Equal("transcript-derived-1", observation.GenerationId));
        }

        IReadOnlyList<HookObservation> reloaded =
            new TranscriptReplayLoader().Load(payloadsDir);
        Assert.NotEmpty(reloaded);
        Assert.All(reloaded, observation =>
            Assert.Equal("transcript-derived-1", observation.GenerationId));
    }

    [Fact]
    public async Task CopilotTurnStopDrainsExistingBindingBeforeClearingSignatures()
    {
        string payloadsDir = TempDir();
        string transcriptPath = Path.Combine(TempDir(), "events.jsonl");
        File.WriteAllText(
            transcriptPath,
            """{"type":"user.message","id":"u1","timestamp":"2026-09-19T09:00:00Z","data":{"interactionId":"i1","turnId":"0","content":"first"}}""" +
            Environment.NewLine);

        TranscriptCaptureStore capture = new(payloadsDir);
        TranscriptBindingJournal journal = new(capture);
        TranscriptSessionRegistry registry = new();
        List<ObservationChange> changes = [];
        object gate = new();

        await using ObservationIngestionCoordinator coordinator = new(
            registry,
            capture,
            journal,
            (change, _) =>
            {
                lock (gate)
                {
                    changes.Add(change);
                }

                return Task.CompletedTask;
            },
            enableTranscripts: true,
            pollInterval: TimeSpan.FromMilliseconds(5));
        using CancellationTokenSource cts = new();
        coordinator.Start(cts.Token);

        await coordinator.IngestHookAsync(
            CopilotHookWithTranscript("c1", transcriptPath),
            cts.Token);
        await WaitUntil(() =>
        {
            lock (gate)
            {
                return changes.Any(change =>
                    change.Observation.Interpretation.Role == ObservationRole.TurnStop);
            }
        });

        DateTimeOffset now = DateTimeOffset.UtcNow;
        File.AppendAllText(
            transcriptPath,
            JsonSerializer.Serialize(new
            {
                type = "user.message",
                id = "u2",
                timestamp = now.ToString("O"),
                data = new
                {
                    interactionId = "i2",
                    turnId = "0",
                    content = "second"
                }
            }) + Environment.NewLine +
            JsonSerializer.Serialize(new
            {
                type = "assistant.message",
                id = "m2",
                timestamp = now.AddMilliseconds(100).ToString("O"),
                data = new
                {
                    interactionId = "i2",
                    turnId = "0",
                    toolRequests = new[]
                    {
                        new
                        {
                            toolCallId = "call_2",
                            name = "powershell",
                            arguments = new { command = "dotnet test" }
                        }
                    }
                }
            }) + Environment.NewLine);

        HookObservation preToolUse = CopilotHook(
            "preToolUse",
            """{"sessionId":"c1","toolName":"powershell","toolArgs":{"command":"dotnet test"}}""");
        await coordinator.IngestHookAsync(preToolUse, cts.Token);
        await WaitUntil(() =>
        {
            lock (gate)
            {
                return changes.Any(change =>
                    change.Observation.EventId == preToolUse.EventId);
            }
        });

        await coordinator.IngestHookAsync(
            CopilotHookWithTranscript("c1", transcriptPath),
            cts.Token);
        await WaitUntil(() =>
        {
            lock (gate)
            {
                return changes.Any(change =>
                    (change.Kind == ObservationChangeKind.AttachEvidence &&
                     change.TargetEventId == preToolUse.EventId) ||
                    (change.Kind == ObservationChangeKind.PromotePrimary &&
                     change.Observation.EventId == preToolUse.EventId));
            }
        });

        lock (gate)
        {
            Assert.Contains(changes, change =>
                (change.Kind == ObservationChangeKind.AttachEvidence &&
                 change.Observation.ToolUseId == "call_2" &&
                 change.TargetEventId == preToolUse.EventId) ||
                (change.Kind == ObservationChangeKind.PromotePrimary &&
                 change.Observation.EventId == preToolUse.EventId));
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("Condition was not met within the timeout.");
    }

    private static HookObservation TranscriptToolUse(string session, string toolCallId, string toolName)
    {
        ITranscriptDialectParser parser = TranscriptDialectParserRegistry.Resolve(DialectIds.ClaudeTranscript);
        string raw = "{\"type\":\"assistant\",\"promptId\":\"p1\",\"uuid\":\"x\",\"sessionId\":\"" + session +
            "\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"" + toolCallId +
            "\",\"name\":\"" + toolName + "\",\"input\":{}}]}}";
        return Assert.Single(parser.Parse(ClaudeLine(raw, session)));
    }

    private static HookObservation TranscriptThinking(string session)
    {
        ITranscriptDialectParser parser = TranscriptDialectParserRegistry.Resolve(DialectIds.ClaudeTranscript);
        string raw = "{\"type\":\"assistant\",\"promptId\":\"p1\",\"uuid\":\"th1\",\"sessionId\":\"" + session +
            "\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"SIG\"}]}}";
        return Assert.Single(parser.Parse(ClaudeLine(raw, session)));
    }

    private static TranscriptLine ClaudeLine(string raw, string session) =>
        new(
            raw,
            "C:/t.jsonl",
            0,
            1,
            1,
            TranscriptFileRole.Main,
            HookProvider.ClaudeCode,
            HookSurface.ClaudeCode,
            DialectIds.ClaudeTranscript,
            $"{HookProvider.ClaudeCode}:{HookSurface.ClaudeCode}:{session}",
            session);

    private static HookObservation ClaudeHook(string payloadJson)
    {
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        ObservationEnvelope envelope = new(
            ObservationEnvelope.CurrentIngressVersion,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            HookProvider.ClaudeCode,
            HookSurface.ClaudeCode,
            HookSurface.ClaudeCode,
            ObservationSourceKind.Hook,
            null,
            payload.RootElement.GetProperty("hook_event_name").GetString(),
            "test",
            null,
            "valid",
            payload.RootElement.Clone());
        string line = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(HookObservation.TryParse(line, out HookObservation? observation));
        return observation!;
    }

    private static HookObservation ClaudeHookWithTranscript(string sessionId, string transcriptPath)
    {
        string escaped = transcriptPath.Replace("\\", "\\\\");
        string payloadJson = "{\"hook_event_name\":\"SessionStart\",\"session_id\":\"" + sessionId +
            "\",\"cwd\":\"C:\\\\Repo\",\"transcript_path\":\"" + escaped + "\"}";
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        ObservationEnvelope envelope = new(
            ObservationEnvelope.CurrentIngressVersion,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            HookProvider.ClaudeCode,
            HookSurface.ClaudeCode,
            HookSurface.ClaudeCode,
            ObservationSourceKind.Hook,
            null,
            "SessionStart",
            "test",
            null,
            "valid",
            payload.RootElement.Clone());
        string line = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(HookObservation.TryParse(line, out HookObservation? observation));
        return observation!;
    }

    private static HookObservation CursorHookWithTranscript(string conversationId, string transcriptPath)
    {
        string escaped = transcriptPath.Replace("\\", "\\\\");
        string payloadJson = $$"""{"hook_event_name":"postToolUse","conversation_id":"{{conversationId}}","generation_id":"g1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"t1","transcript_path":"{{escaped}}"}""";
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        ObservationEnvelope envelope = new(
            ObservationEnvelope.CurrentIngressVersion,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            HookProvider.Cursor,
            HookSurface.CursorIde,
            HookSurface.CursorIde,
            ObservationSourceKind.Hook,
            null,
            "postToolUse",
            "test",
            null,
            "valid",
            payload.RootElement.Clone());
        string line = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(HookObservation.TryParse(line, out HookObservation? observation));
        return observation!;
    }

    private static IReadOnlyList<HookObservation> CopilotTranscript(string raw)
    {
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.CopilotCliTranscript);
        long offset = unchecked((uint)StringComparer.Ordinal.GetHashCode(raw));
        TranscriptRowScanner.RowMeta metadata = TranscriptRowScanner.Read(raw);
        return parser.Parse(new TranscriptLine(
            raw,
            "C:/events.jsonl",
            offset,
            1,
            1,
            TranscriptFileRole.Main,
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            DialectIds.CopilotCliTranscript,
            "GitHubCopilot:CopilotCli:c1",
            "c1",
            ObservedAtUtc: metadata.Timestamp,
            TurnHint: "derived-1"));
    }

    private static HookObservation CopilotHook(string eventName, string payloadJson)
    {
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        ObservationEnvelope envelope = new(
            ObservationEnvelope.CurrentIngressVersion,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            HookProvider.GitHubCopilot,
            HookSurface.CopilotCli,
            HookSurface.CopilotCli,
            ObservationSourceKind.Hook,
            eventName,
            eventName,
            "test",
            null,
            "valid",
            payload.RootElement.Clone());
        string line = JsonSerializer.Serialize(
            envelope,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(HookObservation.TryParse(line, out HookObservation? observation));
        return observation!;
    }

    private static HookObservation CopilotHookWithTranscript(
        string sessionId,
        string transcriptPath)
    {
        string escaped = transcriptPath.Replace("\\", "\\\\");
        return CopilotHook(
            "agentStop",
            $$"""{"sessionId":"{{sessionId}}","cwd":"C:\\Repo","transcriptPath":"{{escaped}}","stopReason":"end_turn"}""");
    }

    private static HookObservation CursorPreToolHook(
        string sessionId,
        string generationId,
        string toolName,
        string path)
    {
        string payloadJson = JsonSerializer.Serialize(new
        {
            hook_event_name = "preToolUse",
            conversation_id = sessionId,
            generation_id = generationId,
            workspace_roots = new[] { @"C:\Repo" },
            tool_name = toolName,
            tool_input = new { path }
        });
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        ObservationEnvelope envelope = new(
            ObservationEnvelope.CurrentIngressVersion,
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            HookProvider.Cursor,
            HookSurface.CursorIde,
            HookSurface.CursorIde,
            ObservationSourceKind.Hook,
            null,
            "preToolUse",
            "test",
            null,
            "valid",
            payload.RootElement.Clone());
        string line = JsonSerializer.Serialize(
            envelope,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(HookObservation.TryParse(line, out HookObservation? observation));
        return observation!;
    }

    private static HookObservation CursorTranscriptTool(
        string sessionId,
        string toolName,
        string path)
    {
        string raw = JsonSerializer.Serialize(new
        {
            role = "assistant",
            message = new
            {
                content = new[]
                {
                    new
                    {
                        type = "tool_use",
                        name = toolName,
                        input = new { path }
                    }
                }
            }
        });
        ITranscriptDialectParser parser =
            TranscriptDialectParserRegistry.Resolve(DialectIds.CursorTranscript);
        return Assert.Single(parser.Parse(new TranscriptLine(
            raw,
            "C:/cursor.jsonl",
            0,
            1,
            1,
            TranscriptFileRole.Main,
            HookProvider.Cursor,
            HookSurface.CursorIde,
            DialectIds.CursorTranscript,
            $"Cursor:CursorIde:{sessionId}",
            sessionId)));
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "harnessspy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string TempFile() => Path.Combine(TempDir(), "tail.jsonl");

    private static string Fixture(string provider, string file) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", provider, file);
}
