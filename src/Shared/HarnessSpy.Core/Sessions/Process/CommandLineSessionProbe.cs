using System.Text.RegularExpressions;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Process;

public sealed partial class CommandLineSessionProbe : IRunningSessionProbe
{
    private readonly ProcessInventoryReader _inventory;
    private readonly string[] _processNames;
    private readonly string[] _commandLineMarkers;

    public CommandLineSessionProbe(
        HookProvider provider,
        IEnumerable<string> processNames,
        ProcessInventoryReader? inventory = null,
        IEnumerable<string>? commandLineMarkers = null)
    {
        Provider = provider;
        _processNames = [.. processNames];
        _inventory = inventory ?? new ProcessInventoryReader();
        _commandLineMarkers = commandLineMarkers is null
            ? []
            : [.. commandLineMarkers];
    }

    public HookProvider Provider { get; }

    public Task<IReadOnlyList<RunningSessionHint>> ProbeAsync(
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<RunningSessionHint>>(
            () => ReadHints(cancellationToken),
            cancellationToken);
    }

    private IReadOnlyList<RunningSessionHint> ReadHints(
        CancellationToken cancellationToken)
    {
        List<RunningSessionHint> hints = [];
        foreach (RunningProcessInfo process in _inventory.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_processNames.Any(
                name => string.Equals(
                    name,
                    Path.GetFileNameWithoutExtension(process.Name),
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (_commandLineMarkers.Length > 0 &&
                !_commandLineMarkers.Any(
                    marker => process.CommandLine?.Contains(
                        marker,
                        StringComparison.OrdinalIgnoreCase) == true))
            {
                continue;
            }

            string? sessionId = ReadSessionId(process.CommandLine);
            string? transcriptPath = ReadNamedArgument(
                process.CommandLine,
                "transcript-path",
                "transcript_path");
            string? workingDirectory = ReadNamedArgument(
                process.CommandLine,
                "cwd",
                "working-directory");

            hints.Add(new RunningSessionHint(
                Provider,
                process.ProcessId,
                process.Name,
                sessionId,
                transcriptPath,
                workingDirectory,
                sessionId is null
                    ? InferenceEvidence.Heuristic
                    : InferenceEvidence.Observed));
        }

        return hints;
    }

    private static string? ReadSessionId(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        Match match = ResumeArgumentRegex().Match(commandLine);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static string? ReadNamedArgument(string? commandLine, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        foreach (string name in names)
        {
            Match match = Regex.Match(
                commandLine,
                $@"(?:--{Regex.Escape(name)})(?:=|\s+)(?:""(?<value>[^""]+)""|(?<value>\S+))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                return match.Groups["value"].Value;
            }
        }

        return null;
    }

    [GeneratedRegex(
        @"(?:--resume(?:=|\s+)|--session-id(?:=|\s+)|\bresume\s+)(?:""(?<id>[A-Za-z0-9_-]+)""|(?<id>[A-Za-z0-9_-]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResumeArgumentRegex();
}
