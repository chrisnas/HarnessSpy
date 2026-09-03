using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Services;

// Durably copies every accepted transcript source row beside the immutable hook
// payloads so replay survives aggressive provider cleanup (Claude deletes most
// subagent files, and empty-session mains, within seconds). Never touches the
// provider-owned file.
//
// Layout: <Payloads>/_transcripts/<scoped-session>/source-<source-id>.jsonl and
// manifest.json.
public sealed class TranscriptCaptureStore
{
    private const string SidecarFolder = "_transcripts";

    private readonly string _payloadsDirectory;
    private readonly object _gate = new();

    // Per-source-file set of (generation:offset) rows already captured, so a row
    // re-tailed within a run, or re-backfilled after an app restart, is never
    // appended twice. Seeded from the existing file on first touch.
    private readonly Dictionary<string, HashSet<string>> _capturedRowsByFile =
        new(StringComparer.OrdinalIgnoreCase);

    public TranscriptCaptureStore(string payloadsDirectory)
    {
        _payloadsDirectory = payloadsDirectory;
    }

    public string SidecarDirectory(string scopedSessionId) =>
        Path.Combine(_payloadsDirectory, SidecarFolder, Sanitize(scopedSessionId));

    // Appends one raw row with its exact coordinates. Returns false when the row
    // was already captured (idempotent) or capture failed; failure never
    // interrupts ingestion.
    public bool AppendSourceRow(
        string scopedSessionId,
        string sourceId,
        TranscriptRawLine line,
        string normalizedPath,
        string dialectId,
        TranscriptFileRole role,
        TranscriptCompleteness completeness)
    {
        try
        {
            lock (_gate)
            {
                string directory = SidecarDirectory(scopedSessionId);
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, $"source-{Sanitize(sourceId)}.jsonl");

                // Idempotent capture: skip a row already present (this run or a
                // previous one) so the sidecar never accumulates duplicates.
                HashSet<string> captured = CapturedRows(file);
                if (!captured.Add($"{line.FileGeneration}:{line.ByteOffset}"))
                {
                    return false;
                }

                JsonObject record = new()
                {
                    ["path"] = normalizedPath,
                    ["dialect"] = dialectId,
                    ["role"] = role.ToString(),
                    ["generation"] = line.FileGeneration,
                    ["offset"] = line.ByteOffset,
                    ["line"] = line.LineNumber,
                    ["completeness"] = completeness.ToString(),
                    ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["raw"] = line.Raw
                };

                File.AppendAllText(file, record.ToJsonString() + Environment.NewLine, Encoding.UTF8);
                return true;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void WriteManifest(string scopedSessionId, TranscriptSessionManifest manifest)
    {
        try
        {
            lock (_gate)
            {
                string directory = SidecarDirectory(scopedSessionId);
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, "manifest.json");
                string json = JsonSerializer.Serialize(
                    manifest,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
                File.WriteAllText(file, json, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Returns the set of already-captured (generation:offset) keys for a source
    // file, seeding it once from any rows a previous run left on disk.
    private HashSet<string> CapturedRows(string file)
    {
        if (_capturedRowsByFile.TryGetValue(file, out HashSet<string>? captured))
        {
            return captured;
        }

        captured = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(file))
        {
            try
            {
                foreach (string existing in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(existing))
                    {
                        continue;
                    }

                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(existing);
                        JsonElement root = document.RootElement;
                        long generation = root.TryGetProperty("generation", out JsonElement g) &&
                            g.TryGetInt64(out long gv) ? gv : 1;
                        long offset = root.TryGetProperty("offset", out JsonElement o) &&
                            o.TryGetInt64(out long ov) ? ov : 0;
                        captured.Add($"{generation}:{offset}");
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
            catch (IOException)
            {
            }
        }

        _capturedRowsByFile[file] = captured;
        return captured;
    }

    private static string Sanitize(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        }

        string result = builder.ToString().Trim('-');
        return string.IsNullOrEmpty(result) ? "unknown" : result;
    }
}

// Per-session capture manifest recording provider versions, dialect, and the
// durable capture state used to prove replay fidelity was preserved.
public sealed record TranscriptSessionManifest(
    string ScopedSessionId,
    string DialectId,
    string? ContractVersion,
    int ParserVersion,
    int ReconcilerVersion,
    EnrichmentCaptureState CaptureState,
    IReadOnlyList<string> SourceFiles,
    string? NativeSessionId = null);
