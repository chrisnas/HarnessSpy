using HarnessSpy.Core.Sessions.Plans;

namespace HarnessSpy.Tests;

public sealed class SessionPlanContentNormalizerTests
{
    private readonly SessionPlanContentNormalizer _normalizer = new();

    [Fact]
    public void NormalizeStripsBomCarriageReturnsAndTrailingNewlines()
    {
        string normalized = _normalizer.Normalize("\uFEFFhello\r\nworld\n\n");

        Assert.Equal("hello\nworld", normalized);
    }

    [Fact]
    public void NormalizePreservesTrailingSpacesAndInternalBlankLines()
    {
        string normalized = _normalizer.Normalize("a  \n\n\nb");

        Assert.Equal("a  \n\n\nb", normalized);
    }

    [Fact]
    public void HashIgnoresTrailingNewlineDifferences()
    {
        string first = _normalizer.Hash(_normalizer.Normalize("text"));
        string second = _normalizer.Hash(_normalizer.Normalize("text\n"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void HashDistinguishesTrailingSpace()
    {
        string first = _normalizer.Hash(_normalizer.Normalize("text"));
        string second = _normalizer.Hash(_normalizer.Normalize("text "));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SnapshotReturnsNullForEmptyContent()
    {
        Assert.Null(_normalizer.Snapshot(null));
        Assert.Null(_normalizer.Snapshot(string.Empty));
        Assert.Null(_normalizer.Snapshot("\n\n"));
    }
}
