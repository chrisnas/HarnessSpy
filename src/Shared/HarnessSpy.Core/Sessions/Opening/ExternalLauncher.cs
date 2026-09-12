using System.Diagnostics;

namespace HarnessSpy.Core.Sessions.Opening;

public interface IExternalLauncher
{
    Task LaunchAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        bool useShellExecute,
        CancellationToken cancellationToken);
}

public sealed class ExternalLauncher : IExternalLauncher
{
    public Task LaunchAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        bool useShellExecute,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = useShellExecute
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        System.Diagnostics.Process.Start(startInfo);
        return Task.CompletedTask;
    }
}
