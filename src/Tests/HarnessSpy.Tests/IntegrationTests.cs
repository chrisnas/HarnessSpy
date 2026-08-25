using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HarnessSpy.Core.Hooks;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;

namespace HarnessSpy.Tests;

public sealed class IntegrationTests
{
    [Theory]
    [InlineData("CursorSpy.Hook", "{}", null, "--event", "sessionStart")]
    [InlineData("ClaudeSpy.Hook", "", "CLAUDE_CODE_CHILD_SESSION", "--event", "SessionStart")]
    [InlineData("CopilotSpy.Hook", "{}", null, "--event", "sessionStart")]
    public async Task ProviderHookProcessesHonorNoOpContracts(
        string projectName,
        string expectedStdout,
        string? environmentMarker,
        string eventArgument,
        string eventName)
    {
        string executable = HookExecutable(projectName);
        ProcessStartInfo startInfo = new(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(eventArgument);
        startInfo.ArgumentList.Add(eventName);
        if (environmentMarker is not null)
        {
            startInfo.Environment[environmentMarker] = "1";
        }

        using Process process = Process.Start(startInfo)!;
        await process.StandardInput.WriteAsync(
            """{"hook_event_name":"SessionStart","session_id":"process-test","cwd":"C:\\Repo"}""");
        process.StandardInput.Close();
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(expectedStdout, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public async Task MixedProviderReplayLoadsVersionedEnvelopes()
    {
        string folder = Path.Combine(
            Path.GetTempPath(),
            "HarnessSpyMixedReplay_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            await WriteEnvelope(
                folder,
                "hs_Cursor_s1_sessionStart_20260822_100000_000_aaaaaaaa.json",
                HookProvider.Cursor,
                HookSurface.CursorIde,
                """{"hook_event_name":"sessionStart","conversation_id":"s1","workspace_roots":["C:\\Repo"]}""");
            await WriteEnvelope(
                folder,
                "hs_ClaudeCode_s2_SessionStart_20260822_100001_000_bbbbbbbb.json",
                HookProvider.ClaudeCode,
                HookSurface.ClaudeCode,
                """{"hook_event_name":"SessionStart","session_id":"s2","cwd":"C:\\Repo","source":"startup"}""");
            await WriteEnvelope(
                folder,
                "hs_GitHubCopilot_s3_sessionStart_20260822_100002_000_cccccccc.json",
                HookProvider.GitHubCopilot,
                HookSurface.CopilotCli,
                """{"sessionId":"s3","timestamp":1787414400000,"cwd":"C:\\Repo","source":"new"}""",
                configuredEvent: "sessionStart");

            IReadOnlyList<HookObservation> observations =
                await new ReplayLoader().LoadAsync(folder);

            Assert.Equal(3, observations.Count);
            Assert.Equal(
                [HookProvider.Cursor, HookProvider.ClaudeCode, HookProvider.GitHubCopilot],
                observations.Select(item => item.Provider));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task NamedPipeListenerFiltersForeignProviders()
    {
        string pipeName = "HarnessSpy.Filter.Tests." + Guid.NewGuid().ToString("N");
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        TaskCompletionSource<HookObservation> received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        NamedPipeListener listener = new(pipeName, HookProvider.Cursor);
        Task listenerTask = listener.RunAsync(
            (observation, _) =>
            {
                received.TrySetResult(observation);
                return Task.CompletedTask;
            },
            cancellation.Token);

        NamedPipePayloadSink sink = new(pipeName, TimeSpan.FromSeconds(2));
        await sink.ForwardAsync(
            Encoding.UTF8.GetBytes(CreateEnvelopeJson(
                HookProvider.ClaudeCode,
                HookSurface.ClaudeCode,
                """{"hook_event_name":"SessionStart","session_id":"foreign","cwd":"C:\\Repo"}""")),
            cancellation.Token);
        await sink.ForwardAsync(
            Encoding.UTF8.GetBytes(CreateEnvelopeJson(
                HookProvider.Cursor,
                HookSurface.CursorIde,
                """{"hook_event_name":"sessionStart","conversation_id":"accepted","workspace_roots":["C:\\Repo"]}""")),
            cancellation.Token);

        HookObservation observation =
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(HookProvider.Cursor, observation.Provider);
        Assert.Equal("accepted", observation.SessionId);

        cancellation.Cancel();
        await listenerTask;
    }

    [Fact]
    public void AgentHostContractIsVersionedAndOpenEnded()
    {
        string schemaPath = Path.Combine(
            SolutionRoot(),
            "Contracts",
            "AgentHost",
            "v1",
            "agent-host.schema.json");
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(schemaPath));

        Assert.Equal(
            "HarnessSpy AgentHost v1",
            schema.RootElement.GetProperty("title").GetString());
        Assert.True(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            1,
            schema.RootElement
                .GetProperty("properties")
                .GetProperty("version")
                .GetProperty("const")
                .GetInt32());
    }

    private static async Task WriteEnvelope(
        string folder,
        string fileName,
        HookProvider provider,
        HookSurface surface,
        string payload,
        string? configuredEvent = null)
    {
        string content = CreateEnvelopeJson(
            provider,
            surface,
            payload,
            configuredEvent);
        await File.WriteAllTextAsync(Path.Combine(folder, fileName), content);
    }

    private static string CreateEnvelopeJson(
        HookProvider provider,
        HookSurface surface,
        string payload,
        string? configuredEvent = null)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        ObservationEnvelope envelope = new(
            ObservationEnvelope.CurrentIngressVersion,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            provider,
            surface,
            surface,
            ObservationSourceKind.Hook,
            configuredEvent,
            configuredEvent ??
                (document.RootElement.TryGetProperty(
                    "hook_event_name",
                    out JsonElement eventName)
                    ? eventName.GetString()
                    : null),
            "integration-test",
            null,
            "valid",
            document.RootElement.Clone());
        return JsonSerializer.Serialize(
            envelope,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string HookExecutable(string projectName)
    {
        return Path.Combine(
            SolutionRoot(),
            "Hooks",
            projectName,
            "bin",
            "Debug",
            "net10.0",
            projectName + ".exe");
    }

    private static string SolutionRoot() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
}
