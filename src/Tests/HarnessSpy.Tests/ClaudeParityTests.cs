using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Wpf.ViewModels;

namespace HarnessSpy.Tests;

// Verifies Claude native relationships and summaries: exact tool_use_id and
// agent_id correlation, native PascalCase names, and native tool grouping.
public sealed class ClaudeParityTests
{
    [Fact]
    public void PostToolUseNestsUnderPreToolUseByToolUseId()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_input":{"command":"dotnet test"},"tool_use_id":"t1"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_input":{"command":"dotnet test"},"tool_response":{"ok":true},"tool_use_id":"t1","duration_ms":40}""",
            "2026-08-20T12:00:02Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel pre = Assert.Single(turn.Children);
        Assert.Equal("PreToolUse", pre.Observation!.HookEventName);
        Assert.Equal("Bash", pre.Observation.ToolName);
        Assert.Equal("PostToolUse", Assert.Single(pre.Children).Observation!.HookEventName);
    }

    [Fact]
    public void PostToolBatchNestsUnderPreToolUseByToolUseId()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_input":{"command":"dotnet test"},"tool_use_id":"t1"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_input":{"command":"dotnet test"},"tool_response":{"ok":true},"tool_use_id":"t1","duration_ms":40}""",
            "2026-08-20T12:00:02Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolBatch","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_calls":[{"tool_name":"Bash","tool_input":{"command":"dotnet test"},"tool_use_id":"t1","tool_response":{"ok":true}}]}""",
            "2026-08-20T12:00:02.2Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel pre = Assert.Single(turn.Children);
        Assert.Equal("PreToolUse", pre.Observation!.HookEventName);
        Assert.Equal(2, pre.Children.Count);
        Assert.Equal("PostToolUse", pre.Children[0].Observation!.HookEventName);
        Assert.Equal("PostToolBatch", pre.Children[1].Observation!.HookEventName);
    }

    [Fact]
    public void PartialMatchPostToolBatchStaysUnmatched()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_use_id":"t1"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_use_id":"t1","duration_ms":40}""",
            "2026-08-20T12:00:02Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolBatch","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_calls":[{"tool_name":"Bash","tool_use_id":"t1"},{"tool_name":"Read","tool_use_id":"t2"}]}""",
            "2026-08-20T12:00:02.2Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        Assert.Equal(2, turn.Children.Count);
        TreeNodeViewModel pre = turn.Children[0];
        Assert.Equal("PreToolUse", pre.Observation!.HookEventName);
        Assert.Equal("PostToolUse", Assert.Single(pre.Children).Observation!.HookEventName);
        Assert.Equal("PostToolBatch", turn.Children[1].Observation!.HookEventName);
    }

    [Fact]
    public void MultiCallPostToolBatchGroupsMatchingPreToolUseNodes()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_use_id":"t1"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Read","tool_use_id":"t2"}""",
            "2026-08-20T12:00:01.1Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_use_id":"t1","duration_ms":40}""",
            "2026-08-20T12:00:02Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Read","tool_use_id":"t2","duration_ms":20}""",
            "2026-08-20T12:00:02.1Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolBatch","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_calls":[{"tool_name":"Bash","tool_use_id":"t1"},{"tool_name":"Read","tool_use_id":"t2"}]}""",
            "2026-08-20T12:00:02.2Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel batch = Assert.Single(turn.Children);
        Assert.Equal("PostToolBatch", batch.Observation!.HookEventName);
        Assert.Equal(2, batch.Children.Count);

        TreeNodeViewModel firstPre = batch.Children[0];
        Assert.Equal("PreToolUse", firstPre.Observation!.HookEventName);
        Assert.Equal("Bash", firstPre.Observation.ToolName);
        Assert.Equal("PostToolUse", Assert.Single(firstPre.Children).Observation!.HookEventName);

        TreeNodeViewModel secondPre = batch.Children[1];
        Assert.Equal("PreToolUse", secondPre.Observation!.HookEventName);
        Assert.Equal("Read", secondPre.Observation.ToolName);
        Assert.Equal("PostToolUse", Assert.Single(secondPre.Children).Observation!.HookEventName);
    }

    [Fact]
    public void MultiCallPostToolBatchAbsorbsExistingParallelWave()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_use_id":"t1"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Read","tool_use_id":"t2"}""",
            "2026-08-20T12:00:01.1Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_use_id":"t1","duration_ms":200}""",
            "2026-08-20T12:00:01.3Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Read","tool_use_id":"t2","duration_ms":20}""",
            "2026-08-20T12:00:01.4Z"));

        // The two completed pres overlap in time, so they were already merged
        // into a ParallelWave before the batch event arrives.
        TreeNodeViewModel waveTurn = OnlyTurn(viewModel);
        TreeNodeViewModel wave = Assert.Single(waveTurn.Children);
        Assert.Equal(TreeNodeKind.ParallelWave, wave.Kind);
        Assert.Equal(2, wave.Children.Count);

        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolBatch","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_calls":[{"tool_name":"Bash","tool_use_id":"t1"},{"tool_name":"Read","tool_use_id":"t2"}]}""",
            "2026-08-20T12:00:01.5Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel batch = Assert.Single(turn.Children);
        Assert.Equal("PostToolBatch", batch.Observation!.HookEventName);
        Assert.Equal(2, batch.Children.Count);
    }

    [Fact]
    public void McpToolNameTonesPreAndPostToolUseGreen()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"mcp__dstrings__get_duplicated_strings","tool_use_id":"t1"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"mcp__dstrings__get_duplicated_strings","tool_use_id":"t1","duration_ms":40}""",
            "2026-08-20T12:00:02Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel pre = Assert.Single(turn.Children);
        Assert.True(pre.IsMcpExecution);
        Assert.True(Assert.Single(pre.Children).IsMcpExecution);
    }

    [Fact]
    public void NativeToolNameIsNotTonedGreen()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_use_id":"t1"}""",
            "2026-08-20T12:00:01Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel pre = Assert.Single(turn.Children);
        Assert.False(pre.IsMcpExecution);
    }

    [Fact]
    public void PostToolBatchAllMcpIsTonedGreen()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolBatch","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_calls":[{"tool_name":"mcp__dstrings__get_duplicated_strings","tool_use_id":"t1"},{"tool_name":"mcp__pstacks__get_parallel_stacks","tool_use_id":"t2"}]}""",
            "2026-08-20T12:00:01Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel batch = Assert.Single(turn.Children);
        Assert.Equal("PostToolBatch", batch.Observation!.HookEventName);
        Assert.True(batch.IsMcpExecution);
    }

    [Fact]
    public void PostToolBatchMixedMcpAndNativeIsNotTonedGreen()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolBatch","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_calls":[{"tool_name":"mcp__dstrings__get_duplicated_strings","tool_use_id":"t1"},{"tool_name":"Bash","tool_use_id":"t2"}]}""",
            "2026-08-20T12:00:01Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel batch = Assert.Single(turn.Children);
        Assert.Equal("PostToolBatch", batch.Observation!.HookEventName);
        Assert.False(batch.IsMcpExecution);
    }

    [Fact]
    public void SubagentStopNestsUnderStartByAgentId()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"SubagentStart","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","agent_id":"a1","agent_type":"Explore"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"SubagentStop","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","agent_id":"a1","agent_type":"Explore","last_assistant_message":"Found it"}""",
            "2026-08-20T12:00:03Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel start = Assert.Single(turn.Children);
        Assert.Equal("SubagentStart", start.Observation!.HookEventName);
        Assert.Equal("SubagentStop", Assert.Single(start.Children).Observation!.HookEventName);
    }

    [Fact]
    public void SummaryCountsMcpToolCallsSeparatelyFromNativeTools()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"mcp__dstrings__get_duplicated_strings","tool_use_id":"t1"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"mcp__dstrings__get_duplicated_strings","tool_use_id":"t1","duration_ms":40}""",
            "2026-08-20T12:00:02Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_use_id":"t2"}""",
            "2026-08-20T12:00:03Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_use_id":"t2","duration_ms":10}""",
            "2026-08-20T12:00:04Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        NodeSummary summary = Assert.IsType<NodeSummary>(turn.NodeSummary);
        Assert.Equal("Bash", Assert.Single(summary.Tools).Name);
        CountedDurationRow mcpRow = Assert.Single(summary.McpCalls);
        Assert.Equal("dstrings/get_duplicated_strings", mcpRow.Name);
        Assert.Equal(1, mcpRow.Count);
        Assert.Equal(40, mcpRow.DurationMs);
    }

    [Fact]
    public void SummaryGroupsToolsByExactNativeName()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"UserPromptSubmit","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","prompt":"build"}""",
            "2026-08-20T12:00:00Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_use_id":"t1"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Bash","tool_use_id":"t1","duration_ms":30}""",
            "2026-08-20T12:00:02Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        NodeSummary summary = Assert.IsType<NodeSummary>(turn.NodeSummary);
        Assert.Equal("Bash", Assert.Single(summary.Tools).Name);
    }

    [Fact]
    public void SummarySplitsReadAndEditIntoFileReadsAndWrites()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Read","tool_input":{"file_path":"C:\\Repo\\src\\App.cs"},"tool_use_id":"t1"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Read","tool_input":{"file_path":"C:\\Repo\\src\\App.cs"},"tool_response":{"ok":true},"tool_use_id":"t1","duration_ms":10}""",
            "2026-08-20T12:00:02Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Edit","tool_input":{"file_path":"C:\\Repo\\src\\Program.cs"},"tool_use_id":"t2"}""",
            "2026-08-20T12:00:03Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"Edit","tool_input":{"file_path":"C:\\Repo\\src\\Program.cs"},"tool_response":{"ok":true},"tool_use_id":"t2","duration_ms":10}""",
            "2026-08-20T12:00:04Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        NodeSummary summary = Assert.IsType<NodeSummary>(turn.NodeSummary);
        Assert.Equal("C:\\Repo\\src\\App.cs", Assert.Single(summary.ReadFiles).FullPath);
        Assert.Equal("C:\\Repo\\src\\Program.cs", Assert.Single(summary.WrittenFiles).FullPath);
        Assert.Empty(summary.DeletedFiles);
    }

    [Fact]
    public void PostToolUseNestsUnderPreToolUseWhenInputEchoesAnswers()
    {
        // AskUserQuestion/ExitPlanMode echo the request input back in their
        // PostToolUse plus an "answers" block, so the canonical tool_input
        // differs from the request. The shared tool_use_id must still pair them.
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Ship it?"}]},"tool_use_id":"t1"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Ship it?"}],"answers":{"Ship it?":"Yes"}},"tool_response":{"ok":true},"tool_use_id":"t1","duration_ms":0}""",
            "2026-08-20T12:00:05Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel pre = Assert.Single(turn.Children);
        Assert.Equal("PreToolUse", pre.Observation!.HookEventName);
        Assert.Equal("PostToolUse", Assert.Single(pre.Children).Observation!.HookEventName);
    }

    [Fact]
    public void PermissionRequestNestsUnderMatchingInFlightPreToolUse()
    {
        // A PermissionRequest carries no tool_use_id, so it pairs to its open
        // request by tool name and canonical tool_input, and does not close it.
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Ship it?"}]},"tool_use_id":"t1"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PermissionRequest","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Ship it?"}]}}""",
            "2026-08-20T12:00:02Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel pre = Assert.Single(turn.Children);
        TreeNodeViewModel permission = Assert.Single(pre.Children);
        Assert.Equal("PermissionRequest", permission.Observation!.HookEventName);
        Assert.True(permission.IsPermission);
    }

    [Fact]
    public void PermissionRequestPostToolUseAndBatchAllNestUnderOneRequest()
    {
        // The full AskUserQuestion approval flow from a real trace: the request
        // stays the single parent of its permission prompt, completion, and
        // one-call batch, while the permission-prompt notification is a
        // highlighted sibling.
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Ship it?"}]},"tool_use_id":"t1"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PermissionRequest","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Ship it?"}]}}""",
            "2026-08-20T12:00:02Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"Notification","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","message":"Claude Code needs your approval","notification_type":"permission_prompt"}""",
            "2026-08-20T12:00:03Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolUse","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Ship it?"}],"answers":{"Ship it?":"Yes"}},"tool_response":{"ok":true},"tool_use_id":"t1","duration_ms":0}""",
            "2026-08-20T12:00:20Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostToolBatch","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","tool_calls":[{"tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Ship it?"}]},"tool_use_id":"t1","tool_response":{"ok":true}}]}""",
            "2026-08-20T12:00:20.2Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        Assert.Equal(2, turn.Children.Count);

        TreeNodeViewModel pre = turn.Children[0];
        Assert.Equal("PreToolUse", pre.Observation!.HookEventName);
        Assert.Collection(
            pre.Children,
            child => Assert.Equal("PermissionRequest", child.Observation!.HookEventName),
            child => Assert.Equal("PostToolUse", child.Observation!.HookEventName),
            child => Assert.Equal("PostToolBatch", child.Observation!.HookEventName));

        TreeNodeViewModel notification = turn.Children[1];
        Assert.Equal("Notification", notification.Observation!.HookEventName);
        Assert.True(notification.IsPermission);
    }

    [Fact]
    public void PermissionPromptNotificationUsesPermissionTone()
    {
        HookObservation observation = Claude(
            """{"hook_event_name":"Notification","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","message":"Claude Code needs your approval","notification_type":"permission_prompt"}""",
            "2026-08-20T12:00:03Z");

        Assert.Equal(ObservationTone.Permission, observation.Interpretation.Tone);
    }

    [Fact]
    public void PostCompactNestsUnderPreCompact()
    {
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreCompact","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","trigger":"auto"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostCompact","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","trigger":"auto","compact_summary":"summary"}""",
            "2026-08-20T12:00:05Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel pre = Assert.Single(turn.Children);
        Assert.Equal("PreCompact", pre.Observation!.HookEventName);
        Assert.True(pre.IsCompaction);
        TreeNodeViewModel post = Assert.Single(pre.Children);
        Assert.Equal("PostCompact", post.Observation!.HookEventName);
        Assert.True(post.IsCompaction);
    }

    [Fact]
    public void CompactionSummarizerSubagentStopNestsUnderPreCompact()
    {
        // The auto-compaction summarizer's SubagentStop has no SubagentStart to
        // nest under, and its final message is the PostCompact compact_summary,
        // so it is pulled under the PreCompact ahead of the PostCompact.
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreCompact","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","trigger":"auto"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"SubagentStop","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","agent_id":"sum1","agent_type":"","last_assistant_message":"SUMMARY BODY"}""",
            "2026-08-20T12:00:03Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostCompact","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","trigger":"auto","compact_summary":"<analysis> SUMMARY BODY </analysis>"}""",
            "2026-08-20T12:00:05Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        TreeNodeViewModel pre = Assert.Single(turn.Children);
        Assert.Equal("PreCompact", pre.Observation!.HookEventName);
        Assert.Collection(
            pre.Children,
            child => Assert.Equal("SubagentStop", child.Observation!.HookEventName),
            child => Assert.Equal("PostCompact", child.Observation!.HookEventName));
    }

    [Fact]
    public void UnrelatedSubagentStopStaysOutsidePreCompact()
    {
        // A SubagentStop whose message is not the compaction summary is left as
        // a sibling; only the PostCompact nests under the PreCompact.
        MainWindowViewModel viewModel = new();
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PreCompact","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","trigger":"auto"}""",
            "2026-08-20T12:00:01Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"SubagentStop","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","agent_id":"other","agent_type":"reviewer","last_assistant_message":"unrelated review output"}""",
            "2026-08-20T12:00:03Z"));
        viewModel.AddObservation(Claude(
            """{"hook_event_name":"PostCompact","session_id":"s1","prompt_id":"p1","cwd":"C:\\Repo","trigger":"auto","compact_summary":"a different compaction summary"}""",
            "2026-08-20T12:00:05Z"));

        TreeNodeViewModel turn = OnlyTurn(viewModel);
        Assert.Equal(2, turn.Children.Count);
        TreeNodeViewModel pre = turn.Children[0];
        Assert.Equal("PreCompact", pre.Observation!.HookEventName);
        Assert.Equal("PostCompact", Assert.Single(pre.Children).Observation!.HookEventName);
        Assert.Equal("SubagentStop", turn.Children[1].Observation!.HookEventName);
    }

    private static TreeNodeViewModel OnlyTurn(MainWindowViewModel viewModel)
    {
        TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
        TreeNodeViewModel session = Assert.Single(workspace.Children);
        return Assert.Single(session.Children, child => child.Kind == TreeNodeKind.Generation);
    }

    private static HookObservation Claude(string payloadJson, string observedAtUtc)
    {
        using JsonDocument payloadDocument = JsonDocument.Parse(payloadJson);
        ObservationEnvelope envelope = new(
            ObservationEnvelope.CurrentIngressVersion,
            Guid.NewGuid(),
            DateTimeOffset.Parse(observedAtUtc),
            HookProvider.ClaudeCode,
            HookSurface.ClaudeCode,
            HookSurface.ClaudeCode,
            ObservationSourceKind.Hook,
            null,
            payloadDocument.RootElement.GetProperty("hook_event_name").GetString(),
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
}
