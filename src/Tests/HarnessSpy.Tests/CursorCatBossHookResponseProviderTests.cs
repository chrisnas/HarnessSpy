using System.Text;
using System.Text.Json;
using HarnessSpy.Core.Hooks;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes.Cursor;
using HarnessSpy.Core.Services;
using HarnessSpy.Wpf.Services;
using HarnessSpy.Wpf.ViewModels;

namespace HarnessSpy.Tests;

public sealed class CursorCatBossHookResponseProviderTests
{
    private readonly CursorCatBossHookResponseProvider _provider =
        new(new CursorCatBossText());

    [Fact]
    public void SessionStartInjectsCatContextAndEnvironment()
    {
        HookResponseDecision decision = Create("sessionStart", """{}""");

        Assert.Equal("Chaircat Whiskers", decision.Response["env"]?["CAT_BOSS"]?.GetValue<string>());
        Assert.DoesNotContain(
            CursorCatBossText.Prefix,
            decision.Response["additional_context"]?.GetValue<string>()?
                .Replace(
                    $"\"{CursorCatBossText.Prefix}\"",
                    string.Empty,
                    StringComparison.Ordinal));
        Assert.Contains(
            "UI attribution marker",
            decision.Response["additional_context"]?.GetValue<string>());
        Assert.Equal("inject_context", decision.Effect?.EffectKind);
        Assert.DoesNotContain(
            CursorCatBossText.Prefix,
            decision.Effect?.Summary ?? string.Empty);
    }

    [Fact]
    public void VagueGuidPromptIsRejectedAndRequiresUserAction()
    {
        HookResponseDecision decision = Create(
            "beforeSubmitPrompt",
            """{"prompt":"Build me a GUID generator."}""");

        Assert.False(decision.Response["continue"]?.GetValue<bool>());
        Assert.StartsWith(
            CursorCatBossText.Prefix,
            decision.Response["user_message"]?.GetValue<string>());
        Assert.True(decision.Effect?.RequiresUserAction);
    }

    [Fact]
    public void DetailedPromptIsAllowedWithoutOutcome()
    {
        HookResponseDecision decision = Create(
            "beforeSubmitPrompt",
            """{"prompt":"Build a .NET console GUID generator."}""");

        Assert.True(decision.Response["continue"]?.GetValue<bool>());
        Assert.Null(decision.Effect);
    }

    [Fact]
    public void DetailedDemoPromptIsRejectedWhenProjectAlreadyExists()
    {
        const string projectPath =
            @"C:\Demos\CatBossGuid\GuidGenerator\GuidGenerator.csproj";
        CursorCatBossHookResponseProvider provider = new(
            new CursorCatBossText(),
            path => string.Equals(
                path,
                projectPath,
                StringComparison.OrdinalIgnoreCase));
        HookResponseDecision decision = Create(
            provider,
            "beforeSubmitPrompt",
            """
            {
              "prompt":"Build a minimal .NET console app named GuidGenerator. Create it with dotnet new.",
              "workspace_roots":["C:\\Demos\\CatBossGuid"]
            }
            """);

        Assert.False(decision.Response["continue"]?.GetValue<bool>());
        Assert.StartsWith(
            CursorCatBossText.Prefix,
            decision.Response["user_message"]?.GetValue<string>());
        Assert.Equal("reject_existing_project", decision.Effect?.EffectKind);
        Assert.True(decision.Effect?.RequiresUserAction);
    }

    [Fact]
    public void DetailedDemoPromptIsAllowedWhenProjectDoesNotExist()
    {
        CursorCatBossHookResponseProvider provider = new(
            new CursorCatBossText(),
            _ => false);
        HookResponseDecision decision = Create(
            provider,
            "beforeSubmitPrompt",
            """
            {
              "prompt":"Build a minimal .NET console app named GuidGenerator. Create it with dotnet new.",
              "workspace_roots":["C:\\Demos\\CatBossGuid"]
            }
            """);

        Assert.True(decision.Response["continue"]?.GetValue<bool>());
        Assert.Null(decision.Effect);
    }

    [Fact]
    public void SubagentStartIsObservationOnly()
    {
        HookResponseDecision decision = Create(
            "subagentStart",
            """{"subagent_type":"explore","task":"Review architecture"}""");

        Assert.Empty(decision.Response);
        Assert.Null(decision.Effect);
    }

    [Theory]
    [InlineData("Subagent")]
    [InlineData("Task")]
    public void SubagentToolIsDeniedBeforeItStarts(string toolName)
    {
        HookResponseDecision decision = Create(
            "preToolUse",
            JsonSerializer.Serialize(new
            {
                tool_name = toolName,
                tool_input = new
                {
                    subagent_type = "generalPurpose",
                    description = "Review architecture",
                    prompt = "Review the proposed architecture."
                }
            }));

        Assert.Equal("deny", decision.Response["permission"]?.GetValue<string>());
        Assert.StartsWith(
            CursorCatBossText.Prefix,
            decision.Response["user_message"]?.GetValue<string>());
        Assert.DoesNotContain(
            CursorCatBossText.Prefix,
            decision.Response["agent_message"]?.GetValue<string>());
        Assert.Equal("deny_subagent", decision.Effect?.EffectKind);
        Assert.Contains(
            decision.Effect!.Changes,
            change =>
                change.Target == "subagent_type" &&
                change.Before == "generalPurpose");
        Assert.Contains(
            decision.Effect.Changes,
            change =>
                change.Target == "task" &&
                change.Before == "Review the proposed architecture.");
    }

    [Fact]
    public void PackageInstallationIsDenied()
    {
        HookResponseDecision decision = Create(
            "preToolUse",
            """
            {
              "tool_name":"Shell",
              "tool_input":{"command":"dotnet add package UUIDNext"}
            }
            """);

        Assert.Equal("deny", decision.Response["permission"]?.GetValue<string>());
        Assert.Equal("deny_tool", decision.Effect?.EffectKind);
    }

    [Fact]
    public void DotnetNewIsRewrittenToCreateLowercaseInitialOutput()
    {
        HookResponseDecision decision = Create(
            "preToolUse",
            """
            {
              "tool_name":"Shell",
              "tool_input":{"command":"dotnet new console -n GuidGenerator"}
            }
            """);

        Assert.Equal("allow", decision.Response["permission"]?.GetValue<string>());
        string command = Assert.IsType<string>(
            decision.Response["updated_input"]?["command"]?.GetValue<string>());
        Assert.Contains("dotnet new console -n GuidGenerator", command);
        Assert.Contains("ToLowerInvariant()", command);
        Assert.Equal("rewrite_tool_input", decision.Effect?.EffectKind);
    }

    [Fact]
    public void BuildCommandIsRewrittenWithoutLosingInputFields()
    {
        HookResponseDecision decision = Create(
            "preToolUse",
            """
            {
              "tool_name":"Shell",
              "tool_input":{
                "command":"dotnet build GuidGenerator",
                "working_directory":"C:\\Demo",
                "custom":42
              }
            }
            """);

        Assert.Equal("allow", decision.Response["permission"]?.GetValue<string>());
        Assert.Equal(
            "dotnet build GuidGenerator --configuration Release",
            decision.Response["updated_input"]?["command"]?.GetValue<string>());
        Assert.Equal(42, decision.Response["updated_input"]?["custom"]?.GetValue<int>());
        Assert.Equal("rewrite_tool_input", decision.Effect?.EffectKind);
    }

    [Fact]
    public void BuildSegmentInCompoundCommandIsRewritten()
    {
        HookResponseDecision decision = Create(
            "preToolUse",
            """
            {
              "tool_name":"Shell",
              "tool_input":{"command":"dotnet build && dotnet run"}
            }
            """);

        Assert.Equal(
            "dotnet build --configuration Release && dotnet run",
            decision.Response["updated_input"]?["command"]?.GetValue<string>());
        Assert.Equal("rewrite_tool_input", decision.Effect?.EffectKind);
    }

    [Theory]
    [InlineData("dotnet build -c Release")]
    [InlineData("dotnet build --configuration=Release")]
    public void ConfiguredBuildIsAllowedWithoutRewrite(string command)
    {
        HookResponseDecision decision = Create(
            "preToolUse",
            JsonSerializer.Serialize(new
            {
                tool_name = "Shell",
                tool_input = new { command }
            }));

        Assert.Equal("allow", decision.Response["permission"]?.GetValue<string>());
        Assert.Null(decision.Response["updated_input"]);
        Assert.Null(decision.Effect);
    }

    [Fact]
    public void LowercaseGuidRunInjectsUppercaseRequirement()
    {
        HookResponseDecision decision = Create(
            "postToolUse",
            """
            {
              "tool_name":"Shell",
              "tool_input":{"command":"dotnet run"},
              "tool_output":"{\"stdout\":\"12345678-abcd-4abc-8abc-123456789abc\"}"
            }
            """);

        Assert.DoesNotContain(
            CursorCatBossText.Prefix,
            decision.Response["additional_context"]?.GetValue<string>());
        Assert.Equal("inject_context", decision.Effect?.EffectKind);
    }

    [Fact]
    public void LowercaseGuidInEscapedShellOutputInjectsUppercaseRequirement()
    {
        HookResponseDecision decision = Create(
            "postToolUse",
            """
            {
              "tool_name":"Shell",
              "tool_input":{"command":"dotnet build && dotnet run"},
              "tool_output":"{\"output\":\"Build succeeded.\\r\\n1794cb56-3377-43f0-a1eb-e85febe1159b\\r\\n\",\"exitCode\":0}"
            }
            """);

        Assert.Equal("inject_context", decision.Effect?.EffectKind);
        Assert.Equal(
            "1794CB56-3377-43F0-A1EB-E85FEBE1159B",
            decision.Effect?.Changes.Single().After);
    }

    [Fact]
    public void UppercaseGuidRunDoesNotInjectAgain()
    {
        HookResponseDecision decision = Create(
            "postToolUse",
            """
            {
              "tool_name":"Shell",
              "tool_input":{"command":"dotnet run"},
              "tool_output":"12345678-ABCD-4ABC-8ABC-123456789ABC"
            }
            """);

        Assert.Empty(decision.Response);
        Assert.Null(decision.Effect);
    }

    [Fact]
    public void FirstCompletedStopCreatesFollowUp()
    {
        HookResponseDecision decision = Create(
            "stop",
            """{"status":"completed","loop_count":0}""");

        Assert.StartsWith(
            CursorCatBossText.Prefix,
            decision.Response["followup_message"]?.GetValue<string>());
        Assert.Equal("auto_followup", decision.Effect?.EffectKind);
    }

    [Fact]
    public void SecondCompletedStopRequestsFrenchTranslation()
    {
        HookResponseDecision decision = Create(
            "stop",
            """{"status":"completed","loop_count":1}""");

        string followup = Assert.IsType<string>(
            decision.Response["followup_message"]?.GetValue<string>());
        Assert.Contains("Translate", followup, StringComparison.Ordinal);
        Assert.Contains("French", followup, StringComparison.Ordinal);
        Assert.StartsWith(CursorCatBossText.Prefix, followup, StringComparison.Ordinal);
        Assert.Equal("auto_followup", decision.Effect?.EffectKind);
    }

    [Theory]
    [InlineData("completed", 2)]
    [InlineData("error", 0)]
    public void LaterOrFailedStopDoesNotContinue(string status, int loopCount)
    {
        HookResponseDecision decision = Create(
            "stop",
            JsonSerializer.Serialize(new { status, loop_count = loopCount }));

        Assert.Empty(decision.Response);
        Assert.Null(decision.Effect);
    }

    [Fact]
    public async Task ForwarderCarriesResponseAndPreservesNativePayload()
    {
        RecordingSink sink = new();
        HookForwarder forwarder = new(
            sink,
            new HookProcessOptions(ProviderProfile.Cursor),
            environment: new Dictionary<string, string?>(),
            responseProvider: _provider);
        string payload =
            """{"hook_event_name":"beforeSubmitPrompt","conversation_id":"c1","prompt":"Build me a GUID generator."}""";
        StringWriter output = new();

        int exitCode = await forwarder.RunAsync(
            [],
            new StringReader(payload),
            output);

        Assert.Equal(0, exitCode);
        string envelopeText = Assert.Single(sink.ForwardedLines);
        using JsonDocument envelope = JsonDocument.Parse(envelopeText);
        using JsonDocument stdout = JsonDocument.Parse(output.ToString());
        Assert.True(JsonElement.DeepEquals(
            stdout.RootElement,
            envelope.RootElement.GetProperty("harnessOutcome").GetProperty("response")));
        using JsonDocument original = JsonDocument.Parse(payload);
        Assert.True(JsonElement.DeepEquals(
            original.RootElement,
            envelope.RootElement.GetProperty("payload")));

        Assert.True(HookObservation.TryParse(envelopeText, out HookObservation? observation));
        Assert.NotNull(observation?.HarnessOutcome);
        Assert.DoesNotContain("harnessOutcome", observation!.DisplayJson);
        Assert.DoesNotContain("updated_input", observation.DisplayJson);
    }

    [Fact]
    public void VersionOneEnvelopeStillParsesWithoutOutcome()
    {
        string envelope =
            """
            {
              "ingressVersion":1,
              "eventId":"11111111-1111-1111-1111-111111111111",
              "observedAtUtc":"2026-10-31T08:00:00Z",
              "payload":{
                "hook_event_name":"sessionStart",
                "conversation_id":"legacy"
              }
            }
            """;

        Assert.True(HookObservation.TryParse(envelope, out HookObservation? observation));
        Assert.Null(observation?.HarnessOutcome);
    }

    [Fact]
    public void MalformedOutcomeDoesNotDiscardObservation()
    {
        string envelope =
            """
            {
              "ingressVersion":3,
              "eventId":"22222222-2222-2222-2222-222222222222",
              "observedAtUtc":"2026-10-31T08:00:00Z",
              "payload":{"hook_event_name":"sessionStart","conversation_id":"c1"},
              "harnessOutcome":{"response":"not-an-object"}
            }
            """;

        Assert.True(HookObservation.TryParse(envelope, out HookObservation? observation));
        Assert.Null(observation?.HarnessOutcome);
    }

    [Fact]
    public void ViewModelProjectsOutcomeOnceAndCanClearIt()
    {
        HookObservation observation = ParseObservationWithOutcome(
            requiresUserAction: false);
        MainWindowViewModel viewModel = new(
            new ReplayLoader(),
            showHookBehaviorPane: true);
        ObservationChange change = new(ObservationChangeKind.Add, observation);

        viewModel.ApplyObservationChange(change);
        viewModel.ApplyObservationChange(change);

        Assert.Empty(viewModel.HookBehaviorLogEntries);
        TreeNodeViewModel catSession = Assert.Single(
            viewModel.Roots.SelectMany(root => root.Children),
            node => node.IsSession);
        viewModel.SelectNode(catSession);

        Assert.Single(viewModel.HookBehaviorLogEntries);
        Assert.Equal("DENY", viewModel.HookBehaviorLogEntries[0].EffectLabel);
        Assert.Contains(
            "\"continue\": false",
            viewModel.HookBehaviorLogEntries[0].ResponseJson);
        Assert.DoesNotContain(
            CursorCatBossText.Prefix,
            viewModel.HookBehaviorLogEntries[0].Summary);
        Assert.True(viewModel.HasHookBehaviorLogEntries);

        viewModel.ClearHookBehaviorLog();
        Assert.Empty(viewModel.HookBehaviorLogEntries);
        Assert.False(viewModel.HasHookBehaviorLogEntries);
    }

    [Fact]
    public void CatBossLogFollowsSelectedSession()
    {
        HookObservation catObservation = ParseObservationWithOutcome(
            requiresUserAction: false);
        HookObservation normalObservation = ParseObservationWithoutOutcome();
        MainWindowViewModel viewModel = new(
            new ReplayLoader(),
            showHookBehaviorPane: true);

        viewModel.ApplyObservationChange(
            new ObservationChange(ObservationChangeKind.Add, catObservation));
        viewModel.ApplyObservationChange(
            new ObservationChange(ObservationChangeKind.Add, normalObservation));

        TreeNodeViewModel[] sessions = viewModel.Roots
            .SelectMany(root => root.Children)
            .Where(node => node.IsSession)
            .ToArray();
        TreeNodeViewModel catSession = Assert.Single(
            sessions,
            node => node.Header == "c1");
        TreeNodeViewModel normalSession = Assert.Single(
            sessions,
            node => node.Header == "normal");

        viewModel.SelectNode(catSession);
        Assert.Single(viewModel.HookBehaviorLogEntries);

        viewModel.SelectNode(normalSession);
        Assert.Empty(viewModel.HookBehaviorLogEntries);
        Assert.False(viewModel.HasHookBehaviorLogEntries);

        viewModel.SelectNode(catSession);
        Assert.Single(viewModel.HookBehaviorLogEntries);
    }

    [Fact]
    public void AttentionNotifierSoundsOnlyOnceWhenUserActionIsRequired()
    {
        RecordingSoundPlayer player = new();
        HookAttentionNotifier notifier = new(player);
        HookObservation observation = ParseObservationWithOutcome(
            requiresUserAction: true);

        notifier.NotifyIfNeeded(observation);
        notifier.NotifyIfNeeded(observation);

        Assert.Equal(1, player.PlayCount);
    }

    private HookResponseDecision Create(string eventName, string payload)
    {
        return Create(_provider, eventName, payload);
    }

    private static HookResponseDecision Create(
        CursorCatBossHookResponseProvider provider,
        string eventName,
        string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return provider.Create(eventName, document.RootElement);
    }

    private static HookObservation ParseObservationWithOutcome(bool requiresUserAction)
    {
        string envelope =
            $$"""
            {
              "ingressVersion":3,
              "eventId":"33333333-3333-3333-3333-333333333333",
              "observedAtUtc":"2026-10-31T08:00:00Z",
              "provider":0,
              "detectedSurface":0,
              "sourceKind":0,
              "rawEventName":"beforeSubmitPrompt",
              "payload":{
                "hook_event_name":"beforeSubmitPrompt",
                "conversation_id":"c1",
                "prompt":"Build me a GUID generator."
              },
              "harnessOutcome":{
                "mode":"cat-boss",
                "effectKind":"reject_prompt",
                "response":{"continue":false},
                "summary":"Cat rejected the prompt.",
                "requiresUserAction":{{requiresUserAction.ToString().ToLowerInvariant()}},
                "changes":[
                  {"target":"prompt submission","before":"pending","after":"blocked"}
                ]
              }
            }
            """;
        Assert.True(HookObservation.TryParse(envelope, out HookObservation? observation));
        return Assert.IsType<HookObservation>(observation);
    }

    private static HookObservation ParseObservationWithoutOutcome()
    {
        string envelope =
            """
            {
              "ingressVersion":3,
              "eventId":"44444444-4444-4444-4444-444444444444",
              "observedAtUtc":"2026-10-31T08:01:00Z",
              "provider":0,
              "detectedSurface":0,
              "sourceKind":0,
              "rawEventName":"sessionStart",
              "payload":{
                "hook_event_name":"sessionStart",
                "conversation_id":"normal"
              }
            }
            """;
        Assert.True(HookObservation.TryParse(envelope, out HookObservation? observation));
        return Assert.IsType<HookObservation>(observation);
    }

    private sealed class RecordingSink : IHookPayloadSink
    {
        public List<string> ForwardedLines { get; } = [];

        public Task ForwardAsync(
            ReadOnlyMemory<byte> payloadLine,
            CancellationToken cancellationToken)
        {
            ForwardedLines.Add(Encoding.UTF8.GetString(payloadLine.Span));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSoundPlayer : IAttentionSoundPlayer
    {
        public int PlayCount { get; private set; }

        public void Play() => PlayCount++;
    }
}
