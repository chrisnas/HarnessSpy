using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sources;

namespace HarnessSpy.Core.Services;

// Converts provider transcript turn metadata into the generation key used by
// the hook tree. Claude exposes the hook turn id directly. Copilot's native
// turnId identifies one assistant/model step and resets for every user
// interaction, so all rows between consecutive user.message records instead
// share one transcript-interaction key. The WPF projection aligns that key to a
// captured hook turn by timestamp; unmatched historical interactions retain
// their namespaced key and can never collide with a process-local derived-N.
public sealed class TranscriptTurnTracker
{
    private readonly bool _usesCopilotInteractions;
    private readonly Dictionary<string, string> _copilotTurnsByInteraction =
        new(StringComparer.Ordinal);

    private int _copilotTurnCount;
    private string? _currentTurnId;

    public TranscriptTurnTracker(string dialectId)
    {
        _usesCopilotInteractions = dialectId == DialectIds.CopilotCliTranscript;
    }

    public string? Observe(TranscriptRowScanner.RowMeta metadata)
    {
        if (!_usesCopilotInteractions)
        {
            if (metadata.TurnId is not null)
            {
                _currentTurnId = metadata.TurnId;
            }

            return _currentTurnId;
        }

        if (metadata.RecordType == "user.message")
        {
            _currentTurnId = ResolveOrCreateCopilotTurn(metadata.InteractionId);
            return _currentTurnId;
        }

        if (metadata.InteractionId is string interactionId)
        {
            if (_copilotTurnsByInteraction.TryGetValue(interactionId, out string? mapped))
            {
                _currentTurnId = mapped;
            }
            else if (_currentTurnId is not null)
            {
                _copilotTurnsByInteraction[interactionId] = _currentTurnId;
            }
            else
            {
                _currentTurnId = ResolveOrCreateCopilotTurn(interactionId);
            }
        }

        return _currentTurnId;
    }

    private string ResolveOrCreateCopilotTurn(string? interactionId)
    {
        if (interactionId is not null &&
            _copilotTurnsByInteraction.TryGetValue(interactionId, out string? existing))
        {
            return existing;
        }

        _copilotTurnCount++;
        string derived = interactionId is null
            ? $"transcript-derived-{_copilotTurnCount}"
            : $"transcript-interaction:{interactionId}";
        if (interactionId is not null)
        {
            _copilotTurnsByInteraction[interactionId] = derived;
        }

        return derived;
    }
}
