using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Services;

// Records the finalized projection decision for each reconciled transcript
// fragment so heuristic (especially Cursor) correlation is reproducible in
// exact replay and is never silently re-derived. Written to
// <sidecar>/bindings.jsonl. A provisional binding is rewritten when finalized.
public sealed class TranscriptBindingJournal
{
    public const int ReconcilerVersion = 1;

    private readonly TranscriptCaptureStore _captureStore;
    private readonly object _gate = new();

    public TranscriptBindingJournal(TranscriptCaptureStore captureStore)
    {
        _captureStore = captureStore;
    }

    public void Record(string scopedSessionId, ObservationChange change)
    {
        HookObservation observation = change.Observation;
        ObservationProvenance? provenance = observation.Provenance;

        try
        {
            lock (_gate)
            {
                string directory = _captureStore.SidecarDirectory(scopedSessionId);
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, "bindings.jsonl");

                JsonObject record = new()
                {
                    ["changeKind"] = change.Kind.ToString(),
                    ["eventId"] = observation.EventId.ToString("N"),
                    ["nativeEvent"] = observation.HookEventName,
                    ["role"] = observation.Interpretation.Role.ToString(),
                    ["evidence"] = observation.Interpretation.Evidence.ToString(),
                    ["relationship"] = change.Relationship.ToString(),
                    ["targetEventId"] = change.TargetEventId?.ToString("N"),
                    ["dedupeKey"] = provenance?.DedupeKey,
                    ["toolCallId"] = observation.ToolUseId,
                    ["turnId"] = observation.GenerationId,
                    ["reconcilerVersion"] = ReconcilerVersion,
                    ["recordedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
                };

                File.AppendAllText(file, record.ToJsonString() + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
