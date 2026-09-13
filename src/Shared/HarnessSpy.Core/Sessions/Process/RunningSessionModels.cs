using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Process;

public sealed record RunningProcessInfo(
    int ProcessId,
    string Name,
    string? CommandLine,
    string? ExecutablePath);

public sealed record RunningSessionHint(
    HookProvider Provider,
    int ProcessId,
    string ProcessName,
    string? NativeSessionId = null,
    string? TranscriptPath = null,
    string? WorkingDirectory = null,
    InferenceEvidence Evidence = InferenceEvidence.Observed);

public interface IRunningSessionProbe
{
    HookProvider Provider { get; }

    Task<IReadOnlyList<RunningSessionHint>> ProbeAsync(
        CancellationToken cancellationToken);
}
