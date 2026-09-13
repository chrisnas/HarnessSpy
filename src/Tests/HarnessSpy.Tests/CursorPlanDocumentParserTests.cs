using HarnessSpy.Core.Sessions.Cursor;

namespace HarnessSpy.Tests;

public sealed class CursorPlanDocumentParserTests
{
    private readonly CursorPlanDocumentParser _parser = new();

    [Fact]
    public void ParsesFrontMatterAndBody()
    {
        string content =
            "---\n" +
            "name: My Plan\n" +
            "overview: Do the thing\n" +
            "todos:\n" +
            "  - id: step-1\n" +
            "    content: First step\n" +
            "    status: completed\n" +
            "isProject: false\n" +
            "---\n" +
            "\n" +
            "# Body heading\n" +
            "Body text\n";

        CursorPlanDocument document = _parser.Parse(content);

        Assert.True(document.HasFrontMatter);
        Assert.Equal("My Plan", document.Name);
        Assert.Equal("Do the thing", document.Overview);
        Assert.False(document.IsProject);
        CursorPlanTodo todo = Assert.Single(document.Todos);
        Assert.Equal("step-1", todo.Id);
        Assert.Equal("First step", todo.Content);
        Assert.Contains("# Body heading", document.Body);
    }

    [Fact]
    public void StructuredKeyIgnoresTodoStatusSoFileMatchesCreatePlan()
    {
        string fileContent =
            "---\n" +
            "name: My Plan\n" +
            "overview: Do the thing\n" +
            "todos:\n" +
            "  - id: step-1\n" +
            "    content: First step\n" +
            "    status: completed\n" +
            "---\n" +
            "Body\n";

        CursorPlanDocument document = _parser.Parse(fileContent);

        // The CreatePlan tool input carries the same todos but no status.
        string createPlanKey = _parser.ComputeStructuredKey(
            "My Plan",
            "Do the thing",
            [new CursorPlanTodo("step-1", "First step", null)]);

        Assert.Equal(createPlanKey, document.StructuredKey);
    }

    [Fact]
    public void MalformedFrontMatterIsReportedButBodyRetained()
    {
        string content =
            "---\n" +
            "name: : : broken\n\tbad: [unclosed\n" +
            "---\n" +
            "Body content\n";

        CursorPlanDocument document = _parser.Parse(content);

        Assert.Contains("Body content", document.Body);
        Assert.NotEmpty(document.Warnings);
    }

    [Fact]
    public void DocumentWithoutFrontMatterIsAllBody()
    {
        CursorPlanDocument document = _parser.Parse("# Just markdown\nNo front matter\n");

        Assert.False(document.HasFrontMatter);
        Assert.Contains("Just markdown", document.Body);
        Assert.Null(document.Name);
    }
}
