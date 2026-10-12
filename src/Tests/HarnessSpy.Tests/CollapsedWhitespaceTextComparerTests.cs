using HarnessSpy.Core.Sessions.Cursor;

namespace HarnessSpy.Tests;

public sealed class CollapsedWhitespaceTextComparerTests
{
    private readonly CollapsedWhitespaceTextComparer _comparer = new();

    [Theory]
    [InlineData("alpha beta", " alpha\tbeta ")]
    [InlineData("alpha\r\nbeta", "alpha \n \t beta")]
    [InlineData("single", "\r\nsingle\t")]
    [InlineData("   ", "\r\n\t")]
    public void EquivalentWhitespaceHasEqualHash(
        string left,
        string right)
    {
        Assert.True(_comparer.Equals(left, right));
        Assert.Equal(
            _comparer.GetHashCode(left),
            _comparer.GetHashCode(right));
    }

    [Fact]
    public void HashCollisionStillUsesNormalizedEquality()
    {
        const string first = "costarring";
        const string second = "liquid";
        Assert.Equal(
            _comparer.GetHashCode(first),
            _comparer.GetHashCode(second));

        HashSet<string> values = new(_comparer)
        {
            first,
            second
        };

        Assert.Equal(2, values.Count);
    }
}
