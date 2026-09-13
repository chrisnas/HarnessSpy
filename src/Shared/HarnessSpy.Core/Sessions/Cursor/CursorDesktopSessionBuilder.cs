using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Cursor;

internal sealed record CursorWorkspaceManifest(
    string WorkspaceId,
    string DirectoryPath,
    string DatabasePath,
    WorkspaceContext Workspace,
    SessionSourceProvenance? Provenance);

internal sealed record CursorDesktopDatabase(
    string Path,
    bool IsGlobal,
    CursorWorkspaceManifest? Manifest);

internal sealed record CursorComposerRelationship(
    string ParentComposerId,
    string? SubagentTypeName,
    int? SideChatSeedTurnCount)
{
    public bool IsSideChat => string.Equals(
        SubagentTypeName,
        "side-chat",
        StringComparison.OrdinalIgnoreCase);
}

internal sealed record CursorDesktopComposerSnapshot
{
    public required string ComposerId { get; init; }

    public string? Title { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public DateTimeOffset? LastUpdatedAtUtc { get; init; }

    public string? Model { get; init; }

    public string? Mode { get; init; }

    public WorkspaceContext Workspace { get; init; } = WorkspaceContext.Unknown;

    public int WorkspacePriority { get; init; }

    public int DataPriority { get; init; }

    public bool? IsArchived { get; init; }

    // True when the composer header lists conversation messages. This lets the
    // headers-only skeleton pass tell real conversations from empty "new chat"
    // shells without reading the message blobs.
    public bool HasConversationHeaders { get; init; }

    public CursorComposerRelationship? Relationship { get; init; }

    public IReadOnlyList<SessionTurn> Turns { get; init; } = [];

    public IReadOnlyList<SessionFileBinding> Files { get; init; } = [];

    public IReadOnlyList<SessionSourceProvenance> Sources { get; init; } = [];

    public IReadOnlyDictionary<string, string?> Metadata { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);
}

internal sealed class CursorDesktopSessionBuilder
{
    private readonly string _composerId;
    private readonly CursorJsonReader _json;
    private readonly Dictionary<string, SessionFileBinding> _files =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SessionSourceProvenance> _sources =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SessionTurn> _turns =
        new(StringComparer.Ordinal);
    private readonly List<SessionTurn> _childTurns = [];
    private readonly Dictionary<string, string?> _metadata =
        new(StringComparer.Ordinal);

    private string? _title;
    private int _titlePriority;
    private DateTimeOffset? _createdAtUtc;
    private DateTimeOffset? _lastUpdatedAtUtc;
    private string? _model;
    private string? _mode;
    private WorkspaceContext _workspace = WorkspaceContext.Unknown;
    private int _workspacePriority;
    private bool _selected;
    private bool? _isArchived;
    private bool _hasConversationHeaders;
    private CursorComposerRelationship? _relationship;
    private int _backgroundChildCount;

    public CursorDesktopSessionBuilder(
        string composerId,
        CursorJsonReader json)
    {
        _composerId = composerId;
        _json = json ?? throw new ArgumentNullException(nameof(json));
    }

    public string ComposerId => _composerId;

    public CursorComposerRelationship? Relationship => _relationship;

    public bool IsBackgroundChild =>
        _relationship is not null && !_relationship.IsSideChat;

    // A composer is worth showing when it has any turns (from its own messages
    // or attached children) or its header advertises conversation messages.
    // Empty shells with none of these are hidden as noise.
    public bool HasContent =>
        _turns.Count > 0 ||
        _childTurns.Count > 0 ||
        _hasConversationHeaders;

    public void Add(CursorDesktopComposerSnapshot snapshot)
    {
        if (!string.Equals(
            snapshot.ComposerId,
            _composerId,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A Cursor composer snapshot cannot change session identity.");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Title) &&
            (string.IsNullOrWhiteSpace(_title) ||
                snapshot.DataPriority >= _titlePriority))
        {
            _title = snapshot.Title;
            _titlePriority = snapshot.DataPriority;
        }

        _createdAtUtc = Minimum(_createdAtUtc, snapshot.CreatedAtUtc);
        _lastUpdatedAtUtc = Maximum(
            _lastUpdatedAtUtc,
            snapshot.LastUpdatedAtUtc);
        _model = snapshot.Model ?? _model;
        _mode = snapshot.Mode ?? _mode;
        _isArchived = snapshot.IsArchived ?? _isArchived;
        _hasConversationHeaders |= snapshot.HasConversationHeaders;
        _relationship = PreferredRelationship(
            _relationship,
            snapshot.Relationship);

        if (snapshot.WorkspacePriority > _workspacePriority ||
            _workspace.Kind == WorkspaceContextKind.Unknown)
        {
            _workspace = snapshot.Workspace;
            _workspacePriority = snapshot.WorkspacePriority;
        }

        foreach (SessionFileBinding file in snapshot.Files)
        {
            _files[file.Path] = file;
        }

        foreach (SessionSourceProvenance source in snapshot.Sources)
        {
            string key =
                $"{source.Path}|{source.DatabaseKey}|{source.LineNumber}|" +
                $"{source.RecordId}|{source.Format}";
            _sources[key] = source;
        }

        foreach ((string key, string? value) in snapshot.Metadata)
        {
            if (!_metadata.ContainsKey(key) || !string.IsNullOrWhiteSpace(value))
            {
                _metadata[key] = value;
            }
        }

        MergeTurns(snapshot.Turns);
    }

    public void MarkSelected()
    {
        _selected = true;
        _metadata["cursor.selected"] = "true";
    }

    public void MarkRelationship(CursorComposerRelationship relationship)
    {
        _relationship = PreferredRelationship(_relationship, relationship);
        CaptureRelationshipMetadata();
    }

    public void AttachBackgroundChild(CursorDesktopSessionBuilder child)
    {
        CursorComposerRelationship? relationship = child.Relationship;
        string agentType = relationship?.SubagentTypeName ?? "cursor-subagent";

        foreach (SessionFileBinding file in child._files.Values)
        {
            _files.TryAdd(
                file.Path,
                file with
                {
                    Role = TranscriptFileRole.Subagent,
                    AgentId = child.ComposerId,
                    ParentSessionId = _composerId
                });
        }

        foreach ((string key, SessionSourceProvenance source) in child._sources)
        {
            _sources.TryAdd(key, source);
        }

        foreach (SessionTurn childTurn in child.OrderedTurnsForAttachment())
        {
            SessionEventRecord[] events = childTurn.Events
                .Select(item => item with
                {
                    AgentId = item.AgentId ?? child.ComposerId,
                    AgentType = item.AgentType ?? agentType
                })
                .ToArray();
            _childTurns.Add(childTurn with { Events = events });
        }

        _metadata[$"cursor.child.{MetadataKey(child.ComposerId)}.type"] =
            relationship?.SubagentTypeName;
        _metadata[$"cursor.child.{MetadataKey(child.ComposerId)}.parentComposerId"] =
            _composerId;
        if (child._selected)
        {
            _metadata[$"cursor.child.{MetadataKey(child.ComposerId)}.selected"] =
                "true";
        }

        _hasConversationHeaders |= child.HasContent;
        _backgroundChildCount++;
        _metadata["cursor.backgroundChildCount"] =
            _backgroundChildCount.ToString();
    }

    public SessionCatalogEntry Build()
    {
        CaptureRelationshipMetadata();

        List<SessionTurn> turns = OrderedMainTurns().ToList();
        if (_relationship?.IsSideChat == true &&
            _relationship.SideChatSeedTurnCount is int seedCount &&
            seedCount > 0)
        {
            int remove = Math.Min(seedCount, turns.Count);
            turns.RemoveRange(0, remove);
            _metadata["cursor.sideChatSkippedInheritedTurns"] = remove.ToString();
        }

        turns.AddRange(_childTurns.OrderBy(
            turn => turn.StartedAtUtc ?? DateTimeOffset.MaxValue));
        for (int index = 0; index < turns.Count; index++)
        {
            turns[index] = turns[index] with { Number = index + 1 };
        }

        string? firstPrompt = turns
            .Select(turn => turn.Prompt)
            .FirstOrDefault(prompt => !string.IsNullOrWhiteSpace(prompt));
        string title = !string.IsNullOrWhiteSpace(_title)
            ? _json.Preview(_title, 160)
            : _json.Preview(firstPrompt, 160);
        if (title.Length == 0)
        {
            title = _composerId;
        }

        DateTimeOffset? turnStart = turns
            .Select(turn => turn.StartedAtUtc)
            .Where(value => value is not null)
            .Min();
        DateTimeOffset? turnEnd = turns
            .Select(turn => turn.EndedAtUtc ?? turn.StartedAtUtc)
            .Where(value => value is not null)
            .Max();
        DateTimeOffset? fileWrite = _files.Count == 0
            ? null
            : _files.Values.Max(file => file.LastWriteTimeUtc);

        _metadata["cursor.archived"] = _isArchived?.ToString();
        _metadata["cursor.source"] = "desktop-sqlite";

        return new SessionCatalogEntry
        {
            CatalogSessionId = $"cursor:{_composerId}",
            NativeSessionId = _composerId,
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Workspace = _workspace,
            Title = title,
            StartedAtUtc = Minimum(_createdAtUtc, turnStart),
            LastActivityAtUtc = Maximum(
                _lastUpdatedAtUtc,
                Maximum(turnEnd, fileWrite)),
            Model = _model,
            Mode = _mode,
            LifecycleState = SessionLifecycleState.Closed,
            LifecycleEvidence = InferenceEvidence.Unavailable,
            IsSelectedInHarness = _selected,
            Files = _files.Values
                .OrderBy(file => file.Role)
                .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Turns = turns,
            Metadata = new Dictionary<string, string?>(
                _metadata,
                StringComparer.Ordinal),
            Sources = _sources.Values.ToArray()
        };
    }

    private void MergeTurns(IReadOnlyList<SessionTurn> candidates)
    {
        foreach (SessionTurn candidate in candidates)
        {
            if (!_turns.TryGetValue(candidate.Id, out SessionTurn? current))
            {
                _turns[candidate.Id] = candidate;
                continue;
            }

            Dictionary<string, SessionEventRecord> events = current.Events
                .ToDictionary(item => item.Id, StringComparer.Ordinal);
            foreach (SessionEventRecord item in candidate.Events)
            {
                events.TryAdd(item.Id, item);
            }

            _turns[candidate.Id] = current with
            {
                Prompt = string.IsNullOrWhiteSpace(current.Prompt)
                    ? candidate.Prompt
                    : current.Prompt,
                StartedAtUtc = Minimum(
                    current.StartedAtUtc,
                    candidate.StartedAtUtc),
                EndedAtUtc = Maximum(
                    current.EndedAtUtc,
                    candidate.EndedAtUtc),
                Events = events.Values
                    .OrderBy(item => item.TimestampUtc ?? DateTimeOffset.MinValue)
                    .ThenBy(item => item.Order)
                    .ToArray()
            };
        }
    }

    private IReadOnlyList<SessionTurn> OrderedMainTurns() =>
        _turns.Values
            .OrderBy(turn => turn.Number)
            .ThenBy(turn => turn.StartedAtUtc ?? DateTimeOffset.MaxValue)
            .ToArray();

    private IReadOnlyList<SessionTurn> OrderedTurnsForAttachment() =>
        OrderedMainTurns()
            .Concat(_childTurns)
            .OrderBy(turn => turn.StartedAtUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(turn => turn.Number)
            .ToArray();

    private void CaptureRelationshipMetadata()
    {
        if (_relationship is null)
        {
            return;
        }

        _metadata["cursor.parentComposerId"] =
            _relationship.ParentComposerId;
        _metadata["cursor.subagentType"] =
            _relationship.SubagentTypeName;
        _metadata["cursor.sideChatSeedTurnCount"] =
            _relationship.SideChatSeedTurnCount?.ToString();
    }

    private string MetadataKey(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int length = 0;
        foreach (char character in value)
        {
            buffer[length++] = char.IsLetterOrDigit(character) ||
                character is '-' or '_'
                    ? character
                    : '_';
        }

        return new string(buffer[..length]);
    }

    private CursorComposerRelationship? PreferredRelationship(
        CursorComposerRelationship? current,
        CursorComposerRelationship? candidate)
    {
        if (candidate is null)
        {
            return current;
        }

        if (current is null ||
            candidate.IsSideChat && !current.IsSideChat ||
            candidate.SideChatSeedTurnCount is not null &&
            current.SideChatSeedTurnCount is null)
        {
            return candidate;
        }

        return current;
    }

    private DateTimeOffset? Minimum(
        DateTimeOffset? first,
        DateTimeOffset? second) =>
        first is null ? second :
        second is null ? first :
        first <= second ? first : second;

    private DateTimeOffset? Maximum(
        DateTimeOffset? first,
        DateTimeOffset? second) =>
        first is null ? second :
        second is null ? first :
        first >= second ? first : second;
}
