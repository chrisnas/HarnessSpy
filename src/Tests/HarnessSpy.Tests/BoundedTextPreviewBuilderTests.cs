using HarnessSpy.Core.Text;

namespace HarnessSpy.Tests;

public sealed class BoundedTextPreviewBuilderTests
{
    private readonly BoundedTextPreviewBuilder _builder = new();

    [Fact]
    public void BuildCollapsesWhitespaceAndTrimsOuterWhitespace()
    {
        string preview = _builder.Build(
            " \r\n  first\tsecond  \n third \r\n",
            80);

        Assert.Equal("first second third", preview);
    }

    [Fact]
    public void BuildStopsAtLimitAndAppendsEllipsis()
    {
        string payload = new('x', 1024 * 1024);

        string preview = _builder.Build(payload, 12);

        Assert.Equal("xxxxxxxxxxxx\u2026", preview);
    }

    [Fact]
    public void BuildDoesNotMarkTrailingWhitespaceAsTruncated()
    {
        string preview = _builder.Build("content \r\n\t", 7);

        Assert.Equal("content", preview);
    }
}
