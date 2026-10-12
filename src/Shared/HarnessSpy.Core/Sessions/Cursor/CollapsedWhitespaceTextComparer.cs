namespace HarnessSpy.Core.Sessions.Cursor;

internal sealed class CollapsedWhitespaceTextComparer : IEqualityComparer<string>
{
    public bool Equals(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        int leftIndex = SkipWhitespace(left, 0);
        int rightIndex = SkipWhitespace(right, 0);
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            while (leftIndex < left.Length &&
                   !char.IsWhiteSpace(left[leftIndex]) &&
                   rightIndex < right.Length &&
                   !char.IsWhiteSpace(right[rightIndex]))
            {
                if (left[leftIndex] != right[rightIndex])
                {
                    return false;
                }

                leftIndex++;
                rightIndex++;
            }

            bool leftAtBoundary =
                leftIndex >= left.Length ||
                char.IsWhiteSpace(left[leftIndex]);
            bool rightAtBoundary =
                rightIndex >= right.Length ||
                char.IsWhiteSpace(right[rightIndex]);
            if (!leftAtBoundary || !rightAtBoundary)
            {
                return false;
            }

            leftIndex = SkipWhitespace(left, leftIndex);
            rightIndex = SkipWhitespace(right, rightIndex);
        }

        return SkipWhitespace(left, leftIndex) == left.Length &&
            SkipWhitespace(right, rightIndex) == right.Length;
    }

    public int GetHashCode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint hash = offset;
        bool hasContent = false;
        bool pendingSpace = false;

        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = hasContent;
                continue;
            }

            if (pendingSpace)
            {
                hash = (hash ^ ' ') * prime;
                pendingSpace = false;
            }

            hash = (hash ^ character) * prime;
            hasContent = true;
        }

        return unchecked((int)hash);
    }

    private int SkipWhitespace(string value, int index)
    {
        while (index < value.Length && char.IsWhiteSpace(value[index]))
        {
            index++;
        }

        return index;
    }
}
