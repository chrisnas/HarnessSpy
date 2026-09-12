using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Copilot;

internal sealed class CopilotEnvelopeContext
{
    public required string Type { get; init; }

    public required string EventIdBase { get; init; }

    public required JsonElement Data { get; init; }

    public required SessionSourceProvenance Provenance { get; init; }

    public DateTimeOffset? TimestampUtc { get; init; }

    public string? ParentId { get; init; }

    public string? AgentId { get; init; }

    public int Order { get; init; }

    public bool IsEphemeral { get; init; }
}

internal sealed class CopilotEventSpec
{
    public string? IdSuffix { get; init; }

    public required string NativeName { get; init; }

    public ObservationRole Role { get; init; } = ObservationRole.Generic;

    public CanonicalEventKind EventKind { get; init; } =
        CanonicalEventKind.ProviderSpecific;

    public CanonicalToolKind ToolKind { get; init; } = CanonicalToolKind.Unknown;

    public ObservationDirection Direction { get; init; } =
        ObservationDirection.None;

    public ObservationTone Tone { get; init; } = ObservationTone.Normal;

    public InferenceEvidence Evidence { get; init; } = InferenceEvidence.Observed;

    public string? TurnId { get; init; }

    public string? AssistantStepId { get; init; }

    public string? ParallelGroupId { get; init; }

    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    public string? McpServerName { get; init; }

    public string? McpToolName { get; init; }

    public string? PromptText { get; init; }

    public string? Text { get; init; }

    public string? Model { get; init; }

    public string? Mode { get; init; }

    public string? Status { get; init; }

    public string? AgentId { get; init; }

    public string? AgentType { get; init; }

    public string? Task { get; init; }

    public double? DurationMs { get; init; }

    public bool IsFailure { get; init; }

    public bool IsAborted { get; init; }

    public bool IsParallelCandidate { get; init; }

    public bool ExcludeFromSummary { get; init; }

    public IReadOnlyList<string> TargetPaths { get; init; } = [];

    public IReadOnlyList<UsageMeasurement> UsageMeasurements { get; init; } = [];

    public SkillEvidence? Skill { get; init; }
}

internal sealed class CopilotToolCorrelation
{
    public string? TurnId { get; set; }

    public string? ToolName { get; set; }

    public CopilotMcpIdentity? Mcp { get; set; }

    public string? AssistantStepId { get; set; }

    public string? ParallelGroupId { get; set; }

    public string? Task { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }

}

internal sealed class CopilotTurnBuilder
{
    private readonly List<SessionEventRecord> _events = [];

    public CopilotTurnBuilder(
        string id,
        int creationOrder,
        InferenceEvidence evidence)
    {
        Id = id;
        CreationOrder = creationOrder;
        Evidence = evidence;
    }

    public string Id { get; }

    public int CreationOrder { get; }

    public string Prompt { get; private set; } = string.Empty;

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? EndedAtUtc { get; private set; }

    public DateTimeOffset? FirstEventAtUtc { get; private set; }

    public int FirstOrder { get; private set; } = int.MaxValue;

    public InferenceEvidence Evidence { get; private set; }

    public void ObservePrompt(string? prompt, DateTimeOffset? timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(Prompt) && !string.IsNullOrWhiteSpace(prompt))
        {
            Prompt = prompt;
        }

        StartedAtUtc = Minimum(StartedAtUtc, timestampUtc);
    }

    public void ObserveStart(DateTimeOffset? timestampUtc)
    {
        StartedAtUtc = Minimum(StartedAtUtc, timestampUtc);
    }

    public void ObserveEnd(DateTimeOffset? timestampUtc)
    {
        EndedAtUtc = Maximum(EndedAtUtc, timestampUtc);
    }

    public void Add(SessionEventRecord record)
    {
        _events.Add(record);
        FirstOrder = Math.Min(FirstOrder, record.Order);
        FirstEventAtUtc = Minimum(FirstEventAtUtc, record.TimestampUtc);
    }

    public void MergeFrom(CopilotTurnBuilder source, string targetTurnId)
    {
        if (string.IsNullOrWhiteSpace(Prompt))
        {
            Prompt = source.Prompt;
        }

        StartedAtUtc = Minimum(StartedAtUtc, source.StartedAtUtc);
        EndedAtUtc = Maximum(EndedAtUtc, source.EndedAtUtc);
        FirstEventAtUtc = Minimum(FirstEventAtUtc, source.FirstEventAtUtc);
        FirstOrder = Math.Min(FirstOrder, source.FirstOrder);
        Evidence = Stronger(Evidence, source.Evidence);
        _events.AddRange(source._events.Select(record =>
            record.TurnId?.Equals(source.Id, StringComparison.Ordinal) == true
                ? record with { TurnId = targetTurnId }
                : record));
    }

    public SessionTurn Build(int number)
    {
        SessionEventRecord[] orderedEvents = _events
            .OrderBy(static record => record.Order)
            .ToArray();

        return new SessionTurn(
            Id,
            number,
            Prompt,
            StartedAtUtc ?? FirstEventAtUtc,
            EndedAtUtc,
            Evidence,
            orderedEvents);
    }

    private static DateTimeOffset? Minimum(
        DateTimeOffset? first,
        DateTimeOffset? second) =>
        first is null ? second :
        second is null ? first :
        first < second ? first : second;

    private static DateTimeOffset? Maximum(
        DateTimeOffset? first,
        DateTimeOffset? second) =>
        first is null ? second :
        second is null ? first :
        first > second ? first : second;

    private static InferenceEvidence Stronger(
        InferenceEvidence first,
        InferenceEvidence second)
    {
        int Rank(InferenceEvidence evidence) => evidence switch
        {
            InferenceEvidence.Observed => 6,
            InferenceEvidence.Corroborated => 5,
            InferenceEvidence.Derived => 4,
            InferenceEvidence.Heuristic => 3,
            InferenceEvidence.Opaque => 2,
            InferenceEvidence.Ambiguous => 1,
            _ => 0
        };

        return Rank(second) > Rank(first) ? second : first;
    }
}

internal sealed class CopilotSessionProjector
{
    private readonly CopilotSessionFileSource _source;
    private readonly CopilotWorkspaceSnapshot _workspace;
    private readonly SessionFileBinding _eventsBinding;
    private readonly WorkspaceNormalizer _workspaceNormalizer;
    private readonly CopilotJsonValueReader _json;
    private readonly CopilotTimestampParser _timestampParser;
    private readonly CopilotToolSemantics _toolSemantics;
    private readonly CopilotUsageExtractor _usageExtractor;
    private readonly CopilotMetadataValueFormatter _metadataFormatter;
    private readonly Dictionary<string, CopilotTurnBuilder> _turns =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _interactionTurns =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activeTurnsByAgent =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastUserTurnsByAgent =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _agentParentTurns =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CopilotToolCorrelation> _tools =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _eventTypeCounts =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _eventIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _currentAggregateMetadataKeys =
        new(StringComparer.Ordinal);
    private readonly List<SessionSourceProvenance> _sources = [];
    private readonly List<SessionFileBinding> _files = [];
    private readonly List<SessionEventRecord> _pendingMainEvents = [];
    private readonly Dictionary<string, string?> _metadata =
        new(StringComparer.Ordinal);
    private readonly CopilotWarningCollector _warnings = new();

    private int _turnCreationSequence;
    private int _checkpointSequence;
    private int _contextSequence;
    private int _resumeCount;
    private string? _contractVersion;
    private string? _currentModel;
    private string? _currentMode;
    private string? _eventCwd;
    private string? _eventGitRoot;
    private string? _eventRepository;
    private string? _eventBranch;
    private string? _firstPrompt;
    private DateTimeOffset? _startedAtUtc;
    private DateTimeOffset? _lastActivityAtUtc;
    private int _lastOpenOrder = -1;
    private int _lastShutdownOrder = -1;
    private bool _sawLifecycleRecord;

    public CopilotSessionProjector(
        CopilotSessionFileSource source,
        CopilotWorkspaceSnapshot workspace,
        SessionFileBinding eventsBinding,
        WorkspaceNormalizer workspaceNormalizer)
    {
        _source = source;
        _workspace = workspace;
        _eventsBinding = eventsBinding;
        _workspaceNormalizer = workspaceNormalizer;
        _json = new CopilotJsonValueReader();
        _timestampParser = new CopilotTimestampParser();
        _toolSemantics = new CopilotToolSemantics(_json);
        _usageExtractor = new CopilotUsageExtractor(_json);
        _metadataFormatter = new CopilotMetadataValueFormatter();

        _files.Add(eventsBinding);
        if (workspace.File is not null)
        {
            _files.Add(workspace.File);
        }

        if (workspace.Provenance is not null)
        {
            _sources.Add(workspace.Provenance);
        }

        foreach ((string key, string? value) in workspace.Metadata)
        {
            if (value is not null)
            {
                _metadata[key] = value;
            }
        }

        _startedAtUtc = workspace.CreatedAtUtc;
        _lastActivityAtUtc = workspace.UpdatedAtUtc;
        if (workspace.Id is not null &&
            !workspace.Id.Equals(source.NativeSessionId, StringComparison.Ordinal))
        {
            AddWarning(
                $"Copilot workspace metadata id '{workspace.Id}' does not match " +
                $"the source id '{source.NativeSessionId}'.");
        }
    }

    public IReadOnlyList<string> Warnings => _warnings.Snapshot();

    public bool IsComplete { get; private set; } = true;

    public void AddUnparsedSource(
        string rawContent,
        int lineNumber,
        long byteOffset)
    {
        _sources.Add(new SessionSourceProvenance(
            SessionSourceKind.CopilotEventsJsonl,
            _eventsBinding.Path,
            "jsonl",
            rawContent,
            lineNumber,
            byteOffset,
            ContractVersion: _contractVersion));
    }

    public void Accept(
        JsonElement root,
        string rawContent,
        int lineNumber,
        long byteOffset)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            AddUnparsedSource(rawContent, lineNumber, byteOffset);
            AddWarning(
                $"Line {lineNumber} in Copilot event log '{_eventsBinding.Path}' " +
                "is not a JSON object.");
            return;
        }

        string? nativeType = _json.String(root, "type");
        if (nativeType is null)
        {
            nativeType = "unknown";
            AddWarning(
                $"Line {lineNumber} in Copilot event log '{_eventsBinding.Path}' " +
                "has no event type and was preserved as an unknown record.");
        }

        bool hasObjectData =
            _json.TryGetProperty(root, out JsonElement dataValue, "data") &&
            dataValue.ValueKind == JsonValueKind.Object;
        JsonElement data = hasObjectData ? dataValue : root;
        if (!hasObjectData)
        {
            AddWarning(
                $"Line {lineNumber} in Copilot event log '{_eventsBinding.Path}' " +
                "has no object-valued data envelope; top-level fields were retained.");
        }
        string? nativeRecordId = _json.String(root, "id");
        string eventIdBase = nativeRecordId ??
            $"{_source.NativeSessionId}:line:{lineNumber}";
        string? parentId = _json.String(root, "parentId");
        string? agentId = _json.String(root, "agentId") ??
            _json.String(data, "agentId");
        DateTimeOffset? timestampUtc = ReadTimestamp(root, data);
        bool isEphemeral = _json.Boolean(root, "ephemeral") == true;

        if (nativeType is "session.start" or "session.resume")
        {
            _contractVersion ??= _json.String(data, "version");
        }

        SessionSourceProvenance provenance = new(
            SessionSourceKind.CopilotEventsJsonl,
            _eventsBinding.Path,
            "jsonl",
            rawContent,
            lineNumber,
            byteOffset,
            nativeRecordId,
            parentId,
            ContractVersion: _contractVersion);
        _sources.Add(provenance);

        CopilotEnvelopeContext context = new()
        {
            Type = nativeType,
            EventIdBase = eventIdBase,
            Data = data,
            Provenance = provenance,
            TimestampUtc = timestampUtc,
            ParentId = parentId,
            AgentId = agentId,
            Order = lineNumber - 1,
            IsEphemeral = isEphemeral
        };

        _lastActivityAtUtc = Maximum(_lastActivityAtUtc, timestampUtc);
        _startedAtUtc = Minimum(_startedAtUtc, timestampUtc);
        _eventTypeCounts[nativeType] =
            _eventTypeCounts.GetValueOrDefault(nativeType) + 1;

        if (HandleSessionRecord(context))
        {
            return;
        }

        switch (nativeType)
        {
            case "user.message":
                HandleUserMessage(context);
                break;

            case "system.message":
                HandleSystemMessage(context);
                break;

            case "assistant.turn_start":
                HandleTurnBoundary(context, isStart: true);
                break;

            case "assistant.turn_end":
                HandleTurnBoundary(context, isStart: false);
                break;

            case "assistant.reasoning":
                HandleAssistantReasoning(context);
                break;

            case "assistant.message":
                HandleAssistantMessage(context);
                break;

            case "tool.user_requested":
                HandleToolRequest(context);
                break;

            case "tool.execution_start":
                HandleToolExecution(context, isStart: true);
                break;

            case "tool.execution_complete":
                HandleToolExecution(context, isStart: false);
                break;

            case "permission.requested":
                HandlePermission(context, isRequested: true);
                break;

            case "permission.completed":
            case "permission.denied":
                HandlePermission(context, isRequested: false);
                break;

            case "session.error":
                HandleRuntimeError(context);
                break;

            default:
                if (nativeType.StartsWith("subagent.", StringComparison.Ordinal))
                {
                    HandleSubagent(context);
                }
                else if (nativeType.StartsWith("skill.", StringComparison.Ordinal))
                {
                    HandleSkill(context);
                }
                else if (nativeType.StartsWith("model.", StringComparison.Ordinal))
                {
                    HandleModelEvent(context);
                }
                else if (nativeType.Contains(
                    "compact",
                    StringComparison.OrdinalIgnoreCase))
                {
                    HandleCompaction(context);
                }
                break;
        }
    }

    public SessionCatalogEntry Build(
        int recordCount,
        int malformedLineCount)
    {
        _metadata["source.layout"] = _source.Layout == CopilotSessionLayout.Current
            ? "current"
            : "legacy";
        _metadata["source.events"] = _source.EventsPath;
        _metadata["source.workspace"] = _source.WorkspacePath;
        _metadata["recordCount"] = recordCount.ToString(CultureInfo.InvariantCulture);
        _metadata["malformedLineCount"] =
            malformedLineCount.ToString(CultureInfo.InvariantCulture);
        _metadata["contractVersion"] = _contractVersion;
        _metadata["copilot.model"] = _currentModel;
        _metadata["copilot.mode"] = _currentMode;
        _metadata["resumeCount"] = _resumeCount.ToString(CultureInfo.InvariantCulture);

        foreach ((string type, int count) in _eventTypeCounts)
        {
            _metadata[$"eventCount.{type}"] =
                count.ToString(CultureInfo.InvariantCulture);
        }

        SessionTurn[] turns = _turns.Values
            .Where(static turn =>
                !string.IsNullOrWhiteSpace(turn.Prompt) ||
                turn.FirstOrder != int.MaxValue)
            .OrderBy(static turn =>
                turn.StartedAtUtc ??
                turn.FirstEventAtUtc ??
                DateTimeOffset.MaxValue)
            .ThenBy(static turn => turn.FirstOrder)
            .ThenBy(static turn => turn.CreationOrder)
            .Select(static (turn, index) => turn.Build(index + 1))
            .ToArray();

        string? workspaceRoot =
            _workspace.GitRoot ??
            _eventGitRoot ??
            _workspace.Cwd ??
            _eventCwd;
        WorkspaceContext normalizedWorkspace =
            _workspaceNormalizer.FromPath(workspaceRoot);

        DateTimeOffset? lastActivity =
            Maximum(_lastActivityAtUtc, _workspace.UpdatedAtUtc);
        lastActivity ??= _eventsBinding.LastWriteTimeUtc;
        DateTimeOffset? startedAt =
            Minimum(_startedAtUtc, _workspace.CreatedAtUtc);

        bool isClosed = _lastShutdownOrder >= _lastOpenOrder &&
            _lastShutdownOrder >= 0;
        SessionLifecycleState lifecycleState = isClosed
            ? SessionLifecycleState.Closed
            : SessionLifecycleState.Open;
        InferenceEvidence lifecycleEvidence = _sawLifecycleRecord
            ? InferenceEvidence.Observed
            : recordCount > 0
                ? InferenceEvidence.Derived
                : InferenceEvidence.Unavailable;

        string title =
            _workspace.Name ??
            PreviewTitle(_firstPrompt) ??
            _source.NativeSessionId;

        SessionSourceProvenance[] sources = _sources
            .Select(source => source with
            {
                ContractVersion = source.ContractVersion ?? _contractVersion
            })
            .ToArray();

        return new SessionCatalogEntry
        {
            CatalogSessionId =
                $"{HookProvider.GitHubCopilot}:{HookSurface.CopilotCli}:" +
                _source.NativeSessionId,
            NativeSessionId = _source.NativeSessionId,
            Provider = HookProvider.GitHubCopilot,
            Surface = HookSurface.CopilotCli,
            Workspace = normalizedWorkspace,
            Title = title,
            StartedAtUtc = startedAt,
            LastActivityAtUtc = lastActivity,
            Model = _currentModel,
            Mode = _currentMode,
            LifecycleState = lifecycleState,
            LifecycleEvidence = lifecycleEvidence,
            Files = _files.ToArray(),
            Turns = turns,
            Metadata = new ReadOnlyDictionary<string, string?>(_metadata),
            Sources = sources
        };
    }

    private bool HandleSessionRecord(CopilotEnvelopeContext context)
    {
        JsonElement data = context.Data;
        switch (context.Type)
        {
            case "session.start":
            {
                _sawLifecycleRecord = true;
                _lastOpenOrder = context.Order;
                ClearCurrentShutdownAggregate();

                string? sessionId = _json.String(data, "sessionId");
                if (sessionId is not null &&
                    !sessionId.Equals(
                        _source.NativeSessionId,
                        StringComparison.Ordinal))
                {
                    AddWarning(
                        $"Copilot event session id '{sessionId}' does not match " +
                        $"the source id '{_source.NativeSessionId}' on line " +
                        $"{context.Provenance.LineNumber}.");
                }

                _contractVersion ??= _json.String(data, "version");
                _metadata["producer"] = _json.String(data, "producer");
                _metadata["copilotVersion"] = _json.String(data, "copilotVersion");
                _metadata["sessionId"] = sessionId;
                _startedAtUtc = Minimum(
                    _startedAtUtc,
                    ReadTimestamp(data, "startTime"));
                _currentModel =
                    _json.String(data, "selectedModel", "model") ??
                    _currentModel;
                _currentMode =
                    _json.String(data, "selectedMode", "sessionMode", "mode") ??
                    _currentMode;
                ApplyContext(data, context.Order);
                return true;
            }

            case "session.resume":
                _sawLifecycleRecord = true;
                _lastOpenOrder = context.Order;
                _resumeCount++;
                ClearCurrentShutdownAggregate();
                _currentModel =
                    _json.String(data, "selectedModel", "model") ??
                    _currentModel;
                _currentMode =
                    _json.String(data, "selectedMode", "sessionMode", "mode") ??
                    _currentMode;
                _metadata["lastResumeTime"] =
                    _json.String(data, "resumeTime") ??
                    context.TimestampUtc?.ToString("O", CultureInfo.InvariantCulture);
                ApplyContext(data, context.Order);
                return true;

            case "session.shutdown":
                _sawLifecycleRecord = true;
                _lastShutdownOrder = context.Order;
                _metadata["shutdown.type"] =
                    _json.String(data, "shutdownType", "reason");
                _metadata["shutdown.timestamp"] =
                    context.TimestampUtc?.ToString("O", CultureInfo.InvariantCulture);
                _currentModel =
                    _json.String(data, "currentModel", "model") ??
                    _currentModel;
                CaptureSessionUsage(
                    data,
                    context.EventIdBase,
                    "usage.final",
                    UsageBehavior.FinalSnapshot);
                return true;

            case "session.usage_checkpoint":
            {
                string prefix = $"usage.checkpoint.{_checkpointSequence++}";
                CaptureSessionUsage(
                    data,
                    context.EventIdBase,
                    prefix,
                    UsageBehavior.CumulativeSnapshot);
                return true;
            }

            case "session.model_change":
            case "model_change":
                _currentModel =
                    _json.String(data, "newModel", "model", "selectedModel") ??
                    _currentModel;
                _metadata["modelChange.cause"] = _json.String(data, "cause");
                _metadata["modelChange.source"] = _json.String(data, "source");
                return true;

            case "session.auto_mode_resolved":
            case "auto_mode_resolved":
                _currentModel =
                    _json.String(data, "chosenModel", "model") ??
                    _currentModel;
                _metadata["autoMode.routingMethod"] =
                    _json.String(data, "routingMethod");
                return true;

            case "session.mode_changed":
            case "session.mode_change":
                _currentMode =
                    _json.String(data, "newMode", "mode") ??
                    _currentMode;
                return true;

            case "session.context_changed":
            case "session.context_change":
                ApplyContext(data, context.Order);
                return true;

            case "session.permissions_changed":
                _metadata["permissions.allowAll"] =
                    _json.Boolean(data, "allowAllPermissions") is bool allowAll
                        ? _metadataFormatter.Format(allowAll)
                        : null;
                _metadata["permissions.mode"] =
                    _json.String(data, "allowAllPermissionMode");
                return true;

            default:
                return false;
        }
    }

    private void HandleUserMessage(CopilotEnvelopeContext context)
    {
        string? nativeTurnId = ReadTurnId(context.Data);
        string turnId = ResolveTurn(
            context.Data,
            context.AgentId,
            allowCreate: true,
            preferredSyntheticPrefix: "user")!;
        CopilotTurnBuilder turn = GetOrCreateTurn(
            turnId,
            nativeTurnId is null
                ? InferenceEvidence.Derived
                : InferenceEvidence.Observed);
        if (context.AgentId is null)
        {
            AttachPendingMainEvents(turn);
        }

        string? prompt = _json.Text(context.Data, "content", "prompt");
        string? mode = _json.String(context.Data, "agentMode", "mode");
        if (context.AgentId is null && mode is not null)
        {
            _currentMode = mode;
        }

        turn.ObservePrompt(prompt, context.TimestampUtc);
        if (_firstPrompt is null && !string.IsNullOrWhiteSpace(prompt))
        {
            _firstPrompt = prompt;
        }

        string agentKey = AgentKey(context.AgentId);
        _lastUserTurnsByAgent[agentKey] = turnId;

        AddToTurn(
            turnId,
            context,
            new CopilotEventSpec
            {
                NativeName = context.Type,
                Role = ObservationRole.PromptSubmitted,
                EventKind = CanonicalEventKind.PromptSubmitted,
                Direction = ObservationDirection.Input,
                TurnId = nativeTurnId ?? turnId,
                PromptText = prompt,
                Mode = mode,
                Status = _json.String(context.Data, "delivery"),
                AgentId = context.AgentId
            });
    }

    private void HandleSystemMessage(CopilotEnvelopeContext context)
    {
        string? nativeTurnId = ReadTurnId(context.Data);
        string? turnId = ResolveTurn(
            context.Data,
            context.AgentId,
            allowCreate: false,
            preferredSyntheticPrefix: "system");
        CopilotEventSpec messageSpec = new()
        {
            NativeName = context.Type,
            Role = ObservationRole.Message,
            Direction = ObservationDirection.Input,
            TurnId = nativeTurnId ?? turnId,
            Text = _json.Text(context.Data, "content"),
            Status = _json.String(context.Data, "role"),
            AgentId = context.AgentId,
            Evidence = turnId is null
                ? InferenceEvidence.Derived
                : InferenceEvidence.Observed,
            ExcludeFromSummary = true
        };
        if (turnId is null)
        {
            if (context.AgentId is null)
            {
                _pendingMainEvents.Add(Materialize(context, messageSpec));
            }

            return;
        }

        AddToTurn(
            turnId,
            context,
            messageSpec);

        JsonElement? skills = _json.Array(context.Data, "skills", "availableSkills");
        if (skills is null)
        {
            return;
        }

        int index = 0;
        foreach (JsonElement skill in skills.Value.EnumerateArray())
        {
            string? name = skill.ValueKind == JsonValueKind.String
                ? skill.GetString()
                : _json.String(skill, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                index++;
                continue;
            }

            string? path = skill.ValueKind == JsonValueKind.Object
                ? _json.String(skill, "path", "sourcePath")
                : null;
            AddToTurn(
                turnId,
                context,
                new CopilotEventSpec
                {
                    IdSuffix = $"available-skill:{index++}",
                    NativeName = "skill.available",
                    Role = ObservationRole.InstructionsLoaded,
                    ToolKind = CanonicalToolKind.Task,
                    TurnId = nativeTurnId ?? turnId,
                    AgentId = context.AgentId,
                    Skill = new SkillEvidence(
                        name,
                        SkillEvidenceStage.Available,
                        InferenceEvidence.Observed,
                        path),
                    ExcludeFromSummary = true
                });
        }
    }

    private void HandleTurnBoundary(
        CopilotEnvelopeContext context,
        bool isStart)
    {
        string? nativeTurnId = ReadTurnId(context.Data);
        string turnId = ResolveTurn(
            context.Data,
            context.AgentId,
            allowCreate: true,
            preferredSyntheticPrefix: "turn")!;
        CopilotTurnBuilder turn = GetOrCreateTurn(
            turnId,
            nativeTurnId is null
                ? InferenceEvidence.Derived
                : InferenceEvidence.Observed);
        if (context.AgentId is null)
        {
            AttachPendingMainEvents(turn);
        }

        string agentKey = AgentKey(context.AgentId);
        if (isStart)
        {
            turn.ObserveStart(context.TimestampUtc);
            _activeTurnsByAgent[agentKey] = turnId;
        }
        else
        {
            turn.ObserveEnd(context.TimestampUtc);
            if (_activeTurnsByAgent.TryGetValue(agentKey, out string? active) &&
                active.Equals(turnId, StringComparison.Ordinal))
            {
                _activeTurnsByAgent.Remove(agentKey);
            }

            if (_lastUserTurnsByAgent.TryGetValue(
                agentKey,
                out string? lastUserTurn) &&
                lastUserTurn.Equals(turnId, StringComparison.Ordinal))
            {
                _lastUserTurnsByAgent.Remove(agentKey);
            }
        }

        AddToTurn(
            turnId,
            context,
            new CopilotEventSpec
            {
                NativeName = context.Type,
                Role = isStart
                    ? ObservationRole.Generic
                    : ObservationRole.TurnStop,
                EventKind = isStart
                    ? CanonicalEventKind.ProviderSpecific
                    : CanonicalEventKind.TurnCompleted,
                Direction = isStart
                    ? ObservationDirection.Input
                    : ObservationDirection.Output,
                Tone = isStart
                    ? ObservationTone.Normal
                    : ObservationTone.Stop,
                TurnId = nativeTurnId ?? turnId,
                AgentId = context.AgentId,
                ExcludeFromSummary = true
            });
    }

    private void HandleAssistantReasoning(CopilotEnvelopeContext context)
    {
        string? nativeTurnId = ReadTurnId(context.Data);
        string? turnId = ResolveTurn(
            context.Data,
            context.AgentId,
            allowCreate: false,
            preferredSyntheticPrefix: "reasoning");
        if (turnId is null)
        {
            return;
        }

        string? text = _json.Text(
            context.Data,
            "content",
            "text",
            "reasoningText");
        bool opaque = text is null &&
            (HasNonNullProperty(context.Data, "reasoningOpaque") ||
             HasNonNullProperty(context.Data, "encryptedContent"));

        AddToTurn(
            turnId,
            context,
            new CopilotEventSpec
            {
                NativeName = context.Type,
                Role = ObservationRole.AgentThought,
                EventKind = CanonicalEventKind.AssistantThought,
                Tone = ObservationTone.Thought,
                Evidence = opaque
                    ? InferenceEvidence.Opaque
                    : InferenceEvidence.Observed,
                TurnId = nativeTurnId ?? turnId,
                Text = text,
                Model = _json.String(context.Data, "model"),
                AgentId = context.AgentId,
                Status = opaque ? "opaque" : null,
                UsageMeasurements = _usageExtractor.ExtractForEvent(
                    context.Type,
                    context.Data,
                    context.EventIdBase)
            });
    }

    private void HandleAssistantMessage(CopilotEnvelopeContext context)
    {
        string? nativeTurnId = ReadTurnId(context.Data);
        string turnId = ResolveTurn(
            context.Data,
            context.AgentId,
            allowCreate: true,
            preferredSyntheticPrefix: "assistant")!;
        GetOrCreateTurn(
            turnId,
            nativeTurnId is null
                ? InferenceEvidence.Derived
                : InferenceEvidence.Observed);
        if (context.AgentId is null)
        {
            AttachPendingMainEvents(_turns[turnId]);
        }

        string assistantStepId =
            _json.String(context.Data, "messageId") ??
            context.EventIdBase;
        string? model = _json.String(context.Data, "model");
        if (context.AgentId is null && model is not null)
        {
            _currentModel = model;
        }

        string? content = _json.Text(context.Data, "content", "text");
        IReadOnlyList<UsageMeasurement> usage =
            _usageExtractor.ExtractForEvent(
                context.Type,
                context.Data,
                context.EventIdBase);

        AddToTurn(
            turnId,
            context,
            new CopilotEventSpec
            {
                NativeName = context.Type,
                Role = string.IsNullOrEmpty(content)
                    ? ObservationRole.Generic
                    : ObservationRole.AgentResponse,
                EventKind = string.IsNullOrEmpty(content)
                    ? CanonicalEventKind.ProviderSpecific
                    : CanonicalEventKind.AssistantMessage,
                Direction = ObservationDirection.Output,
                TurnId = nativeTurnId ?? turnId,
                AssistantStepId = assistantStepId,
                Text = content,
                Model = model,
                Status = _json.String(context.Data, "phase"),
                AgentId = context.AgentId,
                DurationMs = ReadDuration(context.Data),
                UsageMeasurements = usage,
                ExcludeFromSummary = string.IsNullOrEmpty(content)
            });

        string? reasoningText = _json.Text(context.Data, "reasoningText");
        if (!string.IsNullOrEmpty(reasoningText))
        {
            AddToTurn(
                turnId,
                context,
                new CopilotEventSpec
                {
                    IdSuffix = "reasoning",
                    NativeName = "assistant.reasoning",
                    Role = ObservationRole.AgentThought,
                    EventKind = CanonicalEventKind.AssistantThought,
                    Tone = ObservationTone.Thought,
                    TurnId = nativeTurnId ?? turnId,
                    AssistantStepId = assistantStepId,
                    Text = reasoningText,
                    Model = model,
                    AgentId = context.AgentId
                });
        }

        if (HasNonNullProperty(context.Data, "reasoningOpaque") ||
            HasNonNullProperty(context.Data, "encryptedContent"))
        {
            AddToTurn(
                turnId,
                context,
                new CopilotEventSpec
                {
                    IdSuffix = "opaque-reasoning",
                    NativeName = "assistant.reasoning",
                    Role = ObservationRole.AgentThought,
                    EventKind = CanonicalEventKind.AssistantThought,
                    Tone = ObservationTone.Thought,
                    Evidence = InferenceEvidence.Opaque,
                    TurnId = nativeTurnId ?? turnId,
                    AssistantStepId = assistantStepId,
                    Model = model,
                    Status = "opaque",
                    AgentId = context.AgentId
                });
        }

        JsonElement? requests = _json.Array(context.Data, "toolRequests");
        if (requests is null)
        {
            return;
        }

        int requestCount = requests.Value.GetArrayLength();
        string? parallelGroupId = requestCount > 1 ? assistantStepId : null;
        int index = 0;
        foreach (JsonElement request in requests.Value.EnumerateArray())
        {
            AddToolRequest(
                context,
                request,
                turnId,
                nativeTurnId ?? turnId,
                assistantStepId,
                parallelGroupId,
                requestCount > 1,
                $"tool:{index++}");
        }
    }

    private void HandleToolRequest(CopilotEnvelopeContext context)
    {
        string? nativeTurnId = ReadTurnId(context.Data);
        string turnId = ResolveTurn(
            context.Data,
            context.AgentId,
            allowCreate: true,
            preferredSyntheticPrefix: "tool")!;
        string assistantStepId =
            _json.String(context.Data, "messageId", "assistantStepId") ??
            context.EventIdBase;
        AddToolRequest(
            context,
            context.Data,
            turnId,
            nativeTurnId ?? turnId,
            assistantStepId,
            null,
            false,
            null);
    }

    private void AddToolRequest(
        CopilotEnvelopeContext context,
        JsonElement request,
        string turnId,
        string nativeTurnId,
        string assistantStepId,
        string? parallelGroupId,
        bool isParallel,
        string? idSuffix)
    {
        string? toolCallId = ReadToolCallId(request);
        string? toolName = _json.String(request, "name", "toolName");
        CopilotMcpIdentity mcp = _toolSemantics.ReadMcpIdentity(request);
        string? task = ReadTask(request);

        if (toolCallId is not null)
        {
            _tools[toolCallId] = new CopilotToolCorrelation
            {
                TurnId = turnId,
                ToolName = toolName,
                Mcp = mcp,
                AssistantStepId = assistantStepId,
                ParallelGroupId = parallelGroupId,
                Task = task
            };
        }

        AddToTurn(
            turnId,
            context,
            new CopilotEventSpec
            {
                IdSuffix = idSuffix,
                NativeName = toolName ?? context.Type,
                Role = ObservationRole.ToolRequest,
                EventKind = CanonicalEventKind.ToolRequested,
                Direction = ObservationDirection.Input,
                ToolKind = _toolSemantics.Classify(toolName, mcp),
                Tone = mcp.IsMcp
                    ? ObservationTone.Mcp
                    : ObservationTone.Normal,
                TurnId = nativeTurnId,
                AssistantStepId = assistantStepId,
                ParallelGroupId = parallelGroupId,
                ToolCallId = toolCallId,
                ToolName = toolName,
                McpServerName = mcp.ServerName,
                McpToolName = mcp.ToolName,
                Text = ReadArgumentsText(request),
                Model = _json.String(request, "model"),
                AgentId = context.AgentId,
                Task = task,
                IsParallelCandidate = isParallel,
                TargetPaths = _toolSemantics.TargetPaths(request)
            });
    }

    private void HandleToolExecution(
        CopilotEnvelopeContext context,
        bool isStart)
    {
        string? toolCallId = ReadToolCallId(context.Data);
        _tools.TryGetValue(toolCallId ?? string.Empty, out CopilotToolCorrelation? known);

        string? nativeTurnId = ReadTurnId(context.Data);
        string? turnId = known?.TurnId ?? ResolveTurn(
            context.Data,
            context.AgentId,
            allowCreate: true,
            preferredSyntheticPrefix: "tool");
        if (turnId is null)
        {
            return;
        }

        string? toolName =
            _json.String(context.Data, "toolName", "name") ??
            known?.ToolName;
        CopilotMcpIdentity directMcp =
            _toolSemantics.ReadMcpIdentity(context.Data);
        CopilotMcpIdentity mcp = new(
            directMcp.ServerName ?? known?.Mcp?.ServerName,
            directMcp.ToolName ?? known?.Mcp?.ToolName);

        known ??= new CopilotToolCorrelation
        {
            TurnId = turnId,
            ToolName = toolName,
            Mcp = mcp
        };
        known.TurnId ??= turnId;
        known.ToolName ??= toolName;
        known.Mcp = mcp.IsMcp ? mcp : known.Mcp;

        if (isStart)
        {
            known.StartedAtUtc = context.TimestampUtc;
        }

        if (toolCallId is not null)
        {
            _tools[toolCallId] = known;
        }

        bool isFailure = !isStart && IsFailure(context.Data);
        bool isAborted = !isStart && IsAborted(context.Data);
        double? durationMs = ReadDuration(context.Data);
        if (!isStart &&
            durationMs is null &&
            known.StartedAtUtc is DateTimeOffset started &&
            context.TimestampUtc is DateTimeOffset completed)
        {
            durationMs = Math.Max(0, (completed - started).TotalMilliseconds);
        }

        string? resultText = !isStart
            ? ReadResultText(context.Data)
            : null;
        string? status = ReadStatus(context.Data);

        AddToTurn(
            turnId,
            context,
            new CopilotEventSpec
            {
                NativeName = context.Type,
                Role = isStart
                    ? ObservationRole.InnerExecutionStart
                    : isFailure
                        ? ObservationRole.ToolFailure
                        : ObservationRole.ToolSuccess,
                EventKind = isStart
                    ? CanonicalEventKind.ProviderSpecific
                    : isFailure
                        ? CanonicalEventKind.ToolFailed
                        : CanonicalEventKind.ToolSucceeded,
                Direction = isStart
                    ? ObservationDirection.Input
                    : ObservationDirection.Output,
                ToolKind = _toolSemantics.Classify(toolName, mcp),
                Tone = isFailure
                    ? ObservationTone.Failure
                    : mcp.IsMcp
                        ? ObservationTone.Mcp
                        : ObservationTone.Normal,
                TurnId = nativeTurnId ?? turnId,
                AssistantStepId = known.AssistantStepId,
                ParallelGroupId = known.ParallelGroupId,
                ToolCallId = toolCallId,
                ToolName = toolName,
                McpServerName = mcp.ServerName,
                McpToolName = mcp.ToolName,
                Text = isStart
                    ? ReadArgumentsText(context.Data)
                    : resultText,
                Model = _json.String(context.Data, "model"),
                Status = isStart ? "started" : status,
                AgentId = context.AgentId,
                Task = known.Task,
                DurationMs = durationMs,
                IsFailure = isFailure,
                IsAborted = isAborted,
                TargetPaths = _toolSemantics.TargetPaths(context.Data)
            });
    }

    private void HandlePermission(
        CopilotEnvelopeContext context,
        bool isRequested)
    {
        string? toolCallId = ReadToolCallId(context.Data);
        _tools.TryGetValue(toolCallId ?? string.Empty, out CopilotToolCorrelation? known);
        string? nativeTurnId = ReadTurnId(context.Data);
        string? turnId = known?.TurnId ?? ResolveTurn(
            context.Data,
            context.AgentId,
            allowCreate: false,
            preferredSyntheticPrefix: "permission");
        if (turnId is null)
        {
            return;
        }

        CopilotMcpIdentity directMcp =
            _toolSemantics.ReadMcpIdentity(context.Data);
        CopilotMcpIdentity mcp = new(
            directMcp.ServerName ?? known?.Mcp?.ServerName,
            directMcp.ToolName ?? known?.Mcp?.ToolName);
        string? status = ReadStatus(context.Data);
        bool denied = context.Type == "permission.denied" ||
            ContainsFailureWord(status);
        string? toolName =
            ReadNestedString(
                context.Data,
                "permissionRequest",
                "toolName") ??
            known?.ToolName;

        AddToTurn(
            turnId,
            context,
            new CopilotEventSpec
            {
                NativeName = context.Type,
                Role = isRequested
                    ? ObservationRole.PermissionRequest
                    : denied
                        ? ObservationRole.PermissionDenied
                        : ObservationRole.Generic,
                EventKind = isRequested
                    ? CanonicalEventKind.PermissionRequested
                    : denied
                        ? CanonicalEventKind.PermissionDenied
                        : CanonicalEventKind.ProviderSpecific,
                Direction = isRequested
                    ? ObservationDirection.Input
                    : ObservationDirection.Output,
                ToolKind = _toolSemantics.Classify(toolName, mcp),
                Tone = mcp.IsMcp
                    ? ObservationTone.Mcp
                    : ObservationTone.Permission,
                TurnId = nativeTurnId ?? turnId,
                AssistantStepId = known?.AssistantStepId,
                ParallelGroupId = known?.ParallelGroupId,
                ToolCallId = toolCallId,
                ToolName = toolName,
                McpServerName = mcp.ServerName,
                McpToolName = mcp.ToolName,
                Status = status,
                AgentId = context.AgentId,
                IsFailure = denied,
                IsAborted = denied,
                TargetPaths = _toolSemantics.TargetPaths(context.Data),
                ExcludeFromSummary = true
            });
    }

    private void HandleSubagent(CopilotEnvelopeContext context)
    {
        bool isStart =
            context.Type.EndsWith(".started", StringComparison.Ordinal) ||
            context.Type.EndsWith(".start", StringComparison.Ordinal);
        string? toolCallId = ReadToolCallId(context.Data);
        _tools.TryGetValue(toolCallId ?? string.Empty, out CopilotToolCorrelation? known);

        string? agentId =
            context.AgentId ??
            _json.String(context.Data, "agentId") ??
            toolCallId;
        string? turnId = known?.TurnId;
        if (turnId is null &&
            agentId is not null &&
            _agentParentTurns.TryGetValue(agentId, out string? parentTurn))
        {
            turnId = parentTurn;
        }

        turnId ??= ResolveTurn(
            context.Data,
            context.AgentId,
            allowCreate: false,
            preferredSyntheticPrefix: "subagent");
        if (turnId is null)
        {
            return;
        }

        if (isStart && agentId is not null)
        {
            _agentParentTurns[agentId] = turnId;
        }

        string? agentType = _json.String(
            context.Data,
            "agentType",
            "agentName",
            "agentDisplayName");
        string? task =
            _json.Text(context.Data, "task", "prompt", "description") ??
            known?.Task;
        double? duration = ReadDuration(context.Data);

        AddToTurn(
            turnId,
            context,
            new CopilotEventSpec
            {
                NativeName = context.Type,
                Role = isStart
                    ? ObservationRole.SubagentStart
                    : ObservationRole.SubagentStop,
                EventKind = isStart
                    ? CanonicalEventKind.SubagentStarted
                    : CanonicalEventKind.SubagentCompleted,
                Direction = isStart
                    ? ObservationDirection.Input
                    : ObservationDirection.Output,
                ToolKind = CanonicalToolKind.Agent,
                TurnId = ReadTurnId(context.Data) ?? turnId,
                AssistantStepId = known?.AssistantStepId,
                ToolCallId = toolCallId,
                ToolName = known?.ToolName,
                Model = _json.String(context.Data, "model"),
                Status = isStart ? "started" : "completed",
                AgentId = agentId,
                AgentType = agentType,
                Task = task,
                DurationMs = duration,
                UsageMeasurements = _usageExtractor.ExtractForEvent(
                    context.Type,
                    context.Data,
                    context.EventIdBase)
            });
    }

    private void HandleSkill(CopilotEnvelopeContext context)
    {
        string? turnId = ResolveTurn(
            context.Data,
            context.AgentId,
            allowCreate: false,
            preferredSyntheticPrefix: "skill");
        if (turnId is null)
        {
            return;
        }

        string? skillName = _json.String(
            context.Data,
            "name",
            "skillName");
        if (skillName is null)
        {
            return;
        }

        SkillEvidenceStage stage = context.Type switch
        {
            "skill.available" => SkillEvidenceStage.Available,
            "skill.attached" => SkillEvidenceStage.Attached,
            "skill.loaded" => SkillEvidenceStage.Loaded,
            "skill.completed" or "skill.execution_completed" =>
                SkillEvidenceStage.ExecutionCorroborated,
            _ => SkillEvidenceStage.Invoked
        };
        string? sourcePath = _json.String(context.Data, "path", "sourcePath");

        AddToTurn(
            turnId,
            context,
            new CopilotEventSpec
            {
                NativeName = context.Type,
                Role = ObservationRole.InstructionsLoaded,
                ToolKind = CanonicalToolKind.Task,
                Direction = ObservationDirection.Input,
                TurnId = ReadTurnId(context.Data) ?? turnId,
                AgentId = context.AgentId,
                Skill = new SkillEvidence(
                    skillName,
                    stage,
                    InferenceEvidence.Observed,
                    sourcePath),
                TargetPaths = sourcePath is null ? [] : [sourcePath],
                ExcludeFromSummary = true
            });
    }

    private void HandleModelEvent(CopilotEnvelopeContext context)
    {
        string? turnId = ResolveExistingModelTurn(context);
        if (turnId is null)
        {
            return;
        }

        bool failure =
            context.Type.Contains("failure", StringComparison.OrdinalIgnoreCase) ||
            context.Type.Contains("error", StringComparison.OrdinalIgnoreCase);
        string? model = _json.String(context.Data, "model");
        if (context.AgentId is null && model is not null)
        {
            _currentModel = model;
        }

        AddToTurn(
            turnId,
            context,
            new CopilotEventSpec
            {
                NativeName = context.Type,
                Role = failure
                    ? ObservationRole.RuntimeError
                    : ObservationRole.Generic,
                EventKind = failure
                    ? CanonicalEventKind.RuntimeError
                    : CanonicalEventKind.ProviderSpecific,
                Tone = failure
                    ? ObservationTone.Failure
                    : ObservationTone.Normal,
                TurnId = ReadTurnId(context.Data) ??
                    _json.String(context.Data, "turn") ??
                    turnId,
                Text = ReadModelText(context.Data),
                Model = model,
                Status = _json.String(context.Data, "kind", "status"),
                AgentId = context.AgentId,
                DurationMs = ReadDuration(context.Data),
                IsFailure = failure,
                UsageMeasurements = _usageExtractor.ExtractForEvent(
                    context.Type,
                    context.Data,
                    context.EventIdBase),
                ExcludeFromSummary = true
            });
    }

    private void HandleRuntimeError(CopilotEnvelopeContext context)
    {
        string? turnId = ResolveTurn(
            context.Data,
            context.AgentId,
            allowCreate: false,
            preferredSyntheticPrefix: "error");
        if (turnId is null)
        {
            return;
        }

        AddToTurn(
            turnId,
            context,
            new CopilotEventSpec
            {
                NativeName = context.Type,
                Role = ObservationRole.RuntimeError,
                EventKind = CanonicalEventKind.RuntimeError,
                Tone = ObservationTone.Failure,
                TurnId = ReadTurnId(context.Data) ?? turnId,
                Text = _json.Text(context.Data, "message", "error"),
                Status = _json.String(context.Data, "errorType", "statusCode"),
                AgentId = context.AgentId,
                IsFailure = true
            });
    }

    private void HandleCompaction(CopilotEnvelopeContext context)
    {
        string? turnId = ResolveTurn(
            context.Data,
            context.AgentId,
            allowCreate: false,
            preferredSyntheticPrefix: "compaction");
        if (turnId is null)
        {
            return;
        }

        bool completed =
            context.Type.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
            context.Type.Contains("end", StringComparison.OrdinalIgnoreCase);
        AddToTurn(
            turnId,
            context,
            new CopilotEventSpec
            {
                NativeName = context.Type,
                Role = completed
                    ? ObservationRole.CompactionEnd
                    : ObservationRole.CompactionStart,
                EventKind = completed
                    ? CanonicalEventKind.CompactionCompleted
                    : CanonicalEventKind.CompactionStarted,
                Tone = ObservationTone.Compaction,
                TurnId = ReadTurnId(context.Data) ?? turnId,
                Status = _json.String(context.Data, "trigger", "reason"),
                AgentId = context.AgentId
            });
    }

    private void AddToTurn(
        string turnId,
        CopilotEnvelopeContext context,
        CopilotEventSpec spec)
    {
        CopilotTurnBuilder turn = GetOrCreateTurn(
            turnId,
            spec.Evidence == InferenceEvidence.Observed
                ? InferenceEvidence.Observed
                : InferenceEvidence.Derived);
        turn.Add(Materialize(context, spec));
    }

    private void AttachPendingMainEvents(CopilotTurnBuilder turn)
    {
        if (_pendingMainEvents.Count == 0)
        {
            return;
        }

        foreach (SessionEventRecord pending in _pendingMainEvents)
        {
            turn.Add(pending);
        }

        _pendingMainEvents.Clear();
    }

    private SessionEventRecord Materialize(
        CopilotEnvelopeContext context,
        CopilotEventSpec spec)
    {
        string requestedId = spec.IdSuffix is null
            ? context.EventIdBase
            : $"{context.EventIdBase}:{spec.IdSuffix}";
        string eventId = UniqueEventId(requestedId);

        return new SessionEventRecord
        {
            Id = eventId,
            NativeName = spec.NativeName,
            Provider = HookProvider.GitHubCopilot,
            Surface = HookSurface.CopilotCli,
            Role = spec.Role,
            EventKind = spec.EventKind,
            ToolKind = spec.ToolKind,
            Direction = spec.Direction,
            Tone = spec.Tone,
            Evidence = spec.Evidence,
            TimestampUtc = context.TimestampUtc,
            Order = context.Order,
            TurnId = spec.TurnId,
            ParentId = context.ParentId,
            AssistantStepId = spec.AssistantStepId,
            ParallelGroupId = spec.ParallelGroupId,
            ToolCallId = spec.ToolCallId,
            ToolName = spec.ToolName,
            McpServerName = spec.McpServerName,
            McpToolName = spec.McpToolName,
            PromptText = spec.PromptText,
            Text = spec.Text,
            Model = spec.Model,
            Mode = spec.Mode,
            Status = spec.Status,
            AgentId = spec.AgentId ?? context.AgentId,
            AgentType = spec.AgentType,
            Task = spec.Task,
            DurationMs = spec.DurationMs,
            IsFailure = spec.IsFailure,
            IsAborted = spec.IsAborted,
            IsParallelCandidate = spec.IsParallelCandidate,
            ExcludeFromSummary =
                spec.ExcludeFromSummary || context.IsEphemeral,
            TargetPaths = spec.TargetPaths,
            UsageMeasurements = spec.UsageMeasurements,
            Skill = spec.Skill,
            Provenance = context.Provenance
        };
    }

    private string? ResolveTurn(
        JsonElement data,
        string? agentId,
        bool allowCreate,
        string preferredSyntheticPrefix)
    {
        if (agentId is not null &&
            _agentParentTurns.TryGetValue(agentId, out string? agentParentTurn))
        {
            return agentParentTurn;
        }

        string? toolCallId = ReadToolCallId(data);
        if (toolCallId is not null &&
            _tools.TryGetValue(toolCallId, out CopilotToolCorrelation? tool) &&
            tool.TurnId is not null)
        {
            return tool.TurnId;
        }

        string? nativeTurnId = ReadTurnId(data);
        string? interactionId = _json.String(data, "interactionId");
        if (nativeTurnId is not null)
        {
            if (interactionId is not null &&
                _interactionTurns.TryGetValue(
                    interactionId,
                    out string? interactionTurn) &&
                interactionTurn.StartsWith(
                    "interaction:",
                    StringComparison.Ordinal) &&
                !interactionTurn.Equals(nativeTurnId, StringComparison.Ordinal))
            {
                MergeTurn(interactionTurn, nativeTurnId);
            }

            if (interactionId is not null)
            {
                _interactionTurns[interactionId] = nativeTurnId;
            }

            return nativeTurnId;
        }

        if (interactionId is not null &&
            _interactionTurns.TryGetValue(interactionId, out string? mappedTurn))
        {
            return mappedTurn;
        }

        string agentKey = AgentKey(agentId);
        if (_activeTurnsByAgent.TryGetValue(agentKey, out string? activeTurn))
        {
            return activeTurn;
        }

        if (_lastUserTurnsByAgent.TryGetValue(agentKey, out string? userTurn))
        {
            return userTurn;
        }

        if (!allowCreate)
        {
            return null;
        }

        string synthetic = interactionId is null
            ? $"{preferredSyntheticPrefix}:{_turnCreationSequence}"
            : $"interaction:{interactionId}";
        if (interactionId is not null)
        {
            _interactionTurns[interactionId] = synthetic;
        }

        return synthetic;
    }

    private CopilotTurnBuilder GetOrCreateTurn(
        string turnId,
        InferenceEvidence evidence)
    {
        if (_turns.TryGetValue(turnId, out CopilotTurnBuilder? turn))
        {
            return turn;
        }

        turn = new CopilotTurnBuilder(
            turnId,
            _turnCreationSequence++,
            evidence);
        _turns.Add(turnId, turn);
        return turn;
    }

    private void MergeTurn(string sourceId, string targetId)
    {
        if (sourceId.Equals(targetId, StringComparison.Ordinal))
        {
            return;
        }

        if (!_turns.TryGetValue(sourceId, out CopilotTurnBuilder? sourceTurn))
        {
            return;
        }

        CopilotTurnBuilder targetTurn =
            GetOrCreateTurn(targetId, InferenceEvidence.Observed);
        targetTurn.MergeFrom(sourceTurn, targetId);
        _turns.Remove(sourceId);

        ReplaceDictionaryValues(_interactionTurns, sourceId, targetId);
        ReplaceDictionaryValues(_activeTurnsByAgent, sourceId, targetId);
        ReplaceDictionaryValues(_lastUserTurnsByAgent, sourceId, targetId);
        ReplaceDictionaryValues(_agentParentTurns, sourceId, targetId);
        foreach (CopilotToolCorrelation tool in _tools.Values)
        {
            if (tool.TurnId?.Equals(sourceId, StringComparison.Ordinal) == true)
            {
                tool.TurnId = targetId;
            }
        }
    }

    private void ApplyContext(JsonElement data, int order)
    {
        JsonElement context = _json.Object(data, "context") ?? data;
        string? cwd = _json.String(context, "cwd", "workingDirectory");
        string? gitRoot = _json.String(context, "gitRoot", "git_root");
        string? repository = _json.String(context, "repository");
        string? branch = _json.String(context, "branch");

        _eventCwd = cwd ?? _eventCwd;
        _eventGitRoot = gitRoot ?? _eventGitRoot;
        _eventRepository = repository ?? _eventRepository;
        _eventBranch = branch ?? _eventBranch;

        string prefix = $"context.{_contextSequence++}";
        _metadata[$"{prefix}.order"] = order.ToString(CultureInfo.InvariantCulture);
        _metadata[$"{prefix}.cwd"] = cwd;
        _metadata[$"{prefix}.gitRoot"] = gitRoot;
        _metadata[$"{prefix}.repository"] = repository;
        _metadata[$"{prefix}.branch"] = branch;
        _metadata["context.current.cwd"] = _eventCwd;
        _metadata["context.current.gitRoot"] = _eventGitRoot;
        _metadata["context.current.repository"] = _eventRepository;
        _metadata["context.current.branch"] = _eventBranch;
        SetMetadataIfMissing("cwd", _eventCwd);
        SetMetadataIfMissing("git_root", _eventGitRoot);
        SetMetadataIfMissing("repository", _eventRepository);
        SetMetadataIfMissing("branch", _eventBranch);
    }

    private void CaptureSessionUsage(
        JsonElement data,
        string sourceRecordId,
        string prefix,
        UsageBehavior behavior)
    {
        IReadOnlyList<UsageMeasurement> measurements =
            _usageExtractor.ExtractSessionAggregate(
                data,
                sourceRecordId,
                behavior);
        foreach (UsageMeasurement measurement in measurements)
        {
            string key = $"{prefix}.{measurement.Name}";
            _metadata[key] = _metadataFormatter.Format(measurement.Value);
            _metadata[$"{key}.unit"] = measurement.Unit;
            _metadata[$"usage.latest.{measurement.Name}"] =
                _metadataFormatter.Format(measurement.Value);
            _metadata[$"usage.latest.{measurement.Name}.unit"] =
                measurement.Unit;
            _metadata[measurement.Name] =
                _metadataFormatter.Format(measurement.Value);
            _metadata[$"{measurement.Name}.unit"] = measurement.Unit;
            _currentAggregateMetadataKeys.Add(measurement.Name);
            _currentAggregateMetadataKeys.Add($"{measurement.Name}.unit");
        }

        if (_json.TryGetProperty(data, out JsonElement modelMetrics, "modelMetrics"))
        {
            _metadata[$"{prefix}.modelMetrics"] = modelMetrics.GetRawText();
            _metadata["modelMetrics"] = modelMetrics.GetRawText();
            _currentAggregateMetadataKeys.Add("modelMetrics");
        }

        foreach (string name in new[]
        {
            "totalNanoAiu",
            "totalPremiumRequests",
            "totalApiDurationMs"
        })
        {
            long? value = _json.Integer(data, name);
            if (value is not null)
            {
                _metadata[name] = _metadataFormatter.Format(value.Value);
            }
        }
    }

    private void ClearCurrentShutdownAggregate()
    {
        string[] keys = _metadata.Keys
            .Where(static key =>
                key.StartsWith("usage.final.", StringComparison.Ordinal) ||
                key.StartsWith("usage.latest.", StringComparison.Ordinal) ||
                key.StartsWith("shutdown.", StringComparison.Ordinal) ||
                key is "totalNanoAiu" or
                    "totalPremiumRequests" or
                    "totalApiDurationMs")
            .ToArray();
        foreach (string key in keys)
        {
            _metadata.Remove(key);
        }

        foreach (string key in _currentAggregateMetadataKeys)
        {
            _metadata.Remove(key);
        }

        _currentAggregateMetadataKeys.Clear();
    }

    private string? ResolveExistingModelTurn(CopilotEnvelopeContext context)
    {
        string? nativeTurnId =
            ReadTurnId(context.Data) ??
            _json.String(context.Data, "turn");
        if (nativeTurnId is not null && _turns.ContainsKey(nativeTurnId))
        {
            return nativeTurnId;
        }

        return ResolveTurn(
            context.Data,
            context.AgentId,
            allowCreate: false,
            preferredSyntheticPrefix: "model");
    }

    private DateTimeOffset? ReadTimestamp(
        JsonElement root,
        JsonElement data)
    {
        if (_json.TryGetProperty(root, out JsonElement timestamp, "timestamp"))
        {
            DateTimeOffset? parsed = _timestampParser.Parse(timestamp);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        foreach (string name in new[] { "timestampMs", "timestamp", "time" })
        {
            if (_json.TryGetProperty(data, out timestamp, name))
            {
                DateTimeOffset? parsed = _timestampParser.Parse(timestamp);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private DateTimeOffset? ReadTimestamp(
        JsonElement data,
        string propertyName)
    {
        return _json.TryGetProperty(data, out JsonElement timestamp, propertyName)
            ? _timestampParser.Parse(timestamp)
            : null;
    }

    private string? ReadTurnId(JsonElement data) =>
        _json.String(data, "turnId", "turn_id");

    private string? ReadToolCallId(JsonElement data)
    {
        string? direct = _json.String(
            data,
            "toolCallId",
            "tool_call_id",
            "callId");
        if (direct is not null)
        {
            return direct;
        }

        foreach (string nestedName in new[]
        {
            "permissionRequest",
            "promptRequest",
            "result"
        })
        {
            JsonElement? nested = _json.Object(data, nestedName);
            if (nested is null)
            {
                continue;
            }

            string? nestedId = _json.String(
                nested.Value,
                "toolCallId",
                "tool_call_id",
                "callId");
            if (nestedId is not null)
            {
                return nestedId;
            }
        }

        return null;
    }

    private string? ReadTask(JsonElement request)
    {
        JsonElement? arguments = _json.Object(request, "arguments", "args");
        return arguments is null
            ? _json.Text(request, "task", "prompt", "description")
            : _json.Text(
                arguments.Value,
                "task",
                "prompt",
                "description",
                "instructions");
    }

    private string? ReadArgumentsText(JsonElement data)
    {
        if (!_json.TryGetProperty(
            data,
            out JsonElement arguments,
            "arguments",
            "args"))
        {
            return null;
        }

        return arguments.ValueKind == JsonValueKind.String
            ? arguments.GetString()
            : arguments.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? null
                : arguments.GetRawText();
    }

    private string? ReadResultText(JsonElement data)
    {
        JsonElement? result = _json.Object(data, "result");
        string? resultText = result is null
            ? null
            : _json.Text(
                result.Value,
                "content",
                "detailedContent",
                "text",
                "message");
        return resultText ??
            _json.Text(data, "content", "error", "message");
    }

    private string? ReadModelText(JsonElement data)
    {
        string? direct = _json.Text(data, "content", "text");
        if (direct is not null)
        {
            return direct;
        }

        JsonElement? message = _json.Object(data, "message", "response");
        return message is null
            ? null
            : _json.Text(message.Value, "content", "text");
    }

    private string? ReadStatus(JsonElement data)
    {
        string? direct = _json.String(
            data,
            "status",
            "kind",
            "stopReason");
        if (direct is not null)
        {
            return direct;
        }

        JsonElement? result = _json.Object(data, "result");
        return result is null
            ? null
            : _json.String(result.Value, "kind", "status");
    }

    private double? ReadDuration(JsonElement data)
    {
        foreach (string name in new[]
        {
            "durationMs",
            "modelCallDurationMs",
            "totalApiDurationMs",
            "endToEndLatencyMs",
            "latencyMs"
        })
        {
            double? value = _json.Number(data, name);
            if (value is not null)
            {
                return Math.Max(0, value.Value);
            }
        }

        return null;
    }

    private bool IsFailure(JsonElement data)
    {
        bool? success = _json.Boolean(data, "success");
        if (success == false)
        {
            return true;
        }

        return HasNonNullProperty(data, "error") ||
            ContainsFailureWord(ReadStatus(data));
    }

    private bool IsAborted(JsonElement data)
    {
        if (_json.Boolean(data, "aborted", "cancelled", "canceled") == true)
        {
            return true;
        }

        string? status = ReadStatus(data);
        return status?.Contains("abort", StringComparison.OrdinalIgnoreCase) == true ||
            status?.Contains("cancel", StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool HasNonNullProperty(JsonElement data, string name)
    {
        return _json.TryGetProperty(data, out JsonElement value, name) &&
            value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
    }

    private string? ReadNestedString(
        JsonElement data,
        string objectName,
        string propertyName)
    {
        JsonElement? nested = _json.Object(data, objectName);
        return nested is null
            ? null
            : _json.String(nested.Value, propertyName);
    }

    private string UniqueEventId(string requestedId)
    {
        if (_eventIds.Add(requestedId))
        {
            return requestedId;
        }

        int suffix = 2;
        string candidate;
        do
        {
            candidate = $"{requestedId}#{suffix++}";
        }
        while (!_eventIds.Add(candidate));

        return candidate;
    }

    private void AddWarning(string warning)
    {
        _warnings.Add(warning);
        IsComplete = false;
    }

    private void SetMetadataIfMissing(string key, string? value)
    {
        if (value is not null &&
            (!_metadata.TryGetValue(key, out string? existing) ||
             string.IsNullOrWhiteSpace(existing)))
        {
            _metadata[key] = value;
        }
    }

    private static string AgentKey(string? agentId) =>
        agentId ?? string.Empty;

    private static bool ContainsFailureWord(string? value)
    {
        return value?.Contains("denied", StringComparison.OrdinalIgnoreCase) == true ||
            value?.Contains("reject", StringComparison.OrdinalIgnoreCase) == true ||
            value?.Contains("fail", StringComparison.OrdinalIgnoreCase) == true ||
            value?.Contains("error", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void ReplaceDictionaryValues(
        IDictionary<string, string> values,
        string oldValue,
        string newValue)
    {
        string[] keys = values
            .Where(pair => pair.Value.Equals(oldValue, StringComparison.Ordinal))
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (string key in keys)
        {
            values[key] = newValue;
        }
    }

    private static string? PreviewTitle(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        string oneLine = prompt.ReplaceLineEndings(" ").Trim();
        const int maximumLength = 120;
        return oneLine.Length <= maximumLength
            ? oneLine
            : oneLine[..maximumLength].TrimEnd() + "\u2026";
    }

    private static DateTimeOffset? Minimum(
        DateTimeOffset? first,
        DateTimeOffset? second) =>
        first is null ? second :
        second is null ? first :
        first < second ? first : second;

    private static DateTimeOffset? Maximum(
        DateTimeOffset? first,
        DateTimeOffset? second) =>
        first is null ? second :
        second is null ? first :
        first > second ? first : second;
}
