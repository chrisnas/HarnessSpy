namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public sealed class CursorBubbleKeyParser
{
    private const string Prefix = "bubbleId:";

    public bool TryParse(
        string? key,
        IReadOnlySet<string> knownComposerIds,
        string? payloadBubbleId,
        out string composerId,
        out string bubbleId)
    {
        composerId = string.Empty;
        bubbleId = string.Empty;
        if (string.IsNullOrWhiteSpace(key) ||
            !key.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string remainder = key[Prefix.Length..];
        string? matchedComposer = knownComposerIds
            .Where(candidate =>
                remainder.StartsWith(
                    candidate + ":",
                    StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Length)
            .FirstOrDefault();
        if (matchedComposer is not null)
        {
            composerId = matchedComposer;
            bubbleId = remainder[(matchedComposer.Length + 1)..];
            return bubbleId.Length > 0;
        }

        if (!string.IsNullOrWhiteSpace(payloadBubbleId))
        {
            string suffix = ":" + payloadBubbleId;
            if (remainder.EndsWith(suffix, StringComparison.Ordinal) &&
                remainder.Length > suffix.Length)
            {
                composerId = remainder[..^suffix.Length];
                bubbleId = payloadBubbleId;
                return true;
            }
        }

        int separator = remainder.IndexOf(':');
        if (separator <= 0 || separator == remainder.Length - 1)
        {
            return false;
        }

        composerId = remainder[..separator];
        bubbleId = remainder[(separator + 1)..];
        return true;
    }
}
