using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;
using HarnessSpy.Core.Sessions.Cursor;

namespace HarnessSpy.Tests;

public sealed class CursorSessionWorkspaceInferrerTests
{
    [Fact]
    public void UniqueManifestSlugPlacesUnknownSession()
    {
        SessionCatalogEntry session = Session(
            "cursor:session",
            "session",
            WorkspaceContext.Unknown,
            projectKey: "c-dev-dd-trace-dotnet-ref-chain");
        WorkspaceContext expected =
            WorkspaceContext.FromRoot(@"C:\dev\dd-trace-dotnet\ref_chain");

        SessionCatalogEntry result = Assert.Single(
            new CursorSessionWorkspaceInferrer().Infer(
                [session],
                [
                    expected,
                    WorkspaceContext.FromRoot(
                        @"C:\dev\dd-trace-dotnet\main")
                ]));

        SessionWorkspaceInference inference = Assert.IsType<
            SessionWorkspaceInference>(result.WorkspaceInference);
        Assert.Equal(expected.Key, inference.Workspace.Key);
        Assert.Equal(InferenceEvidence.Derived, inference.Evidence);
        Assert.Equal(90, inference.Score);
        Assert.Contains(
            inference.EvidenceItems,
            static item =>
                item.Kind ==
                SessionWorkspaceEvidenceKind.ProviderProjectKey);
        Assert.Equal(WorkspaceContextKind.Unknown, result.Workspace.Kind);
    }

    [Fact]
    public void ProjectKeyWinsOverCrossRepositoryPaths()
    {
        WorkspaceContext expected =
            WorkspaceContext.FromRoot(@"C:\dev\dd-trace-dotnet\ref_chain");
        SessionCatalogEntry session = Session(
            "cursor:session",
            "session",
            new WorkspaceNormalizer().FromRoots(
            [
                @"C:\dev\dd-trace-dotnet\ref_chain",
                @"C:\dev\dd-trace-dotnet\main"
            ]),
            projectKey: "c-dev-dd-trace-dotnet-ref-chain",
            events:
            [
                Event(
                    "tool-main",
                    @"C:\dev\dd-trace-dotnet\main\src\file.cpp")
            ]);

        SessionCatalogEntry result = Assert.Single(
            new CursorSessionWorkspaceInferrer().Infer(
                [session],
                [
                    expected,
                    WorkspaceContext.FromRoot(
                        @"C:\dev\dd-trace-dotnet\main")
                ]));

        SessionWorkspaceInference inference = Assert.IsType<
            SessionWorkspaceInference>(result.WorkspaceInference);
        Assert.Equal(expected.Key, inference.Workspace.Key);
        Assert.True(inference.Score >= 90);
        Assert.Equal(2, result.Workspace.DisplayRoots.Count);
    }

    [Fact]
    public void DuplicateNativeIdPlacesScopedAlias()
    {
        WorkspaceContext workspace =
            WorkspaceContext.FromRoot(@"C:\github\chrisnas\GitCompanion");
        SessionCatalogEntry canonical = Session(
            "cursor:shared",
            "shared",
            workspace);
        SessionCatalogEntry alias = Session(
            "cursor:shared:project:alias",
            "shared",
            WorkspaceContext.Unknown,
            projectKey: "empty-window");

        SessionCatalogEntry[] results =
        [
            .. new CursorSessionWorkspaceInferrer().Infer(
                [canonical, alias],
                [],
                [canonical, alias])
        ];

        Assert.Null(results[0].WorkspaceInference);
        SessionWorkspaceInference inference = Assert.IsType<
            SessionWorkspaceInference>(results[1].WorkspaceInference);
        Assert.Equal(workspace.Key, inference.Workspace.Key);
        Assert.Equal(InferenceEvidence.Corroborated, inference.Evidence);
        Assert.Equal(95, inference.Score);
    }

    [Fact]
    public void ToolPathPlacesNumericProjectSession()
    {
        WorkspaceContext workspace = WorkspaceContext.FromRoot(
            @"C:\Users\Christophe Nasarre\source\repos\WhisperDictation");
        SessionCatalogEntry session = Session(
            "cursor:session",
            "session",
            WorkspaceContext.Unknown,
            projectKey: "1780435118174",
            events:
            [
                Event(
                    "read",
                    @"C:\Users\Christophe Nasarre\source\repos\" +
                    @"WhisperDictation\src\App.xaml")
            ]);

        SessionCatalogEntry result = Assert.Single(
            new CursorSessionWorkspaceInferrer().Infer(
                [session],
                [workspace]));

        SessionWorkspaceInference inference = Assert.IsType<
            SessionWorkspaceInference>(result.WorkspaceInference);
        Assert.Equal(workspace.Key, inference.Workspace.Key);
        Assert.Equal(InferenceEvidence.Heuristic, inference.Evidence);
        Assert.Equal(75, inference.Score);
    }

    [Fact]
    public void ConflictingPathCandidatesRemainAmbiguous()
    {
        WorkspaceContext first =
            WorkspaceContext.FromRoot(@"C:\repo-one");
        WorkspaceContext second =
            WorkspaceContext.FromRoot(@"C:\repo-two");
        SessionCatalogEntry session = Session(
            "cursor:session",
            "session",
            WorkspaceContext.Unknown,
            events:
            [
                Event("first", @"C:\repo-one\src\a.cpp"),
                Event("second", @"C:\repo-two\src\b.cpp")
            ]);

        SessionCatalogEntry result = Assert.Single(
            new CursorSessionWorkspaceInferrer().Infer(
                [session],
                [first, second]));

        SessionWorkspaceInference inference = Assert.IsType<
            SessionWorkspaceInference>(result.WorkspaceInference);
        Assert.Equal(InferenceEvidence.Ambiguous, inference.Evidence);
        Assert.Equal(WorkspaceContextKind.Unknown, inference.Workspace.Kind);
        Assert.Equal(2, inference.Alternatives.Count);
    }

    [Fact]
    public void ExplicitProviderMultirootIsPreserved()
    {
        SessionCatalogEntry session = Session(
            "cursor:session",
            "session",
            new WorkspaceNormalizer().FromRoots(
            [
                @"C:\first",
                @"C:\second"
            ])) with
        {
            Metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["cursor.workspaceResolution"] =
                    "explicit-workspace-roots"
            }
        };

        SessionCatalogEntry result = Assert.Single(
            new CursorSessionWorkspaceInferrer().Infer(
                [session],
                [
                    WorkspaceContext.FromRoot(@"C:\first"),
                    WorkspaceContext.FromRoot(@"C:\second")
                ]));

        Assert.Null(result.WorkspaceInference);
        Assert.Equal(2, result.Workspace.DisplayRoots.Count);
    }

    private static SessionCatalogEntry Session(
        string catalogId,
        string nativeId,
        WorkspaceContext workspace,
        string? projectKey = null,
        IReadOnlyList<SessionEventRecord>? events = null)
    {
        Dictionary<string, string?> metadata = new(StringComparer.Ordinal);
        if (projectKey is not null)
        {
            metadata["cursor.projectKey"] = projectKey;
        }

        IReadOnlyList<SessionTurn> turns = events is null
            ? []
            :
            [
                new SessionTurn(
                    $"{catalogId}:turn:1",
                    1,
                    "inspect",
                    null,
                    null,
                    InferenceEvidence.Derived,
                    events)
            ];
        return new SessionCatalogEntry
        {
            CatalogSessionId = catalogId,
            NativeSessionId = nativeId,
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Workspace = workspace,
            Title = nativeId,
            Turns = turns,
            Metadata = metadata
        };
    }

    private static SessionEventRecord Event(
        string id,
        string targetPath) =>
        new()
        {
            Id = id,
            NativeName = "Read",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Role = ObservationRole.ToolRequest,
            EventKind = CanonicalEventKind.ToolRequested,
            ToolKind = CanonicalToolKind.FileRead,
            TargetPaths = [targetPath],
            Provenance = new SessionSourceProvenance(
                SessionSourceKind.CursorTranscriptJsonl,
                "session.jsonl",
                "cursor-agent-jsonl")
        };
}
