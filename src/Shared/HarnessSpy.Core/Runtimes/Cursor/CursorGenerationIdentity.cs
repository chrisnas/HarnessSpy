namespace HarnessSpy.Core.Runtimes.Cursor;

// Cursor 3.19 can emit a second afterAgentThought observation whose
// generation_id is "<turn-guid>-<step>-<token>". The prefix is the actual
// conversation turn; the suffix identifies an assistant step and must not
// create another turn in the hook tree.
internal sealed class CursorGenerationIdentity
{
    private const int GuidLength = 36;

    public string? CanonicalTurnId(string? generationId) =>
        IsStepScoped(generationId)
            ? generationId![..GuidLength]
            : generationId;

    public bool IsStepScoped(string? generationId)
    {
        if (string.IsNullOrWhiteSpace(generationId) ||
            generationId.Length <= GuidLength + 3 ||
            generationId[GuidLength] != '-' ||
            !Guid.TryParseExact(generationId.AsSpan(0, GuidLength), "D", out _))
        {
            return false;
        }

        ReadOnlySpan<char> suffix = generationId.AsSpan(GuidLength + 1);
        int separator = suffix.IndexOf('-');
        if (separator <= 0 || separator == suffix.Length - 1)
        {
            return false;
        }

        ReadOnlySpan<char> step = suffix[..separator];
        ReadOnlySpan<char> token = suffix[(separator + 1)..];
        return step.IndexOfAnyExceptInRange('0', '9') < 0 &&
            token.Length == 4 &&
            token.IndexOfAnyExcept(
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789") < 0;
    }
}
