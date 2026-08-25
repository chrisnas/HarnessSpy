using System.IO;
using System.IO.Pipes;
using System.Text;
using CursorSpy.App.Models;

namespace CursorSpy.App.Services;

public sealed class NamedPipeListener(string pipeName = NamedPipeListener.DefaultPipeName)
{
    public const string DefaultPipeName = "HarnessSpy.Ingest.v1";

    public async Task RunAsync(
        Func<HookObservation, CancellationToken, Task> onObservation,
        CancellationToken stoppingToken,
        Action<Exception>? onPipeCreationError = null)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server;

            try
            {
                server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception creationException)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                // Pipe creation is fatal for the listener: report it and stop accepting.
                onPipeCreationError?.Invoke(creationException);
                return;
            }

            try
            {
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);

                NamedPipeServerStream connectedServer = server;
                server = null;
                _ = Task.Run(() => ReadClientAsync(connectedServer, onObservation, stoppingToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                if (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), stoppingToken).ConfigureAwait(false);
                }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private static async Task ReadClientAsync(
        NamedPipeServerStream server,
        Func<HookObservation, CancellationToken, Task> onObservation,
        CancellationToken stoppingToken)
    {
        await using (server)
        {
            try
            {
                using MemoryStream buffer = new();
                await server.CopyToAsync(buffer, stoppingToken).ConfigureAwait(false);

                string content = Encoding.UTF8.GetString(buffer.ToArray());
                foreach (string frame in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = frame.TrimEnd('\r');
                    if (!HookObservation.TryParse(line, out HookObservation? observation) || observation is null)
                    {
                        continue;
                    }

                    await onObservation(observation, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch
            {
                // A bad client or callback should not stop the listener accept loop.
            }
        }
    }
}
