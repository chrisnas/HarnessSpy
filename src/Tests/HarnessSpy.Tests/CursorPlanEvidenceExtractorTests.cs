using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Cursor;
using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Tests;

public sealed class CursorPlanEvidenceExtractorTests
{
    private readonly CursorPlanEvidenceExtractor _extractor = new();

    [Fact]
    public void ExtractsPlanPathFromCursorAttachmentUri()
    {
        const string text =
            "<code_selection path=\"" +
            "file:///c%3A/Users/Test/.cursor/plans/example.plan.md\">";

        string path = Assert.Single(_extractor.ExtractPlanPaths(text));

        Assert.True(Path.IsPathRooted(path));
        Assert.EndsWith(
            @"\.cursor\plans\example.plan.md",
            path,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractsWorkspacePathsAndLeadingSessionIdentifier()
    {
        const string sessionId =
            "f7a1468f-72a9-4c07-b773-aa0db6395576";
        string markdown =
            $"<!-- {sessionId} -->\n" +
            "---\nname: Example\n---\n" +
            "Update [the implementation](C:\\repo\\src\\feature.cs).\n";

        Assert.Equal(
            sessionId,
            _extractor.ExtractNativeSessionIdHint(markdown));
        Assert.Equal(
            @"C:\repo\src\feature.cs",
            Assert.Single(_extractor.ExtractWorkspacePathHints(markdown)));
    }

    [Fact]
    public void PromptAttachmentCreatesReferenceActivityWithoutOwnership()
    {
        const string planUri =
            "file:///c%3A/Users/Test/.cursor/plans/example.plan.md";
        SessionSourceProvenance provenance = new(
            SessionSourceKind.CursorTranscriptJsonl,
            @"C:\session.jsonl",
            "jsonl");
        SessionEventRecord prompt = new()
        {
            Id = "prompt-1",
            NativeName = "user",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Role = ObservationRole.PromptSubmitted,
            EventKind = CanonicalEventKind.PromptSubmitted,
            TurnId = "turn-1",
            Text =
                $"<attached_files><code_selection path=\"{planUri}\">" +
                "</attached_files>",
            Provenance = provenance
        };
        SessionTurn turn = new(
            "turn-1",
            1,
            "Implement this plan",
            null,
            null,
            InferenceEvidence.Observed,
            [prompt]);
        SessionCatalogEntry session = new()
        {
            CatalogSessionId = "cursor:s1",
            NativeSessionId = "s1",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Workspace = WorkspaceContext.FromRoot(@"C:\repo"),
            Title = "Session",
            Turns = [turn]
        };

        SessionPlanActivity activity = Assert.Single(
            new CursorPlanActivityExtractor().Extract([session]));

        Assert.Equal(SessionPlanActivityKind.Referenced, activity.Kind);
        Assert.False(activity.EstablishesOwnership);
        Assert.True(activity.SupportsWorkspaceInference);
        Assert.EndsWith(
            @"\.cursor\plans\example.plan.md",
            activity.Locator.Path,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ReadFile", CanonicalToolKind.FileRead)]
    [InlineData("rg", CanonicalToolKind.TextSearch)]
    [InlineData("Glob", CanonicalToolKind.FileSearch)]
    [InlineData("Shell", CanonicalToolKind.Shell)]
    [InlineData("UnknownTool", CanonicalToolKind.Unknown)]
    public void NonMutatingToolPlanPathCreatesReferenceWithoutWorkspaceEvidence(
        string toolName,
        CanonicalToolKind toolKind)
    {
        SessionPlanActivity activity = ExtractToolActivity(toolName, toolKind);

        Assert.Equal(SessionPlanActivityKind.Referenced, activity.Kind);
        Assert.False(activity.HasOpaqueResult);
        Assert.False(activity.EstablishesOwnership);
        Assert.False(activity.SupportsWorkspaceInference);
    }

    [Theory]
    [InlineData("WriteFile", CanonicalToolKind.FileWrite)]
    [InlineData("ApplyPatch", CanonicalToolKind.FileEdit)]
    public void MutatingToolPlanPathCreatesOpaqueUpdate(
        string toolName,
        CanonicalToolKind toolKind)
    {
        SessionPlanActivity activity = ExtractToolActivity(toolName, toolKind);

        Assert.Equal(SessionPlanActivityKind.Updated, activity.Kind);
        Assert.True(activity.HasOpaqueResult);
    }

    private static SessionPlanActivity ExtractToolActivity(
        string toolName,
        CanonicalToolKind toolKind)
    {
        const string planPath =
            @"C:\Users\Test\.cursor\plans\example.plan.md";
        SessionSourceProvenance provenance = new(
            SessionSourceKind.CursorTranscriptJsonl,
            @"C:\session.jsonl",
            "jsonl");
        SessionEventRecord tool = new()
        {
            Id = "tool-1",
            NativeName = toolName,
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Role = ObservationRole.ToolRequest,
            EventKind = CanonicalEventKind.ToolRequested,
            ToolKind = toolKind,
            ToolName = toolName,
            TurnId = "turn-1",
            TargetPaths = [planPath],
            Provenance = provenance
        };
        SessionTurn turn = new(
            "turn-1",
            1,
            "Inspect the plan",
            null,
            null,
            InferenceEvidence.Observed,
            [tool]);
        SessionCatalogEntry session = new()
        {
            CatalogSessionId = "cursor:s1",
            NativeSessionId = "s1",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Workspace = WorkspaceContext.FromRoot(@"C:\repo"),
            Title = "Session",
            Turns = [turn]
        };

        return Assert.Single(
            new CursorPlanActivityExtractor().Extract([session]));
    }
}
