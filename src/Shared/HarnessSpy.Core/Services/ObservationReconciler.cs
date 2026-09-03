using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;

namespace HarnessSpy.Core.Services;

// Hook-first reconciliation. Hooks are the ordering and summary authority;
// transcript fragments enrich a matching hook node or, when nothing matches,
// appear as their own transcript-only node. Correlation is exact when a native
// id is shared (Claude tool_use.id, Copilot toolCallId) and heuristic when only
// a tool signature is available (Cursor). The reconciler is not thread-safe by
// itself; the coordinator serializes all calls through one loop.
public sealed class ObservationReconciler
{
    // Provenance dedupe keys already projected, so a replayed/re-tailed row is
    // never projected twice.
    private readonly HashSet<string> _seenProvenance = new(StringComparer.OrdinalIgnoreCase);

    // Canonical hook nodes indexed for correlation.
    private readonly Dictionary<string, Guid> _byToolCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<Guid>> _byToolSignature = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _bySubagentId = new(StringComparer.Ordinal);

    // Transcript-only nodes that a later hook may promote to canonical.
    private readonly Dictionary<string, Guid> _transcriptToolCallNodes = new(StringComparer.Ordinal);

    public IReadOnlyList<ObservationChange> Reconcile(HookObservation observation)
    {
        if (observation.IsTranscriptSourced)
        {
            return ReconcileTranscript(observation);
        }

        return RegisterHook(observation);
    }

    private IReadOnlyList<ObservationChange> RegisterHook(HookObservation hook)
    {
        // A hook that opens a tool call is indexed for transcript correlation.
        // PreToolUse opens the canonical node.
        if (hook.ToolUseId is string toolCallId && hook.Interpretation.OpensToolCall)
        {
            string key = ToolCallKey(hook.ProviderScopedSessionId, toolCallId);
            _byToolCallId[key] = hook.EventId;

            if (_transcriptToolCallNodes.Remove(key, out Guid transcriptNode))
            {
                // A transcript tool node was projected before this hook; the
                // PromotePrimary handler adds the hook and re-parents that node,
                // so no separate Add is emitted here.
                return [new ObservationChange(ObservationChangeKind.PromotePrimary, hook, transcriptNode)];
            }
        }

        if (hook.Interpretation.OpensToolCall && hook.ToolName is not null)
        {
            EnqueueSignature(ToolSignature(hook), hook.EventId);
        }

        if (hook.Interpretation.OpensSubagent && hook.SubagentId is string agentId)
        {
            _bySubagentId[SubagentKey(hook.ProviderScopedSessionId, agentId)] = hook.EventId;
        }

        return [new ObservationChange(ObservationChangeKind.Add, hook)];
    }

    private IReadOnlyList<ObservationChange> ReconcileTranscript(HookObservation transcript)
    {
        // Idempotent: a row already projected is dropped.
        if (transcript.Provenance is { } provenance && !_seenProvenance.Add(provenance.DedupeKey))
        {
            return [];
        }

        Guid? match = FindCanonicalMatch(transcript);
        if (match is Guid target)
        {
            return [new ObservationChange(
                ObservationChangeKind.AttachEvidence, transcript, target,
                RelationshipFor(transcript))];
        }

        // No canonical hook matched. Enrichment-only fragments still appear as
        // their own node so a deleted/absent hook does not hide the content;
        // record tool-request nodes (by their native id) so a late PreToolUse
        // hook can adopt them. This does not set OpensToolCall on the transcript
        // node, so it never participates in hook in-flight pairing.
        if (transcript.ToolUseId is string toolCallId &&
            transcript.Interpretation.Role == ObservationRole.ToolRequest)
        {
            _transcriptToolCallNodes[ToolCallKey(transcript.ProviderScopedSessionId, toolCallId)] =
                transcript.EventId;
        }

        return [new ObservationChange(ObservationChangeKind.Add, transcript)];
    }

    private Guid? FindCanonicalMatch(HookObservation transcript)
    {
        // Exact id match first (Claude tool_use.id, Copilot toolCallId).
        if (transcript.ToolUseId is string toolCallId &&
            _byToolCallId.TryGetValue(
                ToolCallKey(transcript.ProviderScopedSessionId, toolCallId),
                out Guid byId))
        {
            return byId;
        }

        // Subagent conversation attaches to its SubagentStart hook.
        if (transcript.SubagentId is string agentId &&
            _bySubagentId.TryGetValue(
                SubagentKey(transcript.ProviderScopedSessionId, agentId),
                out Guid bySubagent))
        {
            return bySubagent;
        }

        // Heuristic signature match for transcripts without a shared id (Cursor).
        if (transcript.Interpretation.Role == ObservationRole.ToolRequest &&
            _byToolSignature.TryGetValue(ToolSignature(transcript), out Queue<Guid>? queue) &&
            queue.Count > 0)
        {
            return queue.Dequeue();
        }

        return null;
    }

    private static TranscriptRelationshipKind RelationshipFor(HookObservation transcript) =>
        transcript.Interpretation.Role switch
        {
            ObservationRole.ToolSuccess or ObservationRole.ToolFailure =>
                TranscriptRelationshipKind.ToolRequestResult,
            ObservationRole.SubagentStart or ObservationRole.SubagentStop =>
                TranscriptRelationshipKind.SubagentConversation,
            _ => TranscriptRelationshipKind.EvidenceOf
        };

    private void EnqueueSignature(string signature, Guid eventId)
    {
        if (!_byToolSignature.TryGetValue(signature, out Queue<Guid>? queue))
        {
            queue = new Queue<Guid>();
            _byToolSignature[signature] = queue;
        }

        queue.Enqueue(eventId);
    }

    // Signature independent of the source: session + coarse tool kind (so
    // Write<->StrReplace align) + primary argument (file path or command).
    private static string ToolSignature(HookObservation observation)
    {
        CanonicalToolKind kind = ToolClassifier.Classify(observation.ToolName);
        string arg = PrimaryArgument(observation) ?? string.Empty;
        return $"{observation.ProviderScopedSessionId}|{kind}|{arg.ToUpperInvariant()}";
    }

    private static string? PrimaryArgument(HookObservation observation)
    {
        if (observation.TargetFilePath is string path)
        {
            return NormalizeArg(path);
        }

        JsonElement payload = observation.Payload;
        string? fromInput =
            RuntimeJson.NestedString(payload, "input", "path", "file_path") ??
            RuntimeJson.NestedString(payload, "tool_input", "path", "file_path") ??
            RuntimeJson.NestedString(payload, "input", "command") ??
            RuntimeJson.String(payload, "command");
        return fromInput is null ? null : NormalizeArg(fromInput);
    }

    private static string NormalizeArg(string value) =>
        value.Replace('/', '\\').Trim();

    private static string ToolCallKey(string scopedSession, string toolCallId) =>
        $"{scopedSession}\0{toolCallId}";

    private static string SubagentKey(string scopedSession, string agentId) =>
        $"{scopedSession}\0agent\0{agentId}";
}
