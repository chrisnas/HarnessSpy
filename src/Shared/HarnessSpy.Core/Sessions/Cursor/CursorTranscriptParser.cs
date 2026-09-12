using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;

namespace HarnessSpy.Core.Sessions.Cursor;

internal sealed record CursorParsedTranscript(
    CursorTranscriptSourceFile Source,
    IReadOnlyList<SessionTurn> Turns,
    IReadOnlyList<SessionSourceProvenance> Provenance,
    IReadOnlyList<JsonElement> Rows,
    string? Model,
    string? Mode,
    bool HasTerminalRecord);

internal sealed class CursorTranscriptParser
{
    private readonly CursorJsonReader _json;
    private readonly CursorIdentityBuilder _identity;

    public CursorTranscriptParser(
        CursorJsonReader json,
        CursorIdentityBuilder identity)
    {
        _json = json ?? throw new ArgumentNullException(nameof(json));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public CursorParsedTranscript Parse(
        CursorTranscriptSourceFile source,
        IReadOnlyList<CursorTranscriptRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rows);

        List<SessionTurn> turns = [];
        List<SessionSourceProvenance> provenance = [];
        List<JsonElement> jsonRows = [];
        CursorTurnBuilder? currentTurn = null;
        int turnNumber = 0;
        int eventOrder = 0;
        string? model = null;
        string? mode = null;
        bool hasTerminalRecord = false;
        Dictionary<string, string> toolNamesById =
            new(StringComparer.Ordinal);

        foreach (CursorTranscriptRow row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement root = row.Json;
            jsonRows.Add(root);
            string? recordId = _json.String(root, "id", "uuid", "messageId");
            string? parentRecordId = _json.String(root, "parentId", "parent_id");
            SessionSourceProvenance rowProvenance = CreateProvenance(
                source,
                row,
                recordId,
                parentRecordId);
            provenance.Add(rowProvenance);

            JsonElement message = GetMessage(root);
            model = _json.String(root, "model", "modelName", "model_id") ??
                _json.String(message, "model", "modelName", "model_id") ??
                model;
            mode = _json.String(
                root,
                "mode",
                "unifiedMode",
                "composerMode",
                "composer_mode") ??
                _json.String(
                    message,
                    "mode",
                    "unifiedMode",
                    "composerMode",
                    "composer_mode") ??
                mode;

            string? recordType = _json.String(root, "type");
            if (string.Equals(
                recordType,
                "turn_ended",
                StringComparison.OrdinalIgnoreCase))
            {
                currentTurn ??= CreateTurn(
                    source,
                    ++turnNumber,
                    prompt: string.Empty,
                    startedAtUtc: null);
                SessionEventRecord ended = CreateTurnEndedEvent(
                    source,
                    row,
                    root,
                    currentTurn.Id,
                    eventOrder++,
                    rowProvenance,
                    model,
                    mode);
                currentTurn.Events.Add(ended);
                currentTurn.EndedAtUtc = ended.TimestampUtc;
                turns.Add(currentTurn.Build());
                currentTurn = null;
                hasTerminalRecord = true;
                continue;
            }

            string? role = _json.String(root, "role");
            if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IReadOnlyList<CursorContentBlock> blocks = ReadContentBlocks(message);
            bool isUser = string.Equals(role, "user", StringComparison.OrdinalIgnoreCase);
            string prompt = isUser ? ReadUserPrompt(blocks) : string.Empty;
            bool startsTurn = isUser && prompt.Length > 0;

            if (startsTurn)
            {
                if (currentTurn is not null)
                {
                    turns.Add(currentTurn.Build());
                }

                currentTurn = CreateTurn(
                    source,
                    ++turnNumber,
                    prompt,
                    Timestamp(root, message));
                currentTurn.Events.Add(CreatePromptEvent(
                    source,
                    row,
                    root,
                    currentTurn.Id,
                    prompt,
                    eventOrder++,
                    rowProvenance,
                    model,
                    mode));
            }

            currentTurn ??= CreateTurn(
                source,
                ++turnNumber,
                prompt: string.Empty,
                Timestamp(root, message));

            if (!isUser)
            {
                eventOrder = AddAssistantEvents(
                    source,
                    row,
                    root,
                    message,
                    blocks,
                    currentTurn,
                    eventOrder,
                    rowProvenance,
                    model,
                    mode,
                    toolNamesById);
            }
            else
            {
                eventOrder = AddUserToolResults(
                    source,
                    row,
                    root,
                    message,
                    blocks,
                    currentTurn,
                    eventOrder,
                    rowProvenance,
                    model,
                    mode,
                    toolNamesById);
            }
        }

        if (currentTurn is not null)
        {
            turns.Add(currentTurn.Build());
        }

        return new CursorParsedTranscript(
            source,
            turns,
            provenance,
            jsonRows,
            model,
            mode,
            hasTerminalRecord);
    }

    private int AddAssistantEvents(
        CursorTranscriptSourceFile source,
        CursorTranscriptRow row,
        JsonElement root,
        JsonElement message,
        IReadOnlyList<CursorContentBlock> blocks,
        CursorTurnBuilder turn,
        int eventOrder,
        SessionSourceProvenance provenance,
        string? model,
        string? mode,
        Dictionary<string, string> toolNamesById)
    {
        int toolCallCount = blocks.Count(
            block => IsToolCallType(block.Type));
        string assistantStepId = _identity.AssistantStepId(
            source.Path,
            row.ByteOffset);
        string? parentId = _json.String(
            message,
            "id",
            "messageId",
            "modelCallId") ??
            _json.String(root, "id", "messageId", "modelCallId");

        for (int index = 0; index < blocks.Count; index++)
        {
            CursorContentBlock block = blocks[index];
            if (string.Equals(block.Type, "text", StringComparison.OrdinalIgnoreCase))
            {
                string? text = ReadBlockText(block.Json);
                if (!string.IsNullOrWhiteSpace(text) &&
                    !string.Equals(text.Trim(), "[REDACTED]", StringComparison.Ordinal))
                {
                    bool thought = toolCallCount > 0;
                    turn.Events.Add(CreateTextEvent(
                        source,
                        row,
                        block,
                        turn.Id,
                        eventOrder++,
                        assistantStepId,
                        parentId,
                        text,
                        thought,
                        explicitThinking: false,
                        provenance,
                        model,
                        mode,
                        index));
                }
            }
            else if (string.Equals(
                block.Type,
                "thinking",
                StringComparison.OrdinalIgnoreCase))
            {
                string? thinking = _json.String(block.Json, "thinking", "text");
                if (!string.IsNullOrWhiteSpace(thinking))
                {
                    turn.Events.Add(CreateTextEvent(
                        source,
                        row,
                        block,
                        turn.Id,
                        eventOrder++,
                        assistantStepId,
                        parentId,
                        thinking,
                        thought: true,
                        explicitThinking: true,
                        provenance,
                        model,
                        mode,
                        index));
                }
            }
            else if (IsToolCallType(block.Type))
            {
                turn.Events.Add(CreateToolRequestEvent(
                    source,
                    row,
                    block,
                    turn.Id,
                    eventOrder++,
                    assistantStepId,
                    parentId,
                    toolCallCount > 1,
                    provenance,
                    model,
                    mode,
                    index,
                    toolNamesById));
            }
            else if (IsToolResultType(block.Type))
            {
                turn.Events.Add(CreateToolResultEvent(
                    source,
                    row,
                    block,
                    turn.Id,
                    eventOrder++,
                    assistantStepId,
                    parentId,
                    provenance,
                    model,
                    mode,
                    index,
                    toolNamesById));
            }
            else if (string.Equals(
                block.Type,
                "command_output",
                StringComparison.OrdinalIgnoreCase))
            {
                string? output = _json.JsonText(block.Json, "output", "content", "text");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    turn.Events.Add(CreateMessageEvent(
                        source,
                        row,
                        block,
                        turn.Id,
                        eventOrder++,
                        assistantStepId,
                        parentId,
                        output,
                        provenance,
                        model,
                        mode,
                        index));
                }
            }
        }

        return eventOrder;
    }

    private int AddUserToolResults(
        CursorTranscriptSourceFile source,
        CursorTranscriptRow row,
        JsonElement root,
        JsonElement message,
        IReadOnlyList<CursorContentBlock> blocks,
        CursorTurnBuilder turn,
        int eventOrder,
        SessionSourceProvenance provenance,
        string? model,
        string? mode,
        IReadOnlyDictionary<string, string> toolNamesById)
    {
        string? parentId = _json.String(message, "id", "messageId") ??
            _json.String(root, "id", "messageId");

        for (int index = 0; index < blocks.Count; index++)
        {
            CursorContentBlock block = blocks[index];
            if (!IsToolResultType(block.Type))
            {
                continue;
            }

            turn.Events.Add(CreateToolResultEvent(
                source,
                row,
                block,
                turn.Id,
                eventOrder++,
                assistantStepId: null,
                parentId,
                provenance,
                model,
                mode,
                index,
                toolNamesById));
        }

        return eventOrder;
    }

    private SessionEventRecord CreatePromptEvent(
        CursorTranscriptSourceFile source,
        CursorTranscriptRow row,
        JsonElement root,
        string turnId,
        string prompt,
        int order,
        SessionSourceProvenance provenance,
        string? model,
        string? mode) =>
        new()
        {
            Id = _identity.EventId(source.Path, row.ByteOffset, 0, "user"),
            NativeName = "user",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Role = ObservationRole.PromptSubmitted,
            EventKind = CanonicalEventKind.PromptSubmitted,
            Direction = ObservationDirection.Input,
            Evidence = InferenceEvidence.Observed,
            TimestampUtc = Timestamp(root, GetMessage(root)),
            Order = order,
            TurnId = turnId,
            PromptText = prompt,
            Text = prompt,
            Model = model,
            Mode = mode,
            AgentId = source.IsChild ? source.NativeSessionId : null,
            AgentType = source.IsChild ? "cursor-subagent" : null,
            Task = source.IsChild ? prompt : null,
            ExcludeFromSummary = true,
            Provenance = provenance
        };

    private SessionEventRecord CreateTextEvent(
        CursorTranscriptSourceFile source,
        CursorTranscriptRow row,
        CursorContentBlock block,
        string turnId,
        int order,
        string assistantStepId,
        string? parentId,
        string text,
        bool thought,
        bool explicitThinking,
        SessionSourceProvenance provenance,
        string? model,
        string? mode,
        int fragmentIndex) =>
        new()
        {
            Id = _identity.EventId(
                source.Path,
                row.ByteOffset,
                fragmentIndex,
                block.Type),
            NativeName = block.Type,
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Role = thought ? ObservationRole.AgentThought : ObservationRole.AgentResponse,
            EventKind = thought
                ? CanonicalEventKind.AssistantThought
                : CanonicalEventKind.AssistantMessage,
            Direction = ObservationDirection.Output,
            Tone = thought ? ObservationTone.Thought : ObservationTone.Normal,
            Evidence = explicitThinking
                ? InferenceEvidence.Observed
                : thought
                    ? InferenceEvidence.Heuristic
                    : InferenceEvidence.Observed,
            TimestampUtc = Timestamp(block.Json, row.Json, GetMessage(row.Json)),
            Order = order,
            TurnId = turnId,
            ParentId = parentId,
            AssistantStepId = assistantStepId,
            Text = text,
            Model = _json.String(block.Json, "model", "modelName") ?? model,
            Mode = _json.String(block.Json, "mode") ?? mode,
            AgentId = source.IsChild ? source.NativeSessionId : null,
            AgentType = source.IsChild ? "cursor-subagent" : null,
            ExcludeFromSummary = true,
            Provenance = provenance
        };

    private SessionEventRecord CreateToolRequestEvent(
        CursorTranscriptSourceFile source,
        CursorTranscriptRow row,
        CursorContentBlock block,
        string turnId,
        int order,
        string assistantStepId,
        string? parentId,
        bool isParallelCandidate,
        SessionSourceProvenance provenance,
        string? model,
        string? mode,
        int fragmentIndex,
        Dictionary<string, string> toolNamesById)
    {
        string? toolName = _json.String(block.Json, "name", "tool_name", "toolName");
        string nativeName = toolName ?? block.Type;
        string? toolCallId = _json.String(
            block.Json,
            "id",
            "tool_use_id",
            "toolCallId",
            "tool_call_id");
        if (!string.IsNullOrWhiteSpace(toolCallId) &&
            !string.IsNullOrWhiteSpace(toolName))
        {
            toolNamesById[toolCallId] = toolName;
        }

        JsonElement input = ToolInput(block.Json);
        (string? mcpServer, string? mcpTool) = ReadMcpIdentity(toolName, input);
        CanonicalToolKind classifiedTool = ToolClassifier.Classify(toolName);
        bool isMcp = mcpServer is not null ||
            mcpTool is not null ||
            classifiedTool == CanonicalToolKind.Mcp ||
            string.Equals(toolName, "CallDynamicTool", StringComparison.Ordinal);

        return new SessionEventRecord
        {
            Id = _identity.EventId(
                source.Path,
                row.ByteOffset,
                fragmentIndex,
                nativeName),
            NativeName = nativeName,
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Role = ObservationRole.ToolRequest,
            EventKind = CanonicalEventKind.ToolRequested,
            ToolKind = isMcp
                ? CanonicalToolKind.Mcp
                : classifiedTool,
            Direction = ObservationDirection.Input,
            Tone = isMcp ? ObservationTone.Mcp : ObservationTone.Normal,
            Evidence = InferenceEvidence.Observed,
            TimestampUtc = Timestamp(block.Json, row.Json, GetMessage(row.Json)),
            Order = order,
            TurnId = turnId,
            ParentId = parentId,
            AssistantStepId = assistantStepId,
            ParallelGroupId = isParallelCandidate ? assistantStepId : null,
            ToolCallId = toolCallId,
            ToolName = toolName,
            McpServerName = mcpServer,
            McpToolName = mcpTool,
            Text = input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? null
                : input.GetRawText(),
            Model = _json.String(block.Json, "model", "modelName") ?? model,
            Mode = _json.String(block.Json, "mode") ?? mode,
            AgentId = source.IsChild ? source.NativeSessionId : null,
            AgentType = source.IsChild ? "cursor-subagent" : null,
            IsParallelCandidate = isParallelCandidate,
            ExcludeFromSummary = true,
            TargetPaths = input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? []
                : _json.TargetPaths(input),
            Provenance = provenance
        };
    }

    private SessionEventRecord CreateToolResultEvent(
        CursorTranscriptSourceFile source,
        CursorTranscriptRow row,
        CursorContentBlock block,
        string turnId,
        int order,
        string? assistantStepId,
        string? parentId,
        SessionSourceProvenance provenance,
        string? model,
        string? mode,
        int fragmentIndex,
        IReadOnlyDictionary<string, string> toolNamesById)
    {
        string? toolName = _json.String(block.Json, "name", "tool_name", "toolName");
        string? toolCallId = _json.String(
            block.Json,
            "tool_use_id",
            "toolCallId",
            "tool_call_id",
            "id");
        if (toolName is null &&
            toolCallId is not null &&
            toolNamesById.TryGetValue(toolCallId, out string? knownToolName))
        {
            toolName = knownToolName;
        }

        string nativeName = toolName ?? block.Type;
        CanonicalToolKind classifiedTool = ToolClassifier.Classify(toolName);
        bool isMcp = classifiedTool == CanonicalToolKind.Mcp ||
            string.Equals(toolName, "CallDynamicTool", StringComparison.Ordinal);
        string? status = _json.String(block.Json, "status");
        bool? explicitError = _json.Boolean(block.Json, "is_error", "isError");
        bool hasErrorValue = _json.TryGetProperty(
            block.Json,
            "error",
            out JsonElement errorValue) &&
            errorValue.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        bool isFailure = explicitError == true ||
            hasErrorValue ||
            IsFailureStatus(status);
        bool isSuccess = explicitError == false || IsSuccessStatus(status);
        string? text = _json.JsonText(
            block.Json,
            "result",
            "output",
            "content",
            "error",
            "text");

        return new SessionEventRecord
        {
            Id = _identity.EventId(
                source.Path,
                row.ByteOffset,
                fragmentIndex,
                nativeName + ":result"),
            NativeName = nativeName,
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Role = isFailure
                ? ObservationRole.ToolFailure
                : isSuccess
                    ? ObservationRole.ToolSuccess
                    : ObservationRole.Message,
            EventKind = isFailure
                ? CanonicalEventKind.ToolFailed
                : isSuccess
                    ? CanonicalEventKind.ToolSucceeded
                    : CanonicalEventKind.ProviderSpecific,
            ToolKind = isMcp ? CanonicalToolKind.Mcp : classifiedTool,
            Direction = ObservationDirection.Output,
            Tone = isFailure
                ? ObservationTone.Failure
                : isMcp
                    ? ObservationTone.Mcp
                    : ObservationTone.Normal,
            Evidence = InferenceEvidence.Observed,
            TimestampUtc = Timestamp(block.Json, row.Json, GetMessage(row.Json)),
            Order = order,
            TurnId = turnId,
            ParentId = parentId,
            AssistantStepId = assistantStepId,
            ToolCallId = toolCallId,
            ToolName = toolName,
            Text = text,
            Model = _json.String(block.Json, "model", "modelName") ?? model,
            Mode = _json.String(block.Json, "mode") ?? mode,
            Status = status,
            AgentId = source.IsChild ? source.NativeSessionId : null,
            AgentType = source.IsChild ? "cursor-subagent" : null,
            IsFailure = isFailure,
            IsAborted = string.Equals(
                status,
                "rejected",
                StringComparison.OrdinalIgnoreCase),
            ExcludeFromSummary = true,
            Provenance = provenance
        };
    }

    private SessionEventRecord CreateMessageEvent(
        CursorTranscriptSourceFile source,
        CursorTranscriptRow row,
        CursorContentBlock block,
        string turnId,
        int order,
        string assistantStepId,
        string? parentId,
        string text,
        SessionSourceProvenance provenance,
        string? model,
        string? mode,
        int fragmentIndex) =>
        new()
        {
            Id = _identity.EventId(
                source.Path,
                row.ByteOffset,
                fragmentIndex,
                block.Type),
            NativeName = block.Type,
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Role = ObservationRole.Message,
            EventKind = CanonicalEventKind.ProviderSpecific,
            Direction = ObservationDirection.Output,
            Evidence = InferenceEvidence.Observed,
            TimestampUtc = Timestamp(block.Json, row.Json, GetMessage(row.Json)),
            Order = order,
            TurnId = turnId,
            ParentId = parentId,
            AssistantStepId = assistantStepId,
            Text = text,
            Model = model,
            Mode = mode,
            AgentId = source.IsChild ? source.NativeSessionId : null,
            AgentType = source.IsChild ? "cursor-subagent" : null,
            ExcludeFromSummary = true,
            Provenance = provenance
        };

    private SessionEventRecord CreateTurnEndedEvent(
        CursorTranscriptSourceFile source,
        CursorTranscriptRow row,
        JsonElement root,
        string turnId,
        int order,
        SessionSourceProvenance provenance,
        string? model,
        string? mode)
    {
        string? status = _json.String(root, "status");
        bool failure = IsFailureStatus(status) ||
            _json.TryGetProperty(root, "error", out JsonElement error) &&
            error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

        return new SessionEventRecord
        {
            Id = _identity.EventId(
                source.Path,
                row.ByteOffset,
                0,
                "turn_ended"),
            NativeName = "turn_ended",
            Provider = HookProvider.Cursor,
            Surface = HookSurface.CursorIde,
            Role = ObservationRole.TurnStop,
            EventKind = CanonicalEventKind.TurnCompleted,
            Direction = ObservationDirection.Output,
            Tone = failure ? ObservationTone.Failure : ObservationTone.Stop,
            Evidence = InferenceEvidence.Observed,
            TimestampUtc = Timestamp(root),
            Order = order,
            TurnId = turnId,
            Text = _json.JsonText(root, "error"),
            Model = model,
            Mode = mode,
            Status = status,
            AgentId = source.IsChild ? source.NativeSessionId : null,
            AgentType = source.IsChild ? "cursor-subagent" : null,
            IsFailure = failure,
            IsAborted = failure ||
                string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase),
            ExcludeFromSummary = true,
            Provenance = provenance
        };
    }

    private IReadOnlyList<CursorContentBlock> ReadContentBlocks(JsonElement message)
    {
        if (!_json.TryGetProperty(message, "content", out JsonElement content))
        {
            return !string.IsNullOrWhiteSpace(_json.String(message, "thinking"))
                ? [new CursorContentBlock("thinking", message)]
                : [];
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return !string.IsNullOrWhiteSpace(_json.String(message, "thinking"))
                ?
                [
                    new CursorContentBlock("thinking", message),
                    new CursorContentBlock("text", content)
                ]
                : [new CursorContentBlock("text", content)];
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<CursorContentBlock> blocks = [];
        foreach (JsonElement item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                blocks.Add(new CursorContentBlock("text", item));
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string type = _json.String(item, "type") ?? "unknown";
            blocks.Add(new CursorContentBlock(type, item));
        }

        if (!blocks.Any(block => string.Equals(
                block.Type,
                "thinking",
                StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(_json.String(message, "thinking")))
        {
            blocks.Insert(0, new CursorContentBlock("thinking", message));
        }

        return blocks;
    }

    private string ReadUserPrompt(IReadOnlyList<CursorContentBlock> blocks)
    {
        string[] text = blocks
            .Where(block => string.Equals(
                block.Type,
                "text",
                StringComparison.OrdinalIgnoreCase))
            .Select(block => ReadBlockText(block.Json))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Where(value => !string.Equals(
                value.Trim(),
                "[REDACTED]",
                StringComparison.Ordinal))
            .ToArray();
        return _json.NormalizeUserPrompt(string.Join("\n", text));
    }

    private string? ReadBlockText(JsonElement block)
    {
        if (block.ValueKind == JsonValueKind.String)
        {
            return block.GetString();
        }

        return _json.String(block, "text");
    }

    private JsonElement GetMessage(JsonElement root) =>
        _json.TryGetProperty(root, "message", out JsonElement message) &&
        message.ValueKind == JsonValueKind.Object
            ? message
            : root;

    private JsonElement ToolInput(JsonElement block)
    {
        if (_json.TryGetProperty(block, "input", out JsonElement input))
        {
            return input;
        }

        if (_json.TryGetProperty(block, "arguments", out JsonElement arguments))
        {
            return arguments;
        }

        return default;
    }

    private (string? Server, string? Tool) ReadMcpIdentity(
        string? toolName,
        JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        string? server = _json.String(
            input,
            "namespace",
            "server",
            "serverName",
            "server_name",
            "mcpServerName",
            "mcp_server_name");
        string? tool = _json.String(
            input,
            "toolName",
            "tool_name",
            "mcpToolName",
            "mcp_tool_name");

        if (server is null &&
            !string.Equals(toolName, "CallDynamicTool", StringComparison.Ordinal) &&
            !string.Equals(toolName, "GetDynamicTools", StringComparison.Ordinal))
        {
            return (null, null);
        }

        return (server, tool);
    }

    private DateTimeOffset? Timestamp(params JsonElement[] values)
    {
        foreach (JsonElement value in values)
        {
            DateTimeOffset? timestamp = _json.Timestamp(
                value,
                "timestamp",
                "createdAt",
                "created_at",
                "time");
            if (timestamp is not null)
            {
                return timestamp;
            }
        }

        return null;
    }

    private CursorTurnBuilder CreateTurn(
        CursorTranscriptSourceFile source,
        int number,
        string prompt,
        DateTimeOffset? startedAtUtc) =>
        new(
            _identity.TranscriptTurnId(
                source.ParentSessionId ?? source.NativeSessionId,
                source.IsChild ? source.NativeSessionId : null,
                number),
            number,
            prompt,
            startedAtUtc);

    private SessionSourceProvenance CreateProvenance(
        CursorTranscriptSourceFile source,
        CursorTranscriptRow row,
        string? recordId,
        string? parentRecordId) =>
        new(
            SessionSourceKind.CursorTranscriptJsonl,
            source.Path,
            DialectIds.CursorTranscript,
            row.RawContent,
            row.LineNumber,
            row.ByteOffset,
            recordId ?? $"line:{row.LineNumber}",
            parentRecordId);

    private bool IsToolCallType(string type) =>
        string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "tool-call", StringComparison.OrdinalIgnoreCase);

    private bool IsToolResultType(string type) =>
        string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "tool-result", StringComparison.OrdinalIgnoreCase);

    private bool IsFailureStatus(string? status) =>
        status is not null &&
        (status.Equals("error", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("failure", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("rejected", StringComparison.OrdinalIgnoreCase));

    private bool IsSuccessStatus(string? status) =>
        status is not null &&
        (status.Equals("success", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("complete", StringComparison.OrdinalIgnoreCase));

    private sealed record CursorContentBlock(string Type, JsonElement Json);

    private sealed class CursorTurnBuilder
    {
        public CursorTurnBuilder(
            string id,
            int number,
            string prompt,
            DateTimeOffset? startedAtUtc)
        {
            Id = id;
            Number = number;
            Prompt = prompt;
            StartedAtUtc = startedAtUtc;
        }

        public string Id { get; }

        public int Number { get; }

        public string Prompt { get; }

        public DateTimeOffset? StartedAtUtc { get; }

        public DateTimeOffset? EndedAtUtc { get; set; }

        public List<SessionEventRecord> Events { get; } = [];

        public SessionTurn Build() => new(
            Id,
            Number,
            Prompt,
            StartedAtUtc,
            EndedAtUtc,
            InferenceEvidence.Derived,
            Events.ToArray());
    }
}
