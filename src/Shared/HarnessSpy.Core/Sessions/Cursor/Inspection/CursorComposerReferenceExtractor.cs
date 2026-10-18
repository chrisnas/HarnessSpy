using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public sealed class CursorComposerReferenceExtractor
{
    private readonly CursorJsonReader _json = new();

    public IReadOnlyList<CursorLogicalReference> Extract(
        CursorRecordClassification record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Dictionary<string, CursorLogicalReference> references =
            new(StringComparer.Ordinal);

        Add(
            references,
            CursorRelationKind.DeclaresComposer,
            record.ComposerId,
            "The storage key or payload declares this composer.",
            record.ComposerId);
        if (record.Kind == CursorRecordKind.Bubble)
        {
            Add(
                references,
                CursorRelationKind.BelongsToComposer,
                record.ComposerId,
                "The bubble storage key identifies its owning composer.");
        }

        if (string.IsNullOrWhiteSpace(record.RawJson) ||
            !_json.TryParse(
                record.RawJson,
                out JsonDocument? document,
                out _) ||
            document is null)
        {
            return references.Values.ToArray();
        }

        using (document)
        {
            Visit(
                document.RootElement,
                references,
                record.ComposerId,
                record.Kind != CursorRecordKind.Bubble,
                depth: 0);
        }

        return references.Values.ToArray();
    }

    private void Visit(
        JsonElement element,
        Dictionary<string, CursorLogicalReference> references,
        string? composerContext,
        bool includeBubbleReferences,
        int depth)
    {
        if (depth > 12)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                Visit(
                    item,
                    references,
                    composerContext,
                    includeBubbleReferences,
                    depth + 1);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? declaredComposer = _json.String(
            element,
            "composerId",
            "composer_id");
        Add(
            references,
            CursorRelationKind.DeclaresComposer,
            declaredComposer,
            "The JSON payload explicitly declares a composer id.",
            declaredComposer);
        string? currentComposer = declaredComposer ?? composerContext;
        if (includeBubbleReferences)
        {
            Add(
                references,
                CursorRelationKind.ConversationBubble,
                _json.String(element, "bubbleId", "bubble_id"),
                "A conversation header references this bubble.",
                currentComposer);
        }

        if (_json.TryGetProperty(
                element,
                "subagentInfo",
                out JsonElement subagentInfo) &&
            subagentInfo.ValueKind == JsonValueKind.Object)
        {
            Add(
                references,
                CursorRelationKind.ParentComposer,
                _json.String(
                    subagentInfo,
                    "parentComposerId",
                    "parent_composer_id"),
                "subagentInfo explicitly references the parent composer.");
        }

        Add(
            references,
            CursorRelationKind.SelectedComposer,
            _json.String(
                element,
                "selectedComposerId",
                "selected_composer_id"),
            "Cursor marks this composer as selected.");
        CollectSelectedComposerIds(element, references);
        CollectWorkspace(element, references);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            Visit(
                property.Value,
                references,
                currentComposer,
                includeBubbleReferences,
                depth + 1);
        }
    }

    private void CollectSelectedComposerIds(
        JsonElement element,
        Dictionary<string, CursorLogicalReference> references)
    {
        foreach (string name in new[]
                 {
                     "selectedComposerIds", "selected_composer_ids"
                 })
        {
            if (!_json.TryGetProperty(
                    element,
                    name,
                    out JsonElement selected))
            {
                continue;
            }

            foreach (string value in Strings(selected, depth: 0))
            {
                Add(
                    references,
                    CursorRelationKind.SelectedComposer,
                    value,
                    "Cursor includes this composer in its selected composer set.");
            }
        }
    }

    private void CollectWorkspace(
        JsonElement element,
        Dictionary<string, CursorLogicalReference> references)
    {
        if (_json.TryGetProperty(
                element,
                "workspaceIdentifier",
                out JsonElement identifier) &&
            identifier.ValueKind == JsonValueKind.Object)
        {
            Add(
                references,
                CursorRelationKind.Workspace,
                _json.String(identifier, "id", "workspaceId"),
                "workspaceIdentifier explicitly names the workspace.");
            if (_json.TryGetProperty(
                    identifier,
                    "uri",
                    out JsonElement uri))
            {
                Add(
                    references,
                    CursorRelationKind.Workspace,
                    uri.ValueKind == JsonValueKind.String
                        ? uri.GetString()
                        : _json.String(uri, "fsPath", "path", "external"),
                    "workspaceIdentifier carries the workspace URI.");
            }
        }

        Add(
            references,
            CursorRelationKind.Workspace,
            _json.String(element, "workspaceId", "workspace_id"),
            "The payload explicitly names the workspace id.");
        Add(
            references,
            CursorRelationKind.Workspace,
            _json.String(
                element,
                "workspacePath",
                "workspace_path",
                "cwd"),
            "The payload explicitly names the workspace path.");
    }

    private IEnumerable<string> Strings(JsonElement element, int depth)
    {
        if (depth > 6)
        {
            yield break;
        }

        if (element.ValueKind == JsonValueKind.String &&
            element.GetString() is string text &&
            !string.IsNullOrWhiteSpace(text))
        {
            yield return text;
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                foreach (string value in Strings(item, depth + 1))
                {
                    yield return value;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                foreach (string value in Strings(
                    property.Value,
                    depth + 1))
                {
                    yield return value;
                }
            }
        }
    }

    private void Add(
        Dictionary<string, CursorLogicalReference> references,
        CursorRelationKind kind,
        string? targetId,
        string explanation,
        string? sourceContextId = null)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return;
        }

        string key = $"{kind}:{sourceContextId}:{targetId}";
        references.TryAdd(
            key,
            new CursorLogicalReference(
                kind,
                targetId,
                InferenceEvidence.Observed,
                explanation,
                sourceContextId));
    }
}
