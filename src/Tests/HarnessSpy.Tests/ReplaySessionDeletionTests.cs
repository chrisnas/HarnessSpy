using System.Globalization;
using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Services;
using HarnessSpy.Wpf.ViewModels;

namespace HarnessSpy.Tests;

public sealed class ReplaySessionDeletionTests
{
    private static readonly JsonSerializerOptions EnvelopeJson =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task DeleteTranscriptOnlySessionRemovesSidecarAndPreservesProviderTranscript()
    {
        string root = CreateTemporaryDirectory();
        string payloads = Directory.CreateDirectory(
            Path.Combine(root, "Payloads")).FullName;

        try
        {
            string replayFile = WriteCursorReplay(
                payloads,
                "session-1",
                @"C:\ReplayWorkspace",
                1);
            (string sidecar, string providerTranscript) =
                CaptureCursorTranscript(
                    root,
                    payloads,
                    "session-1");
            File.Delete(replayFile);

            MainWindowViewModel viewModel = new();
            await viewModel.LoadFolderAsync(payloads);

            TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
            TreeNodeViewModel session = Assert.Single(
                workspace.Children,
                child => child.Kind == TreeNodeKind.Session);

            Assert.True(viewModel.CanDeleteSessionFiles(session));
            Assert.True(viewModel.TryDeleteSessionFiles(
                session,
                out int deletedFileCount,
                out string? error),
                error);
            Assert.True(deletedFileCount >= 3);
            Assert.False(File.Exists(replayFile));
            Assert.False(Directory.Exists(sidecar));
            Assert.True(File.Exists(providerTranscript));

            MainWindowViewModel restarted = new();
            await restarted.LoadFolderAsync(payloads);
            Assert.Empty(restarted.Roots);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteWorkspaceSessionsRemovesEveryReplayAndSidecar()
    {
        string root = CreateTemporaryDirectory();
        string payloads = Directory.CreateDirectory(
            Path.Combine(root, "Payloads")).FullName;

        try
        {
            string firstReplay = WriteCursorReplay(
                payloads,
                "session-1",
                @"C:\ReplayWorkspace",
                1);
            string secondReplay = WriteCursorReplay(
                payloads,
                "session-2",
                @"C:\ReplayWorkspace",
                2);
            (string firstSidecar, string firstProviderTranscript) =
                CaptureCursorTranscript(
                    root,
                    payloads,
                    "session-1");
            (string secondSidecar, string secondProviderTranscript) =
                CaptureCursorTranscript(
                    root,
                    payloads,
                    "session-2");

            MainWindowViewModel viewModel = new();
            await viewModel.LoadFolderAsync(payloads);

            TreeNodeViewModel workspace = Assert.Single(viewModel.Roots);
            Assert.Equal(
                2,
                workspace.Children.Count(
                    child => child.Kind == TreeNodeKind.Session));
            Assert.True(viewModel.CanDeleteWorkspaceSessions(workspace));

            Assert.True(viewModel.TryDeleteWorkspaceSessions(
                workspace,
                out int deletedSessionCount,
                out int deletedFileCount,
                out string? error),
                error);
            Assert.Equal(2, deletedSessionCount);
            Assert.True(deletedFileCount >= 6);
            Assert.Empty(viewModel.Roots);
            Assert.False(File.Exists(firstReplay));
            Assert.False(File.Exists(secondReplay));
            Assert.False(Directory.Exists(firstSidecar));
            Assert.False(Directory.Exists(secondSidecar));
            Assert.True(File.Exists(firstProviderTranscript));
            Assert.True(File.Exists(secondProviderTranscript));

            MainWindowViewModel restarted = new();
            await restarted.LoadFolderAsync(payloads);
            Assert.Empty(restarted.Roots);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string WriteCursorReplay(
        string payloads,
        string sessionId,
        string workspace,
        int sequence)
    {
        DateTimeOffset observedAtUtc =
            DateTimeOffset.UtcNow.AddMinutes(-10).AddSeconds(sequence);
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            hook_event_name = "beforeSubmitPrompt",
            conversation_id = sessionId,
            generation_id = $"generation-{sequence}",
            workspace_roots = new[] { workspace },
            prompt = $"prompt {sequence}"
        });
        ObservationEnvelope envelope = new(
            ObservationEnvelope.CurrentIngressVersion,
            Guid.NewGuid(),
            observedAtUtc,
            HookProvider.Cursor,
            HookSurface.CursorIde,
            HookSurface.CursorIde,
            ObservationSourceKind.Hook,
            "beforeSubmitPrompt",
            "beforeSubmitPrompt",
            "test",
            SourceFilePath: null,
            ParseStatus: "valid",
            Payload: payload);

        string timestamp = observedAtUtc
            .ToLocalTime()
            .ToString(
                "yyyyMMdd_HHmmss_fff",
                CultureInfo.InvariantCulture);
        string suffix = sequence.ToString("x8", CultureInfo.InvariantCulture);
        string path = Path.Combine(
            payloads,
            $"hs_Cursor_{sessionId}_beforeSubmitPrompt_{timestamp}_{suffix}.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(envelope, EnvelopeJson));
        return path;
    }

    private static (string Sidecar, string ProviderTranscript)
        CaptureCursorTranscript(
            string root,
            string payloads,
            string sessionId)
    {
        string providerDirectory = Directory.CreateDirectory(
            Path.Combine(root, "ProviderTranscripts")).FullName;
        string providerTranscript = Path.Combine(
            providerDirectory,
            $"{sessionId}.jsonl");
        string raw = JsonSerializer.Serialize(new
        {
            role = "user",
            message = new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text =
                            $"<user_query>prompt {sessionId}</user_query>"
                    }
                }
            }
        });
        File.WriteAllText(
            providerTranscript,
            raw + Environment.NewLine);

        string scopedSessionId =
            $"{HookProvider.Cursor}:{HookSurface.CursorIde}:{sessionId}";
        TranscriptCaptureStore capture = new(payloads);
        Assert.True(capture.AppendSourceRow(
            scopedSessionId,
            "main",
            new TranscriptRawLine(raw, 0, 1, 1),
            providerTranscript,
            DialectIds.CursorTranscript,
            TranscriptFileRole.Main,
            TranscriptCompleteness.Complete));
        capture.WriteManifest(
            scopedSessionId,
            new TranscriptSessionManifest(
                scopedSessionId,
                DialectIds.CursorTranscript,
                ContractVersion: null,
                ParserVersion: 3,
                ReconcilerVersion: TranscriptBindingJournal.ReconcilerVersion,
                CaptureState: EnrichmentCaptureState.LiveCaptured,
                SourceFiles: ["main"],
                NativeSessionId: sessionId));

        string sidecar = capture.SidecarDirectory(scopedSessionId);
        File.WriteAllText(
            Path.Combine(sidecar, "bindings.jsonl"),
            "{}" + Environment.NewLine);
        return (sidecar, providerTranscript);
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "harnessspy-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
