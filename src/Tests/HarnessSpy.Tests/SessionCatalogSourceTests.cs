using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Claude;
using HarnessSpy.Core.Sessions.Copilot;
using HarnessSpy.Core.Sessions.Cursor;
using Microsoft.Data.Sqlite;

namespace HarnessSpy.Tests;

public sealed class SessionCatalogSourceTests
{
    [Fact]
    public async Task CursorSourceBuildsDerivedTurnAndParallelTools()
    {
        string root = CreateTempDirectory();
        try
        {
            string transcriptDirectory = Path.Combine(
                root,
                ".cursor",
                "projects",
                "c-repo",
                "agent-transcripts",
                "cursor-session");
            Directory.CreateDirectory(transcriptDirectory);
            File.Copy(
                Fixture("Cursor", "session.jsonl"),
                Path.Combine(transcriptDirectory, "cursor-session.jsonl"));

            SessionDiscoveryContext context = new(
                root,
                root,
                _ => null,
                new SessionDiscoveryLimits(MaximumDuration: TimeSpan.FromSeconds(5)));
            CursorSessionCatalogSource source = new(context);

            SessionCatalogScanResult result = await source.ScanAsync(
                CancellationToken.None);

            SessionCatalogEntry session = Assert.Single(result.Sessions);
            SessionTurn turn = Assert.Single(session.Turns);
            Assert.Equal(HookProvider.Cursor, session.Provider);
            Assert.Equal("inspect the project", turn.Prompt);
            SessionEventRecord[] tools = turn.Events
                .Where(static item => item.Role == ObservationRole.ToolRequest)
                .ToArray();
            Assert.Equal(2, tools.Length);
            Assert.All(tools, static item => Assert.True(item.IsParallelCandidate));
            Assert.Single(tools.Select(static item => item.ParallelGroupId).Distinct());
            Assert.Contains(
                turn.Events,
                static item =>
                    item.Role == ObservationRole.AgentThought &&
                    item.Text == "I should inspect two areas.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CursorSourceReadsDesktopComposerAndBubbles()
    {
        string root = CreateTempDirectory();
        try
        {
            string globalStorage = Path.Combine(
                root,
                "Cursor",
                "User",
                "globalStorage");
            Directory.CreateDirectory(globalStorage);
            string databasePath = Path.Combine(globalStorage, "state.vscdb");
            await CreateCursorDatabaseAsync(databasePath);

            SessionDiscoveryContext context = new(
                root,
                root,
                _ => null,
                new SessionDiscoveryLimits(MaximumDuration: TimeSpan.FromSeconds(5)));
            CursorSessionCatalogSource source = new(context);

            SessionCatalogScanResult result = await source.ScanAsync(
                CancellationToken.None);

            SessionCatalogEntry session = Assert.Single(result.Sessions);
            Assert.Equal("Desktop session", session.Title);
            SessionTurn turn = Assert.Single(session.Turns);
            Assert.Equal("inspect desktop", turn.Prompt);
            Assert.Contains(
                turn.Events,
                static item =>
                    item.Role == ObservationRole.AgentThought &&
                    item.Text == "Thinking about it");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CursorSourceStreamsEnrichedDesktopSessionsDuringScan()
    {
        string root = CreateTempDirectory();
        try
        {
            string globalStorage = Path.Combine(
                root,
                "Cursor",
                "User",
                "globalStorage");
            Directory.CreateDirectory(globalStorage);
            string databasePath = Path.Combine(
                globalStorage,
                "state.vscdb");
            await CreateCursorStreamingDatabaseAsync(
                databasePath,
                sessionCount: 4);

            SessionDiscoveryContext context = new(
                root,
                root,
                _ => null,
                new SessionDiscoveryLimits(
                    MaximumDuration: TimeSpan.FromSeconds(5)));
            CursorSessionCatalogSource source = new(context);
            RecordingProgress<IReadOnlyList<SessionCatalogEntry>> progress =
                new();

            SessionCatalogScanResult result = await source.ScanAsync(
                progress,
                CancellationToken.None);

            Assert.Equal(4, result.Sessions.Count);
            Assert.NotEmpty(progress.Values);

            // Skeletons (no turns) are published first, then the same sessions
            // are rehydrated with their turns. Snapshots are time-coalesced, so
            // assert the skeleton-then-hydrate progression rather than exact
            // per-session batch sizes.
            Assert.Contains(
                progress.Values,
                static batch => batch.Any(session => session.Turns.Count == 0));
            Assert.Contains(
                progress.Values,
                static batch =>
                    batch.Count == 4 &&
                    batch.All(session => session.Turns.Count > 0));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CursorProgressIncludesTranscriptOnlySessionsBeforeFinal()
    {
        string root = CreateTempDirectory();
        try
        {
            string globalStorage = Path.Combine(
                root,
                "Cursor",
                "User",
                "globalStorage");
            Directory.CreateDirectory(globalStorage);
            await CreateCursorStreamingDatabaseAsync(
                Path.Combine(globalStorage, "state.vscdb"),
                sessionCount: 4);

            string transcriptDirectory = Path.Combine(
                root,
                ".cursor",
                "projects",
                "c-repo",
                "agent-transcripts",
                "cursor-session");
            Directory.CreateDirectory(transcriptDirectory);
            File.Copy(
                Fixture("Cursor", "session.jsonl"),
                Path.Combine(
                    transcriptDirectory,
                    "cursor-session.jsonl"));

            SessionDiscoveryContext context = new(
                root,
                root,
                _ => null,
                new SessionDiscoveryLimits(
                    MaximumDuration: TimeSpan.FromSeconds(5)));
            CursorSessionCatalogSource source = new(context);
            RecordingProgress<IReadOnlyList<SessionCatalogEntry>> progress =
                new();

            SessionCatalogScanResult result = await source.ScanAsync(
                progress,
                CancellationToken.None);

            Assert.Equal(5, result.Sessions.Count);
            Assert.Equal(result.Sessions.Count, progress.Values.Last().Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClaudeSourceBuildsTurnsAndParallelTools()
    {
        string root = CreateTempDirectory();
        try
        {
            string project = Path.Combine(
                root,
                ".claude",
                "projects",
                "C--repo");
            Directory.CreateDirectory(project);
            File.Copy(
                Fixture("Claude", "session.jsonl"),
                Path.Combine(project, "claude-session.jsonl"));

            SessionDiscoveryContext context = new(
                root,
                root,
                _ => null,
                new SessionDiscoveryLimits(MaximumDuration: TimeSpan.FromSeconds(5)));
            ClaudeCodeSessionCatalogSource source = new(context);

            SessionCatalogScanResult result = await source.ScanAsync(
                CancellationToken.None);

            SessionCatalogEntry session = Assert.Single(result.Sessions);
            SessionTurn turn = Assert.Single(session.Turns);
            Assert.Equal(HookProvider.ClaudeCode, session.Provider);
            Assert.Equal("run the tests", turn.Prompt);
            Assert.Equal(
                2,
                turn.Events.Count(static item =>
                    item.Role == ObservationRole.ToolRequest));
            Assert.Contains(
                turn.Events,
                static item =>
                    item.Role == ObservationRole.AgentThought &&
                    item.Text == "I should run both suites.");
            Assert.Contains(
                turn.Events,
                static item =>
                    item.Role == ObservationRole.ToolSuccess &&
                    item.ToolCallId == "tool-1");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClaudeSourceDetectsSkillToolInvocation()
    {
        string root = CreateTempDirectory();
        try
        {
            string project = Path.Combine(
                root,
                ".claude",
                "projects",
                "C--repo");
            Directory.CreateDirectory(project);

            // A skill invocation surfaces as the "Skill" tool_use whose input
            // names the activated skill; no SKILL.md read precedes it here.
            await File.WriteAllLinesAsync(
                Path.Combine(project, "skill-session.jsonl"),
                [
                    """{"type":"user","promptId":"prompt-1","uuid":"user-1","sessionId":"skill-session","cwd":"C:\\repo","timestamp":"2026-09-05T08:00:00Z","message":{"role":"user","content":[{"type":"text","text":"find memory leaks"}]}}""",
                    """{"type":"assistant","promptId":"prompt-1","uuid":"assistant-1","parentUuid":"user-1","sessionId":"skill-session","timestamp":"2026-09-05T08:00:01Z","message":{"role":"assistant","model":"claude-sonnet","content":[{"type":"tool_use","id":"tool-1","name":"Skill","input":{"skill":"dotnet-memory-analysis","args":"look for leaks in the dump"}}]}}"""
                ]);

            SessionDiscoveryContext context = new(
                root,
                root,
                _ => null,
                new SessionDiscoveryLimits(MaximumDuration: TimeSpan.FromSeconds(5)));
            ClaudeCodeSessionCatalogSource source = new(context);

            SessionCatalogScanResult result = await source.ScanAsync(
                CancellationToken.None);

            SessionCatalogEntry session = Assert.Single(result.Sessions);
            SkillEvidence skill = Assert.Single(
                session.Turns
                    .SelectMany(static turn => turn.Events)
                    .Select(static item => item.Skill)
                    .OfType<SkillEvidence>());
            Assert.Equal("dotnet-memory-analysis", skill.SkillName);
            Assert.Equal(SkillEvidenceStage.Invoked, skill.Stage);
            Assert.Equal(InferenceEvidence.Observed, skill.Evidence);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CopilotSourceBuildsExactTurnReasoningAndTools()
    {
        string root = CreateTempDirectory();
        try
        {
            string sessionDirectory = Path.Combine(
                root,
                ".copilot",
                "session-state",
                "copilot-session");
            Directory.CreateDirectory(sessionDirectory);
            File.Copy(
                Fixture("Copilot", "events.jsonl"),
                Path.Combine(sessionDirectory, "events.jsonl"));
            File.Copy(
                Fixture("Copilot", "workspace.yaml"),
                Path.Combine(sessionDirectory, "workspace.yaml"));

            SessionDiscoveryContext context = new(
                root,
                root,
                _ => null,
                new SessionDiscoveryLimits(MaximumDuration: TimeSpan.FromSeconds(5)));
            CopilotCliSessionCatalogSource source = new(context);

            SessionCatalogScanResult result = await source.ScanAsync(
                CancellationToken.None);

            SessionCatalogEntry session = Assert.Single(result.Sessions);
            SessionTurn turn = Assert.Single(session.Turns);
            Assert.Equal(HookProvider.GitHubCopilot, session.Provider);
            Assert.Equal("inspect two files", turn.Prompt);
            Assert.Equal("plan", session.Mode);
            Assert.Contains(
                turn.Events,
                static item =>
                    item.Role == ObservationRole.AgentThought &&
                    item.Text == "I should read both files.");
            SessionEventRecord[] tools = turn.Events
                .Where(static item => item.Role == ObservationRole.ToolRequest)
                .ToArray();
            Assert.Equal(2, tools.Length);
            Assert.All(tools, static item => Assert.True(item.IsParallelCandidate));
            Assert.Contains(
                turn.Events,
                static item =>
                    item.Role == ObservationRole.ToolSuccess &&
                    item.ToolCallId == "tool-1");
            Assert.Equal("5000", session.Metadata["totalApiDurationMs"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Fixture(params string[] parts) =>
        Path.Combine(
            [AppContext.BaseDirectory, "Fixtures", "SessionViewer", .. parts]);

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "HarnessSpy-SessionViewer-Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task CreateCursorDatabaseAsync(string path)
    {
        await using SqliteConnection connection = new($"Data Source={path}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value BLOB);
            INSERT INTO cursorDiskKV(key, value) VALUES
            (
                'composerData:desktop-session',
                '{"composerId":"desktop-session","name":"Desktop session","createdAt":1788595200000,"lastUpdatedAt":1788595203000,"workspaceIdentifier":{"id":"workspace-1","uri":{"fsPath":"C:\\repo"}},"fullConversationHeadersOnly":[{"bubbleId":"user-1","type":1},{"bubbleId":"thought-1","type":2}]}'
            ),
            (
                'bubbleId:desktop-session:user-1',
                '{"bubbleId":"user-1","type":1,"createdAt":1788595200000,"text":"inspect desktop","requestId":"request-1"}'
            ),
            (
                'bubbleId:desktop-session:thought-1',
                '{"bubbleId":"thought-1","type":2,"createdAt":1788595201000,"text":"Thinking about it","isThought":true,"requestId":"request-1"}'
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateCursorStreamingDatabaseAsync(
        string path,
        int sessionCount)
    {
        await using SqliteConnection connection = new($"Data Source={path}");
        await connection.OpenAsync();
        await using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value BLOB);";
            await create.ExecuteNonQueryAsync();
        }

        for (int index = 1; index <= sessionCount; index++)
        {
            string composerId = $"desktop-session-{index}";
            string bubbleId = $"user-{index}";
            string composer = JsonSerializer.Serialize(new
            {
                composerId,
                name = $"Desktop session {index}",
                createdAt = 1788595200000L + index,
                lastUpdatedAt = 1788595203000L + index,
                workspaceIdentifier = new
                {
                    id = "workspace-1",
                    uri = new { fsPath = @"C:\repo" }
                },
                fullConversationHeadersOnly = new[]
                {
                    new { bubbleId, type = 1 }
                }
            });
            string bubble = JsonSerializer.Serialize(new
            {
                bubbleId,
                type = 1,
                createdAt = 1788595200000L + index,
                text = $"prompt {index}",
                requestId = $"request-{index}"
            });

            await using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO cursorDiskKV(key, value)
                VALUES ($composerKey, $composer), ($bubbleKey, $bubble);
                """;
            insert.Parameters.AddWithValue(
                "$composerKey",
                $"composerData:{composerId}");
            insert.Parameters.AddWithValue("$composer", composer);
            insert.Parameters.AddWithValue(
                "$bubbleKey",
                $"bubbleId:{composerId}:{bubbleId}");
            insert.Parameters.AddWithValue("$bubble", bubble);
            await insert.ExecuteNonQueryAsync();
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }
}
