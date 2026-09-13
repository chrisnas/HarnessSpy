using System.Diagnostics;
using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Process;

public sealed class ClaudeRunningSessionProbe : IRunningSessionProbe
{
    private readonly CommandLineSessionProbe _commandLineProbe;
    private readonly TimeSpan _timeout;

    public ClaudeRunningSessionProbe(
        ProcessInventoryReader? inventory = null,
        TimeSpan? timeout = null)
    {
        _commandLineProbe = new CommandLineSessionProbe(
            HookProvider.ClaudeCode,
            ["claude"],
            inventory);
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    public HookProvider Provider => HookProvider.ClaudeCode;

    public async Task<IReadOnlyList<RunningSessionHint>> ProbeAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RunningSessionHint> commandLineHints =
            await _commandLineProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RunningSessionHint> agentViewHints =
            await ReadAgentViewAsync(cancellationToken).ConfigureAwait(false);

        return commandLineHints
            .Concat(agentViewHints)
            .DistinctBy(
                static hint =>
                    $"{hint.ProcessId}|{hint.NativeSessionId}|{hint.WorkingDirectory}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<RunningSessionHint>> ReadAgentViewAsync(
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = new(_timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                Arguments = "/d /s /c \"claude agents --json\"",
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using System.Diagnostics.Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                return [];
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return [];
            }

            return ParseAgentView(await outputTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<RunningSessionHint> ParseAgentView(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<RunningSessionHint> hints = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            string? sessionId = ReadString(item, "sessionId", "session_id", "id");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                continue;
            }

            int processId = ReadInt(item, "pid", "processId") ?? 0;
            hints.Add(new RunningSessionHint(
                HookProvider.ClaudeCode,
                processId,
                "claude",
                sessionId,
                ReadString(item, "transcriptPath", "transcript_path"),
                ReadString(item, "cwd"),
                InferenceEvidence.Observed));
        }

        return hints;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value) &&
                value.TryGetInt32(out int parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
