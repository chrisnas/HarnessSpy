namespace HarnessSpy.Core.Text;

public sealed class BoundedTextPreviewBuilder
{
    private const char Ellipsis = '\u2026';
    private const int MaximumStackCharacters = 512;

    public string Build(string? value, int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        char[]? heapBuffer = null;
        Span<char> buffer = maximumLength < MaximumStackCharacters
            ? stackalloc char[maximumLength + 1]
            : heapBuffer = new char[maximumLength + 1];

        int written = 0;
        bool hasContent = false;
        bool pendingSpace = false;
        bool truncated = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = hasContent;
                continue;
            }

            if (pendingSpace)
            {
                if (written >= maximumLength)
                {
                    truncated = true;
                    break;
                }

                buffer[written++] = ' ';
                pendingSpace = false;
            }

            if (written >= maximumLength)
            {
                truncated = true;
                break;
            }

            buffer[written++] = character;
            hasContent = true;
        }

        if (!hasContent)
        {
            return string.Empty;
        }

        if (truncated)
        {
            buffer[written++] = Ellipsis;
        }

        return new string(buffer[..written]);
    }

    public string? BuildOrNull(string? value, int maximumLength)
    {
        string preview = Build(value, maximumLength);
        return preview.Length == 0 ? null : preview;
    }
}
