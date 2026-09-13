using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;

namespace HarnessSpy.Core.Sessions.Claude;

internal sealed class ClaudeProjectLocation
{
    public ClaudeProjectLocation(string directoryPath)
    {
        DirectoryPath = directoryPath;
        EncodedName = Path.GetFileName(
            directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    public string DirectoryPath { get; }

    public string EncodedName { get; }
}

internal sealed class ClaudeTranscriptFile
{
    public required string Path { get; init; }

    public required TranscriptFileRole Role { get; init; }

    public required DateTimeOffset LastWriteTimeUtc { get; init; }

    public required long Length { get; init; }

    public bool IsRecovery { get; init; }

    public string? AgentId { get; init; }

    public string? ParentSessionId { get; init; }
}

internal sealed class ClaudeTranscriptIdentityResolver
{
    private readonly ClaudeJsonAccessor _json = new();

    public string ResolveMainSessionId(
        IReadOnlyList<ClaudeTranscriptRow> rows,
        string fallback,
        IList<string> warnings)
    {
        HashSet<string> sessionIds = new(StringComparer.Ordinal);
        foreach (ClaudeTranscriptRow row in rows)
        {
            string? sessionId = _json.String(row.Json, "sessionId", "session_id");
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                sessionIds.Add(sessionId);
            }
        }

        if (sessionIds.Count == 1)
        {
            return sessionIds.Single();
        }

        if (sessionIds.Count > 1)
        {
            warnings.Add(
                $"Claude transcript '{rows[0].Path}' contains conflicting session IDs; " +
                $"using filename identity '{fallback}'.");
        }

        return fallback;
    }

    public string ResolveParentSessionId(
        IReadOnlyList<ClaudeTranscriptRow> rows,
        string fallback)
    {
        foreach (ClaudeTranscriptRow row in rows)
        {
            string? parent = _json.String(
                row.Json,
                "parentSessionId",
                "parent_session_id",
                "sessionId",
                "session_id");
            if (!string.IsNullOrWhiteSpace(parent))
            {
                return parent;
            }
        }

        return fallback;
    }

    public string? ResolveAgentId(
        IReadOnlyList<ClaudeTranscriptRow> rows,
        string? fallback)
    {
        foreach (ClaudeTranscriptRow row in rows)
        {
            string? agentId = _json.String(row.Json, "agentId", "agent_id");
            if (!string.IsNullOrWhiteSpace(agentId))
            {
                return agentId;
            }
        }

        return fallback;
    }
}

internal sealed class ClaudeSessionCatalogBuilder
{
    private readonly Dictionary<string, ClaudeSessionAccumulator> _sessions =
        new(StringComparer.Ordinal);
    private readonly WorkspaceNormalizer _workspaceNormalizer;

    public ClaudeSessionCatalogBuilder(WorkspaceNormalizer workspaceNormalizer)
    {
        _workspaceNormalizer = workspaceNormalizer ??
            throw new ArgumentNullException(nameof(workspaceNormalizer));
    }

    public ClaudeSessionAccumulator GetOrCreate(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out ClaudeSessionAccumulator? session))
        {
            session = new ClaudeSessionAccumulator(sessionId, _workspaceNormalizer);
            _sessions.Add(sessionId, session);
        }

        return session;
    }

    public IReadOnlyList<SessionCatalogEntry> Build() =>
        _sessions.Values
            .Select(static session => session.Build())
            .OrderBy(static session => session.Workspace.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(static session => session.LastActivityAtUtc)
            .ThenBy(static session => session.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<Plans.SessionPlanActivity> BuildPlanActivities() =>
        _sessions.Values
            .SelectMany(static session => session.PlanActivities)
            .ToArray();
}

internal sealed class ClaudeSessionAccumulator
{
    private readonly string _sessionId;
    private readonly WorkspaceNormalizer _workspaceNormalizer;
    private readonly ClaudeJsonAccessor _json = new();
    private readonly Dictionary<string, ClaudeTurnAccumulator> _turns =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _eventTurns =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolNamesById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionFileBinding> _files =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SessionSourceProvenance> _sources = [];
    private readonly Dictionary<string, string?> _metadata =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _usageSnapshots =
        new(StringComparer.Ordinal);
    private readonly List<ClaudeIndexWorkspaceCandidate> _indexWorkspaces = [];
    private readonly List<string> _mainFilePaths = [];
    private readonly List<Plans.SessionPlanActivity> _planActivities = [];

    private string? _encodedWorkspacePath;
    private string? _mainCwd;
    private int _mainCwdPriority = -1;
    private string? _subagentCwd;
    private string? _title;
    private int _titlePriority;
    private string? _firstPrompt;
    private int _firstPromptPriority = -1;
    private string? _model;
    private int _modelPriority = -1;
    private string? _eventModel;
    private string? _mode;
    private int _modePriority = -1;
    private string? _permissionMode;
    private int _permissionModePriority = -1;
    private DateTimeOffset? _startedAtUtc;
    private DateTimeOffset? _lastActivityAtUtc;
    private DateTimeOffset? _indexStartedAtUtc;
    private DateTimeOffset? _indexLastActivityAtUtc;
    private ClaudeCostStateCandidate? _costState;
    private int _order;
    private bool _costStateProjected;

    public ClaudeSessionAccumulator(
        string sessionId,
        WorkspaceNormalizer workspaceNormalizer)
    {
        _sessionId = sessionId;
        _workspaceNormalizer = workspaceNormalizer;
    }

    public IReadOnlyList<Plans.SessionPlanActivity> PlanActivities => _planActivities;

    private string CatalogSessionId =>
        $"{HookProvider.ClaudeCode}:{HookSurface.ClaudeCode}:{_sessionId}";

    public void AddIndex(
        ClaudeProjectLocation project,
        ClaudeSessionIndex index,
        ClaudeSessionIndexEntry entry)
    {
        RecordProject(project);

        _sources.Add(new SessionSourceProvenance(
            SessionSourceKind.ClaudeSessionIndex,
            index.Path,
            "json",
            index.RawContent,
            ContractVersion: index.Version));
        SessionSourceProvenance provenance = new(
            SessionSourceKind.ClaudeSessionIndex,
            index.Path,
            "json",
            entry.RawContent,
            RecordId: entry.SessionId,
            ContractVersion: index.Version);
        _sources.Add(provenance);

        SetTitle(entry.Summary, 60);
        SetTitle(entry.FirstPrompt, 50);
        SetFirstPrompt(entry.FirstPrompt, 1);
        _indexStartedAtUtc = Minimum(_indexStartedAtUtc, entry.CreatedAtUtc);
        _indexLastActivityAtUtc = Maximum(
            _indexLastActivityAtUtc,
            entry.ModifiedAtUtc ?? entry.FileModifiedAtUtc);

        _metadata["claude.index.path"] = index.Path;
        _metadata["claude.index.version"] = index.Version;
        _metadata["claude.index.originalPath"] = index.OriginalPath;
        foreach ((string key, string? value) in entry.Metadata)
        {
            _metadata[$"claude.index.{key}"] = value;
        }

        _indexWorkspaces.Add(new ClaudeIndexWorkspaceCandidate(
            index.OriginalPath,
            entry.ProjectPath,
            entry.FullPath));
    }

    public void AddTranscript(
        ClaudeProjectLocation project,
        ClaudeTranscriptFile file,
        IReadOnlyList<ClaudeTranscriptRow> rows)
    {
        RecordProject(project);
        AddFileBinding(file);
        if (file.Role == TranscriptFileRole.Main)
        {
            _mainFilePaths.Add(file.Path);
        }

        string? lastPromptId = null;
        InferenceEvidence lastPromptEvidence = InferenceEvidence.Unavailable;

        foreach (ClaudeTranscriptRow row in rows)
        {
            JsonElement root = row.Json;
            string? rowSessionId = _json.String(root, "sessionId", "session_id");
            if (!string.IsNullOrWhiteSpace(rowSessionId) &&
                !string.Equals(rowSessionId, _sessionId, StringComparison.Ordinal))
            {
                _metadata[
                    $"claude.sessionIdConflict.{StableHash(row.Path)}.{row.LineNumber}"] =
                    rowSessionId;
            }

            SessionSourceProvenance provenance = Provenance(row);
            _sources.Add(provenance);

            DateTimeOffset? timestamp = _json.Timestamp(root, "timestamp", "createdAt", "created_at");
            _startedAtUtc = Minimum(_startedAtUtc, timestamp);
            _lastActivityAtUtc = Maximum(_lastActivityAtUtc, timestamp);

            CaptureCommonMetadata(file, root);
            string? cwd = _json.String(root, "cwd");
            if (!string.IsNullOrWhiteSpace(cwd))
            {
                if (file.Role == TranscriptFileRole.Main)
                {
                    int priority = file.IsRecovery ? 0 : 1;
                    if (priority > _mainCwdPriority)
                    {
                        _mainCwd = cwd;
                        _mainCwdPriority = priority;
                    }
                }
                else
                {
                    _subagentCwd ??= cwd;
                }
            }

            string? explicitPromptId = _json.String(root, "promptId", "prompt_id");
            if (!string.IsNullOrWhiteSpace(explicitPromptId))
            {
                lastPromptId = explicitPromptId;
                lastPromptEvidence = InferenceEvidence.Observed;
            }

            string type = _json.String(root, "type") ?? "unknown";
            switch (type)
            {
                case "user":
                    ProcessUserRow(
                        file,
                        row,
                        provenance,
                        timestamp,
                        ref lastPromptId,
                        ref lastPromptEvidence);
                    break;

                case "assistant":
                    ProcessAssistantRow(
                        file,
                        row,
                        provenance,
                        timestamp,
                        explicitPromptId ?? lastPromptId,
                        explicitPromptId is not null
                            ? InferenceEvidence.Observed
                            : lastPromptEvidence);
                    break;

                case "system":
                    ProcessSystemRow(
                        file,
                        row,
                        provenance,
                        timestamp,
                        explicitPromptId ?? lastPromptId,
                        explicitPromptId is not null
                            ? InferenceEvidence.Observed
                            : lastPromptEvidence);
                    break;

                case "attachment":
                    ProcessAttachmentRow(
                        file,
                        row,
                        provenance,
                        timestamp,
                        explicitPromptId ?? lastPromptId,
                        explicitPromptId is not null
                            ? InferenceEvidence.Observed
                            : lastPromptEvidence);
                    break;

                case "mode":
                    if (file.Role == TranscriptFileRole.Main)
                    {
                        SetMode(
                            _json.String(root, "mode"),
                            file.IsRecovery ? 1 : 2);
                    }
                    break;

                case "permission-mode":
                    if (file.Role == TranscriptFileRole.Main)
                    {
                        SetPermissionMode(
                            _json.String(
                                root,
                                "permissionMode",
                                "permission_mode",
                                "mode"),
                            file.IsRecovery ? 1 : 2);
                    }
                    break;

                case "ai-title":
                    string? aiTitle = _json.String(root, "aiTitle", "title");
                    if (file.Role == TranscriptFileRole.Main)
                    {
                        SetTitle(aiTitle, 90);
                    }
                    _metadata["claude.aiTitle"] = aiTitle;
                    break;

                case "custom-title":
                    string? customTitle = _json.String(root, "customTitle", "title");
                    if (file.Role == TranscriptFileRole.Main)
                    {
                        SetTitle(customTitle, 100);
                    }
                    _metadata["claude.customTitle"] = customTitle;
                    break;

                case "agent-name":
                    string? agentName = _json.String(root, "agentName", "name");
                    if (file.Role == TranscriptFileRole.Main)
                    {
                        SetTitle(agentName, 80);
                    }
                    _metadata["claude.agentName"] = agentName;
                    break;

                case "last-prompt":
                    string? lastPrompt = _json.String(root, "lastPrompt", "prompt");
                    _metadata["claude.lastPrompt"] = lastPrompt;
                    if (file.Role == TranscriptFileRole.Main)
                    {
                        SetFirstPrompt(lastPrompt, file.IsRecovery ? 1 : 2);
                    }
                    break;

                case "cost-state":
                    if (file.Role == TranscriptFileRole.Main)
                    {
                        CaptureCostState(
                            file,
                            row,
                            provenance,
                            timestamp,
                            explicitPromptId ?? lastPromptId);
                    }
                    else
                    {
                        StoreRawMetadata(file, row, type);
                    }
                    break;

                case "fork-context-ref":
                    CaptureForkContext(file, root);
                    StoreRawMetadata(file, row, type);
                    break;

                default:
                    StoreRawMetadata(file, row, type);
                    break;
            }
        }
    }

    public void AddAgentMetadata(
        ClaudeProjectLocation project,
        ClaudeTranscriptFile file,
        string rawContent,
        JsonElement json)
    {
        RecordProject(project);
        AddFileBinding(file);

        string agentId =
            _json.String(json, "agentId", "agent_id") ??
            file.AgentId ??
            "unknown";
        SessionSourceProvenance provenance = new(
            SessionSourceKind.ClaudeTranscriptJsonl,
            file.Path,
            "json",
            rawContent,
            RecordId: agentId,
            ParentRecordId: _json.String(json, "toolUseId", "tool_use_id"));
        _sources.Add(provenance);

        foreach (JsonProperty property in json.EnumerateObject())
        {
            _metadata[
                $"claude.subagent.{MetadataKey(agentId)}.{property.Name}"] =
                _json.MetadataText(property.Value);
        }
    }

    public SessionCatalogEntry Build()
    {
        ProjectCostState();

        IReadOnlyList<SessionTurn> turns = BuildTurns();
        DateTimeOffset? startedAt = Minimum(_startedAtUtc, _indexStartedAtUtc);
        DateTimeOffset? lastActivity = Maximum(
            _lastActivityAtUtc,
            _indexLastActivityAtUtc);

        if (startedAt is null)
        {
            startedAt = turns
                .Select(static turn => turn.StartedAtUtc)
                .Where(static value => value is not null)
                .Min();
        }

        if (lastActivity is null)
        {
            lastActivity = turns
                .Select(static turn => turn.EndedAtUtc ?? turn.StartedAtUtc)
                .Where(static value => value is not null)
                .Max();
        }

        if (lastActivity is null && _files.Count > 0)
        {
            lastActivity = _files.Values.Max(static file => file.LastWriteTimeUtc);
        }

        string title =
            NormalizeTitle(_title) ??
            NormalizeTitle(_firstPrompt) ??
            _sessionId;

        return new SessionCatalogEntry
        {
            CatalogSessionId =
                $"{HookProvider.ClaudeCode}:{HookSurface.ClaudeCode}:{_sessionId}",
            NativeSessionId = _sessionId,
            Provider = HookProvider.ClaudeCode,
            Surface = HookSurface.ClaudeCode,
            Workspace = ResolveWorkspace(),
            Title = title,
            StartedAtUtc = startedAt,
            LastActivityAtUtc = lastActivity,
            Model = _model,
            Mode = _permissionMode ?? _mode,
            LifecycleState = SessionLifecycleState.Closed,
            LifecycleEvidence = InferenceEvidence.Unavailable,
            Files = _files.Values
                .OrderBy(static file => file.Role)
                .ThenBy(static file => file.AgentId, StringComparer.Ordinal)
                .ThenBy(static file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Turns = turns,
            Metadata = new Dictionary<string, string?>(_metadata, StringComparer.Ordinal),
            Sources = _sources
                .DistinctBy(
                    static source =>
                        $"{source.Path}|{source.LineNumber}|{source.RecordId}|{source.Format}",
                    StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private void ProcessUserRow(
        ClaudeTranscriptFile file,
        ClaudeTranscriptRow row,
        SessionSourceProvenance provenance,
        DateTimeOffset? timestamp,
        ref string? lastPromptId,
        ref InferenceEvidence lastPromptEvidence)
    {
        JsonElement root = row.Json;
        if (!root.TryGetProperty("message", out JsonElement message) ||
            !message.TryGetProperty("content", out JsonElement content))
        {
            return;
        }

        List<JsonElement> toolResults = [];
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (_json.String(block, "type") == "tool_result")
                {
                    toolResults.Add(block);
                }
            }
        }

        bool isMeta = _json.Boolean(root, "isMeta", "is_meta") ?? false;
        bool explicitlyHuman = string.Equals(
            _json.NestedString(root, "origin", "kind"),
            "human",
            StringComparison.OrdinalIgnoreCase);
        string? prompt = _json.UserText(content);
        bool isHumanPrompt =
            !isMeta &&
            !string.IsNullOrWhiteSpace(prompt) &&
            (toolResults.Count == 0 || explicitlyHuman);

        if (isHumanPrompt)
        {
            string? promptId = _json.String(root, "promptId", "prompt_id");
            InferenceEvidence evidence;
            if (string.IsNullOrWhiteSpace(promptId))
            {
                promptId = $"inferred:{RecordIdentity(row, provenance)}";
                evidence = InferenceEvidence.Derived;
            }
            else
            {
                evidence = InferenceEvidence.Observed;
            }

            lastPromptId = promptId;
            lastPromptEvidence = evidence;
            string normalizedPrompt = NormalizePrompt(prompt) ?? string.Empty;
            SetFirstPrompt(
                normalizedPrompt,
                file.Role == TranscriptFileRole.Main
                    ? file.IsRecovery ? 2 : 3
                    : 0);

            SessionEventRecord promptEvent = BaseEvent(
                file,
                row,
                provenance,
                timestamp,
                promptId,
                "user",
                "prompt") with
            {
                Role = ObservationRole.PromptSubmitted,
                EventKind = CanonicalEventKind.PromptSubmitted,
                Direction = ObservationDirection.Input,
                PromptText = normalizedPrompt,
                Text = normalizedPrompt,
                Mode = _json.String(root, "permissionMode", "permission_mode"),
                Evidence = evidence
            };
            AddEvent(promptEvent, normalizedPrompt, evidence);
        }

        int blockIndex = 0;
        foreach (JsonElement block in toolResults)
        {
            string? promptId =
                _json.String(root, "promptId", "prompt_id") ??
                lastPromptId;
            InferenceEvidence evidence = promptId is null
                ? InferenceEvidence.Unavailable
                : _json.String(root, "promptId", "prompt_id") is not null
                    ? InferenceEvidence.Observed
                    : lastPromptEvidence;
            string? toolCallId = _json.String(block, "tool_use_id", "toolUseId");
            string? toolName = toolCallId is not null &&
                _toolNamesById.TryGetValue(toolCallId, out string? knownName)
                    ? knownName
                    : _json.String(block, "tool_name", "toolName");
            bool isFailure = IsToolFailure(root, block);
            bool isAborted =
                (_json.Boolean(root, "interrupted") ?? false) ||
                (_json.Boolean(block, "interrupted") ?? false) ||
                !string.IsNullOrWhiteSpace(_json.String(root, "toolDenialKind"));

            SessionEventRecord resultEvent = BaseEvent(
                file,
                row,
                provenance,
                timestamp,
                promptId,
                "tool_result",
                $"tool-result:{blockIndex}") with
            {
                Role = isFailure ? ObservationRole.ToolFailure : ObservationRole.ToolSuccess,
                EventKind = isFailure
                    ? CanonicalEventKind.ToolFailed
                    : CanonicalEventKind.ToolSucceeded,
                ToolKind = ToolClassifier.Classify(toolName),
                Direction = ObservationDirection.Output,
                Tone = isFailure ? ObservationTone.Failure : ObservationTone.Normal,
                ToolCallId = toolCallId,
                ToolName = toolName,
                McpServerName = McpParts(toolName).Server,
                McpToolName = McpParts(toolName).Tool,
                Text = ToolResultText(root, block),
                Status = ToolResultStatus(root, block, isFailure),
                IsFailure = isFailure,
                IsAborted = isAborted,
                Evidence = evidence,
                TargetPaths = TargetPaths(root, block)
            };
            AddEvent(resultEvent, null, evidence);
            blockIndex++;
        }
    }

    private void ProcessAssistantRow(
        ClaudeTranscriptFile file,
        ClaudeTranscriptRow row,
        SessionSourceProvenance provenance,
        DateTimeOffset? timestamp,
        string? promptId,
        InferenceEvidence turnEvidence)
    {
        JsonElement root = row.Json;
        if (!root.TryGetProperty("message", out JsonElement message) ||
            message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        _eventModel = _json.String(message, "model");
        int modelPriority = file.Role == TranscriptFileRole.Main
            ? file.IsRecovery ? 1 : 2
            : 0;
        if (!string.IsNullOrWhiteSpace(_eventModel) &&
            modelPriority >= _modelPriority)
        {
            _model = _eventModel;
            _modelPriority = modelPriority;
            _metadata["claude.model"] = _model;
        }

        string? assistantStepId = _json.String(message, "id");
        IReadOnlyList<UsageMeasurement> usage = ReadUsage(message, provenance, assistantStepId);
        if (!message.TryGetProperty("content", out JsonElement content))
        {
            if (usage.Count > 0)
            {
                SessionEventRecord usageEvent = BaseEvent(
                    file,
                    row,
                    provenance,
                    timestamp,
                    promptId,
                    "assistant",
                    "usage") with
                {
                    Role = ObservationRole.Message,
                    AssistantStepId = assistantStepId,
                    Model = _eventModel,
                    Evidence = turnEvidence,
                    UsageMeasurements = usage,
                    ExcludeFromSummary = true
                };
                AddEvent(usageEvent, null, turnEvidence);
            }

            return;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            string? text = content.GetString();
            SessionEventRecord textEvent = BaseEvent(
                file,
                row,
                provenance,
                timestamp,
                promptId,
                "text",
                "text:0") with
            {
                Role = ObservationRole.AgentResponse,
                EventKind = CanonicalEventKind.AssistantMessage,
                Text = text,
                AssistantStepId = assistantStepId,
                Model = _eventModel,
                Evidence = turnEvidence,
                UsageMeasurements = usage
            };
            AddEvent(textEvent, null, turnEvidence);
            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        JsonElement[] blocks = content.EnumerateArray().ToArray();
        int toolUseCount = blocks.Count(block => _json.String(block, "type") == "tool_use");
        string? parallelGroupId = toolUseCount > 1
            ? $"{_sessionId}:{file.AgentId ?? "main"}:" +
              $"{_json.String(root, "uuid") ?? assistantStepId ?? StableHash(row.RawContent)}:tools"
            : null;
        bool usageAttached = false;

        for (int index = 0; index < blocks.Length; index++)
        {
            JsonElement block = blocks[index];
            string blockType = _json.String(block, "type") ?? "assistant-content";
            IReadOnlyList<UsageMeasurement> eventUsage =
                !usageAttached && usage.Count > 0 ? usage : [];
            if (eventUsage.Count > 0)
            {
                usageAttached = true;
            }

            SessionEventRecord? projected = blockType switch
            {
                "thinking" or "redacted_thinking" => ThinkingEvent(
                    file,
                    row,
                    provenance,
                    timestamp,
                    promptId,
                    turnEvidence,
                    message,
                    block,
                    index,
                    assistantStepId,
                    eventUsage),

                "text" => TextEvent(
                    file,
                    row,
                    provenance,
                    timestamp,
                    promptId,
                    turnEvidence,
                    message,
                    block,
                    index,
                    assistantStepId,
                    eventUsage),

                "tool_use" => ToolUseEvent(
                    file,
                    row,
                    provenance,
                    timestamp,
                    promptId,
                    turnEvidence,
                    message,
                    block,
                    index,
                    assistantStepId,
                    eventUsage,
                    toolUseCount > 1,
                    parallelGroupId),

                _ => UnknownAssistantEvent(
                    file,
                    row,
                    provenance,
                    timestamp,
                    promptId,
                    turnEvidence,
                    block,
                    index,
                    assistantStepId,
                    eventUsage)
            };

            AddEvent(projected, null, turnEvidence);
        }
    }

    private SessionEventRecord ThinkingEvent(
        ClaudeTranscriptFile file,
        ClaudeTranscriptRow row,
        SessionSourceProvenance provenance,
        DateTimeOffset? timestamp,
        string? promptId,
        InferenceEvidence turnEvidence,
        JsonElement message,
        JsonElement block,
        int index,
        string? assistantStepId,
        IReadOnlyList<UsageMeasurement> usage)
    {
        string? thinking = _json.String(block, "thinking", "text");
        string? signature = _json.String(block, "signature");
        bool opaque =
            string.IsNullOrEmpty(thinking) &&
            (!string.IsNullOrEmpty(signature) ||
             _json.String(block, "type") == "redacted_thinking");

        return BaseEvent(
            file,
            row,
            provenance,
            timestamp,
            promptId,
            "thinking",
            $"thinking:{index}") with
        {
            Role = ObservationRole.AgentThought,
            EventKind = CanonicalEventKind.AssistantThought,
            Tone = ObservationTone.Thought,
            Text = opaque ? null : thinking,
            Model = _eventModel,
            Status = opaque ? "opaque" : null,
            AssistantStepId = assistantStepId,
            Evidence = opaque ? InferenceEvidence.Opaque : turnEvidence,
            UsageMeasurements = usage,
            Skill = SkillAttribution(row.Json, message, block, row.Path)
        };
    }

    private SessionEventRecord TextEvent(
        ClaudeTranscriptFile file,
        ClaudeTranscriptRow row,
        SessionSourceProvenance provenance,
        DateTimeOffset? timestamp,
        string? promptId,
        InferenceEvidence turnEvidence,
        JsonElement message,
        JsonElement block,
        int index,
        string? assistantStepId,
        IReadOnlyList<UsageMeasurement> usage) =>
        BaseEvent(
            file,
            row,
            provenance,
            timestamp,
            promptId,
            "text",
            $"text:{index}") with
        {
            Role = ObservationRole.AgentResponse,
            EventKind = CanonicalEventKind.AssistantMessage,
            Text = _json.String(block, "text"),
            Model = _eventModel,
            AssistantStepId = assistantStepId,
            Evidence = turnEvidence,
            UsageMeasurements = usage,
            Skill = SkillAttribution(row.Json, message, block, row.Path)
        };

    private SessionEventRecord ToolUseEvent(
        ClaudeTranscriptFile file,
        ClaudeTranscriptRow row,
        SessionSourceProvenance provenance,
        DateTimeOffset? timestamp,
        string? promptId,
        InferenceEvidence turnEvidence,
        JsonElement message,
        JsonElement block,
        int index,
        string? assistantStepId,
        IReadOnlyList<UsageMeasurement> usage,
        bool isParallelCandidate,
        string? parallelGroupId)
    {
        string nativeToolName = _json.String(block, "name") ?? "tool_use";
        string? toolCallId = _json.String(block, "id");
        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            _toolNamesById[toolCallId] = nativeToolName;
        }

        (string? mcpServer, string? mcpTool) = McpParts(nativeToolName);
        mcpServer =
            _json.String(block, "attributionMcpServer") ??
            _json.String(message, "attributionMcpServer") ??
            _json.String(row.Json, "attributionMcpServer") ??
            mcpServer;
        mcpTool =
            _json.String(block, "attributionMcpTool") ??
            _json.String(message, "attributionMcpTool") ??
            _json.String(row.Json, "attributionMcpTool") ??
            mcpTool;

        JsonElement input = block.TryGetProperty("input", out JsonElement value)
            ? value
            : default;
        string? agentType = input.ValueKind == JsonValueKind.Object
            ? _json.String(input, "subagent_type", "agent_type", "type")
            : null;
        string? task = input.ValueKind == JsonValueKind.Object
            ? _json.String(input, "prompt", "description", "task")
            : null;

        return BaseEvent(
            file,
            row,
            provenance,
            timestamp,
            promptId,
            nativeToolName,
            $"tool-use:{index}") with
        {
            Role = ObservationRole.ToolRequest,
            EventKind = CanonicalEventKind.ToolRequested,
            ToolKind = ToolClassifier.Classify(nativeToolName),
            ToolCallId = toolCallId,
            ToolName = nativeToolName,
            McpServerName = mcpServer,
            McpToolName = mcpTool,
            Text = input.ValueKind == JsonValueKind.Undefined
                ? null
                : input.GetRawText(),
            Model = _eventModel,
            AssistantStepId = assistantStepId,
            ParallelGroupId = parallelGroupId,
            AgentType = agentType,
            Task = task,
            IsParallelCandidate = isParallelCandidate,
            ExcludeFromSummary = true,
            Evidence = turnEvidence,
            TargetPaths = TargetPaths(block),
            UsageMeasurements = usage,
            Skill = SkillFromToolUse(nativeToolName, input, row.Json, message, block, row.Path)
        };
    }

    private SessionEventRecord UnknownAssistantEvent(
        ClaudeTranscriptFile file,
        ClaudeTranscriptRow row,
        SessionSourceProvenance provenance,
        DateTimeOffset? timestamp,
        string? promptId,
        InferenceEvidence turnEvidence,
        JsonElement block,
        int index,
        string? assistantStepId,
        IReadOnlyList<UsageMeasurement> usage)
    {
        string nativeName = _json.String(block, "type") ?? "assistant-content";
        return BaseEvent(
            file,
            row,
            provenance,
            timestamp,
            promptId,
            nativeName,
            $"content:{index}") with
        {
            Role = ObservationRole.Message,
            Text = _json.ContentText(block),
            Model = _eventModel,
            AssistantStepId = assistantStepId,
            Evidence = turnEvidence,
            UsageMeasurements = usage,
            ExcludeFromSummary = true
        };
    }

    private void ProcessSystemRow(
        ClaudeTranscriptFile file,
        ClaudeTranscriptRow row,
        SessionSourceProvenance provenance,
        DateTimeOffset? timestamp,
        string? promptId,
        InferenceEvidence turnEvidence)
    {
        JsonElement root = row.Json;
        string subtype = _json.String(root, "subtype") ?? "system";
        if (subtype == "turn_duration")
        {
            SessionEventRecord durationEvent = BaseEvent(
                file,
                row,
                provenance,
                timestamp,
                promptId,
                subtype,
                subtype) with
            {
                Role = ObservationRole.TurnStop,
                EventKind = CanonicalEventKind.TurnCompleted,
                Direction = ObservationDirection.Output,
                Tone = ObservationTone.Stop,
                DurationMs = _json.Double(root, "durationMs", "duration_ms"),
                Status = "completed",
                Evidence = turnEvidence
            };
            AddEvent(durationEvent, null, turnEvidence);
            return;
        }

        if (subtype == "compact_boundary")
        {
            double? duration = root.TryGetProperty(
                "compactMetadata",
                out JsonElement compactMetadata)
                    ? _json.Double(compactMetadata, "durationMs", "duration_ms")
                    : null;
            SessionEventRecord compactEvent = BaseEvent(
                file,
                row,
                provenance,
                timestamp,
                promptId,
                subtype,
                subtype) with
            {
                Role = ObservationRole.CompactionEnd,
                EventKind = CanonicalEventKind.CompactionCompleted,
                Direction = ObservationDirection.Output,
                Tone = ObservationTone.Compaction,
                Text = _json.String(root, "content"),
                DurationMs = duration,
                Evidence = turnEvidence
            };
            AddEvent(compactEvent, null, turnEvidence);
            return;
        }

        StoreRawMetadata(file, row, $"system.{subtype}");
    }

    private void ProcessAttachmentRow(
        ClaudeTranscriptFile file,
        ClaudeTranscriptRow row,
        SessionSourceProvenance provenance,
        DateTimeOffset? timestamp,
        string? promptId,
        InferenceEvidence turnEvidence)
    {
        if (!row.Json.TryGetProperty("attachment", out JsonElement attachment) ||
            attachment.ValueKind != JsonValueKind.Object)
        {
            StoreRawMetadata(file, row, "attachment");
            return;
        }

        string attachmentType = _json.String(attachment, "type") ?? "attachment";
        if (attachmentType is "plan_mode" or "plan_mode_exit")
        {
            CapturePlanModeAttachment(
                attachment,
                provenance,
                timestamp,
                promptId,
                attachmentType);
            StoreRawMetadata(file, row, $"attachment.{attachmentType}");
            return;
        }

        if (attachmentType is not ("skill_listing" or "skill_activated"))
        {
            StoreRawMetadata(file, row, $"attachment.{attachmentType}");
            return;
        }

        SkillEvidenceStage stage = attachmentType == "skill_activated"
            ? SkillEvidenceStage.Invoked
            : SkillEvidenceStage.Available;
        IReadOnlyList<string> skills = SkillNames(attachment);
        int index = 0;
        foreach (string skill in skills)
        {
            SessionEventRecord skillEvent = BaseEvent(
                file,
                row,
                provenance,
                timestamp,
                promptId,
                attachmentType,
                $"skill:{index}:{skill}") with
            {
                Role = ObservationRole.Message,
                Text = skill,
                Evidence = turnEvidence,
                ExcludeFromSummary = true,
                Skill = new SkillEvidence(
                    skill,
                    stage,
                    InferenceEvidence.Observed,
                    row.Path)
            };
            AddEvent(skillEvent, null, turnEvidence);
            index++;
        }

        StoreRawMetadata(file, row, $"attachment.{attachmentType}");
    }

    // A plan_mode attachment names the plan file the session is actively
    // planning into. It carries the plan file path, session id, and prompt id,
    // which is the strongest binding of a Claude plan to a session and turn.
    private void CapturePlanModeAttachment(
        JsonElement attachment,
        SessionSourceProvenance provenance,
        DateTimeOffset? timestamp,
        string? promptId,
        string attachmentType)
    {
        string? planFilePath = _json.String(
            attachment,
            "planFilePath",
            "plan_file_path",
            "planPath",
            "plan_path");
        if (string.IsNullOrWhiteSpace(planFilePath))
        {
            return;
        }

        string idSuffix = attachmentType == "plan_mode_exit" ? "exit" : "enter";
        _planActivities.Add(new Plans.SessionPlanActivity
        {
            Id = $"claude-plan-activity:{_sessionId}:{promptId ?? "session"}:" +
                $"planmode:{idSuffix}:{_order++}",
            Kind = Plans.SessionPlanActivityKind.Referenced,
            Provider = HookProvider.ClaudeCode,
            CatalogSessionId = CatalogSessionId,
            NativeSessionId = _sessionId,
            TurnId = promptId,
            SourceEventId = null,
            Order = _order,
            TimestampUtc = timestamp,
            Locator = new Plans.SessionPlanLocator(Path: planFilePath),
            Content = null,
            IsSuccessful = true,
            HasOpaqueResult = false,
            EstablishesOwnership = true,
            Evidence = InferenceEvidence.Observed,
            Provenance = provenance
        });
    }

    private void CaptureCostState(
        ClaudeTranscriptFile file,
        ClaudeTranscriptRow row,
        SessionSourceProvenance provenance,
        DateTimeOffset? timestamp,
        string? promptId)
    {
        DateTimeOffset? start = _json.UnixTimestamp(
            row.Json,
            "startTime",
            "start_time");
        double? duration = _json.Double(row.Json, "totalDuration", "total_duration");
        DateTimeOffset? derivedTimestamp = timestamp;
        if (derivedTimestamp is null &&
            start is not null &&
            duration is double durationMs &&
            double.IsFinite(durationMs))
        {
            try
            {
                derivedTimestamp = start.Value.AddMilliseconds(durationMs);
            }
            catch (ArgumentOutOfRangeException)
            {
                derivedTimestamp = null;
            }
        }

        ClaudeCostStateCandidate candidate = new(
            row,
            provenance,
            promptId,
            start,
            derivedTimestamp,
            file.IsRecovery ? 0 : 1,
            _order++);
        if (_costState is null || candidate.IsNewerThan(_costState))
        {
            _costState = candidate;
        }
    }

    private void ProjectCostState()
    {
        if (_costStateProjected || _costState is null)
        {
            return;
        }

        _costStateProjected = true;
        ClaudeCostStateCandidate cost = _costState;
        JsonElement root = cost.Row.Json;
        IReadOnlyList<UsageMeasurement> usage = CostMeasurements(root, cost.Provenance);
        _metadata["claude.costState"] = cost.Row.RawContent;

        DateTimeOffset? start = cost.StartedAtUtc;
        DateTimeOffset? end = cost.TimestampUtc;
        _startedAtUtc = Minimum(_startedAtUtc, start);
        _lastActivityAtUtc = Maximum(_lastActivityAtUtc, end);

        ClaudeTranscriptFile syntheticFile = new()
        {
            Path = cost.Row.Path,
            Role = TranscriptFileRole.Main,
            Length = 0,
            LastWriteTimeUtc = default
        };
        SessionEventRecord costEvent = BaseEvent(
            syntheticFile,
            cost.Row,
            cost.Provenance,
            end,
            cost.PromptId,
            "cost-state",
            "latest") with
        {
            Role = ObservationRole.Message,
            Status = "latest-snapshot",
            DurationMs = _json.Double(root, "totalDuration", "total_duration"),
            Evidence = InferenceEvidence.Observed,
            UsageMeasurements = usage,
            ExcludeFromSummary = true
        };
        AddEvent(costEvent, null, InferenceEvidence.Observed);
    }

    private IReadOnlyList<UsageMeasurement> ReadUsage(
        JsonElement message,
        SessionSourceProvenance provenance,
        string? assistantStepId)
    {
        if (!message.TryGetProperty("usage", out JsonElement usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        string sourceRecordId =
            assistantStepId ??
            provenance.RecordId ??
            $"{provenance.Path}:{provenance.LineNumber}";
        string snapshotKey = $"{sourceRecordId}|{usage.GetRawText()}";
        if (!_usageSnapshots.Add(snapshotKey))
        {
            return [];
        }

        List<UsageMeasurement> measurements = [];
        AddUsage(
            measurements,
            usage,
            "input_tokens",
            UsageScope.Turn,
            UsageBehavior.CumulativeSnapshot,
            sourceRecordId);
        AddUsage(
            measurements,
            usage,
            "cache_read_input_tokens",
            UsageScope.Turn,
            UsageBehavior.CumulativeSnapshot,
            sourceRecordId);
        AddUsage(
            measurements,
            usage,
            "cache_creation_input_tokens",
            UsageScope.Turn,
            UsageBehavior.CumulativeSnapshot,
            sourceRecordId);
        AddUsage(
            measurements,
            usage,
            "output_tokens",
            UsageScope.Turn,
            UsageBehavior.Delta,
            sourceRecordId);

        if (usage.TryGetProperty(
                "output_tokens_details",
                out JsonElement outputDetails) &&
            outputDetails.ValueKind == JsonValueKind.Object)
        {
            AddUsage(
                measurements,
                outputDetails,
                "thinking_tokens",
                UsageScope.Turn,
                UsageBehavior.Delta,
                sourceRecordId);
        }

        if (usage.TryGetProperty("server_tool_use", out JsonElement serverToolUse) &&
            serverToolUse.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in serverToolUse.EnumerateObject())
            {
                AddUsage(
                    measurements,
                    serverToolUse,
                    property.Name,
                    UsageScope.Turn,
                    UsageBehavior.Delta,
                    sourceRecordId,
                    unit: "requests");
            }
        }

        return measurements;
    }

    private IReadOnlyList<UsageMeasurement> CostMeasurements(
        JsonElement root,
        SessionSourceProvenance provenance)
    {
        string sourceRecordId =
            provenance.RecordId ??
            $"cost-state:{provenance.Path}:{provenance.LineNumber}";
        List<UsageMeasurement> measurements = [];

        AddUsage(
            measurements,
            root,
            "totalAPIDuration",
            UsageScope.Session,
            UsageBehavior.FinalSnapshot,
            sourceRecordId,
            "milliseconds");
        AddUsage(
            measurements,
            root,
            "totalAPIDurationWithoutRetries",
            UsageScope.Session,
            UsageBehavior.FinalSnapshot,
            sourceRecordId,
            "milliseconds");
        AddUsage(
            measurements,
            root,
            "totalToolDuration",
            UsageScope.Session,
            UsageBehavior.FinalSnapshot,
            sourceRecordId,
            "milliseconds");
        AddUsage(
            measurements,
            root,
            "totalDuration",
            UsageScope.Session,
            UsageBehavior.FinalSnapshot,
            sourceRecordId,
            "milliseconds");
        AddUsage(
            measurements,
            root,
            "totalLinesAdded",
            UsageScope.Session,
            UsageBehavior.FinalSnapshot,
            sourceRecordId,
            "lines");
        AddUsage(
            measurements,
            root,
            "totalLinesRemoved",
            UsageScope.Session,
            UsageBehavior.FinalSnapshot,
            sourceRecordId,
            "lines");

        if (_json.Double(root, "totalCostUSD", "total_cost_usd") is double cost)
        {
            double microDollars = Math.Round(
                cost * 1_000_000d,
                MidpointRounding.AwayFromZero);
            if (double.IsFinite(microDollars) &&
                microDollars >= long.MinValue &&
                microDollars <= long.MaxValue)
            {
                measurements.Add(new UsageMeasurement(
                    "total_cost_usd",
                    (long)microDollars,
                    "micro-usd",
                    UsageScope.Session,
                    UsageBehavior.FinalSnapshot,
                    sourceRecordId));
            }
        }

        if (root.TryGetProperty("modelUsage", out JsonElement modelUsage) &&
            modelUsage.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty model in modelUsage.EnumerateObject())
            {
                if (model.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                AddModelUsage(measurements, model.Name, model.Value, sourceRecordId);
            }
        }

        return measurements;
    }

    private void AddModelUsage(
        List<UsageMeasurement> measurements,
        string model,
        JsonElement usage,
        string sourceRecordId)
    {
        AddUsage(
            measurements,
            usage,
            "inputTokens",
            UsageScope.Session,
            UsageBehavior.FinalSnapshot,
            sourceRecordId,
            namePrefix: $"{model}.");
        AddUsage(
            measurements,
            usage,
            "outputTokens",
            UsageScope.Session,
            UsageBehavior.FinalSnapshot,
            sourceRecordId,
            namePrefix: $"{model}.");
        AddUsage(
            measurements,
            usage,
            "cacheReadInputTokens",
            UsageScope.Session,
            UsageBehavior.FinalSnapshot,
            sourceRecordId,
            namePrefix: $"{model}.");
        AddUsage(
            measurements,
            usage,
            "cacheCreationInputTokens",
            UsageScope.Session,
            UsageBehavior.FinalSnapshot,
            sourceRecordId,
            namePrefix: $"{model}.");
        AddUsage(
            measurements,
            usage,
            "webSearchRequests",
            UsageScope.Session,
            UsageBehavior.FinalSnapshot,
            sourceRecordId,
            "requests",
            $"{model}.");
    }

    private void AddUsage(
        List<UsageMeasurement> measurements,
        JsonElement container,
        string nativeName,
        UsageScope scope,
        UsageBehavior behavior,
        string sourceRecordId,
        string unit = "tokens",
        string namePrefix = "")
    {
        if (_json.Long(container, nativeName) is long value)
        {
            measurements.Add(new UsageMeasurement(
                namePrefix + nativeName,
                value,
                unit,
                scope,
                behavior,
                sourceRecordId));
        }
    }

    private SessionEventRecord BaseEvent(
        ClaudeTranscriptFile file,
        ClaudeTranscriptRow row,
        SessionSourceProvenance provenance,
        DateTimeOffset? timestamp,
        string? turnId,
        string nativeName,
        string suffix) =>
        new()
        {
            Id = $"{_sessionId}:{file.AgentId ?? "main"}:" +
                $"{RecordIdentity(row, provenance)}:{MetadataKey(suffix)}",
            NativeName = nativeName,
            Provider = HookProvider.ClaudeCode,
            Surface = HookSurface.ClaudeCode,
            Role = ObservationRole.Generic,
            TimestampUtc = timestamp,
            Order = _order++,
            TurnId = turnId,
            ParentId = provenance.ParentRecordId,
            AgentId = file.AgentId ?? _json.String(row.Json, "agentId", "agent_id"),
            AgentType = _json.String(row.Json, "attributionAgent", "agentType", "agent_type"),
            Provenance = provenance
        };

    private void AddEvent(
        SessionEventRecord eventRecord,
        string? prompt,
        InferenceEvidence turnEvidence)
    {
        string turnId = eventRecord.TurnId ??
            (eventRecord.AgentId is string agentId
                ? $"agent:{agentId}:unscoped"
                : $"session:{_sessionId}:unscoped");
        if (eventRecord.TurnId is null)
        {
            eventRecord = eventRecord with { TurnId = turnId };
        }

        if (_eventTurns.TryGetValue(eventRecord.Id, out string? previousTurn) &&
            _turns.TryGetValue(previousTurn, out ClaudeTurnAccumulator? previous))
        {
            previous.Remove(eventRecord.Id);
        }

        if (!_turns.TryGetValue(turnId, out ClaudeTurnAccumulator? turn))
        {
            turn = new ClaudeTurnAccumulator(
                turnId,
                eventRecord.Order,
                turnEvidence);
            _turns.Add(turnId, turn);
        }

        turn.Add(eventRecord, prompt, turnEvidence);
        _eventTurns[eventRecord.Id] = turnId;
    }

    private IReadOnlyList<SessionTurn> BuildTurns()
    {
        ClaudeTurnAccumulator[] ordered = _turns.Values
            .Where(static turn => turn.EventCount > 0)
            .OrderBy(static turn => turn.StartedAtUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(static turn => turn.FirstOrder)
            .ToArray();

        List<SessionTurn> turns = new(ordered.Length);
        for (int index = 0; index < ordered.Length; index++)
        {
            turns.Add(ordered[index].Build(index + 1));
        }

        return turns;
    }

    private WorkspaceContext ResolveWorkspace()
    {
        foreach (ClaudeIndexWorkspaceCandidate candidate in _indexWorkspaces)
        {
            if (!IndexCandidateMatchesSession(candidate))
            {
                continue;
            }

            string? path = ExistingDirectory(candidate.OriginalPath) ??
                ExistingDirectory(candidate.ProjectPath);
            if (path is not null)
            {
                return _workspaceNormalizer.FromPath(path);
            }
        }

        if (_encodedWorkspacePath is not null)
        {
            return _workspaceNormalizer.FromPath(_encodedWorkspacePath);
        }

        if (!string.IsNullOrWhiteSpace(_mainCwd))
        {
            return _workspaceNormalizer.FromPath(_mainCwd);
        }

        if (!string.IsNullOrWhiteSpace(_subagentCwd))
        {
            return _workspaceNormalizer.FromPath(_subagentCwd);
        }

        return WorkspaceContext.Unknown;
    }

    private bool IndexCandidateMatchesSession(ClaudeIndexWorkspaceCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.FullPath))
        {
            return true;
        }

        string? fullPath = NormalizePath(candidate.FullPath);
        if (fullPath is null)
        {
            return false;
        }

        if (_mainFilePaths.Any(path => PathsEqual(path, fullPath)))
        {
            return true;
        }

        string fileName = Path.GetFileName(fullPath);
        return fileName.StartsWith(_sessionId, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(fullPath);
    }

    private void RecordProject(ClaudeProjectLocation project)
    {
        _metadata.TryAdd("claude.projectDirectory", project.DirectoryPath);
        _metadata.TryAdd("claude.encodedProject", project.EncodedName);
        if (_encodedWorkspacePath is null)
        {
            _encodedWorkspacePath =
                _workspaceNormalizer.DecodeClaudeProjectName(project.EncodedName);
        }
    }

    private void AddFileBinding(ClaudeTranscriptFile file)
    {
        _files[file.Path] = new SessionFileBinding(
            file.Path,
            SessionSourceKind.ClaudeTranscriptJsonl,
            DialectIds.ClaudeTranscript,
            file.Role,
            file.LastWriteTimeUtc,
            file.Length,
            file.AgentId,
            file.Role == TranscriptFileRole.Subagent
                ? file.ParentSessionId ?? _sessionId
                : null);

        if (file.IsRecovery)
        {
            int recoveryCount = 0;
            if (_metadata.TryGetValue(
                    "claude.recoverySourceCount",
                    out string? existing))
            {
                int.TryParse(
                    existing,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out recoveryCount);
            }

            _metadata["claude.recoverySourceCount"] =
                (recoveryCount + 1).ToString(CultureInfo.InvariantCulture);
        }
    }

    private SessionSourceProvenance Provenance(ClaudeTranscriptRow row)
    {
        JsonElement root = row.Json;
        string? recordId =
            _json.String(root, "uuid") ??
            _json.NestedString(root, "message", "id");
        return new SessionSourceProvenance(
            SessionSourceKind.ClaudeTranscriptJsonl,
            row.Path,
            "jsonl",
            row.RawContent,
            row.LineNumber,
            row.ByteOffset,
            recordId,
            _json.String(root, "parentUuid", "parent_uuid"),
            ContractVersion: _json.String(root, "version"));
    }

    private void CaptureCommonMetadata(
        ClaudeTranscriptFile file,
        JsonElement root)
    {
        CaptureLatest(root, "version", "claude.version");
        CaptureLatest(root, "gitBranch", "claude.gitBranch");
        CaptureLatest(root, "entrypoint", "claude.entrypoint");
        CaptureLatest(root, "slug", "claude.slug");

        if (file.Role != TranscriptFileRole.Main)
        {
            return;
        }

        int priority = file.IsRecovery ? 1 : 2;
        string? rowMode = _json.String(root, "mode");
        SetMode(rowMode, priority);
        string? permissionMode = _json.String(root, "permissionMode", "permission_mode");
        SetPermissionMode(permissionMode, priority);
    }

    private void CaptureLatest(
        JsonElement root,
        string propertyName,
        string metadataName)
    {
        string? value = _json.String(root, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            _metadata[metadataName] = value;
        }
    }

    private void CaptureForkContext(ClaudeTranscriptFile file, JsonElement root)
    {
        string agentId =
            _json.String(root, "agentId", "agent_id") ??
            file.AgentId ??
            "unknown";
        _metadata[$"claude.subagent.{MetadataKey(agentId)}.parentSessionId"] =
            _json.String(root, "parentSessionId", "parent_session_id");
        _metadata[$"claude.subagent.{MetadataKey(agentId)}.parentLastUuid"] =
            _json.String(root, "parentLastUuid", "parent_last_uuid");
    }

    private void StoreRawMetadata(
        ClaudeTranscriptFile file,
        ClaudeTranscriptRow row,
        string type)
    {
        string source = file.AgentId ?? Path.GetFileName(file.Path);
        string key =
            $"claude.raw.{MetadataKey(source)}.{row.LineNumber}.{MetadataKey(type)}";
        _metadata[key] = row.RawContent;
    }

    // The "Skill" tool_use is the authoritative record that a skill actually
    // ran: its input names the activated skill (input.skill == "<name>"). This
    // does not depend on a SKILL.md read, which Claude does not always emit.
    // Any other tool falls back to attribution fields carried on the record.
    private SkillEvidence? SkillFromToolUse(
        string nativeToolName,
        JsonElement input,
        JsonElement row,
        JsonElement message,
        JsonElement block,
        string sourcePath)
    {
        if (string.Equals(nativeToolName, "Skill", StringComparison.OrdinalIgnoreCase) &&
            input.ValueKind == JsonValueKind.Object)
        {
            string? invoked = _json.String(input, "skill", "skillName", "skill_name");
            if (!string.IsNullOrWhiteSpace(invoked))
            {
                return new SkillEvidence(
                    invoked.Trim(),
                    SkillEvidenceStage.Invoked,
                    InferenceEvidence.Observed,
                    sourcePath);
            }
        }

        return SkillAttribution(row, message, block, sourcePath);
    }

    private SkillEvidence? SkillAttribution(
        JsonElement row,
        JsonElement message,
        JsonElement block,
        string sourcePath)
    {
        string? name =
            AttributionName(block, "attributionSkill") ??
            AttributionName(message, "attributionSkill") ??
            AttributionName(row, "attributionSkill") ??
            AttributionName(block, "skill") ??
            AttributionName(row, "skill");
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new SkillEvidence(
                name,
                SkillEvidenceStage.Invoked,
                InferenceEvidence.Observed,
                sourcePath);
    }

    private string? AttributionName(JsonElement container, string propertyName)
    {
        if (!container.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => _json.String(value, "name", "skillName", "skill_name"),
            _ => null
        };
    }

    private IReadOnlyList<string> SkillNames(JsonElement attachment)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string propertyName in new[] { "names", "skills", "skillNames" })
        {
            if (!attachment.TryGetProperty(propertyName, out JsonElement value) ||
                value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement item in value.EnumerateArray())
            {
                string? name = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object => _json.String(item, "name", "skillName"),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name.Trim());
                }
            }
        }

        string? content = _json.String(attachment, "content");
        if (!string.IsNullOrWhiteSpace(content))
        {
            foreach (string line in content.Split(
                         ['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith("- ", StringComparison.Ordinal))
                {
                    continue;
                }

                string name = line[2..];
                int colon = name.IndexOf(':');
                if (colon >= 0)
                {
                    name = name[..colon];
                }

                name = name.Trim();
                if (name.Length > 0)
                {
                    names.Add(name);
                }
            }
        }

        return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private bool IsToolFailure(JsonElement row, JsonElement block)
    {
        if ((_json.Boolean(block, "is_error", "isError") ?? false) ||
            (_json.Boolean(row, "is_error", "isError") ?? false) ||
            !string.IsNullOrWhiteSpace(_json.String(row, "toolDenialKind")))
        {
            return true;
        }

        if (!row.TryGetProperty("toolUseResult", out JsonElement result))
        {
            return false;
        }

        if (result.ValueKind == JsonValueKind.String)
        {
            string? text = result.GetString();
            return text?.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) == true;
        }

        return result.ValueKind == JsonValueKind.Object &&
            ((_json.Boolean(result, "interrupted", "is_error", "isError") ?? false) ||
             _json.Boolean(result, "success") == false ||
             result.TryGetProperty("error", out _));
    }

    private string? ToolResultText(JsonElement row, JsonElement block)
    {
        string? text = _json.ContentText(block);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return row.TryGetProperty("toolUseResult", out JsonElement result)
            ? _json.ContentText(result) ?? result.GetRawText()
            : null;
    }

    private string ToolResultStatus(
        JsonElement row,
        JsonElement block,
        bool isFailure)
    {
        if (!string.IsNullOrWhiteSpace(_json.String(row, "toolDenialKind")))
        {
            return "denied";
        }

        bool interrupted =
            (_json.Boolean(block, "interrupted") ?? false) ||
            (row.TryGetProperty("toolUseResult", out JsonElement result) &&
             result.ValueKind == JsonValueKind.Object &&
             (_json.Boolean(result, "interrupted") ?? false));
        return interrupted ? "interrupted" : isFailure ? "failure" : "success";
    }

    private IReadOnlyList<string> TargetPaths(params JsonElement[] containers)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement container in containers)
        {
            CollectTargetPaths(container, paths, depth: 0);
        }

        return paths.ToArray();
    }

    private void CollectTargetPaths(
        JsonElement element,
        HashSet<string> paths,
        int depth)
    {
        if (depth > 4)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                CollectTargetPaths(item, paths, depth + 1);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String &&
                IsPathProperty(property.Name) &&
                property.Value.GetString() is string path &&
                !string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path);
            }
            else if (property.Value.ValueKind is
                     JsonValueKind.Object or JsonValueKind.Array)
            {
                CollectTargetPaths(property.Value, paths, depth + 1);
            }
        }
    }

    private static bool IsPathProperty(string name) =>
        name.Equals("path", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("file_path", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("filePath", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("notebook_path", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("notebookPath", StringComparison.OrdinalIgnoreCase);

    private static (string? Server, string? Tool) McpParts(string? nativeToolName)
    {
        if (string.IsNullOrWhiteSpace(nativeToolName) ||
            !nativeToolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return (null, null);
        }

        string remainder = nativeToolName[5..];
        int separator = remainder.IndexOf("__", StringComparison.Ordinal);
        return separator <= 0
            ? (remainder, null)
            : (remainder[..separator], remainder[(separator + 2)..]);
    }

    private void SetTitle(string? value, int priority)
    {
        string? normalized = NormalizeTitle(value);
        if (normalized is null || priority < _titlePriority)
        {
            return;
        }

        _title = normalized;
        _titlePriority = priority;
    }

    private void SetFirstPrompt(string? value, int priority)
    {
        string? normalized = NormalizePrompt(value);
        if (normalized is null || priority <= _firstPromptPriority)
        {
            return;
        }

        _firstPrompt = normalized;
        _firstPromptPriority = priority;
    }

    private void SetMode(string? value, int priority)
    {
        if (string.IsNullOrWhiteSpace(value) || priority < _modePriority)
        {
            return;
        }

        _mode = value;
        _modePriority = priority;
        _metadata["claude.mode"] = value;
    }

    private void SetPermissionMode(string? value, int priority)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            priority < _permissionModePriority)
        {
            return;
        }

        _permissionMode = value;
        _permissionModePriority = priority;
        _metadata["claude.permissionMode"] = value;
    }

    private static string? NormalizePrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        return prompt.Trim();
    }

    private static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string oneLine = title.ReplaceLineEndings(" ").Trim();
        const int maximumLength = 160;
        return oneLine.Length <= maximumLength
            ? oneLine
            : oneLine[..maximumLength].TrimEnd() + "\u2026";
    }

    private static string RecordIdentity(
        ClaudeTranscriptRow row,
        SessionSourceProvenance provenance) =>
        provenance.RecordId ?? StableHash(row.RawContent);

    private static string MetadataKey(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            builder.Append(
                char.IsLetterOrDigit(character) || character is '-' or '_'
                    ? character
                    : '_');
        }

        return builder.ToString();
    }

    private static string StableHash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static DateTimeOffset? Minimum(
        DateTimeOffset? first,
        DateTimeOffset? second) =>
        first is null ? second :
        second is null ? first :
        first <= second ? first : second;

    private static DateTimeOffset? Maximum(
        DateTimeOffset? first,
        DateTimeOffset? second) =>
        first is null ? second :
        second is null ? first :
        first >= second ? first : second;

    private static string? ExistingDirectory(string? value)
    {
        string? normalized = NormalizePath(value);
        return normalized is not null && Directory.Exists(normalized)
            ? normalized
            : null;
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded.StartsWith("~/", StringComparison.Ordinal) ||
            expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[2..]);
        }

        try
        {
            string fullPath = Path.GetFullPath(expanded);
            string? root = Path.GetPathRoot(fullPath);
            return string.Equals(
                    fullPath,
                    root,
                    StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
            PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string first, string second)
    {
        string? normalizedFirst = NormalizePath(first);
        string? normalizedSecond = NormalizePath(second);
        return normalizedFirst is not null &&
            normalizedSecond is not null &&
            string.Equals(
                normalizedFirst,
                normalizedSecond,
                StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class ClaudeTurnAccumulator
{
    private readonly string _id;
    private readonly Dictionary<string, SessionEventRecord> _events =
        new(StringComparer.Ordinal);
    private string _prompt = string.Empty;
    private InferenceEvidence _evidence;

    public ClaudeTurnAccumulator(
        string id,
        int firstOrder,
        InferenceEvidence evidence)
    {
        _id = id;
        FirstOrder = firstOrder;
        _evidence = evidence;
    }

    public int FirstOrder { get; }

    public int EventCount => _events.Count;

    public DateTimeOffset? StartedAtUtc =>
        _events.Values
            .Select(static item => item.TimestampUtc)
            .Where(static value => value is not null)
            .Min();

    public void Add(
        SessionEventRecord eventRecord,
        string? prompt,
        InferenceEvidence evidence)
    {
        _events[eventRecord.Id] = eventRecord;
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            _prompt = prompt;
        }

        _evidence = Stronger(_evidence, evidence);
    }

    public void Remove(string eventId) => _events.Remove(eventId);

    public SessionTurn Build(int number)
    {
        SessionEventRecord[] events = _events.Values
            .OrderBy(static item => item.TimestampUtc ?? DateTimeOffset.MinValue)
            .ThenBy(static item => item.Order)
            .ToArray();
        DateTimeOffset? started = events
            .Select(static item => item.TimestampUtc)
            .Where(static value => value is not null)
            .Min();
        DateTimeOffset? ended = events
            .Select(static item => item.TimestampUtc)
            .Where(static value => value is not null)
            .Max();
        return new SessionTurn(
            _id,
            number,
            _prompt,
            started,
            ended,
            _evidence,
            events);
    }

    private static InferenceEvidence Stronger(
        InferenceEvidence first,
        InferenceEvidence second)
    {
        static int Rank(InferenceEvidence value) => value switch
        {
            InferenceEvidence.Observed => 7,
            InferenceEvidence.Corroborated => 6,
            InferenceEvidence.Derived => 5,
            InferenceEvidence.Heuristic => 4,
            InferenceEvidence.Opaque => 3,
            InferenceEvidence.Ambiguous => 2,
            _ => 1
        };

        return Rank(second) > Rank(first) ? second : first;
    }
}

internal sealed class ClaudeIndexWorkspaceCandidate
{
    public ClaudeIndexWorkspaceCandidate(
        string? originalPath,
        string? projectPath,
        string? fullPath)
    {
        OriginalPath = originalPath;
        ProjectPath = projectPath;
        FullPath = fullPath;
    }

    public string? OriginalPath { get; }

    public string? ProjectPath { get; }

    public string? FullPath { get; }
}

internal sealed class ClaudeCostStateCandidate
{
    public ClaudeCostStateCandidate(
        ClaudeTranscriptRow row,
        SessionSourceProvenance provenance,
        string? promptId,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? timestampUtc,
        int sourcePriority,
        int order)
    {
        Row = row;
        Provenance = provenance;
        PromptId = promptId;
        StartedAtUtc = startedAtUtc;
        TimestampUtc = timestampUtc;
        SourcePriority = sourcePriority;
        Order = order;
    }

    public ClaudeTranscriptRow Row { get; }

    public SessionSourceProvenance Provenance { get; }

    public string? PromptId { get; }

    public DateTimeOffset? StartedAtUtc { get; }

    public DateTimeOffset? TimestampUtc { get; }

    public int SourcePriority { get; }

    public int Order { get; }

    public bool IsNewerThan(ClaudeCostStateCandidate other)
    {
        if (TimestampUtc is not null || other.TimestampUtc is not null)
        {
            if (TimestampUtc is null)
            {
                return false;
            }

            if (other.TimestampUtc is null || TimestampUtc > other.TimestampUtc)
            {
                return true;
            }

            if (TimestampUtc < other.TimestampUtc)
            {
                return false;
            }
        }

        return SourcePriority > other.SourcePriority ||
            (SourcePriority == other.SourcePriority && Order > other.Order);
    }
}

internal sealed class ClaudeJsonAccessor
{
    public string? String(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    public string? NestedString(
        JsonElement element,
        string objectName,
        string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(objectName, out JsonElement nested)
                ? String(nested, propertyName)
                : null;
    }

    public bool? Boolean(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            if (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out bool parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public long? Long(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out long result))
            {
                return result;
            }

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(
                    value.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out result))
            {
                return result;
            }
        }

        return null;
    }

    public double? Double(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out double result))
            {
                return result;
            }

            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(
                    value.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out result))
            {
                return result;
            }
        }

        return null;
    }

    public DateTimeOffset? Timestamp(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(
                    value.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset timestamp))
            {
                return timestamp;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out long number))
            {
                return UnixTimestamp(number);
            }
        }

        return null;
    }

    public DateTimeOffset? UnixTimestamp(
        JsonElement element,
        params string[] names)
    {
        long? value = Long(element, names);
        return value is long number ? UnixTimestamp(number) : null;
    }

    public string? UserText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string> text = [];
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (String(block, "type") != "text")
            {
                continue;
            }

            string? value = String(block, "text");
            if (!string.IsNullOrWhiteSpace(value))
            {
                text.Add(value);
            }
        }

        return text.Count == 0 ? null : string.Join(Environment.NewLine, text);
    }

    public string? ContentText(JsonElement element) =>
        ContentText(element, depth: 0);

    public string? MetadataText(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            _ => value.GetRawText()
        };

    private string? ContentText(JsonElement element, int depth)
    {
        if (depth > 6)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            string[] values = element
                .EnumerateArray()
                .Select(item => ContentText(item, depth + 1))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
            return values.Length == 0 ? null : string.Join(Environment.NewLine, values);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string propertyName in new[]
                 {
                     "text", "content", "stdout", "stderr", "error", "message"
                 })
        {
            if (element.TryGetProperty(propertyName, out JsonElement value) &&
                ContentText(value, depth + 1) is string text &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static DateTimeOffset? UnixTimestamp(long value)
    {
        try
        {
            return value <= -100_000_000_000 ||
                value >= 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
