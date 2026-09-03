using System.IO;
using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sources;

namespace HarnessSpy.Core.Services;

// Rehydrates transcript observations from the durable sidecar so a replayed or
// restarted session shows the same transcript nodes it did live, even after the
// provider deleted the original files. Reads
// <payloads>/_transcripts/<session>/{manifest.json, source-*.jsonl} and reruns
// the dialect parser over the captured raw rows (reinterpret replay).
public sealed class TranscriptReplayLoader
{
    private const string SidecarFolder = "_transcripts";

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<HookObservation> Load(string payloadsFolder)
    {
        string root = Path.Combine(payloadsFolder, SidecarFolder);
        if (!Directory.Exists(root))
        {
            return [];
        }

        List<HookObservation> observations = [];
        foreach (string sessionDirectory in SafeEnumerateDirectories(root))
        {
            LoadSession(sessionDirectory, observations);
        }

        return observations;
    }

    private static void LoadSession(string sessionDirectory, List<HookObservation> observations)
    {
        string manifestPath = Path.Combine(sessionDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return;
        }

        TranscriptSessionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<TranscriptSessionManifest>(
                File.ReadAllText(manifestPath), ManifestJson);
        }
        catch (JsonException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        if (manifest is null || !TranscriptDialectParserRegistry.IsSupported(manifest.DialectId))
        {
            return;
        }

        (HookProvider provider, HookSurface surface) =
            TranscriptDialectParserRegistry.SurfaceFor(manifest.DialectId);
        ITranscriptDialectParser parser = TranscriptDialectParserRegistry.Resolve(manifest.DialectId);

        foreach (string sourceFile in SafeEnumerateFiles(sessionDirectory, "source-*.jsonl"))
        {
            LoadSourceFile(sourceFile, manifest, provider, surface, parser, observations);
        }
    }

    private static void LoadSourceFile(
        string sourceFile,
        TranscriptSessionManifest manifest,
        HookProvider provider,
        HookSurface surface,
        ITranscriptDialectParser parser,
        List<HookObservation> observations)
    {
        string name = Path.GetFileNameWithoutExtension(sourceFile);
        (TranscriptFileRole role, string? agentId) = name.StartsWith("source-agent-", StringComparison.Ordinal)
            ? (TranscriptFileRole.Subagent, name["source-agent-".Length..])
            : (TranscriptFileRole.Main, (string?)null);

        string[] lines;
        try
        {
            lines = File.ReadAllLines(sourceFile);
        }
        catch (IOException)
        {
            return;
        }

        // Carry the last-seen turn id forward across rows (Claude stamps it
        // only on user rows) so replayed assistant fragments join the right turn.
        string? lastTurnId = null;
        foreach (string wrapper in lines)
        {
            if (string.IsNullOrWhiteSpace(wrapper))
            {
                continue;
            }

            HookObservation[] parsed = ParseCapturedRow(
                wrapper, manifest, provider, surface, role, agentId, parser, ref lastTurnId);
            observations.AddRange(parsed);
        }
    }

    private static HookObservation[] ParseCapturedRow(
        string wrapper,
        TranscriptSessionManifest manifest,
        HookProvider provider,
        HookSurface surface,
        TranscriptFileRole role,
        string? agentId,
        ITranscriptDialectParser parser,
        ref string? lastTurnId)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(wrapper);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("raw", out JsonElement rawElement) ||
                rawElement.ValueKind != JsonValueKind.String)
            {
                return [];
            }

            string raw = rawElement.GetString() ?? string.Empty;
            string path = root.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? manifest.ScopedSessionId
                : manifest.ScopedSessionId;
            long offset = root.TryGetProperty("offset", out JsonElement o) && o.TryGetInt64(out long ov) ? ov : 0;
            int lineNumber = root.TryGetProperty("line", out JsonElement l) && l.TryGetInt32(out int lv) ? lv : 1;
            long generation = root.TryGetProperty("generation", out JsonElement g) && g.TryGetInt64(out long gv) ? gv : 1;

            TranscriptRowScanner.RowMeta meta = TranscriptRowScanner.Read(raw);
            if (meta.TurnId is string turnId)
            {
                lastTurnId = turnId;
            }

            TranscriptLine line = new(
                raw,
                path,
                offset,
                lineNumber,
                generation,
                role,
                provider,
                surface,
                manifest.DialectId,
                manifest.ScopedSessionId,
                manifest.NativeSessionId,
                agentId,
                CapturedPath: path,
                ContractVersion: manifest.ContractVersion,
                ObservedAtUtc: meta.Timestamp,
                TurnHint: lastTurnId);

            return [.. parser.Parse(line)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root);
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern);
        }
        catch (IOException)
        {
            return [];
        }
    }
}
