using System.Globalization;
using System.Text.RegularExpressions;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Services;

public sealed class ReplayLoader
{
    private static readonly Regex TimestampedPayloadFile = new(
        @"_(?<timestamp>\d{8}_\d{6}_\d{3})_(?<suffix>[0-9a-fA-F]{8})\.json$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
            files = Directory
                .EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
                .ToArray();
        }
        catch
        {
            return [];
        }

        List<ReplayEntry> entries = [];
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryReadObservedAt(file, out DateTimeOffset observedAtUtc))
            {
                continue;
            }

            string content;
            try
            {
                content = await File
                    .ReadAllTextAsync(file, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            string sourceFilePath = GetFullPath(file);
            if (HookObservation.TryParseRawPayload(
                    content,
                    observedAtUtc,
                    sourceFilePath,
                    out HookObservation? observation) &&
                observation is not null)
            {
                entries.Add(new ReplayEntry(
                    observation.ObservedAtUtc,
                    sourceFilePath,
                    observation));
            }
        }

        return entries
            .OrderBy(entry => entry.ObservedAtUtc)
            .ThenBy(entry => entry.SourceFilePath, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Observation)
            .ToArray();
    }

    private static bool TryReadObservedAt(
        string file,
        out DateTimeOffset observedAtUtc)
    {
        observedAtUtc = default;
        Match match = TimestampedPayloadFile.Match(Path.GetFileName(file));
        if (!match.Success ||
            !DateTime.TryParseExact(
                match.Groups["timestamp"].Value,
                "yyyyMMdd_HHmmss_fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTime timestamp))
        {
            return false;
        }

        observedAtUtc = new DateTimeOffset(timestamp).ToUniversalTime();
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
}
