using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Services;

// Hook-first reconciliation. Hooks are the ordering and summary authority;
// transcript fragments enrich a matching hook node or, when nothing matches,
// appear as their own transcript-only node. Correlation is exact when a native
// id is shared (Claude tool_use.id, Copilot toolCallId) and heuristic when only
// a tool signature is available (Cursor). The reconciler is not thread-safe by
// itself; the coordinator serializes all calls through one loop.
public sealed class ObservationReconciler
{
    private static readonly TimeSpan MaxSignatureSkew = TimeSpan.FromSeconds(30);

    // Provenance dedupe keys already projected, so a replayed/re-tailed row is
    // never projected twice.
    private readonly HashSet<string> _seenProvenance = new(StringComparer.OrdinalIgnoreCase);

    // Canonical hook nodes indexed for correlation.
    private readonly Dictionary<string, Guid> _byToolCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CanonicalToolCandidate>> _byToolSignature =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _bySubagentId = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _matchedCanonicalTools = [];

    // Transcript-only nodes that a later hook may promote to canonical.
    private readonly Dictionary<string, Guid> _transcriptToolCallNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PendingTranscriptTool>> _pendingTranscriptToolsBySignature =
        new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _pendingTranscriptNodeIds = [];
    private readonly ToolCorrelationSignatureBuilder _signatureBuilder = new();

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
        if (hook.Provider == HookProvider.GitHubCopilot &&
            hook.Surface == HookSurface.CopilotCli &&
            hook.Interpretation.Role is ObservationRole.TurnStop or ObservationRole.SessionEnd)
        {
            ClearCopilotSignatureState(hook.ProviderScopedSessionId);
        }

        // A hook that opens a tool call is indexed for transcript correlation.
        // PreToolUse opens the canonical node.
        if (hook.ToolUseId is string toolCallId && hook.Interpretation.OpensToolCall)
        {
            string key = ToolCallKey(hook.ProviderScopedSessionId, toolCallId);
            _byToolCallId[key] = hook.EventId;

            if (_transcriptToolCallNodes.Remove(key, out Guid transcriptNode))
            {
                _pendingTranscriptNodeIds.Remove(transcriptNode);
                _matchedCanonicalTools.Add(hook.EventId);
                // A transcript tool node was projected before this hook; the
                // PromotePrimary handler adds the hook and re-parents that node,
                // so no separate Add is emitted here.
                return [new ObservationChange(ObservationChangeKind.PromotePrimary, hook, transcriptNode)];
            }
        }

        if (hook.Interpretation.OpensToolCall && hook.ToolName is not null)
        {
            string signature = _signatureBuilder.Build(hook);
            if (TryTakePendingTranscript(
                signature,
                hook,
                out PendingTranscriptTool pending))
            {
                _pendingTranscriptNodeIds.Remove(pending.EventId);
                _matchedCanonicalTools.Add(hook.EventId);
                if (pending.ToolCallId is string pendingToolCallId)
                {
                    string key = ToolCallKey(hook.ProviderScopedSessionId, pendingToolCallId);
                    _transcriptToolCallNodes.Remove(key);
                    _byToolCallId[key] = hook.EventId;
                }

                return [new ObservationChange(
                    ObservationChangeKind.PromotePrimary,
                    hook,
                    pending.EventId)];
            }

            EnqueueSignature(signature, hook);
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
            if (transcript.Interpretation.Role == ObservationRole.ToolRequest)
            {
                RegisterTranscriptToolAlias(transcript, target);
                _matchedCanonicalTools.Add(target);
            }

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

        if (transcript.Interpretation.Role == ObservationRole.ToolRequest)
        {
            string signature = _signatureBuilder.Build(transcript);
            if (!_pendingTranscriptToolsBySignature.TryGetValue(
                signature,
                out List<PendingTranscriptTool>? pending))
            {
                pending = [];
                _pendingTranscriptToolsBySignature[signature] = pending;
            }

            pending.Add(new PendingTranscriptTool(
                transcript.EventId,
                transcript.ToolUseId,
                transcript.EffectiveTimestamp));
            _pendingTranscriptNodeIds.Add(transcript.EventId);
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

        if (transcript.ToolUseId is string pendingToolCallId &&
            _transcriptToolCallNodes.TryGetValue(
                ToolCallKey(transcript.ProviderScopedSessionId, pendingToolCallId),
                out Guid transcriptToolNode))
        {
            return transcriptToolNode;
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
            TryTakeCanonicalTool(
                _signatureBuilder.Build(transcript),
                transcript,
                out Guid canonicalTool))
        {
            return canonicalTool;
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

    private void EnqueueSignature(string signature, HookObservation hook)
    {
        if (!_byToolSignature.TryGetValue(
            signature,
            out List<CanonicalToolCandidate>? candidates))
        {
            candidates = [];
            _byToolSignature[signature] = candidates;
        }

        candidates.Add(new CanonicalToolCandidate(hook.EventId, hook.EffectiveTimestamp));
    }

    private bool TryTakeCanonicalTool(
        string signature,
        HookObservation transcript,
        out Guid eventId)
    {
        eventId = default;
        if (!_byToolSignature.TryGetValue(
            signature,
            out List<CanonicalToolCandidate>? candidates))
        {
            return false;
        }

        int bestIndex = -1;
        TimeSpan bestSkew = TimeSpan.MaxValue;
        for (int index = 0; index < candidates.Count; index++)
        {
            CanonicalToolCandidate candidate = candidates[index];
            if (_matchedCanonicalTools.Contains(candidate.EventId))
            {
                continue;
            }

            if (!UsesAuthoritativeTranscriptClock(transcript))
            {
                bestIndex = index;
                break;
            }

            TimeSpan skew = Abs(candidate.Timestamp - transcript.EffectiveTimestamp);
            if (skew <= MaxSignatureSkew && skew < bestSkew)
            {
                bestIndex = index;
                bestSkew = skew;
            }
        }

        if (bestIndex >= 0)
        {
            eventId = candidates[bestIndex].EventId;
            candidates.RemoveAt(bestIndex);
            return true;
        }

        return false;
    }

    private bool TryTakePendingTranscript(
        string signature,
        HookObservation hook,
        out PendingTranscriptTool pendingTool)
    {
        pendingTool = default;
        if (!_pendingTranscriptToolsBySignature.TryGetValue(
            signature,
            out List<PendingTranscriptTool>? candidates))
        {
            return false;
        }

        int bestIndex = -1;
        TimeSpan bestSkew = TimeSpan.MaxValue;
        for (int index = 0; index < candidates.Count; index++)
        {
            PendingTranscriptTool candidate = candidates[index];
            if (!_pendingTranscriptNodeIds.Contains(candidate.EventId))
            {
                continue;
            }

            if (hook.Provider != HookProvider.GitHubCopilot ||
                hook.Surface != HookSurface.CopilotCli)
            {
                bestIndex = index;
                break;
            }

            TimeSpan skew = Abs(candidate.Timestamp - hook.EffectiveTimestamp);
            if (skew <= MaxSignatureSkew && skew < bestSkew)
            {
                bestIndex = index;
                bestSkew = skew;
            }
        }

        if (bestIndex >= 0)
        {
            pendingTool = candidates[bestIndex];
            candidates.RemoveAt(bestIndex);
            return true;
        }

        return false;
    }

    private static TimeSpan Abs(TimeSpan value) =>
        value < TimeSpan.Zero ? -value : value;

    private static bool UsesAuthoritativeTranscriptClock(HookObservation observation) =>
        observation.Provider == HookProvider.GitHubCopilot &&
        observation.Surface == HookSurface.CopilotCli;

    private void ClearCopilotSignatureState(string scopedSessionId)
    {
        string prefix = scopedSessionId + "|";
        foreach (string key in _byToolSignature.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray())
        {
            _byToolSignature.Remove(key);
        }

        foreach (string key in _pendingTranscriptToolsBySignature.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray())
        {
            foreach (PendingTranscriptTool pending in _pendingTranscriptToolsBySignature[key])
            {
                _pendingTranscriptNodeIds.Remove(pending.EventId);
            }

            _pendingTranscriptToolsBySignature.Remove(key);
        }
    }

    private void RegisterTranscriptToolAlias(HookObservation transcript, Guid target)
    {
        if (transcript.ToolUseId is not string toolCallId)
        {
            return;
        }

        string key = ToolCallKey(transcript.ProviderScopedSessionId, toolCallId);
        _byToolCallId[key] = target;
        _transcriptToolCallNodes.Remove(key);
        _pendingTranscriptNodeIds.Remove(transcript.EventId);
    }

    private static string ToolCallKey(string scopedSession, string toolCallId) =>
        $"{scopedSession}\0{toolCallId}";

    private static string SubagentKey(string scopedSession, string agentId) =>
        $"{scopedSession}\0agent\0{agentId}";

    private readonly record struct PendingTranscriptTool(
        Guid EventId,
        string? ToolCallId,
        DateTimeOffset Timestamp);

    private readonly record struct CanonicalToolCandidate(
        Guid EventId,
        DateTimeOffset Timestamp);
}
