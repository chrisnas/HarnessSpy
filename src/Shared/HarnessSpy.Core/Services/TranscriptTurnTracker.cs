using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sources;

namespace HarnessSpy.Core.Services;

// Converts provider transcript turn metadata into the generation key used by
// the hook tree. Claude exposes the hook turn id directly. Copilot groups its
// model-step turnIds by interactionId. Cursor exposes neither, so rows between
// each user prompt and turn_ended record receive one transcript interaction
// key that the WPF projection aliases to the matching hook turn.
public sealed class TranscriptTurnTracker
{
    private readonly bool _usesCopilotInteractions;
    private readonly bool _usesCursorInteractions;
    private readonly Dictionary<string, string> _copilotTurnsByInteraction =
        new(StringComparer.Ordinal);

    private int _copilotTurnCount;
    private int _cursorTurnCount;
    private string? _currentTurnId;

    public TranscriptTurnTracker(string dialectId)
    {
        _usesCopilotInteractions = dialectId == DialectIds.CopilotCliTranscript;
        _usesCursorInteractions = dialectId == DialectIds.CursorTranscript;
    }

    public string? Observe(TranscriptRowScanner.RowMeta metadata)
    {
        if (_usesCursorInteractions)
        {
            return ObserveCursorInteraction(metadata);
        }

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

    private string? ObserveCursorInteraction(
        TranscriptRowScanner.RowMeta metadata)
    {
        if (string.Equals(
                metadata.Role,
                "user",
                StringComparison.OrdinalIgnoreCase))
        {
            _currentTurnId = NextCursorTurn();
        }
        else if (_currentTurnId is null &&
                 (string.Equals(
                      metadata.Role,
                      "assistant",
                      StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(
                      metadata.RecordType,
                      "turn_ended",
                      StringComparison.OrdinalIgnoreCase)))
        {
            // A transcript can be discovered after its first prompt row was
            // rotated away. Keep the remaining assistant activity together.
            _currentTurnId = NextCursorTurn();
        }

        string? observedTurn = _currentTurnId;
        if (string.Equals(
                metadata.RecordType,
                "turn_ended",
                StringComparison.OrdinalIgnoreCase))
        {
            _currentTurnId = null;
        }

        return observedTurn;
    }

    private string NextCursorTurn()
    {
        _cursorTurnCount++;
        return $"transcript-cursor-turn:{_cursorTurnCount}";
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
