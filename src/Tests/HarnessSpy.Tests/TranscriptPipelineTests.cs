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
