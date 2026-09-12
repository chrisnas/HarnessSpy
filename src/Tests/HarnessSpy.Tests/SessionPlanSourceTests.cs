using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Copilot;
using HarnessSpy.Core.Sessions.Cursor;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Tests;

public sealed class SessionPlanSourceTests
{
    private readonly SessionPlanCatalogAssembler _assembler = new();

    [Fact]
    public async Task CursorPlanBindsToCreatingSessionByStructuredContent()
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
            await File.WriteAllTextAsync(
                Path.Combine(transcriptDirectory, "cursor-session.jsonl"),
                CursorTranscript());

            string plansDirectory = Path.Combine(root, ".cursor", "plans");
            Directory.CreateDirectory(plansDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(plansDirectory, "my_plan.plan.md"),
                CursorPlanFile());

            SessionDiscoveryContext context = new(
                root,
                root,
                _ => null,
                new SessionDiscoveryLimits(MaximumDuration: TimeSpan.FromSeconds(5)));
            CursorSessionCatalogSource source = new(context);

            SessionCatalogScanResult result =
                await source.ScanAsync(CancellationToken.None);
            IReadOnlyList<SessionPlanArtifact> plans = _assembler.Assemble(
                result.Sessions,
                result.PlanFragment);

            SessionPlanArtifact plan = Assert.Single(plans);
            Assert.NotNull(plan.BoundCatalogSessionId);
            Assert.Equal(
                SessionPlanBindingReason.CursorStructuredContentMatch,
                plan.BindingReason);
            Assert.Equal(InferenceEvidence.Corroborated, plan.BindingEvidence);
            Assert.Equal(0, plan.ObservedUpdateCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CopilotPlanBindsToSessionByDirectory()
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
            await File.WriteAllTextAsync(
                Path.Combine(sessionDirectory, "plan.md"),
                "# Copilot plan\nStep one\n");

            SessionDiscoveryContext context = new(
                root,
                root,
                _ => null,
                new SessionDiscoveryLimits(MaximumDuration: TimeSpan.FromSeconds(5)));
            CopilotCliSessionCatalogSource source = new(context);

            SessionCatalogScanResult result =
                await source.ScanAsync(CancellationToken.None);
            SessionCatalogEntry session = Assert.Single(result.Sessions);
            IReadOnlyList<SessionPlanArtifact> plans = _assembler.Assemble(
                result.Sessions,
                result.PlanFragment);

            SessionPlanArtifact plan = Assert.Single(plans);
            Assert.Equal(session.CatalogSessionId, plan.BoundCatalogSessionId);
            Assert.Equal(
                SessionPlanBindingReason.CopilotSessionDirectory,
                plan.BindingReason);
            Assert.Equal(InferenceEvidence.Observed, plan.BindingEvidence);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CursorPlanWithoutMatchingSessionStaysOrphan()
    {
        string root = CreateTempDirectory();
        try
        {
            string plansDirectory = Path.Combine(root, ".cursor", "plans");
            Directory.CreateDirectory(plansDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(plansDirectory, "lonely.plan.md"),
                CursorPlanFile());

            SessionDiscoveryContext context = new(
                root,
                root,
                _ => null,
                new SessionDiscoveryLimits(MaximumDuration: TimeSpan.FromSeconds(5)));
            CursorSessionCatalogSource source = new(context);

            SessionCatalogScanResult result =
                await source.ScanAsync(CancellationToken.None);
            IReadOnlyList<SessionPlanArtifact> plans = _assembler.Assemble(
                result.Sessions,
                result.PlanFragment);

            SessionPlanArtifact plan = Assert.Single(plans);
            Assert.Null(plan.BoundCatalogSessionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CursorTranscript() =>
        """
        {"role":"user","message":{"content":[{"type":"text","text":"<user_query>make a plan</user_query>"}]}}
        {"role":"assistant","message":{"content":[{"type":"tool_use","name":"CreatePlan","input":{"name":"My Plan","overview":"Do the thing","plan":"# Body\nContent line","todos":[{"id":"step-1","content":"First step"}]}}]}}
        {"type":"turn_ended","status":"success"}
        """;

    private static string CursorPlanFile() =>
        "---\n" +
        "name: My Plan\n" +
        "overview: Do the thing\n" +
        "todos:\n" +
        "  - id: step-1\n" +
        "    content: First step\n" +
        "    status: completed\n" +
        "isProject: false\n" +
        "---\n" +
        "\n" +
        "# Body\n" +
        "Content line\n";

    private static string Fixture(params string[] parts) =>
        Path.Combine(
            [AppContext.BaseDirectory, "Fixtures", "SessionViewer", .. parts]);

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "HarnessSpy-SessionViewer-PlanTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
