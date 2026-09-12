using System.Globalization;
using HarnessSpy.Core.Sessions.Plans;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace HarnessSpy.Core.Sessions.Cursor;

public sealed record CursorPlanTodo(string Id, string Content, string? Status);

public sealed record CursorPlanDocument(
    string? Name,
    string? Overview,
    IReadOnlyList<CursorPlanTodo> Todos,
    string Body,
    bool? IsProject,
    string StructuredKey,
    bool HasFrontMatter,
    IReadOnlyList<string> Warnings);

// Parses a Cursor `*.plan.md` document (YAML front matter plus markdown body)
// and computes a normalized structured key. The same key can be computed from a
// transcript `CreatePlan` tool input, so a plan file can be correlated to the
// session that created it even though the file itself carries no session id.
public sealed class CursorPlanDocumentParser
{
    private readonly IDeserializer _deserializer =
        new DeserializerBuilder().Build();

    public CursorPlanDocument Parse(string rawContent)
    {
        ArgumentNullException.ThrowIfNull(rawContent);

        string normalized = rawContent
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (normalized.Length > 0 && normalized[0] == '\uFEFF')
        {
            normalized = normalized[1..];
        }

        List<string> warnings = [];
        if (!TrySplitFrontMatter(
                normalized,
                out string frontMatter,
                out string body))
        {
            // No front matter: the whole document is the body. Still usable as a
            // plan, but there is no structured key to correlate with CreatePlan.
            string keyless = ComputeStructuredKey(null, null, []);
            return new CursorPlanDocument(
                Name: null,
                Overview: null,
                Todos: [],
                Body: body,
                IsProject: null,
                StructuredKey: keyless,
                HasFrontMatter: false,
                Warnings: warnings);
        }

        string? name = null;
        string? overview = null;
        bool? isProject = null;
        List<CursorPlanTodo> todos = [];

        try
        {
            Dictionary<object, object?> values =
                _deserializer.Deserialize<Dictionary<object, object?>>(frontMatter) ??
                new Dictionary<object, object?>();
            name = Scalar(values, "name");
            overview = Scalar(values, "overview");
            isProject = Boolean(values, "isProject");
            todos = ReadTodos(values);
        }
        catch (Exception exception) when (
            exception is YamlException or InvalidOperationException)
        {
            warnings.Add(
                "Could not parse Cursor plan front matter; the body was kept as " +
                $"raw markdown: {exception.Message}");
        }

        string structuredKey = ComputeStructuredKey(name, overview, todos);
        return new CursorPlanDocument(
            name,
            overview,
            todos,
            body,
            isProject,
            structuredKey,
            HasFrontMatter: true,
            warnings);
    }

    // Computes the correlation key from CreatePlan components. Todo status is
    // ignored because the CreatePlan input never carries one; only the stable
    // id/content pairs, plus name and overview, participate.
    public string ComputeStructuredKey(
        string? name,
        string? overview,
        IReadOnlyList<CursorPlanTodo> todos)
    {
        List<string> parts =
        [
            "name:" + NormalizeLine(name),
            "overview:" + NormalizeLine(overview)
        ];
        foreach (CursorPlanTodo todo in todos)
        {
            parts.Add($"todo:{NormalizeLine(todo.Id)}={NormalizeLine(todo.Content)}");
        }

        return string.Join("\n", parts);
    }

    private static bool TrySplitFrontMatter(
        string content,
        out string frontMatter,
        out string body)
    {
        frontMatter = string.Empty;
        body = content;
        if (!content.StartsWith("---\n", StringComparison.Ordinal))
        {
            return false;
        }

        int closing = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (closing < 0)
        {
            return false;
        }

        frontMatter = content[4..(closing + 1)];
        int bodyStart = closing + 4;

        // Skip the newline that terminates the closing delimiter line.
        if (bodyStart < content.Length && content[bodyStart] == '\n')
        {
            bodyStart++;
        }

        body = bodyStart <= content.Length
            ? content[bodyStart..]
            : string.Empty;
        return true;
    }

    private List<CursorPlanTodo> ReadTodos(IReadOnlyDictionary<object, object?> values)
    {
        List<CursorPlanTodo> todos = [];
        if (!TryGet(values, "todos", out object? raw) ||
            raw is not System.Collections.IEnumerable sequence ||
            raw is string)
        {
            return todos;
        }

        foreach (object? item in sequence)
        {
            switch (item)
            {
                case IReadOnlyDictionary<object, object?> readOnlyEntry:
                    todos.Add(ReadTodo(readOnlyEntry));
                    break;
                case IDictionary<object, object?> entry:
                    todos.Add(ReadTodo(new Dictionary<object, object?>(entry)));
                    break;
                case string text:
                    todos.Add(new CursorPlanTodo(string.Empty, text, null));
                    break;
            }
        }

        return todos;
    }

    private static CursorPlanTodo ReadTodo(IReadOnlyDictionary<object, object?> entry) =>
        new(
            Scalar(entry, "id") ?? string.Empty,
            Scalar(entry, "content") ?? string.Empty,
            Scalar(entry, "status"));

    private static string NormalizeLine(string? value) =>
        value is null
            ? string.Empty
            : value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();

    private static bool TryGet(
        IReadOnlyDictionary<object, object?> values,
        string key,
        out object? value)
    {
        foreach ((object candidate, object? candidateValue) in values)
        {
            string text = Convert.ToString(candidate, CultureInfo.InvariantCulture) ??
                string.Empty;
            if (text.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = candidateValue;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string? Scalar(
        IReadOnlyDictionary<object, object?> values,
        string key)
    {
        if (!TryGet(values, key, out object? value) || value is null)
        {
            return null;
        }

        if (value is string text)
        {
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        if (value is System.Collections.IEnumerable and not string)
        {
            return null;
        }

        string? converted = value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value.ToString();
        return string.IsNullOrWhiteSpace(converted) ? null : converted;
    }

    private static bool? Boolean(
        IReadOnlyDictionary<object, object?> values,
        string key)
    {
        string? text = Scalar(values, key);
        if (text is null)
        {
            return null;
        }

        return bool.TryParse(text, out bool parsed) ? parsed : null;
    }
}
