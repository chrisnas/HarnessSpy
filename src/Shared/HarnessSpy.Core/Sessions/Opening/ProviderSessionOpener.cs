using System.Text.RegularExpressions;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Opening;

public sealed partial class ProviderSessionOpener : ISessionOpener
{
    private readonly IExternalLauncher _launcher;

    public ProviderSessionOpener(IExternalLauncher? launcher = null)
    {
        _launcher = launcher ?? new ExternalLauncher();
    }

    public bool CanOpen(SessionCatalogEntry session)
    {
        if (!SafeSessionIdRegex().IsMatch(session.NativeSessionId))
        {
            return false;
        }

        return session.Provider switch
        {
            HookProvider.Cursor => session.Workspace.DisplayRoots.Count > 0 ||
                IsCursorCli(session),
            HookProvider.ClaudeCode => true,
            HookProvider.GitHubCopilot => true,
            _ => false
        };
    }

    public async Task<SessionOpenResult> OpenAsync(
        SessionCatalogEntry session,
        CancellationToken cancellationToken = default)
    {
        if (!CanOpen(session))
        {
            return new SessionOpenResult(false, "This session has no supported opener.");
        }

        try
        {
            switch (session.Provider)
            {
                case HookProvider.Cursor:
                    await OpenCursorAsync(session, cancellationToken).ConfigureAwait(false);
                    break;

                case HookProvider.ClaudeCode:
                    await OpenClaudeAsync(session, cancellationToken).ConfigureAwait(false);
                    break;

                case HookProvider.GitHubCopilot:
                    await OpenCopilotAsync(session, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    return new SessionOpenResult(false, "Unknown harness.");
            }

            return new SessionOpenResult(true);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
                InvalidOperationException or
                FileNotFoundException)
        {
            return new SessionOpenResult(false, exception.Message);
        }
    }

    private Task OpenCursorAsync(
        SessionCatalogEntry session,
        CancellationToken cancellationToken)
    {
        if (IsCursorCli(session))
        {
            return OpenTerminalAsync(
                $"agent --resume={Quote(session.NativeSessionId)}",
                session,
                cancellationToken);
        }

        string workspace = session.Workspace.DisplayRoots.First();
        return _launcher.LaunchAsync(
            "cursor",
            $"--reuse-window {Quote(workspace)}",
            workspace,
            useShellExecute: true,
            cancellationToken);
    }

    private Task OpenClaudeAsync(
        SessionCatalogEntry session,
        CancellationToken cancellationToken)
    {
        string? entrypoint = MetadataValue(
            session,
            "entrypoint",
            "claude.entrypoint");
        if (entrypoint?.Contains(
                "vscode",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            string uri =
                "vscode://anthropic.claude-code/open?session=" +
                Uri.EscapeDataString(session.NativeSessionId);
            return _launcher.LaunchAsync(
                uri,
                string.Empty,
                null,
                useShellExecute: true,
                cancellationToken);
        }

        return OpenTerminalAsync(
            $"claude --resume {Quote(session.NativeSessionId)}",
            session,
            cancellationToken);
    }

    private Task OpenCopilotAsync(
        SessionCatalogEntry session,
        CancellationToken cancellationToken)
    {
        if (session.Metadata.TryGetValue("client_name", out string? clientName) &&
            string.Equals(clientName, "github/autopilot", StringComparison.OrdinalIgnoreCase))
        {
            string uri = "ghapp://sessions/" + Uri.EscapeDataString(session.NativeSessionId);
            return _launcher.LaunchAsync(
                uri,
                string.Empty,
                null,
                useShellExecute: true,
                cancellationToken);
        }

        return OpenTerminalAsync(
            $"copilot --resume={Quote(session.NativeSessionId)}",
            session,
            cancellationToken);
    }

    private Task OpenTerminalAsync(
        string command,
        SessionCatalogEntry session,
        CancellationToken cancellationToken)
    {
        string? workspace = session.Workspace.DisplayRoots.FirstOrDefault();
        string comspec = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        return _launcher.LaunchAsync(
            comspec,
            $"/d /K {command}",
            workspace,
            useShellExecute: true,
            cancellationToken);
    }

    private static bool IsCursorCli(SessionCatalogEntry session) =>
        MetadataValue(session, "entrypoint", "cursor.entrypoint") is string value &&
        value.Contains("cli", StringComparison.OrdinalIgnoreCase);

    private static string? MetadataValue(
        SessionCatalogEntry session,
        params string[] keys)
    {
        foreach (string key in keys)
        {
            if (session.Metadata.TryGetValue(key, out string? value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,512}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSessionIdRegex();
}
