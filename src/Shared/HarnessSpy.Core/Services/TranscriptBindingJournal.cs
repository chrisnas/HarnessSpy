using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Services;

// Records the projection decision observed while live transcript rows are
// reconciled. Written to <sidecar>/bindings.jsonl as a diagnostic audit trail.
// The manifest records which versions captured the audit; replay deliberately
// reinterprets raw rows with the current code, so this journal is not a replay
// instruction stream.
public sealed class TranscriptBindingJournal
{
    public const int ReconcilerVersion = 3;

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
                    ["bindingEvidence"] = change.BindingEvidence.ToString(),
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
