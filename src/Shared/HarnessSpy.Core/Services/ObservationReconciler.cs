using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;
using HarnessSpy.Core.Runtimes.Cursor;

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
    private static readonly TimeSpan MaxCursorThoughtDuplicateSkew =
        TimeSpan.FromSeconds(5);

    // Provenance dedupe keys already projected, so a replayed/re-tailed row is
    // never projected twice.
    private readonly HashSet<string> _seenProvenance = new(StringComparer.OrdinalIgnoreCase);

    // Canonical hook nodes indexed for correlation.
    private readonly Dictionary<string, Guid> _byToolCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CanonicalToolCandidate>> _byToolSignature =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CanonicalToolCandidate>>
        _byCursorExecutionSignature = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _bySubagentId = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _matchedCanonicalTools = [];
    private readonly Dictionary<string, List<Guid>> _cursorEvidenceHooks =
        new(StringComparer.Ordinal);

    // Transcript-only nodes that a later hook may promote to canonical.
    private readonly Dictionary<string, Guid> _transcriptToolCallNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PendingTranscriptTool>> _pendingTranscriptToolsBySignature =
        new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _pendingTranscriptNodeIds = [];
    private readonly Dictionary<string, List<Guid>> _pendingCursorEvidence =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _cursorAssistantStepTargets =
        new(StringComparer.Ordinal);
    private readonly ToolCorrelationSignatureBuilder _signatureBuilder = new();
    private readonly CursorGenerationIdentity _cursorGenerationIdentity = new();
    private readonly Dictionary<string, CursorThoughtCandidate> _cursorThoughts =
        new(StringComparer.Ordinal);

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
        if (IsDuplicateCursorThought(hook))
        {
            return [];
        }

        if (hook.Provider == HookProvider.Cursor &&
            hook.Surface == HookSurface.CursorIde &&
            hook.Interpretation.Role is
                ObservationRole.TurnStop or ObservationRole.SessionEnd)
        {
            ClearCursorThoughts(hook.ProviderScopedSessionId);
        }

        if (TryCursorEvidenceKey(hook, out string? cursorEvidenceKey))
        {
            if (TryTakePendingCursorEvidence(cursorEvidenceKey, out Guid transcriptNode))
            {
                return [new ObservationChange(
                    ObservationChangeKind.PromotePrimary,
                    hook,
                    transcriptNode,
                    TranscriptRelationshipKind.EvidenceOf,
                    InferenceEvidence.Heuristic)];
            }

            if (!_cursorEvidenceHooks.TryGetValue(
                cursorEvidenceKey,
                out List<Guid>? hooks))
            {
                hooks = [];
                _cursorEvidenceHooks[cursorEvidenceKey] = hooks;
            }

            hooks.Add(hook.EventId);
        }

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
                    pending.EventId,
                    TranscriptRelationshipKind.EvidenceOf,
                    InferenceEvidence.Heuristic)];
            }

            EnqueueSignature(signature, hook);
        }

        if (IsCursorExecutionFallback(hook) &&
            hook.ToolName is not null)
        {
            string signature = _signatureBuilder.Build(hook);
            if (TryTakePendingTranscript(
                    signature,
                    hook,
                    out PendingTranscriptTool pending))
            {
                _pendingTranscriptNodeIds.Remove(pending.EventId);
                _matchedCanonicalTools.Add(hook.EventId);
                return [new ObservationChange(
                    ObservationChangeKind.PromotePrimary,
                    hook,
                    pending.EventId,
                    TranscriptRelationshipKind.EvidenceOf,
                    InferenceEvidence.Heuristic)];
            }

            EnqueueCandidate(
                _byCursorExecutionSignature,
                signature,
                hook);
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

        Guid? match = FindCanonicalMatch(
            transcript,
            out InferenceEvidence bindingEvidence);
        if (match is Guid target)
        {
            if (transcript.Interpretation.Role == ObservationRole.ToolRequest)
            {
                RegisterTranscriptToolAlias(transcript, target);
                RegisterCursorAssistantStepTarget(transcript, target);
                _matchedCanonicalTools.Add(target);
            }

            return [new ObservationChange(
                ObservationChangeKind.AttachEvidence, transcript, target,
                RelationshipFor(transcript),
                bindingEvidence)];
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

        if (TryCursorEvidenceKey(transcript, out string? cursorEvidenceKey))
        {
            if (!_pendingCursorEvidence.TryGetValue(
                cursorEvidenceKey,
                out List<Guid>? pendingEvidence))
            {
                pendingEvidence = [];
                _pendingCursorEvidence[cursorEvidenceKey] = pendingEvidence;
            }

            pendingEvidence.Add(transcript.EventId);
        }

        return [new ObservationChange(ObservationChangeKind.Add, transcript)];
    }

    private Guid? FindCanonicalMatch(
        HookObservation transcript,
        out InferenceEvidence bindingEvidence)
    {
        bindingEvidence = InferenceEvidence.Observed;
        if (TryCursorEvidenceKey(transcript, out string? cursorEvidenceKey) &&
            _cursorEvidenceHooks.TryGetValue(
                cursorEvidenceKey,
                out List<Guid>? evidenceHooks) &&
            evidenceHooks.Count == 1)
        {
            Guid matched = evidenceHooks[0];
            evidenceHooks.RemoveAt(0);
            bindingEvidence = InferenceEvidence.Heuristic;
            return matched;
        }

        if (transcript.Provider == HookProvider.Cursor &&
            transcript.Surface == HookSurface.CursorIde &&
            transcript.Interpretation.Role == ObservationRole.AgentThought &&
            transcript.AssistantStepId is string assistantStepId &&
            _cursorAssistantStepTargets.TryGetValue(
                CursorAssistantStepKey(transcript, assistantStepId),
                out Guid assistantStepTarget))
        {
            bindingEvidence = InferenceEvidence.Heuristic;
            return assistantStepTarget;
        }

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
        if (transcript.Interpretation.Role == ObservationRole.ToolRequest)
        {
            string signature = _signatureBuilder.Build(transcript);
            if (TryTakeCanonicalTool(
                    signature,
                    transcript,
                    out CanonicalToolCandidate canonicalTool))
            {
                if (transcript.Provider == HookProvider.Cursor)
                {
                    DiscardMatchingCursorExecution(
                        signature,
                        canonicalTool.Timestamp);
                }

                bindingEvidence = InferenceEvidence.Heuristic;
                return canonicalTool.EventId;
            }

            if (transcript.Provider == HookProvider.Cursor &&
                transcript.Surface == HookSurface.CursorIde &&
                TryTakeCursorExecution(
                    signature,
                    out CanonicalToolCandidate executionHook))
            {
                bindingEvidence = InferenceEvidence.Heuristic;
                return executionHook.EventId;
            }
        }

        return null;
    }

    private static TranscriptRelationshipKind RelationshipFor(HookObservation transcript)
    {
        if (transcript.SubagentId is not null &&
            transcript.Interpretation.Role is
                ObservationRole.AgentThought or
                ObservationRole.AgentResponse or
                ObservationRole.Message or
                ObservationRole.SystemPrompt)
        {
            return TranscriptRelationshipKind.SubagentConversation;
        }

        return transcript.Interpretation.Role switch
        {
            ObservationRole.PromptSubmitted when transcript.Interpretation.Skill is not null =>
                TranscriptRelationshipKind.AttachmentForPrompt,
            ObservationRole.ToolSuccess or ObservationRole.ToolFailure =>
                TranscriptRelationshipKind.ToolRequestResult,
            ObservationRole.SubagentStart or ObservationRole.SubagentStop =>
                TranscriptRelationshipKind.SubagentConversation,
            _ => TranscriptRelationshipKind.EvidenceOf
        };
    }

    private void EnqueueSignature(
        string signature,
        HookObservation hook) =>
        EnqueueCandidate(_byToolSignature, signature, hook);

    private static void EnqueueCandidate(
        Dictionary<string, List<CanonicalToolCandidate>> index,
        string signature,
        HookObservation hook)
    {
        if (!index.TryGetValue(
            signature,
            out List<CanonicalToolCandidate>? candidates))
        {
            candidates = [];
            index[signature] = candidates;
        }

        candidates.Add(new CanonicalToolCandidate(hook.EventId, hook.EffectiveTimestamp));
    }

    private bool TryTakeCanonicalTool(
        string signature,
        HookObservation transcript,
        out CanonicalToolCandidate matched)
    {
        matched = default;
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
            matched = candidates[bestIndex];
            candidates.RemoveAt(bestIndex);
            return true;
        }

        return false;
    }

    private bool TryTakeCursorExecution(
        string signature,
        out CanonicalToolCandidate matched)
    {
        matched = default;
        if (!_byCursorExecutionSignature.TryGetValue(
                signature,
                out List<CanonicalToolCandidate>? candidates))
        {
            return false;
        }

        int index = candidates.FindIndex(candidate =>
            !_matchedCanonicalTools.Contains(candidate.EventId));
        if (index < 0)
        {
            return false;
        }

        matched = candidates[index];
        candidates.RemoveAt(index);
        return true;
    }

    private void DiscardMatchingCursorExecution(
        string signature,
        DateTimeOffset canonicalTimestamp)
    {
        if (!_byCursorExecutionSignature.TryGetValue(
                signature,
                out List<CanonicalToolCandidate>? candidates))
        {
            return;
        }

        int bestIndex = -1;
        TimeSpan bestSkew = TimeSpan.MaxValue;
        for (int index = 0; index < candidates.Count; index++)
        {
            TimeSpan skew =
                candidates[index].Timestamp - canonicalTimestamp;
            if (skew >= TimeSpan.Zero &&
                skew <= MaxSignatureSkew &&
                skew < bestSkew)
            {
                bestIndex = index;
                bestSkew = skew;
            }
        }

        if (bestIndex >= 0)
        {
            candidates.RemoveAt(bestIndex);
        }
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

    private bool TryTakePendingCursorEvidence(string key, out Guid eventId)
    {
        eventId = default;
        if (!_pendingCursorEvidence.TryGetValue(
                key,
                out List<Guid>? pending) ||
            pending.Count != 1)
        {
            return false;
        }

        eventId = pending[0];
        pending.RemoveAt(0);
        if (pending.Count == 0)
        {
            _pendingCursorEvidence.Remove(key);
        }

        return true;
    }

    private static bool TryCursorEvidenceKey(
        HookObservation observation,
        out string key)
    {
        key = string.Empty;
        if (observation.Provider != HookProvider.Cursor ||
            observation.Surface != HookSurface.CursorIde)
        {
            return false;
        }

        string? value = observation.Interpretation.Role switch
        {
            ObservationRole.PromptSubmitted => NormalizePrompt(observation.PromptText),
            ObservationRole.AgentThought or ObservationRole.AgentResponse =>
                NormalizeText(observation.Text),
            ObservationRole.TurnStop => NormalizeStopStatus(observation.Status),
            _ => null
        };
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        key =
            $"{observation.ProviderScopedSessionId}\0" +
            $"{observation.Interpretation.Role}\0{value}";
        return true;
    }

    private static string? NormalizePrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        const string open = "<user_query>";
        const string close = "</user_query>";
        int start = prompt.IndexOf(open, StringComparison.Ordinal);
        if (start >= 0)
        {
            start += open.Length;
            int end = prompt.IndexOf(close, start, StringComparison.Ordinal);
            prompt = end < 0 ? prompt[start..] : prompt[start..end];
        }

        return NormalizeText(prompt);
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = TrimTrailingRedaction(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(
            " ",
            value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
    }

    private static string TrimTrailingRedaction(string value)
    {
        const string redacted = "[REDACTED]";
        string trimmed = value.TrimEnd();
        while (trimmed.EndsWith(redacted, StringComparison.Ordinal))
        {
            int markerStart = trimmed.Length - redacted.Length;
            if (markerStart > 0 && !char.IsWhiteSpace(trimmed[markerStart - 1]))
            {
                break;
            }

            trimmed = trimmed[..markerStart].TrimEnd();
        }

        return trimmed;
    }

    private static string? NormalizeStopStatus(string? value)
    {
        string? normalized = NormalizeText(value)?.ToLowerInvariant();
        return normalized switch
        {
            "success" or "completed" or "complete" or "end_turn" => "completed",
            "error" or "failed" or "failure" or "aborted" => "aborted",
            _ => normalized
        };
    }

    private static string ToolCallKey(string scopedSession, string toolCallId) =>
        $"{scopedSession}\0{toolCallId}";

    private static string SubagentKey(string scopedSession, string agentId) =>
        $"{scopedSession}\0agent\0{agentId}";

    private static bool IsCursorExecutionFallback(HookObservation hook) =>
        hook.Provider == HookProvider.Cursor &&
        hook.Surface == HookSurface.CursorIde &&
        (hook.Interpretation.Role == ObservationRole.InnerExecutionStart ||
         hook.Interpretation.Role == ObservationRole.FileAccess &&
         hook.Interpretation.Direction == ObservationDirection.Input);

    private void RegisterCursorAssistantStepTarget(
        HookObservation transcript,
        Guid target)
    {
        if (transcript.Provider != HookProvider.Cursor ||
            transcript.Surface != HookSurface.CursorIde ||
            transcript.AssistantStepId is not string assistantStepId)
        {
            return;
        }

        _cursorAssistantStepTargets.TryAdd(
            CursorAssistantStepKey(transcript, assistantStepId),
            target);
    }

    private static string CursorAssistantStepKey(
        HookObservation observation,
        string assistantStepId) =>
        $"{observation.ProviderScopedSessionId}\0{assistantStepId}";

    private bool IsDuplicateCursorThought(HookObservation hook)
    {
        if (hook.Provider != HookProvider.Cursor ||
            hook.Surface != HookSurface.CursorIde ||
            hook.Interpretation.Role != ObservationRole.AgentThought ||
            NormalizeText(hook.Text) is not string text ||
            hook.GenerationId is not string turnId)
        {
            return false;
        }

        string? rawGenerationId =
            RuntimeJson.String(hook.Payload, "generation_id");
        if (rawGenerationId is null)
        {
            return false;
        }

        bool isStepScoped =
            _cursorGenerationIdentity.IsStepScoped(rawGenerationId);
        string key =
            $"{hook.ProviderScopedSessionId}\0{turnId}\0{text}";
        if (_cursorThoughts.TryGetValue(
                key,
                out CursorThoughtCandidate existing) &&
            existing.IsStepScoped != isStepScoped &&
            Abs(existing.Timestamp - hook.EffectiveTimestamp) <=
                MaxCursorThoughtDuplicateSkew)
        {
            return true;
        }

        _cursorThoughts[key] = new CursorThoughtCandidate(
            hook.EffectiveTimestamp,
            isStepScoped);
        return false;
    }

    private void ClearCursorThoughts(string scopedSessionId)
    {
        string prefix = scopedSessionId + "\0";
        foreach (string key in _cursorThoughts.Keys
                     .Where(key => key.StartsWith(
                         prefix,
                         StringComparison.Ordinal))
                     .ToArray())
        {
            _cursorThoughts.Remove(key);
        }
    }

    private readonly record struct PendingTranscriptTool(
        Guid EventId,
        string? ToolCallId,
        DateTimeOffset Timestamp);

    private readonly record struct CanonicalToolCandidate(
        Guid EventId,
        DateTimeOffset Timestamp);

    private readonly record struct CursorThoughtCandidate(
        DateTimeOffset Timestamp,
        bool IsStepScoped);
}
