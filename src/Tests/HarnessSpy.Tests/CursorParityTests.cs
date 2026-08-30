using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;
using HarnessSpy.Wpf.ViewModels;
using HarnessSpy.Core.Hooks;

namespace HarnessSpy.Tests;

public sealed class HookForwarderTests
{
    [Fact]
    public async Task ValidPayloadReachesPipeAndStdoutIsNoOp()
    {
        string pipeName = "CursorSpy.Tests." + Guid.NewGuid().ToString("N");
        string payload = """
            {
              "hook_event_name": "sessionStart",
              "conversation_id": "conversation-1",
              "generation_id": "generation-1",
              "workspace_roots": ["C:\\Repo"],
              "cursor_version": "test"
            }
            """;

        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        Task<string?> readTask = ReadOneLineAsync(server);
        StringWriter output = new();
        HookForwarder forwarder = new(new NamedPipePayloadSink(pipeName, TimeSpan.FromSeconds(1)));

        int exitCode = await forwarder.RunAsync([], new StringReader(payload), output);
        string? forwarded = await readTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, exitCode);
        Assert.Equal("{}", output.ToString());
        Assert.NotNull(forwarded);

        using JsonDocument envelope = JsonDocument.Parse(forwarded);
        JsonElement root = envelope.RootElement;

        Assert.Equal(ObservationEnvelope.CurrentIngressVersion, root.GetProperty("ingressVersion").GetInt32());
        Assert.NotEqual(Guid.Empty, root.GetProperty("eventId").GetGuid());
        Assert.Equal("sessionStart", root.GetProperty("payload").GetProperty("hook_event_name").GetString());
        Assert.Equal("conversation-1", root.GetProperty("payload").GetProperty("conversation_id").GetString());
    }

    [Fact]
    public async Task MissingPipeServerFailsOpenQuickly()
    {
        string pipeName = "CursorSpy.Tests.Missing." + Guid.NewGuid().ToString("N");
        string payload = """{"hook_event_name":"stop","conversation_id":"conversation-1","workspace_roots":["C:\\Repo"]}""";
        string folder = Path.Combine(
            Path.GetTempPath(),
            "HarnessSpyMissingPipeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            StringWriter output = new();
            FileHookDiagnostics diagnostics = new(folder);
            HookForwarder forwarder = new(
                new NamedPipePayloadSink(pipeName, TimeSpan.FromMilliseconds(50)),
                diagnostics);

            Stopwatch stopwatch = Stopwatch.StartNew();
            int exitCode = await forwarder.RunAsync([], new StringReader(payload), output);
            stopwatch.Stop();

            Assert.Equal(0, exitCode);
            Assert.Equal("{}", output.ToString());
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.False(File.Exists(Path.Combine(folder, FileHookDiagnostics.ErrorLogFileName)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task SinkWriteFailureIsStillLogged()
    {
        const string payload =
            """{"hook_event_name":"stop","conversation_id":"conversation-1","workspace_roots":["C:\\Repo"]}""";
        string folder = Path.Combine(
            Path.GetTempPath(),
            "HarnessSpySinkFailureTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            FileHookDiagnostics diagnostics = new(folder);
            HookForwarder forwarder = new(new FailingSink(), diagnostics);

            await forwarder.RunAsync([], new StringReader(payload), new StringWriter());

            string errorLog = await File.ReadAllTextAsync(
                Path.Combine(folder, FileHookDiagnostics.ErrorLogFileName));
            Assert.Contains("Failed to forward 'stop'", errorLog, StringComparison.Ordinal);
            Assert.Contains("Simulated pipe write failure.", errorLog, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidJsonFailsOpenAndIsNotForwarded()
    {
        RecordingSink sink = new();
        HookForwarder forwarder = new(sink);
        StringWriter output = new();

        int exitCode = await forwarder.RunAsync([], new StringReader("{"), output);

        Assert.Equal(0, exitCode);
        Assert.Equal("{}", output.ToString());
        Assert.Empty(sink.ForwardedLines);
    }

    [Theory]
    [InlineData("{", "Payload length: 1.")]
    [InlineData("", "Payload length: 0 (empty stdin).")]
    public async Task FailedPayloadIsSavedAndLinkedFromErrorLog(
        string payload,
        string expectedPayloadDescription)
    {
        string folder = Path.Combine(
            Path.GetTempPath(),
            "HarnessSpyFailedPayloadTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            RecordingSink sink = new();
            FileHookDiagnostics diagnostics = new(folder);
            HookForwarder forwarder = new(sink, diagnostics);
            StringWriter output = new();

            int exitCode = await forwarder.RunAsync([], new StringReader(payload), output);

            string payloadFile = Assert.Single(
                Directory.EnumerateFiles(Path.Combine(folder, "Payloads"), "*.json"));
            string errorLog = await File.ReadAllTextAsync(
                Path.Combine(folder, FileHookDiagnostics.ErrorLogFileName));

            Assert.Equal(0, exitCode);
            Assert.Equal("{}", output.ToString());
            Assert.Empty(sink.ForwardedLines);
            Assert.Equal(payload, await File.ReadAllTextAsync(payloadFile));
            Assert.Contains($"Failed payload file: {payloadFile}.", errorLog, StringComparison.Ordinal);
            Assert.Contains(expectedPayloadDescription, errorLog, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task FileDiagnosticsPrefixesPayloadFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), "CursorSpyHookDiagnosticsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            FileHookDiagnostics diagnostics = new(folder);
            await diagnostics.SavePayloadAsync(
                "conversation-1",
                "sessionStart",
                """{"hook_event_name":"sessionStart","conversation_id":"conversation-1"}""",
                CancellationToken.None);

            string payloadsFolder = Path.Combine(folder, "Payloads");
            Assert.True(Directory.Exists(payloadsFolder));
            string file = Assert.Single(Directory.EnumerateFiles(payloadsFolder, "*.json"));
            Assert.StartsWith(FileHookDiagnostics.PayloadFilePrefix, Path.GetFileName(file), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task SpawningProcessIsCapturedInPipeEnvelopeAndSavedPayload()
    {
        const string payload =
            """{"hook_event_name":"stop","conversation_id":"conversation-1","workspace_roots":["C:\\Repo"]}""";
        string folder = Path.Combine(
            Path.GetTempPath(),
            "HarnessSpySpawningProcessTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            RecordingSink sink = new();
            FileHookDiagnostics diagnostics = new(folder);
            HookForwarder forwarder = new(
                sink,
                new HookProcessOptions(ProviderProfile.Cursor),
                diagnostics,
                runtimeDetector: null,
                environment: new Dictionary<string, string?>(),
                spawningProcessResolver: new FixedSpawningProcessResolver(1234, "node"));

            await forwarder.RunAsync([], new StringReader(payload), new StringWriter());

            string forwarded = Assert.Single(sink.ForwardedLines);
            using JsonDocument envelope = JsonDocument.Parse(forwarded);
            JsonElement root = envelope.RootElement;
            Assert.Equal(1234, root.GetProperty("spawningProcessId").GetInt32());
            Assert.Equal("node", root.GetProperty("spawningProcessName").GetString());

            string payloadFile = Assert.Single(
                Directory.EnumerateFiles(Path.Combine(folder, "Payloads"), "*.json"));
            using JsonDocument saved = JsonDocument.Parse(await File.ReadAllTextAsync(payloadFile));
            JsonElement savedRoot = saved.RootElement;
            Assert.Equal(1234, savedRoot.GetProperty("spawningProcessId").GetInt32());
            Assert.Equal("node", savedRoot.GetProperty("spawningProcessName").GetString());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task<string?> ReadOneLineAsync(NamedPipeServerStream server)
    {
        await server.WaitForConnectionAsync();
        using StreamReader reader = new(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return await reader.ReadLineAsync();
    }

    private sealed class RecordingSink : IHookPayloadSink
    {
        public List<string> ForwardedLines { get; } = [];

        public Task ForwardAsync(ReadOnlyMemory<byte> payloadLine, CancellationToken cancellationToken)
        {
            ForwardedLines.Add(Encoding.UTF8.GetString(payloadLine.Span));
            return Task.CompletedTask;
        }
    }

    private sealed class FailingSink : IHookPayloadSink
    {
        public Task ForwardAsync(ReadOnlyMemory<byte> payloadLine, CancellationToken cancellationToken)
            => Task.FromException(new IOException("Simulated pipe write failure."));
    }

    private sealed class FixedSpawningProcessResolver(int processId, string processName)
        : ISpawningProcessResolver
    {
        public SpawningProcessInfo Resolve() => new(processId, processName);
    }
}

public sealed class ProjectionTests
{
    [Fact]
    public void WorkspaceAndSessionProjectionHandlesRootVariants()
    {
        MainWindowViewModel viewModel = new();

        viewModel.AddObservation(ParsePayload("""{"hook_event_name":"workspaceOpen","workspace_roots":["C:\\Repo\\"],"cursor_version":"test"}"""));
        viewModel.AddObservation(ParsePayload("""{"hook_event_name":"sessionStart","conversation_id":"conversation-1","session_id":"conversation-1","workspace_roots":["c:\\repo"],"composer_mode":"agent"}"""));
        viewModel.AddObservation(ParsePayload("""{"hook_event_name":"beforeSubmitPrompt","conversation_id":"conversation-1","workspace_roots":["D:\\Other"],"prompt":"stay put"}"""));
        viewModel.AddObservation(ParsePayload("""{"hook_event_name":"sessionStart","conversation_id":"conversation-2","workspace_roots":[],"composer_mode":"ask"}"""));
        viewModel.AddObservation(ParsePayload("""{"hook_event_name":"afterAgentResponse","conversation_id":"conversation-3","text":"missing workspace_roots"}"""));
        viewModel.AddObservation(ParsePayload("""{"hook_event_name":"preToolUse","workspace_roots":["C:\\Repo"],"tool_name":"Shell"}"""));

        TreeNodeViewModel repoWorkspace = Assert.Single(
            viewModel.Roots,
            root => root.Header.Equals("C:\\Repo", StringComparison.OrdinalIgnoreCase));
        TreeNodeViewModel noWorkspace = Assert.Single(viewModel.Roots, root => root.Header == "No workspace");
        TreeNodeViewModel unknownWorkspace = Assert.Single(viewModel.Roots, root => root.Header == "Unknown workspace");

        TreeNodeViewModel sessionOne = Assert.Single(repoWorkspace.Children, child => child.Header == "stay put");
        Assert.Equal(2, sessionOne.Children.Count);
        Assert.Contains(repoWorkspace.Children, child => child.Kind == TreeNodeKind.Observation && child.Header.StartsWith("workspaceOpen", StringComparison.Ordinal));
        Assert.Contains(repoWorkspace.Children, child => child.Header == "Unknown session");
        Assert.Single(noWorkspace.Children, child => child.Header == "conversation-2");
        Assert.Single(unknownWorkspace.Children, child => child.Header == "conversation-3");
        Assert.DoesNotContain(viewModel.Roots, root => root.Header.Equals("D:\\Other", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SessionIdFallbackAndRepeatedHookNamesRemainDistinct()
    {
        MainWindowViewModel viewModel = new();

        viewModel.AddObservation(ParsePayload("""{"hook_event_name":"postToolUse","session_id":"session-only","workspace_roots":["C:\\Repo"],"tool_name":"Shell"}"""));
        viewModel.AddObservation(ParsePayload("""{"hook_event_name":"postToolUse","session_id":"session-only","workspace_roots":["C:\\Repo"],"tool_name":"Shell","duration":25}"""));

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);

        Assert.Equal("session-only", session.Header);
        Assert.Equal(2, session.Children.Count);
        Assert.All(session.Children, child => Assert.StartsWith("postToolUse", child.Header, StringComparison.Ordinal));
    }

    [Fact]
    public void TabHooksStayAtSessionLevelAndShowFilename()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}"""));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"prompt":"edit Foo"}"""));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeTabFileRead","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"file_path":"C:\\Repo\\Foo.cs","content":"class Foo {}"}"""));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterTabFileEdit","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"file_path":"C:\\Repo\\Bar.cs","edits":[]}"""));

        TreeNodeViewModel session = Assert.Single(Assert.Single(viewModel.Roots).Children);
        TreeNodeViewModel turn = Assert.Single(session.Children, child => child.Kind == TreeNodeKind.Generation);
        Assert.Single(turn.Children);
        Assert.Contains(session.Children, child => child.Header == "beforeTabFileRead · Foo.cs");
        Assert.Contains(session.Children, child => child.Header == "afterTabFileEdit · Bar.cs");
        Assert.Equal(SessionStatus.Active, session.Status);
    }

    [Fact]
    public void GenerationIdGroupsTurnEventsAndSummarizesActivity()
    {
        MainWindowViewModel viewModel = new();

        // Session-scoped event (no generation_id) stays directly under the session.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"],"composer_mode":"agent"}""",
            "2026-08-20T12:00:00Z"));

        // Turn 1: prompt, one shell command, one file edit, then stop.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"prompt":"add logging to Foo"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeShellExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"command":"git status"}""",
            "2026-08-20T12:00:02Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterFileEdit","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"file_path":"Foo.cs"}""",
            "2026-08-20T12:00:03Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"stop","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"]}""",
            "2026-08-20T12:00:05Z"));

        // Turn 2: a second prompt gets its own generation node.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"conv-1","generation_id":"gen-2","workspace_roots":["C:\\Repo"],"prompt":"now write a test"}""",
            "2026-08-20T12:00:06Z"));

        // sessionEnd carries a generation_id but must stay beside sessionStart at
        // the session level, not inside a turn.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionEnd","conversation_id":"conv-1","generation_id":"gen-2","workspace_roots":["C:\\Repo"],"reason":"user_close"}""",
            "2026-08-20T12:00:07Z"));

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);

        Assert.Equal("add logging to Foo", session.Header);
        Assert.Single(session.Children, child => child.Kind == TreeNodeKind.Observation && child.Header.StartsWith("sessionStart", StringComparison.Ordinal));
        Assert.Single(session.Children, child => child.Kind == TreeNodeKind.Observation && child.Header.StartsWith("sessionEnd", StringComparison.Ordinal));

        TreeNodeViewModel turn1 = Assert.Single(session.Children, child => child.Kind == TreeNodeKind.Generation && child.TurnNumber == 1);
        TreeNodeViewModel turn2 = Assert.Single(session.Children, child => child.Kind == TreeNodeKind.Generation && child.TurnNumber == 2);

        Assert.Equal(4, turn1.Children.Count);
        Assert.Equal("Turn 1 · add logging to Foo", turn1.Header);
        Assert.Equal("1 edit \u00b7 1 cmd \u00b7 4s 000ms", turn1.Summary);

        // The gen-2 sessionEnd did not attach under turn 2.
        Assert.Single(turn2.Children);
        Assert.Equal("Turn 2 · now write a test", turn2.Header);
    }

    [Fact]
    public void OccurrenceHeaderSurfacesInterestingFieldsPerHook()
    {
        Assert.Equal(
            "beforeShellExecution · git status",
            ParsePayload("""{"hook_event_name":"beforeShellExecution","command":"git status","cwd":"/x"}""").OccurrenceHeader);

        Assert.Equal(
            "preToolUse · Read (claude) · C:\\repo\\Foo.cs",
            ParsePayload("""{"hook_event_name":"preToolUse","tool_name":"Read","model":"claude","tool_input":{"file_path":"C:\\repo\\Foo.cs"}}""").OccurrenceHeader);

        Assert.Equal(
            "postToolUse · Shell (gpt-5)",
            ParsePayload("""{"hook_event_name":"postToolUse","tool_name":"Shell","model":"gpt-5"}""").OccurrenceHeader);

        Assert.Equal(
            "stop · completed · in 1.18M · out 8.15k · cache r 1.01M · cache w 173.96k",
            ParsePayload("""{"hook_event_name":"stop","status":"completed","input_tokens":1180993,"output_tokens":8146,"cache_read_tokens":1007022,"cache_write_tokens":173957}""").OccurrenceHeader);

        Assert.Equal(
            "afterAgentResponse · in 500 · out 250",
            ParsePayload("""{"hook_event_name":"afterAgentResponse","input_tokens":500,"output_tokens":250}""").OccurrenceHeader);

        Assert.Equal(
            "sessionEnd · user_close · ok",
            ParsePayload("""{"hook_event_name":"sessionEnd","reason":"user_close","final_status":"ok"}""").OccurrenceHeader);

        Assert.Equal(
            "postToolUseFailure · Shell · timeout · Command timed out after 30s",
            ParsePayload("""{"hook_event_name":"postToolUseFailure","tool_name":"Shell","failure_type":"timeout","error_message":"Command timed out after 30s"}""").OccurrenceHeader);

        Assert.Equal(
            "postToolUseFailure · Shell · timeout · First line only...",
            ParsePayload("""{"hook_event_name":"postToolUseFailure","tool_name":"Shell","failure_type":"timeout","error_message":"First line only\nSecond line hidden"}""").OccurrenceHeader);

        Assert.Equal(
            "beforeMCPExecution · dotnet-dstrings · get_duplicated_strings",
            ParsePayload("""{"hook_event_name":"beforeMCPExecution","mcp_server_name":"dotnet-dstrings","tool_name":"get_duplicated_strings","command":"dotnet-dstrings"}""").OccurrenceHeader);

        Assert.Equal(
            "afterMCPExecution · dotnet-dstrings · get_duplicated_strings · 500ms",
            ParsePayload("""{"hook_event_name":"afterMCPExecution","mcp_server_name":"dotnet-dstrings","tool_name":"get_duplicated_strings","duration":500}""").OccurrenceHeader);

        Assert.Equal(
            "beforeTabFileRead · Foo.cs",
            ParsePayload("""{"hook_event_name":"beforeTabFileRead","file_path":"C:\\repo\\Foo.cs","content":"class Foo {}"}""").OccurrenceHeader);

        Assert.Equal(
            "afterTabFileEdit · Foo.cs",
            ParsePayload("""{"hook_event_name":"afterTabFileEdit","file_path":"C:\\repo\\Foo.cs","edits":[{"old_string":"Foo","new_string":"Bar"}]}""").OccurrenceHeader);
    }

    [Fact]
    public void TabHookDetailFieldsShowContentAndEditsWithoutRepeatingFilename()
    {
        HookObservation read = ParsePayload(
            """{"hook_event_name":"beforeTabFileRead","file_path":"C:\\repo\\Foo.cs","content":"class Foo {}"}""");

        Assert.Equal(["content"], read.DetailFields.Select(field => field.Name).ToArray());
        Assert.Equal("class Foo {}", read.DetailFields.Single(field => field.Name == "content").Value);

        HookObservation edit = ParsePayload(
            """
            {
              "hook_event_name": "afterTabFileEdit",
              "file_path": "C:\\repo\\Foo.cs",
              "edits": [
                {
                  "old_string": "Foo",
                  "new_string": "Bar",
                  "range": {
                    "start_line_number": 10,
                    "start_column": 5,
                    "end_line_number": 10,
                    "end_column": 20
                  },
                  "old_line": "class Foo {}",
                  "new_line": "class Bar {}"
                }
              ]
            }
            """);

        Assert.Equal(
            [
                "edits[0].old_string",
                "edits[0].new_string",
                "edits[0].range.start_line_number",
                "edits[0].range.start_column",
                "edits[0].range.end_line_number",
                "edits[0].range.end_column",
                "edits[0].old_line",
                "edits[0].new_line"
            ],
            edit.DetailFields.Select(field => field.Name).ToArray());
        Assert.Equal("Foo", edit.DetailFields.Single(field => field.Name == "edits[0].old_string").Value);
        Assert.Equal("10", edit.DetailFields.Single(field => field.Name == "edits[0].range.start_line_number").Value);
    }

    [Fact]
    public void WorkspaceOpenDetailFieldsSurfaceInInspectorTable()
    {
        HookObservation observation = ParsePayload(
            """{"hook_event_name":"workspaceOpen","cursor_version":"1.7.2","user_email":"dev@example.com","workspace_roots":["C:\\Repo","D:\\Other"]}""");

        Assert.Equal(
            ["cursor_version", "user_email", "workspace_roots[0]", "workspace_roots[1]"],
            observation.DetailFields.Select(field => field.Name).ToArray());
        Assert.Equal("1.7.2", observation.DetailFields.Single(field => field.Name == "cursor_version").Value);
        Assert.Equal("dev@example.com", observation.DetailFields.Single(field => field.Name == "user_email").Value);
        Assert.Equal(@"C:\Repo", observation.DetailFields.Single(field => field.Name == "workspace_roots[0]").Value);
        Assert.Equal(@"D:\Other", observation.DetailFields.Single(field => field.Name == "workspace_roots[1]").Value);
    }

    [Fact]
    public void McpExecutionDetailFieldsSurfaceInInspectorTable()
    {
        HookObservation before = ParsePayload(
            """{"hook_event_name":"beforeMCPExecution","mcp_server_name":"dotnet-dstrings","tool_name":"get_duplicated_strings","command":"dotnet-dstrings","tool_input":"{\"dumpPath\":\"C:\\\\dump.dmp\"}"}""");

        Assert.Equal(
            ["mcp_server_name", "tool_name", "command", "tool_input.dumpPath"],
            before.DetailFields.Select(field => field.Name).ToArray());
        Assert.Equal(
            @"C:\dump.dmp",
            before.DetailFields.Single(field => field.Name == "tool_input.dumpPath").Value);

        HookObservation after = ParsePayload(
            """{"hook_event_name":"afterMCPExecution","mcp_server_name":"dotnet-dstrings","tool_name":"get_duplicated_strings","command":"dotnet-dstrings","tool_input":"{\"dumpPath\":\"C:\\\\dump.dmp\"}","result_json":"{\"isError\":false}","duration":500}""");

        Assert.Equal(
            ["duration", "mcp_server_name", "tool_name", "command", "tool_input.dumpPath", "result_json.isError"],
            after.DetailFields.Select(field => field.Name).ToArray());
        Assert.Equal(
            @"C:\dump.dmp",
            after.DetailFields.Single(field => field.Name == "tool_input.dumpPath").Value);
        Assert.Equal(
            "false",
            after.DetailFields.Single(field => field.Name == "result_json.isError").Value);
    }

    [Theory]
    [InlineData(456, "456ms")]
    [InlineData(4500, "4s 500ms")]
    [InlineData(65000, "1m 05s 000ms")]
    [InlineData(3661500, "1h 01m 01s 500ms")]
    [InlineData(0, "0ms")]
    public void FormatDurationDropsLeadingZeroUnits(double milliseconds, string expected)
    {
        Assert.Equal(expected, HookObservation.FormatDuration(TimeSpan.FromMilliseconds(milliseconds)));
    }

    private static HookObservation ParsePayload(string payloadJson, string observedAtUtc = "2026-08-20T12:00:00Z")
    {
        string envelope = $$"""
            {
              "ingressVersion": 1,
              "eventId": "{{Guid.NewGuid()}}",
              "observedAtUtc": "{{observedAtUtc}}",
              "payload": {{payloadJson}}
            }
            """;

        Assert.True(HookObservation.TryParse(envelope, out HookObservation? observation));
        return Assert.IsType<HookObservation>(observation);
    }
}

public sealed class NestingAndParallelWaveTests
{
    [Fact]
    public void PostToolUseNestsUnderMatchingPreToolUse()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}""",
            "2026-08-20T12:00:00Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"tool-A"}""",
            "2026-08-20T12:00:01Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"tool-A","duration":50}""",
            "2026-08-20T12:00:02Z"));

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel generation = session.Children.Single(c => c.Kind == TreeNodeKind.Generation);

        // The generation should have 1 child (the preToolUse), not 2.
        TreeNodeViewModel preNode = Assert.Single(generation.Children);
        Assert.Equal("preToolUse", preNode.Observation!.HookEventName);

        // The postToolUse should be nested under the preToolUse.
        TreeNodeViewModel postNode = Assert.Single(preNode.Children);
        Assert.Equal("postToolUse", postNode.Observation!.HookEventName);

        // Duration badge should be set on the pre node.
        Assert.Equal("50ms", preNode.Summary);
        Assert.False(preNode.IsExpanded);
    }

    [Fact]
    public void PostToolUseNestsWhenToolUseIdIsReusedAcrossTools()
    {
        MainWindowViewModel viewModel = new();
        const string sharedId = "call-batch\nfc-response";

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"WebSearch","tool_use_id":"call-batch\nfc-response","tool_input":{"search_term":"Cursor hooks"}}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Write","tool_use_id":"call-batch\nfc-response","tool_input":{"file_path":"hooks.txt","content":"result"}}""",
            "2026-08-20T12:00:02Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Write","tool_use_id":"call-batch\nfc-response","tool_input":{"file_path":"hooks.txt","content":"result"},"duration":100}""",
            "2026-08-20T12:00:03Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"WebSearch","tool_use_id":"call-batch\nfc-response","tool_input":{"search_term":"Cursor hooks"},"duration":3000}""",
            "2026-08-20T12:00:04Z"));

        TreeNodeViewModel generation = Assert.Single(
            Assert.Single(Assert.Single(viewModel.Roots).Children).Children,
            child => child.Kind == TreeNodeKind.Generation);
        TreeNodeViewModel wave = Assert.Single(generation.Children);
        Assert.Equal(TreeNodeKind.ParallelWave, wave.Kind);

        TreeNodeViewModel webSearchPre = Assert.Single(
            wave.Children,
            child => child.Observation?.ToolName == "WebSearch");
        TreeNodeViewModel writePre = Assert.Single(
            wave.Children,
            child => child.Observation?.ToolName == "Write");

        Assert.Equal(sharedId, webSearchPre.Observation!.ToolUseId);
        Assert.Equal("WebSearch", Assert.Single(webSearchPre.Children).Observation!.ToolName);
        Assert.Equal("Write", Assert.Single(writePre.Children).Observation!.ToolName);
    }

    [Fact]
    public void PostToolUseMatchesCanonicalInputWhenPropertyOrderDiffers()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}""",
            "2026-08-20T12:00:00Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"tool-canonical","tool_input":{"path":"C:\\Repo\\a.cs","options":{"case_sensitive":false,"patterns":["x","y"]}}}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"tool-canonical","tool_input":{"options":{"patterns":["x","y"],"case_sensitive":false},"path":"C:\\Repo\\a.cs"},"duration":25}""",
            "2026-08-20T12:00:01.025Z"));

        TreeNodeViewModel generation = GetOnlyGeneration(viewModel);
        TreeNodeViewModel preNode = Assert.Single(generation.Children);
        Assert.Equal("postToolUse", Assert.Single(preNode.Children).Observation!.HookEventName);
    }

    [Fact]
    public void AmbiguousIdenticalToolCallsRemainUncorrelated()
    {
        MainWindowViewModel viewModel = new();
        const string sharedId = "shared-call";
        const string input = """{"command":"dotnet test"}""";

        viewModel.AddObservation(ParsePayload(
            $$"""{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"{{sharedId}}","tool_input":{{input}}}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(ParsePayload(
            $$"""{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"{{sharedId}}","tool_input":{{input}}}""",
            "2026-08-20T12:00:01.100Z"));
        viewModel.AddObservation(ParsePayload(
            $$"""{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"{{sharedId}}","tool_input":{{input}},"duration":100}""",
            "2026-08-20T12:00:01.200Z"));

        TreeNodeViewModel generation = GetOnlyGeneration(viewModel);
        Assert.Equal(3, generation.Children.Count);
        Assert.Equal(2, generation.Children.Count(
            child => child.Observation?.HookEventName == "preToolUse"));
        Assert.Single(
            generation.Children,
            child => child.Observation?.HookEventName == "postToolUse");
        Assert.All(
            generation.Children.Where(child => child.Observation?.HookEventName == "preToolUse"),
            child => Assert.Empty(child.Children));
    }

    [Fact]
    public void PostToolUseFailureNestsUnderMatchingPreToolUse()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}""",
            "2026-08-20T12:00:00Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"tool-B"}""",
            "2026-08-20T12:00:01Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUseFailure","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"tool-B","failure_type":"timeout","duration":30000}""",
            "2026-08-20T12:00:31Z"));

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel generation = session.Children.Single(c => c.Kind == TreeNodeKind.Generation);

        TreeNodeViewModel preNode = Assert.Single(generation.Children);
        TreeNodeViewModel failNode = Assert.Single(preNode.Children);
        Assert.Equal("postToolUseFailure", failNode.Observation!.HookEventName);
    }

    [Fact]
    public void PostToolUseFailureMatchesReusedIdByInput()
    {
        MainWindowViewModel viewModel = new();

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"shared-shell","tool_input":{"command":"dotnet test"}}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"shared-shell","tool_input":{"command":"dotnet build"}}""",
            "2026-08-20T12:00:01.100Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUseFailure","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"shared-shell","tool_input":{"command":"dotnet build"},"failure_type":"error","error_message":"build failed","duration":400}""",
            "2026-08-20T12:00:01.500Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"shared-shell","tool_input":{"command":"dotnet test"},"duration":1000}""",
            "2026-08-20T12:00:02Z"));

        TreeNodeViewModel[] calls = GetToolCallNodes(GetOnlyGeneration(viewModel)).ToArray();
        TreeNodeViewModel build = Assert.Single(
            calls,
            call => call.Observation!.Payload
                .GetProperty("tool_input")
                .GetProperty("command")
                .GetString() == "dotnet build");
        TreeNodeViewModel test = Assert.Single(
            calls,
            call => call.Observation!.Payload
                .GetProperty("tool_input")
                .GetProperty("command")
                .GetString() == "dotnet test");

        Assert.Equal(
            "postToolUseFailure",
            Assert.Single(build.Children).Observation!.HookEventName);
        Assert.Equal(
            "postToolUse",
            Assert.Single(test.Children).Observation!.HookEventName);
    }

    [Fact]
    public void FailedShellCallDoesNotCaptureRetryCompletion()
    {
        MainWindowViewModel viewModel = new();

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"shared-shell","tool_input":{"command":"dotnet test"}}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeShellExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"command":"dotnet test"}""",
            "2026-08-20T12:00:01.100Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUseFailure","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"shared-shell","tool_input":{"command":"dotnet test"},"failure_type":"timeout","duration":1000}""",
            "2026-08-20T12:00:02Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"shared-shell","tool_input":{"command":"dotnet test"}}""",
            "2026-08-20T12:00:03Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeShellExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"command":"dotnet test"}""",
            "2026-08-20T12:00:03.100Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterShellExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"command":"dotnet test","output":"passed","duration":500}""",
            "2026-08-20T12:00:03.600Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"shared-shell","tool_input":{"command":"dotnet test"},"duration":600}""",
            "2026-08-20T12:00:03.700Z"));

        TreeNodeViewModel generation = GetOnlyGeneration(viewModel);
        TreeNodeViewModel[] calls = GetToolCallNodes(generation).ToArray();
        Assert.Equal(2, calls.Length);

        TreeNodeViewModel failed = Assert.Single(
            calls,
            call => call.Children.Any(
                child => child.Observation?.HookEventName == "postToolUseFailure"));
        TreeNodeViewModel retry = Assert.Single(
            calls,
            call => call.Children.Any(
                child => child.Observation?.HookEventName == "postToolUse"));

        TreeNodeViewModel failedBefore = Assert.Single(
            failed.Children,
            child => child.Observation?.HookEventName == "beforeShellExecution");
        Assert.Empty(failedBefore.Children);

        TreeNodeViewModel retryBefore = Assert.Single(
            retry.Children,
            child => child.Observation?.HookEventName == "beforeShellExecution");
        Assert.Equal(
            "afterShellExecution",
            Assert.Single(retryBefore.Children).Observation!.HookEventName);
    }

    [Fact]
    public void ShellInnerHooksNestUnderPreToolUse()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}""",
            "2026-08-20T12:00:00Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"tool-C"}""",
            "2026-08-20T12:00:01Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeShellExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"command":"git status"}""",
            "2026-08-20T12:00:01.100Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterShellExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"command":"git status","output":"clean","duration":500}""",
            "2026-08-20T12:00:01.600Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"tool-C","duration":600}""",
            "2026-08-20T12:00:01.700Z"));

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel generation = session.Children.Single(c => c.Kind == TreeNodeKind.Generation);

        // Only the preToolUse at generation level.
        TreeNodeViewModel preNode = Assert.Single(generation.Children);
        Assert.Equal("preToolUse", preNode.Observation!.HookEventName);

        // preToolUse has 2 children: beforeShellExecution and postToolUse.
        Assert.Equal(2, preNode.Children.Count);
        TreeNodeViewModel beforeShell = preNode.Children[0];
        TreeNodeViewModel postTool = preNode.Children[1];
        Assert.Equal("beforeShellExecution", beforeShell.Observation!.HookEventName);
        Assert.Equal("postToolUse", postTool.Observation!.HookEventName);

        // afterShellExecution is nested under beforeShellExecution.
        TreeNodeViewModel afterShell = Assert.Single(beforeShell.Children);
        Assert.Equal("afterShellExecution", afterShell.Observation!.HookEventName);
    }

    [Fact]
    public void ConcurrentShellHooksMatchByCommandInsteadOfArrivalOrder()
    {
        MainWindowViewModel viewModel = new();

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"shared-shell","tool_input":{"command":"dotnet test","working_directory":"C:\\Repo"}}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"shared-shell","tool_input":{"command":"git status","working_directory":"C:\\Repo"}}""",
            "2026-08-20T12:00:01.100Z"));

        // The specialized starts arrive in the opposite order from the generic
        // preToolUse nodes. Tool-name-only matching would swap their parents.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeShellExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"command":"dotnet test","cwd":"C:\\Repo","sandbox":false}""",
            "2026-08-20T12:00:01.200Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeShellExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"command":"git status","cwd":"C:\\Repo","sandbox":false}""",
            "2026-08-20T12:00:01.300Z"));

        // Completions are not LIFO: dotnet test completes before git status.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterShellExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"command":"dotnet test","output":"passed","cwd":"C:\\Repo","sandbox":false,"duration":500}""",
            "2026-08-20T12:00:01.700Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterShellExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"command":"git status","output":"clean","cwd":"C:\\Repo","sandbox":false,"duration":700}""",
            "2026-08-20T12:00:02Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"shared-shell","tool_input":{"working_directory":"C:\\Repo","command":"git status"},"duration":900}""",
            "2026-08-20T12:00:02.100Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"shared-shell","tool_input":{"working_directory":"C:\\Repo","command":"dotnet test"},"duration":1200}""",
            "2026-08-20T12:00:02.200Z"));

        TreeNodeViewModel generation = GetOnlyGeneration(viewModel);
        TreeNodeViewModel[] calls = GetToolCallNodes(generation).ToArray();
        Assert.Equal(2, calls.Length);

        AssertShellCall(calls, "dotnet test", "passed");
        AssertShellCall(calls, "git status", "clean");
    }

    [Fact]
    public void ConcurrentReadHooksMatchByFilePathInsteadOfArrivalOrder()
    {
        MainWindowViewModel viewModel = new();

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"shared-read","tool_input":{"file_path":"C:\\Repo\\baseline_report.txt"}}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"shared-read","tool_input":{"file_path":"C:\\Repo\\after_report.txt"}}""",
            "2026-08-20T12:00:01.100Z"));

        // beforeReadFile arrives in the opposite order from the preToolUse nodes.
        // Tool-name + arrival-order matching would swap their parents.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeReadFile","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"file_path":"C:\\Repo\\after_report.txt"}""",
            "2026-08-20T12:00:01.200Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeReadFile","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"file_path":"C:\\Repo\\baseline_report.txt"}""",
            "2026-08-20T12:00:01.300Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"shared-read","tool_input":{"file_path":"C:\\Repo\\baseline_report.txt"},"duration":10}""",
            "2026-08-20T12:00:01.400Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"shared-read","tool_input":{"file_path":"C:\\Repo\\after_report.txt"},"duration":20}""",
            "2026-08-20T12:00:01.500Z"));

        TreeNodeViewModel generation = GetOnlyGeneration(viewModel);
        TreeNodeViewModel[] calls = GetToolCallNodes(generation).ToArray();
        Assert.Equal(2, calls.Length);

        AssertReadCall(calls, "C:\\Repo\\baseline_report.txt");
        AssertReadCall(calls, "C:\\Repo\\after_report.txt");
    }

    [Fact]
    public void SoleReadCaptureNestsBeforeReadFileWithoutFilePath()
    {
        MainWindowViewModel viewModel = new();

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"read-1","tool_input":{"file_path":"C:\\Repo\\only.cs"}}""",
            "2026-08-20T12:00:01Z"));

        // No file_path on the hook: it should still fall back onto the single
        // in-flight Read rather than being orphaned.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeReadFile","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"]}""",
            "2026-08-20T12:00:01.100Z"));

        TreeNodeViewModel generation = GetOnlyGeneration(viewModel);
        TreeNodeViewModel preNode = Assert.Single(generation.Children);
        Assert.Equal("beforeReadFile", Assert.Single(preNode.Children).Observation!.HookEventName);
    }

    [Fact]
    public void ConcurrentEditHooksMatchByFilePathInsteadOfArrivalOrder()
    {
        MainWindowViewModel viewModel = new();

        // A Write, a StrReplace (path), and an EditNotebook (target_notebook)
        // are all in flight at once.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Write","tool_use_id":"shared-edit","tool_input":{"file_path":"C:\\Repo\\a.cs"}}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"StrReplace","tool_use_id":"shared-edit","tool_input":{"path":"C:\\Repo\\b.cs"}}""",
            "2026-08-20T12:00:01.100Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"EditNotebook","tool_use_id":"shared-edit","tool_input":{"target_notebook":"C:\\Repo\\c.ipynb"}}""",
            "2026-08-20T12:00:01.200Z"));

        // afterFileEdit hooks arrive out of order relative to the pre nodes.
        // Tool-name + arrival-order matching would swap their parents.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterFileEdit","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"file_path":"C:\\Repo\\c.ipynb"}""",
            "2026-08-20T12:00:01.300Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterFileEdit","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"file_path":"C:\\Repo\\a.cs"}""",
            "2026-08-20T12:00:01.400Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterFileEdit","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"file_path":"C:\\Repo\\b.cs"}""",
            "2026-08-20T12:00:01.500Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Write","tool_use_id":"shared-edit","tool_input":{"file_path":"C:\\Repo\\a.cs"},"duration":10}""",
            "2026-08-20T12:00:01.600Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"StrReplace","tool_use_id":"shared-edit","tool_input":{"path":"C:\\Repo\\b.cs"},"duration":20}""",
            "2026-08-20T12:00:01.700Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"EditNotebook","tool_use_id":"shared-edit","tool_input":{"target_notebook":"C:\\Repo\\c.ipynb"},"duration":30}""",
            "2026-08-20T12:00:01.800Z"));

        TreeNodeViewModel generation = GetOnlyGeneration(viewModel);
        TreeNodeViewModel[] calls = GetToolCallNodes(generation).ToArray();
        Assert.Equal(3, calls.Length);

        AssertFileEditCall(calls, "file_path", "C:\\Repo\\a.cs");
        AssertFileEditCall(calls, "path", "C:\\Repo\\b.cs");
        AssertFileEditCall(calls, "target_notebook", "C:\\Repo\\c.ipynb");
    }

    [Fact]
    public void SoleEditCaptureNestsAfterFileEditWithoutFilePath()
    {
        MainWindowViewModel viewModel = new();

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Write","tool_use_id":"write-1","tool_input":{"file_path":"C:\\Repo\\only.cs"}}""",
            "2026-08-20T12:00:01Z"));

        // No file_path on the hook: it should still fall back onto the single
        // in-flight edit rather than being orphaned.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterFileEdit","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"]}""",
            "2026-08-20T12:00:01.100Z"));

        TreeNodeViewModel generation = GetOnlyGeneration(viewModel);
        TreeNodeViewModel preNode = Assert.Single(generation.Children);
        Assert.Equal("afterFileEdit", Assert.Single(preNode.Children).Observation!.HookEventName);
    }

    [Fact]
    public void McpInnerHooksNestUnderPreToolUse()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}""",
            "2026-08-20T12:00:00Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"MCP:get_duplicated_strings","tool_use_id":"tool-D"}""",
            "2026-08-20T12:00:01Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeMCPExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"mcp_server_name":"dotnet-dstrings","tool_name":"get_duplicated_strings","command":"dotnet-dstrings","tool_input":"{\"dumpPath\":\"C:\\\\dump.dmp\"}"}""",
            "2026-08-20T12:00:01.100Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterMCPExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"mcp_server_name":"dotnet-dstrings","tool_name":"get_duplicated_strings","tool_input":"{\"dumpPath\":\"C:\\\\dump.dmp\"}","result_json":"{\"isError\":false}","duration":500}""",
            "2026-08-20T12:00:01.600Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"MCP:get_duplicated_strings","tool_use_id":"tool-D","duration":600}""",
            "2026-08-20T12:00:01.700Z"));

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel generation = session.Children.Single(c => c.Kind == TreeNodeKind.Generation);

        TreeNodeViewModel preNode = Assert.Single(generation.Children);
        Assert.Equal("preToolUse", preNode.Observation!.HookEventName);

        Assert.Equal(2, preNode.Children.Count);
        TreeNodeViewModel beforeMcp = preNode.Children[0];
        TreeNodeViewModel postTool = preNode.Children[1];
        Assert.Equal("beforeMCPExecution", beforeMcp.Observation!.HookEventName);
        Assert.Equal("postToolUse", postTool.Observation!.HookEventName);

        TreeNodeViewModel afterMcp = Assert.Single(beforeMcp.Children);
        Assert.Equal("afterMCPExecution", afterMcp.Observation!.HookEventName);
    }

    [Fact]
    public void ConcurrentMcpHooksMatchByServerToolAndInput()
    {
        MainWindowViewModel viewModel = new();

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"MCP:get_duplicated_strings","tool_use_id":"shared-mcp","mcp_server_name":"dotnet-dstrings","tool_input":{"dumpPath":"C:\\a.dmp","countThreshold":10}}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"MCP:get_duplicated_strings","tool_use_id":"shared-mcp","mcp_server_name":"dotnet-dstrings","tool_input":{"dumpPath":"C:\\b.dmp","countThreshold":20}}""",
            "2026-08-20T12:00:01.100Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeMCPExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"mcp_server_name":"dotnet-dstrings","tool_name":"get_duplicated_strings","command":"dotnet-dstrings","tool_input":"{\"countThreshold\":10,\"dumpPath\":\"C:\\\\a.dmp\"}"}""",
            "2026-08-20T12:00:01.200Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeMCPExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"mcp_server_name":"dotnet-dstrings","tool_name":"get_duplicated_strings","command":"dotnet-dstrings","tool_input":"{\"dumpPath\":\"C:\\\\b.dmp\",\"countThreshold\":20}"}""",
            "2026-08-20T12:00:01.300Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterMCPExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"mcp_server_name":"dotnet-dstrings","tool_name":"get_duplicated_strings","tool_input":"{\"dumpPath\":\"C:\\\\a.dmp\",\"countThreshold\":10}","result_json":"{\"target\":\"a\"}","duration":500}""",
            "2026-08-20T12:00:01.700Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterMCPExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"mcp_server_name":"dotnet-dstrings","tool_name":"get_duplicated_strings","tool_input":"{\"countThreshold\":20,\"dumpPath\":\"C:\\\\b.dmp\"}","result_json":"{\"target\":\"b\"}","duration":700}""",
            "2026-08-20T12:00:02Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"MCP:get_duplicated_strings","tool_use_id":"shared-mcp","mcp_server_name":"dotnet-dstrings","tool_input":{"countThreshold":20,"dumpPath":"C:\\b.dmp"},"duration":900}""",
            "2026-08-20T12:00:02.100Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"MCP:get_duplicated_strings","tool_use_id":"shared-mcp","mcp_server_name":"dotnet-dstrings","tool_input":{"countThreshold":10,"dumpPath":"C:\\a.dmp"},"duration":1200}""",
            "2026-08-20T12:00:02.200Z"));

        TreeNodeViewModel generation = GetOnlyGeneration(viewModel);
        TreeNodeViewModel[] calls = GetToolCallNodes(generation).ToArray();
        Assert.Equal(2, calls.Length);

        AssertMcpCall(calls, @"C:\a.dmp", "a");
        AssertMcpCall(calls, @"C:\b.dmp", "b");
    }

    [Fact]
    public void SubagentStopNestsUnderSubagentStart()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}""",
            "2026-08-20T12:00:00Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"subagentStart","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"subagent_id":"sub-1","subagent_type":"explore","task":"search codebase"}""",
            "2026-08-20T12:00:01Z"));

        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"subagentStop","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"subagent_id":"sub-1","status":"completed","duration_ms":5000}""",
            "2026-08-20T12:00:06Z"));

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel generation = session.Children.Single(c => c.Kind == TreeNodeKind.Generation);

        TreeNodeViewModel startNode = Assert.Single(generation.Children);
        Assert.Equal("subagentStart", startNode.Observation!.HookEventName);

        TreeNodeViewModel stopNode = Assert.Single(startNode.Children);
        Assert.Equal("subagentStop", stopNode.Observation!.HookEventName);
        Assert.Equal("5s 000ms", startNode.Summary);
    }

    [Fact]
    public void ParallelToolCallsFormWaveNode()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}""",
            "2026-08-20T12:00:00Z"));

        // 3 parallel Grep calls: pre A, pre B, pre C, then post A, post B, post C.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Grep","tool_use_id":"grep-A"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Grep","tool_use_id":"grep-B"}""",
            "2026-08-20T12:00:01.100Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Grep","tool_use_id":"grep-C"}""",
            "2026-08-20T12:00:01.200Z"));

        // Post A finishes first.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Grep","tool_use_id":"grep-A","duration":500}""",
            "2026-08-20T12:00:01.500Z"));
        // Post C finishes second.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Grep","tool_use_id":"grep-C","duration":1000}""",
            "2026-08-20T12:00:02.200Z"));
        // Post B finishes last.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Grep","tool_use_id":"grep-B","duration":2000}""",
            "2026-08-20T12:00:03.100Z"));

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel generation = session.Children.Single(c => c.Kind == TreeNodeKind.Generation);

        // All 3 should be wrapped in a ParallelWave node.
        TreeNodeViewModel waveNode = Assert.Single(generation.Children);
        Assert.Equal(TreeNodeKind.ParallelWave, waveNode.Kind);
        Assert.Contains("3 calls", waveNode.Header);
        Assert.Equal(3, waveNode.Children.Count);

        // Wave duration = max(post.ObservedAtUtc) - min(pre.ObservedAtUtc)
        // = 12:00:03.1 - 12:00:01.0 = 2.1s
        Assert.Equal("2s 100ms", waveNode.Summary);
    }

    [Fact]
    public void SequentialCallsDoNotFormWave()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}""",
            "2026-08-20T12:00:00Z"));

        // Call A: pre then post (completes before B starts).
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"read-A"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"read-A","duration":100}""",
            "2026-08-20T12:00:01.100Z"));

        // Call B: starts after A finishes.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"read-B"}""",
            "2026-08-20T12:00:02Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"read-B","duration":200}""",
            "2026-08-20T12:00:02.200Z"));

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel generation = session.Children.Single(c => c.Kind == TreeNodeKind.Generation);

        // No wave: each pre remains a direct child of the generation.
        Assert.Equal(2, generation.Children.Count);
        Assert.All(generation.Children, child =>
            Assert.Equal("preToolUse", child.Observation!.HookEventName));
    }

    [Fact]
    public void RecomputeGenerationWalksNestedDescendants()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}""",
            "2026-08-20T12:00:00Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"prompt":"build and test"}""",
            "2026-08-20T12:00:01Z"));

        // Shell command nested inside preToolUse.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"shell-1"}""",
            "2026-08-20T12:00:02Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeShellExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"command":"dotnet build"}""",
            "2026-08-20T12:00:02.100Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterShellExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"command":"dotnet build","duration":5000}""",
            "2026-08-20T12:00:07Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Shell","tool_use_id":"shell-1","duration":5100}""",
            "2026-08-20T12:00:07.100Z"));

        // File edit nested inside a Write preToolUse.
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Write","tool_use_id":"write-1"}""",
            "2026-08-20T12:00:08Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterFileEdit","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"file_path":"Foo.cs"}""",
            "2026-08-20T12:00:08.200Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Write","tool_use_id":"write-1","duration":300}""",
            "2026-08-20T12:00:08.300Z"));

        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        TreeNodeViewModel generation = session.Children.Single(c => c.Kind == TreeNodeKind.Generation);

        // Summary should count 1 cmd and 1 edit from descendants.
        Assert.Contains("1 cmd", generation.Summary);
        Assert.Contains("1 edit", generation.Summary);
        Assert.Contains("Shell\u00d71", generation.Summary);
        Assert.Contains("Write\u00d71", generation.Summary);
        Assert.StartsWith("Turn 1 \u00b7 build and test", generation.Header);
    }

    private static TreeNodeViewModel GetOnlyGeneration(MainWindowViewModel viewModel)
    {
        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        return Assert.Single(
            session.Children,
            child => child.Kind == TreeNodeKind.Generation);
    }

    private static IEnumerable<TreeNodeViewModel> GetToolCallNodes(
        TreeNodeViewModel generation)
    {
        foreach (TreeNodeViewModel child in generation.Children)
        {
            if (child.Kind == TreeNodeKind.ParallelWave)
            {
                foreach (TreeNodeViewModel call in child.Children)
                {
                    yield return call;
                }
            }
            else if (child.Observation?.HookEventName == "preToolUse")
            {
                yield return child;
            }
        }
    }

    private static void AssertShellCall(
        IReadOnlyList<TreeNodeViewModel> calls,
        string command,
        string expectedOutput)
    {
        TreeNodeViewModel call = Assert.Single(
            calls,
            candidate => candidate.Observation!.Payload
                .GetProperty("tool_input")
                .GetProperty("command")
                .GetString() == command);
        TreeNodeViewModel before = Assert.Single(
            call.Children,
            child => child.Observation?.HookEventName == "beforeShellExecution");
        Assert.Equal(command, before.Observation!.Payload.GetProperty("command").GetString());
        TreeNodeViewModel after = Assert.Single(before.Children);
        Assert.Equal(command, after.Observation!.Payload.GetProperty("command").GetString());
        Assert.Equal(expectedOutput, after.Observation.Payload.GetProperty("output").GetString());
    }

    private static void AssertReadCall(
        IReadOnlyList<TreeNodeViewModel> calls,
        string filePath)
    {
        TreeNodeViewModel call = Assert.Single(
            calls,
            candidate => candidate.Observation!.Payload
                .GetProperty("tool_input")
                .GetProperty("file_path")
                .GetString() == filePath);
        TreeNodeViewModel before = Assert.Single(
            call.Children,
            child => child.Observation?.HookEventName == "beforeReadFile");
        Assert.Equal(filePath, before.Observation!.Payload.GetProperty("file_path").GetString());
    }

    private static void AssertFileEditCall(
        IReadOnlyList<TreeNodeViewModel> calls,
        string inputProperty,
        string filePath)
    {
        TreeNodeViewModel call = Assert.Single(
            calls,
            candidate => candidate.Observation!.Payload
                .GetProperty("tool_input")
                .TryGetProperty(inputProperty, out JsonElement value) &&
                value.GetString() == filePath);
        TreeNodeViewModel after = Assert.Single(
            call.Children,
            child => child.Observation?.HookEventName == "afterFileEdit");
        Assert.Equal(filePath, after.Observation!.Payload.GetProperty("file_path").GetString());
    }

    private static void AssertMcpCall(
        IReadOnlyList<TreeNodeViewModel> calls,
        string dumpPath,
        string expectedTarget)
    {
        TreeNodeViewModel call = Assert.Single(
            calls,
            candidate => candidate.Observation!.Payload
                .GetProperty("tool_input")
                .GetProperty("dumpPath")
                .GetString() == dumpPath);
        TreeNodeViewModel before = Assert.Single(
            call.Children,
            child => child.Observation?.HookEventName == "beforeMCPExecution");
        TreeNodeViewModel after = Assert.Single(before.Children);

        using JsonDocument result = JsonDocument.Parse(
            after.Observation!.Payload.GetProperty("result_json").GetString()!);
        Assert.Equal(expectedTarget, result.RootElement.GetProperty("target").GetString());
    }

    private static HookObservation ParsePayload(string payloadJson, string observedAtUtc = "2026-08-20T12:00:00Z")
    {
        string envelope = $$"""
            {
              "ingressVersion": 1,
              "eventId": "{{Guid.NewGuid()}}",
              "observedAtUtc": "{{observedAtUtc}}",
              "payload": {{payloadJson}}
            }
            """;

        Assert.True(HookObservation.TryParse(envelope, out HookObservation? observation));
        return Assert.IsType<HookObservation>(observation);
    }
}

public sealed class NodeSummaryTests
{
    [Theory]
    [InlineData(@"C:\Users\me\.claude\skills\dotnet-threads-analysis\SKILL.md", "dotnet-threads-analysis")]
    [InlineData(@"C:\Users\me\.cursor\skills-cursor\canvas\SKILL.md", "canvas")]
    [InlineData("/c:/Users/me/.cursor/skills-cursor/canvas/SKILL.md", "canvas")]
    [InlineData(@"C:\repo\README.md", null)]
    public void TryGetSkillNameUsesParentFolder(string path, string? expected)
    {
        Assert.Equal(expected, HookObservation.TryGetSkillName(path));
    }

    [Theory]
    [InlineData("/dotnet-memory-analysis look at C:\\dump for leaks", "/dotnet-memory-analysis")]
    [InlineData("  /loop 5m do the thing", "/loop")]
    [InlineData("/split-to-prs", "/split-to-prs")]
    [InlineData("<user_query>\n/dotnet-memory-analysis analyze\n</user_query>", "/dotnet-memory-analysis")]
    [InlineData("please analyze with /dotnet-memory-analysis the dump", "/dotnet-memory-analysis")]
    [InlineData("please run /loop later.", "/loop")]
    [InlineData("run /loop and then /split-to-prs now", "/loop,/split-to-prs")]
    [InlineData("/c:/Users/me/skills/canvas/SKILL.md", "")]
    [InlineData("use and/or logic and see http://example.com", "")]
    [InlineData("just a normal prompt", "")]
    [InlineData("", "")]
    public void TryGetSlashCommandsDetectsInvocationsAnywhere(string prompt, string expected)
    {
        Assert.Equal(expected, string.Join(",", HookObservation.TryGetSlashCommands(prompt)));
    }

    [Theory]
    [InlineData("follow the dotnet-memory-analysis skill workflow", "dotnet-memory-analysis")]
    [InlineData("pivot to **dotnet-threads-analysis** skill", "dotnet-threads-analysis")]
    [InlineData("use the `windbg-bridge` skill here", "windbg-bridge")]
    [InlineData("the dotnet-memory-analysis skill then the windbg-bridge skill", "dotnet-memory-analysis,windbg-bridge")]
    [InlineData("the memory analysis skill is generic", "")]
    [InlineData("this skill is great", "")]
    [InlineData("no mention at all", "")]
    [InlineData("", "")]
    public void TryGetSkillMentionsFindsHyphenatedSkillReferences(string text, string expected)
    {
        Assert.Equal(expected, string.Join(",", HookObservation.TryGetSkillMentions(text)));
    }

    [Fact]
    public void TurnSummaryCapturesToolsMcpSkillsTokensAndSubagents()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}""",
            "2026-08-20T12:00:00Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"prompt":"/dotnet-memory-analysis analyze the dump"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterAgentThought","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"text":"read the skill","duration_ms":1500}""",
            "2026-08-20T12:00:01.5Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"t1","tool_input":{"file_path":"C:/Users/me/.claude/skills/dotnet-threads-analysis/SKILL.md"}}""",
            "2026-08-20T12:00:02Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Read","tool_use_id":"t1","duration":10}""",
            "2026-08-20T12:00:02.1Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Grep","tool_use_id":"t2"}""",
            "2026-08-20T12:00:03Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"Grep","tool_use_id":"t2","duration":20}""",
            "2026-08-20T12:00:03.1Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"MCP:get_duplicated_strings","tool_use_id":"t3"}""",
            "2026-08-20T12:00:04Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeMCPExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"mcp_server_name":"dotnet-dstrings","tool_name":"get_duplicated_strings"}""",
            "2026-08-20T12:00:04.1Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterMCPExecution","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"mcp_server_name":"dotnet-dstrings","tool_name":"get_duplicated_strings","duration":50}""",
            "2026-08-20T12:00:04.2Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"postToolUse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"tool_name":"MCP:get_duplicated_strings","tool_use_id":"t3","duration":55}""",
            "2026-08-20T12:00:04.3Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"subagentStart","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"subagent_id":"sa-1","subagent_type":"explore","task":"Find the dump parser"}""",
            "2026-08-20T12:00:05Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"subagentStop","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"subagent_id":"sa-1","subagent_type":"explore","status":"completed","duration_ms":1500,"task":"Find the dump parser"}""",
            "2026-08-20T12:00:06.5Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterAgentThought","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"text":"summarize findings","duration_ms":2500}""",
            "2026-08-20T12:00:06.8Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterAgentResponse","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"input_tokens":10000,"output_tokens":200,"cache_read_tokens":8000,"cache_write_tokens":100}""",
            "2026-08-20T12:00:07Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"stop","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"status":"completed","input_tokens":10000,"output_tokens":200,"cache_read_tokens":8000,"cache_write_tokens":100}""",
            "2026-08-20T12:00:07.1Z"));

        TreeNodeViewModel session = Assert.Single(Assert.Single(viewModel.Roots).Children);
        TreeNodeViewModel turn = Assert.Single(session.Children, child => child.Kind == TreeNodeKind.Generation);
        NodeSummary summary = Assert.IsType<NodeSummary>(turn.NodeSummary);

        Assert.Equal(2, summary.ToolCallCount);
        Assert.Equal(["Grep", "Read"], summary.Tools.Select(row => row.Name).ToArray());
        Assert.Equal(10, summary.Tools.Single(row => row.Name == "Read").DurationMs);
        Assert.Equal(20, summary.Tools.Single(row => row.Name == "Grep").DurationMs);
        Assert.Equal(1, summary.McpCallCount);
        Assert.Equal("dotnet-dstrings/get_duplicated_strings", Assert.Single(summary.McpCalls).Name);
        Assert.Equal(50, Assert.Single(summary.McpCalls).DurationMs);
        Assert.Equal(["dotnet-threads-analysis"], summary.Skills.ToArray());
        Assert.Equal(["/dotnet-memory-analysis"], summary.Commands.ToArray());
        Assert.Equal(2, summary.ThoughtCount);
        Assert.Equal(4000, summary.ThoughtDurationMs);
        Assert.Equal(32, summary.ThoughtCharacterCount);
        Assert.Equal("32", Assert.Single(summary.Thoughts).Name);
        Assert.True(summary.HasThoughts);
        Assert.Equal(10000, summary.InputTokens);
        Assert.Equal(200, summary.OutputTokens);
        Assert.Equal(8000, summary.CacheReadTokens);
        Assert.Equal(100, summary.CacheWriteTokens);
        Assert.Contains("in 10k", summary.TokenLine);
        Assert.Contains("out 200", summary.TokenLine);
        SubagentSummary agent = Assert.Single(summary.Subagents);
        Assert.Equal("explore", agent.Type);
        Assert.Equal(1500, agent.DurationMs);
        Assert.Equal("completed", agent.Status);
        Assert.Contains("Read\u00d71", turn.Summary);
        Assert.Contains("Grep\u00d71", turn.Summary);

        NodeSummary sessionSummary = Assert.IsType<NodeSummary>(session.NodeSummary);
        Assert.Equal(1, sessionSummary.TurnCount);
        Assert.Equal(2, sessionSummary.ToolCallCount);
        Assert.Equal(2, sessionSummary.ThoughtCount);
        Assert.Equal(4000, sessionSummary.ThoughtDurationMs);
        Assert.Equal(32, sessionSummary.ThoughtCharacterCount);
        Assert.Contains("1 turn", session.Summary);
        Assert.Contains("2 tools", session.Summary);
        Assert.Contains("200 out", session.Summary);
    }

    [Fact]
    public void SessionSummarySumsOutputTokensAndCountsAbortedTurns()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"prompt":"first"}""",
            "2026-08-20T12:00:00Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"stop","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"status":"completed","input_tokens":1000,"output_tokens":50}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"conv-1","generation_id":"gen-2","workspace_roots":["C:\\Repo"],"prompt":"second"}""",
            "2026-08-20T12:00:02Z"));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"stop","conversation_id":"conv-1","generation_id":"gen-2","workspace_roots":["C:\\Repo"],"status":"aborted","input_tokens":5000,"output_tokens":80}""",
            "2026-08-20T12:00:03Z"));

        TreeNodeViewModel session = Assert.Single(Assert.Single(viewModel.Roots).Children);
        TreeNodeViewModel turn2 = Assert.Single(session.Children, child => child.Kind == TreeNodeKind.Generation && child.TurnNumber == 2);
        Assert.True(turn2.NodeSummary!.IsAborted);
        Assert.Contains("aborted", turn2.Summary);

        NodeSummary sessionSummary = session.NodeSummary!;
        Assert.Equal(2, sessionSummary.TurnCount);
        Assert.Equal(1, sessionSummary.AbortedTurnCount);
        Assert.Equal(5000, sessionSummary.InputTokens);
        Assert.Equal(130, sessionSummary.OutputTokens);
        Assert.Contains("1 aborted", session.Summary);
        Assert.Contains("130 out", session.Summary);

        viewModel.SelectNode(session);
        Assert.True(viewModel.HasSelectedDashboard);
        Assert.Empty(viewModel.SelectedFields);

        viewModel.SelectNode(turn2);
        Assert.True(viewModel.HasSelectedDashboard);

        TreeNodeViewModel stopNode = Assert.Single(turn2.Children, child => child.IsStop);
        viewModel.SelectNode(stopNode);
        Assert.False(viewModel.HasSelectedDashboard);
        Assert.Contains("\"hook_event_name\": \"stop\"", viewModel.SelectedPayloadText, StringComparison.Ordinal);
    }

    [Fact]
    public void FindNextStartsFromSelectedNodeAndWalksTree()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}"""));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"prompt":"alpha needle"}"""));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"afterAgentThought","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"text":"beta needle","duration_ms":100}"""));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"stop","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"status":"completed"}"""));

        TreeNodeViewModel turn = Assert.Single(
            Assert.Single(Assert.Single(viewModel.Roots).Children).Children,
            child => child.Kind == TreeNodeKind.Generation);
        TreeNodeViewModel promptNode = turn.Children[0];
        TreeNodeViewModel thoughtNode = turn.Children[1];

        viewModel.SelectNode(thoughtNode);
        viewModel.SearchQuery = "needle";

        NodeSearchMatch? first = viewModel.FindNext(previous: false);
        NodeSearchMatch match = Assert.IsType<NodeSearchMatch>(first);
        Assert.Same(thoughtNode, match.Node);
        Assert.Equal(NodeSearchTarget.FieldValue, match.Target);

        NodeSearchMatch? second = viewModel.FindNext(previous: false);
        match = Assert.IsType<NodeSearchMatch>(second);
        Assert.Same(promptNode, match.Node);
    }

    [Fact]
    public void FindNextStartsFromSelectedTurnNode()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}"""));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"prompt":"turn-one-token"}"""));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"conv-1","generation_id":"gen-2","workspace_roots":["C:\\Repo"],"prompt":"turn-two-token"}"""));

        TreeNodeViewModel session = Assert.Single(Assert.Single(viewModel.Roots).Children);
        TreeNodeViewModel turn1 = Assert.Single(session.Children, child => child.TurnNumber == 1);
        TreeNodeViewModel turn2 = Assert.Single(session.Children, child => child.TurnNumber == 2);
        TreeNodeViewModel turn2Prompt = turn2.Children[0];

        viewModel.SelectNode(turn2);
        viewModel.SearchQuery = "turn-two-token";

        NodeSearchMatch? match = viewModel.FindNext(previous: false);
        Assert.Same(turn2Prompt, match?.Node);

        viewModel.SelectNode(turn2);
        viewModel.SearchQuery = "turn-one-token";
        match = viewModel.FindNext(previous: false);
        Assert.Same(turn1.Children[0], match?.Node);
    }

    [Fact]
    public void FindNextPrefersFieldMatchOverPayload()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"prompt":"needle in field"}"""));

        viewModel.SearchQuery = "needle";
        NodeSearchMatch? match = viewModel.FindNext(previous: false);
        NodeSearchMatch result = Assert.IsType<NodeSearchMatch>(match);
        Assert.Equal(NodeSearchTarget.FieldValue, result.Target);
        Assert.Equal("prompt", result.Node.Observation!.DetailFields[result.FieldIndex].Name);
    }

    [Fact]
    public void FindNextWrapsAcrossNodes()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}"""));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"prompt":"unique-alpha"}"""));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"stop","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"status":"completed"}"""));

        TreeNodeViewModel turn = Assert.Single(
            Assert.Single(Assert.Single(viewModel.Roots).Children).Children,
            child => child.Kind == TreeNodeKind.Generation);
        TreeNodeViewModel promptNode = turn.Children[0];

        viewModel.SearchQuery = "unique-alpha";
        NodeSearchMatch? first = viewModel.FindNext(previous: false);
        Assert.Same(promptNode, first?.Node);

        NodeSearchMatch? wrapped = viewModel.FindNext(previous: false);
        Assert.Same(promptNode, wrapped?.Node);
    }

    [Fact]
    public void PreCompactNodeShowsCompactionFieldsInHeaderAndTable()
    {
        HookObservation observation = ParsePayload(
            """
            {
              "hook_event_name": "preCompact",
              "conversation_id": "conv-1",
              "generation_id": "gen-1",
              "workspace_roots": ["C:\\Repo"],
              "trigger": "auto",
              "context_usage_percent": 92.0255,
              "context_window_size": 200000,
              "message_count": 311,
              "messages_to_compact": 309,
              "is_first_compaction": true
            }
            """);

        Assert.Equal(
            "preCompact · auto · 92.03% · 200k · 311/309",
            observation.OccurrenceHeader);

        PayloadField[] fields = observation.DetailFields.ToArray();
        Assert.Equal(5, fields.Length);
        Assert.Equal("is_first_compaction", fields[0].Name);
        Assert.Equal("true", fields[0].Value);
        Assert.Equal("trigger", fields[1].Name);
        Assert.Equal("auto", fields[1].Value);
        Assert.Equal("context usage percent", fields[2].Name);
        Assert.Equal("92.03%", fields[2].Value);
        Assert.Equal("window size", fields[3].Name);
        Assert.Equal("200k", fields[3].Value);
        Assert.Equal("messages count/to compact", fields[4].Name);
        Assert.Equal("311/309", fields[4].Value);
    }

    [Fact]
    public void PreCompactNodeUsesLightForegroundWhenSelected()
    {
        HookObservation observation = ParsePayload(
            """{"hook_event_name":"preCompact","conversation_id":"conv-1","workspace_roots":["C:\\Repo"],"trigger":"auto"}""");
        var node = new TreeNodeViewModel("key", observation.OccurrenceHeader, TreeNodeKind.Observation, observation);

        Assert.True(node.IsPreCompact);
        Assert.True(node.UsesLightForegroundWhenSelected);
    }

    [Fact]
    public void FindNextMatchesHookNameBeforePayload()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}"""));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"prompt":"hello"}"""));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preCompact","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"trigger":"auto"}"""));

        TreeNodeViewModel turn = Assert.Single(
            Assert.Single(Assert.Single(viewModel.Roots).Children).Children,
            child => child.Kind == TreeNodeKind.Generation);
        TreeNodeViewModel compactNode = turn.Children[1];

        viewModel.SearchQuery = "preCompact";
        NodeSearchMatch? match = viewModel.FindNext(previous: false);
        NodeSearchMatch result = Assert.IsType<NodeSearchMatch>(match);
        Assert.Same(compactNode, result.Node);
        Assert.Equal(NodeSearchTarget.NodeName, result.Target);
    }

    [Fact]
    public void FindNextMatchesNodeHeaderText()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"sessionStart","conversation_id":"conv-1","workspace_roots":["C:\\Repo"]}"""));
        viewModel.AddObservation(ParsePayload(
            """{"hook_event_name":"preCompact","conversation_id":"conv-1","generation_id":"gen-1","workspace_roots":["C:\\Repo"],"trigger":"manual","context_usage_percent":80,"context_window_size":100000,"message_count":10,"messages_to_compact":8}"""));

        TreeNodeViewModel compactNode = Assert.Single(
            Assert.Single(
                Assert.Single(Assert.Single(viewModel.Roots).Children).Children,
                child => child.Kind == TreeNodeKind.Generation).Children,
            child => child.IsPreCompact);

        viewModel.SearchQuery = "manual";
        NodeSearchMatch? match = viewModel.FindNext(previous: false);
        NodeSearchMatch result = Assert.IsType<NodeSearchMatch>(match);
        Assert.Same(compactNode, result.Node);
        Assert.Equal(NodeSearchTarget.NodeName, result.Target);
    }

    private static HookObservation ParsePayload(string payloadJson, string observedAtUtc = "2026-08-20T12:00:00Z")
    {
        string envelope = $$"""
            {
              "ingressVersion": 1,
              "eventId": "{{Guid.NewGuid()}}",
              "observedAtUtc": "{{observedAtUtc}}",
              "payload": {{payloadJson}}
            }
            """;

        Assert.True(HookObservation.TryParse(envelope, out HookObservation? observation));
        return Assert.IsType<HookObservation>(observation);
    }
}

public sealed class ReplayTests
{
    [Fact]
    public void RawPayloadParseSynthesizesReplayObservation()
    {
        string sourceFile = Path.Combine(Path.GetTempPath(), "payload.json");
        DateTimeOffset observedAtUtc = DateTimeOffset.Parse("2026-08-21T08:41:50Z");

        bool parsed = HookObservation.TryParseRawPayload(
            """
            {
              "hook_event_name": "beforeSubmitPrompt",
              "conversation_id": "conversation-1",
              "generation_id": "generation-1",
              "workspace_roots": ["C:\\Repo"],
              "prompt": "replay this"
            }
            """,
            observedAtUtc,
            sourceFile,
            out HookObservation? observation);

        HookObservation replayed = Assert.IsType<HookObservation>(observation);
        Assert.True(parsed);
        Assert.NotEqual(Guid.Empty, replayed.EventId);
        Assert.Equal(observedAtUtc, replayed.ObservedAtUtc);
        Assert.Equal(sourceFile, replayed.SourceFilePath);
        Assert.Equal("beforeSubmitPrompt", replayed.HookEventName);
        Assert.Equal("conversation-1", replayed.SessionId);
        Assert.Equal("generation-1", replayed.GenerationId);
        Assert.Contains("replay this", replayed.DisplayJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplayLoaderSkipsMalformedJsonAndPreservesUnknownHooksChronologically()
    {
        string folder = Path.Combine(Path.GetTempPath(), "CursorSpyReplayTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            string laterFile = Path.Combine(folder, "hp_conversation-1_beforeSubmitPrompt_20260821_084151_000_bbbbbbbb.json");
            string earlierFile = Path.Combine(folder, "conversation-1_sessionStart_20260821_084150_000_aaaaaaaa.json");
            string invalidFile = Path.Combine(folder, "conversation-1_stop_20260821_084152_000_cccccccc.json");
            string arrayFile = Path.Combine(folder, "conversation-1_postToolUse_20260821_084153_000_dddddddd.json");
            string runtimeConfigFile = Path.Combine(folder, "CursorSpy.Hook.runtimeconfig.json");
            string unknownHookFile = Path.Combine(folder, "hp_conversation-1_notAHook_20260821_084154_000_eeeeeeee.json");
            string mismatchedHookFile = Path.Combine(folder, "hp_conversation-1_stop_20260821_084155_000_ffffffff.json");

            await File.WriteAllTextAsync(
                laterFile,
                """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"conversation-1","generation_id":"generation-1","workspace_roots":["C:\\Repo"],"prompt":"second"}""");
            await File.WriteAllTextAsync(
                earlierFile,
                """{"hook_event_name":"sessionStart","conversation_id":"conversation-1","workspace_roots":["C:\\Repo"]}""");
            await File.WriteAllTextAsync(invalidFile, "{");
            await File.WriteAllTextAsync(arrayFile, "[]");
            await File.WriteAllTextAsync(
                runtimeConfigFile,
                """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");
            await File.WriteAllTextAsync(
                unknownHookFile,
                """{"hook_event_name":"notAHook","conversation_id":"conversation-1","workspace_roots":["C:\\Repo"]}""");
            await File.WriteAllTextAsync(
                mismatchedHookFile,
                """{"hook_event_name":"sessionEnd","conversation_id":"conversation-1","workspace_roots":["C:\\Repo"]}""");

            ReplayLoader loader = new();
            IReadOnlyList<HookObservation> observations = await loader.LoadAsync(folder);

            Assert.Equal(4, observations.Count);
            Assert.Equal("sessionStart", observations[0].HookEventName);
            Assert.Equal("beforeSubmitPrompt", observations[1].HookEventName);
            Assert.Equal("notAHook", observations[2].HookEventName);
            Assert.Equal("sessionEnd", observations[3].HookEventName);
            Assert.Equal(Path.GetFullPath(earlierFile), observations[0].SourceFilePath);
            Assert.Equal(Path.GetFullPath(laterFile), observations[1].SourceFilePath);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ReplayLoaderAcceptsTabHookPayloadFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), "CursorSpyReplayTabTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            string readFile = Path.Combine(folder, "hp_conversation-1_beforeTabFileRead_20260821_084200_000_aaaaaaaa.json");
            string editFile = Path.Combine(folder, "hp_conversation-1_afterTabFileEdit_20260821_084201_000_bbbbbbbb.json");

            await File.WriteAllTextAsync(
                readFile,
                """{"hook_event_name":"beforeTabFileRead","conversation_id":"conversation-1","file_path":"C:\\Repo\\Foo.cs","content":"class Foo {}"}""");
            await File.WriteAllTextAsync(
                editFile,
                """{"hook_event_name":"afterTabFileEdit","conversation_id":"conversation-1","file_path":"C:\\Repo\\Foo.cs","edits":[]}""");

            ReplayLoader loader = new();
            IReadOnlyList<HookObservation> observations = await loader.LoadAsync(folder);

            Assert.Equal(2, observations.Count);
            Assert.Equal("beforeTabFileRead", observations[0].HookEventName);
            Assert.Equal("afterTabFileEdit", observations[1].HookEventName);
            Assert.Equal("Foo.cs", Path.GetFileName(observations[0].TargetFilePath));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public sealed class NamedPipeListenerTests
{
    [Fact]
    public async Task ListenerSkipsMalformedLineAndAcceptsNextValidClient()
    {
        string pipeName = "CursorSpy.Listener.Tests." + Guid.NewGuid().ToString("N");
        NamedPipeListener listener = new(pipeName);
        List<HookObservation> observations = [];
        TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));

        Task listenerTask = listener.RunAsync(
            (observation, _) =>
            {
                observations.Add(observation);
                observed.TrySetResult();
                return Task.CompletedTask;
            },
            cancellation.Token);

        await WriteClientLineAsync(pipeName, "not-json");
        await WriteClientLineAsync(
            pipeName,
            Envelope("""{"hook_event_name":"workspaceOpen","workspace_roots":["C:\\Repo"],"cursor_version":"test"}"""));

        await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        try
        {
            await listenerTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
        }

        HookObservation observation = Assert.Single(observations);
        Assert.Equal("workspaceOpen", observation.HookEventName);
    }

    private static async Task WriteClientLineAsync(string pipeName, string line)
    {
        await using NamedPipeClientStream client = new(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        await client.ConnectAsync(timeout.Token);
        await client.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), timeout.Token);
        await client.FlushAsync(timeout.Token);
    }

    private static string Envelope(string payloadJson)
    {
        string prettyEnvelope = $$"""
            {
              "ingressVersion": 1,
              "eventId": "{{Guid.NewGuid()}}",
              "observedAtUtc": "2026-08-20T12:00:00Z",
              "payload": {{payloadJson}}
            }
            """;

        using JsonDocument document = JsonDocument.Parse(prettyEnvelope);
        return JsonSerializer.Serialize(document.RootElement);
    }
}

public sealed class TreeNodeAncestorPathTests
{
    [Fact]
    public void ReturnsPathFromContainingRootToTarget()
    {
        TreeNodeViewModel otherRoot = new("other-root", "Other Root", TreeNodeKind.Workspace);
        otherRoot.Children.Add(new TreeNodeViewModel("other-child", "Other Child", TreeNodeKind.Session));

        TreeNodeViewModel root = new("root", "Root", TreeNodeKind.Workspace);
        TreeNodeViewModel session = new("session", "Session", TreeNodeKind.Session);
        TreeNodeViewModel generation = new("generation", "Generation", TreeNodeKind.Generation);
        TreeNodeViewModel target = new("target", "Target", TreeNodeKind.Observation);
        TreeNodeViewModel sibling = new("sibling", "Sibling", TreeNodeKind.Observation);

        generation.Children.Add(target);
        generation.Children.Add(sibling);
        session.Children.Add(generation);
        root.Children.Add(session);

        List<TreeNodeViewModel>? path = TreeNodeViewModel.FindAncestorPath([otherRoot, root], target);

        Assert.NotNull(path);
        Assert.Equal([root, session, generation, target], path);
    }

    [Fact]
    public void ReturnsNullWhenTargetIsNotReachableFromRoots()
    {
        TreeNodeViewModel root = new("root", "Root", TreeNodeKind.Workspace);
        root.Children.Add(new TreeNodeViewModel("child", "Child", TreeNodeKind.Session));

        TreeNodeViewModel unrelated = new("unrelated", "Unrelated", TreeNodeKind.Observation);

        Assert.Null(TreeNodeViewModel.FindAncestorPath([root], unrelated));
    }

    [Fact]
    public void ReturnsSingleElementPathWhenTargetIsARoot()
    {
        TreeNodeViewModel root = new("root", "Root", TreeNodeKind.Workspace);

        List<TreeNodeViewModel>? path = TreeNodeViewModel.FindAncestorPath([root], root);

        Assert.NotNull(path);
        Assert.Equal([root], path);
    }
}
