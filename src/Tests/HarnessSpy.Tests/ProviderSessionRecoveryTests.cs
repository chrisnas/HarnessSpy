using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Claude;
using HarnessSpy.Core.Sessions.Copilot;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Tests;

public sealed class ProviderSessionRecoveryTests
{
    private readonly SessionPlanCatalogAssembler _planAssembler = new();

    [Fact]
    public async Task CopilotDiscoversMeaningfulWorkspaceWithoutEventLog()
    {
        string root = CreateTempDirectory();
        try
        {
            string stateRoot = Path.Combine(
                root,
                ".copilot",
                "session-state");
            string meaningful = Path.Combine(stateRoot, "metadata-session");
            string empty = Path.Combine(stateRoot, "empty-session");
            Directory.CreateDirectory(meaningful);
            Directory.CreateDirectory(empty);
            await File.WriteAllTextAsync(
                Path.Combine(meaningful, "workspace.yaml"),
                """
                id: metadata-session
                cwd: C:\repo
                summary: Recovered Copilot session
                created_at: 2026-10-01T08:00:00Z
                updated_at: 2026-10-01T08:05:00Z
                client_name: github/autopilot
                mc_task_id: task-1
                mc_session_id: mission-session
                """);
            await File.WriteAllTextAsync(
                Path.Combine(empty, "workspace.yaml"),
                """
                id: empty-session
                cwd: C:\repo
                created_at: 2026-10-01T08:00:00Z
                updated_at: 2026-10-01T08:00:00Z
                """);

            SessionCatalogScanResult result = await CopilotSource(root)
                .ScanAsync(CancellationToken.None);

            SessionCatalogEntry session = Assert.Single(result.Sessions);
            Assert.Equal("metadata-session", session.NativeSessionId);
            Assert.Equal("Recovered Copilot session", session.Title);
            Assert.Equal("true", session.Metadata["copilot.metadataOnly"]);
            Assert.Equal("task-1", session.Metadata["mc_task_id"]);
            Assert.Equal("mission-session", session.Metadata["mc_session_id"]);
            Assert.Equal(
                SessionLifecycleState.Closed,
                session.LifecycleState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CopilotPlanOnlyDirectoryCreatesBoundSessionAndPlan()
    {
        string root = CreateTempDirectory();
        try
        {
            string directory = Path.Combine(
                root,
                ".copilot",
                "session-state",
                "plan-only-session");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "plan.md"),
                "# Retained plan\n\nImplement it.\n");

            SessionCatalogScanResult result = await CopilotSource(root)
                .ScanAsync(CancellationToken.None);
            SessionCatalogEntry session = Assert.Single(result.Sessions);
            SessionPlanArtifact plan = Assert.Single(
                _planAssembler.Assemble(
                    result.Sessions,
                    result.PlanFragment));

            Assert.Equal(
                session.CatalogSessionId,
                plan.BoundCatalogSessionId);
            Assert.Equal(
                SessionPlanBindingReason.CopilotSessionDirectory,
                plan.BindingReason);
            Assert.Equal("Retained plan", plan.Title);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CopilotNativePlanChangeSupersedesToolFallback()
    {
        string root = CreateTempDirectory();
        try
        {
            string directory = Path.Combine(
                root,
                ".copilot",
                "session-state",
                "native-plan-session");
            Directory.CreateDirectory(directory);
            string planPath = Path.Combine(directory, "plan.md");
            await File.WriteAllTextAsync(planPath, "# Native plan\n");
            await File.WriteAllTextAsync(
                Path.Combine(directory, "workspace.yaml"),
                """
                id: native-plan-session
                cwd: C:\repo
                name: Native plan session
                created_at: 2026-10-01T08:00:00Z
                updated_at: 2026-10-01T08:00:05Z
                """);
            await WriteJsonLinesAsync(
                Path.Combine(directory, "events.jsonl"),
                new
                {
                    type = "session.start",
                    id = "start",
                    timestamp = "2026-10-01T08:00:00Z",
                    data = new
                    {
                        sessionId = "native-plan-session",
                        version = 1,
                        context = new { cwd = @"C:\repo" }
                    }
                },
                new
                {
                    type = "user.message",
                    id = "user",
                    timestamp = "2026-10-01T08:00:01Z",
                    data = new
                    {
                        interactionId = "interaction-1",
                        content = "make a plan"
                    }
                },
                new
                {
                    type = "assistant.message",
                    id = "assistant",
                    timestamp = "2026-10-01T08:00:02Z",
                    data = new
                    {
                        interactionId = "interaction-1",
                        messageId = "message-1",
                        toolRequests = new[]
                        {
                            new
                            {
                                toolCallId = "tool-1",
                                name = "create",
                                arguments = new
                                {
                                    path = planPath,
                                    file_text = "# Native plan"
                                }
                            }
                        }
                    }
                },
                new
                {
                    type = "tool.execution_complete",
                    id = "tool-complete",
                    timestamp = "2026-10-01T08:00:03Z",
                    data = new
                    {
                        interactionId = "interaction-1",
                        toolCallId = "tool-1",
                        toolName = "create",
                        success = true
                    }
                },
                new
                {
                    type = "session.plan_changed",
                    id = "plan-created",
                    parentId = "tool-complete",
                    timestamp = "2026-10-01T08:00:04Z",
                    data = new { operation = "create" }
                },
                new
                {
                    type = "session.shutdown",
                    id = "shutdown",
                    timestamp = "2026-10-01T08:00:05Z",
                    data = new { shutdownType = "routine" }
                });

            SessionCatalogScanResult result = await CopilotSource(root)
                .ScanAsync(CancellationToken.None);
            SessionPlanArtifact plan = Assert.Single(
                _planAssembler.Assemble(
                    result.Sessions,
                    result.PlanFragment));
            SessionPlanActivity activity = Assert.Single(plan.Activities);

            Assert.Equal(
                SessionPlanActivityKind.Created,
                activity.Kind);
            Assert.Equal("plan-created", activity.SourceEventId);
            Assert.Equal("interaction:interaction-1", activity.TurnId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CopilotPreservesConflictingWorkspaceEvidence()
    {
        string root = CreateTempDirectory();
        try
        {
            string directory = Path.Combine(
                root,
                ".copilot",
                "session-state",
                "workspace-conflict");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "workspace.yaml"),
                """
                id: workspace-conflict
                git_root: C:\repo-a
                name: Workspace conflict
                created_at: 2026-10-01T08:00:00Z
                updated_at: 2026-10-01T08:00:01Z
                """);
            await WriteJsonLinesAsync(
                Path.Combine(directory, "events.jsonl"),
                new
                {
                    type = "session.start",
                    id = "start",
                    timestamp = "2026-10-01T08:00:00Z",
                    data = new
                    {
                        sessionId = "workspace-conflict",
                        version = 1,
                        context = new
                        {
                            cwd = @"C:\repo-b",
                            gitRoot = @"C:\repo-b"
                        }
                    }
                },
                new
                {
                    type = "session.shutdown",
                    id = "shutdown",
                    timestamp = "2026-10-01T08:00:01Z",
                    data = new { shutdownType = "routine" }
                });

            SessionCatalogEntry session = Assert.Single(
                (await CopilotSource(root).ScanAsync(
                    CancellationToken.None)).Sessions);

            Assert.Equal(
                WorkspaceContext.FromRoot(@"C:\repo-a").Key,
                session.Workspace.Key);
            SessionWorkspaceInference inference =
                Assert.IsType<SessionWorkspaceInference>(
                    session.WorkspaceInference);
            Assert.Equal(InferenceEvidence.Ambiguous, inference.Evidence);
            Assert.Equal(2, inference.Alternatives.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClaudeHistoryBuildsSessionWithoutProjectsRoot()
    {
        string root = CreateTempDirectory();
        try
        {
            string configRoot = Path.Combine(root, ".claude");
            string workspace = Path.Combine(root, "history-workspace");
            Directory.CreateDirectory(configRoot);
            Directory.CreateDirectory(workspace);
            await WriteJsonLinesAsync(
                Path.Combine(configRoot, "history.jsonl"),
                new
                {
                    display = "Recovered Claude session",
                    timestamp = 1790841600000,
                    project = workspace,
                    sessionId = "history-session"
                });

            SessionCatalogScanResult result = await ClaudeSource(root)
                .ScanAsync(CancellationToken.None);
            SessionCatalogEntry session = Assert.Single(result.Sessions);

            Assert.Equal("history-session", session.NativeSessionId);
            Assert.Equal("Recovered Claude session", session.Title);
            Assert.Equal(
                WorkspaceContext.FromRoot(workspace).Key,
                session.Workspace.Key);
            Assert.Contains(
                session.Sources,
                static source =>
                    source.SourceKind ==
                    SessionSourceKind.ClaudeHistoryJsonl);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClaudeSessionCacheBuildsSessionWhenTranscriptIsGone()
    {
        string root = CreateTempDirectory();
        try
        {
            string project = Path.Combine(
                root,
                ".claude",
                "projects",
                "C--missing-project");
            Directory.CreateDirectory(project);
            string missingTranscript = Path.Combine(
                project,
                "cache-session.jsonl");
            Dictionary<string, object> entries = new()
            {
                [missingTranscript] = new
                {
                    session = new
                    {
                        actual_session_id = "cache-session",
                        file_path = missingTranscript,
                        project_name = "Cached project",
                        message_count = 12,
                        first_message_time = "2026-10-01T08:00:00Z",
                        last_message_time = "2026-10-01T08:05:00Z",
                        summary = "Recovered from session cache",
                        entrypoint = "cli"
                    },
                    sidechain_count = 0,
                    first_user_content = "cached prompt",
                    last_user_content = "cached prompt"
                }
            };
            await File.WriteAllTextAsync(
                Path.Combine(project, ".session_cache.json"),
                JsonSerializer.Serialize(new { version = 11, entries }));

            SessionCatalogScanResult result = await ClaudeSource(root)
                .ScanAsync(CancellationToken.None);
            SessionCatalogEntry session = Assert.Single(result.Sessions);

            Assert.Equal("cache-session", session.NativeSessionId);
            Assert.Equal("Recovered from session cache", session.Title);
            Assert.Equal("12", session.Metadata["claude.cache.message_count"]);
            Assert.Contains(
                session.Sources,
                static source =>
                    source.SourceKind ==
                    SessionSourceKind.ClaudeSessionCache);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClaudePlanScanDoesNotRequireProjectsRoot()
    {
        string root = CreateTempDirectory();
        try
        {
            string plans = Path.Combine(root, ".claude", "plans");
            Directory.CreateDirectory(plans);
            await File.WriteAllTextAsync(
                Path.Combine(plans, "orphan.md"),
                "# Standalone Claude plan\n");

            SessionCatalogScanResult result = await ClaudeSource(root)
                .ScanAsync(CancellationToken.None);

            Assert.Empty(result.Sessions);
            Assert.Single(result.PlanFragment.Artifacts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClaudeRecoversMissingPlanAndRejectsFailedExitRevision()
    {
        string root = CreateTempDirectory();
        try
        {
            string workspace = Path.Combine(root, "repo");
            string project = Path.Combine(
                root,
                ".claude",
                "projects",
                "project");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(project);
            string sessionId = "claude-plan-session";
            string planPath = Path.Combine(
                root,
                ".claude",
                "plans",
                "recovered-plan.md");
            await WriteJsonLinesAsync(
                Path.Combine(project, $"{sessionId}.jsonl"),
                ClaudeUser(
                    sessionId,
                    workspace,
                    "prompt-1",
                    "make a plan",
                    "2026-10-01T08:00:00Z"),
                ClaudeAttachment(
                    sessionId,
                    workspace,
                    "prompt-1",
                    planPath,
                    "plan_mode",
                    planExists: false,
                    "2026-10-01T08:00:01Z"),
                ClaudeToolRequest(
                    sessionId,
                    workspace,
                    "prompt-1",
                    "write-plan",
                    "Write",
                    new
                    {
                        file_path = planPath,
                        content = "# Initial plan\n\nFirst body."
                    },
                    "2026-10-01T08:00:02Z"),
                ClaudeToolResult(
                    sessionId,
                    workspace,
                    "prompt-1",
                    "write-plan",
                    isError: false,
                    "written",
                    "2026-10-01T08:00:03Z"),
                ClaudeToolRequest(
                    sessionId,
                    workspace,
                    "prompt-1",
                    "exit-rejected",
                    "ExitPlanMode",
                    new { plan = "# Rejected plan\n\nDo not retain." },
                    "2026-10-01T08:00:04Z"),
                ClaudeToolResult(
                    sessionId,
                    workspace,
                    "prompt-1",
                    "exit-rejected",
                    isError: true,
                    "User rejected tool use",
                    "2026-10-01T08:00:05Z"),
                ClaudeToolRequest(
                    sessionId,
                    workspace,
                    "prompt-1",
                    "exit-approved",
                    "ExitPlanMode",
                    new { plan = "# Approved plan\n\nFinal body." },
                    "2026-10-01T08:00:06Z"),
                ClaudeToolResult(
                    sessionId,
                    workspace,
                    "prompt-1",
                    "exit-approved",
                    isError: false,
                    "User approved the plan",
                    "2026-10-01T08:00:07Z"),
                ClaudeAttachment(
                    sessionId,
                    workspace,
                    "prompt-1",
                    planPath,
                    "plan_mode_exit",
                    planExists: true,
                    "2026-10-01T08:00:08Z"));

            SessionCatalogScanResult result = await ClaudeSource(root)
                .ScanAsync(CancellationToken.None);
            SessionPlanArtifact recovered = Assert.Single(
                result.PlanFragment.Artifacts);
            SessionPlanArtifact plan = Assert.Single(
                _planAssembler.Assemble(
                    result.Sessions,
                    result.PlanFragment));

            Assert.Equal("true", recovered.Metadata[
                "claude.plan.sourceFileMissing"]);
            Assert.Equal("Approved plan", plan.Title);
            Assert.NotNull(plan.BoundCatalogSessionId);
            Assert.Equal(1, plan.ObservedUpdateCount);
            Assert.Contains(
                plan.Activities,
                activity =>
                    activity.ToolCallId == "exit-rejected" &&
                    !activity.IsSuccessful);
            Assert.Contains(
                plan.Activities,
                activity =>
                    activity.ToolCallId == "exit-approved" &&
                    activity.IsSuccessful);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CopilotCliSessionCatalogSource CopilotSource(string root) =>
        new(Context(root));

    private static ClaudeCodeSessionCatalogSource ClaudeSource(string root) =>
        new(Context(root));

    private static SessionDiscoveryContext Context(string root) =>
        new(
            root,
            root,
            _ => null,
            new SessionDiscoveryLimits(
                MaximumDuration: TimeSpan.FromSeconds(5)));

    private static object ClaudeUser(
        string sessionId,
        string cwd,
        string promptId,
        string prompt,
        string timestamp) =>
        new
        {
            parentUuid = (string?)null,
            isSidechain = false,
            promptId,
            type = "user",
            message = new { role = "user", content = prompt },
            uuid = $"user-{promptId}",
            timestamp,
            origin = new { kind = "human" },
            cwd,
            sessionId
        };

    private static object ClaudeAttachment(
        string sessionId,
        string cwd,
        string promptId,
        string planPath,
        string attachmentType,
        bool planExists,
        string timestamp) =>
        new
        {
            parentUuid = $"parent-{timestamp}",
            isSidechain = false,
            promptId,
            attachment = new
            {
                type = attachmentType,
                planFilePath = planPath,
                planExists
            },
            type = "attachment",
            uuid = $"attachment-{timestamp}",
            timestamp,
            cwd,
            sessionId
        };

    private static object ClaudeToolRequest(
        string sessionId,
        string cwd,
        string promptId,
        string toolCallId,
        string toolName,
        object input,
        string timestamp) =>
        new
        {
            parentUuid = $"parent-{timestamp}",
            isSidechain = false,
            promptId,
            message = new
            {
                model = "claude-test",
                id = $"message-{toolCallId}",
                type = "message",
                role = "assistant",
                content = new[]
                {
                    new
                    {
                        type = "tool_use",
                        id = toolCallId,
                        name = toolName,
                        input
                    }
                }
            },
            type = "assistant",
            uuid = $"assistant-{toolCallId}",
            timestamp,
            cwd,
            sessionId
        };

    private static object ClaudeToolResult(
        string sessionId,
        string cwd,
        string promptId,
        string toolCallId,
        bool isError,
        string content,
        string timestamp) =>
        new
        {
            parentUuid = $"parent-{timestamp}",
            isSidechain = false,
            promptId,
            type = "user",
            message = new
            {
                role = "user",
                content = new[]
                {
                    new
                    {
                        type = "tool_result",
                        content,
                        is_error = isError,
                        tool_use_id = toolCallId
                    }
                }
            },
            uuid = $"result-{toolCallId}",
            timestamp,
            toolUseResult = isError ? "User rejected tool use" : content,
            toolDenialKind = isError ? "user-rejected" : null,
            cwd,
            sessionId
        };

    private static async Task WriteJsonLinesAsync(
        string path,
        params object[] rows)
    {
        await File.WriteAllLinesAsync(
            path,
            rows.Select(static row => JsonSerializer.Serialize(row)));
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "HarnessSpy-Provider-Recovery",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
