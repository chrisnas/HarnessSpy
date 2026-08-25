using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CursorSpy.App.Models;

namespace CursorSpy.App.Services;

public sealed class ReplayLoader
{
    private static readonly Regex PayloadFileName = new(
        @"^(?:hp_)?(?<session>.+)_(?<hook>[A-Za-z][A-Za-z0-9]*)_(?<timestamp>\d{8}_\d{6}_\d{3})_(?<suffix>[0-9a-fA-F]{8})\.json$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> KnownHookEventNames = new(StringComparer.Ordinal)
    {
        "sessionStart",
        "sessionEnd",
        "beforeSubmitPrompt",
        "afterAgentThought",
        "afterAgentResponse",
        "preToolUse",
        "postToolUse",
        "postToolUseFailure",
        "beforeShellExecution",
        "afterShellExecution",
        "beforeMCPExecution",
        "afterMCPExecution",
        "beforeReadFile",
        "afterFileEdit",
        "subagentStart",
        "subagentStop",
        "preCompact",
        "stop",
        "workspaceOpen",
        "beforeTabFileRead",
        "afterTabFileEdit"
    };

    public async Task<IReadOnlyList<HookObservation>> LoadAsync(
        string folder,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch
        {
            return [];
        }

        List<ReplayEntry> entries = [];
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryReadPayloadFileInfo(file, out PayloadFileInfo payloadFile))
            {
                continue;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            DateTimeOffset observedAtUtc = payloadFile.ObservedAtUtc;
            string sourceFilePath = GetFullPath(file);
            if (HookObservation.TryParseRawPayload(content, observedAtUtc, sourceFilePath, out HookObservation? observation) &&
                observation is not null &&
                StringComparer.Ordinal.Equals(payloadFile.HookEventName, observation.HookEventName))
            {
                entries.Add(new ReplayEntry(observedAtUtc, sourceFilePath, observation));
            }
        }

        return entries
            .OrderBy(entry => entry.ObservedAtUtc)
            .ThenBy(entry => entry.SourceFilePath, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Observation)
            .ToArray();
    }

    private static bool TryReadPayloadFileInfo(string file, out PayloadFileInfo payloadFile)
    {
        payloadFile = default;

        Match match = PayloadFileName.Match(Path.GetFileName(file));
        if (!match.Success)
        {
            return false;
        }

        string hookEventName = match.Groups["hook"].Value;
        if (!KnownHookEventNames.Contains(hookEventName))
        {
            return false;
        }

        if (!DateTime.TryParseExact(
            match.Groups["timestamp"].Value,
            "yyyyMMdd_HHmmss_fff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out DateTime timestamp))
        {
            return false;
        }

        payloadFile = new PayloadFileInfo(hookEventName, new DateTimeOffset(timestamp).ToUniversalTime());
        return true;
    }

    private static string GetFullPath(string file)
    {
        try
        {
            return Path.GetFullPath(file);
        }
        catch
        {
            return file;
        }
    }

    private sealed record ReplayEntry(
        DateTimeOffset ObservedAtUtc,
        string SourceFilePath,
        HookObservation Observation);

    private readonly record struct PayloadFileInfo(
        string HookEventName,
        DateTimeOffset ObservedAtUtc);
}
