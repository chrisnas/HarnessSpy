using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;

namespace HarnessSpy.Core.Sessions.Cursor;

internal sealed record CursorSqliteJsonRecord(
    string Key,
    string RawContent,
    JsonElement Json);

internal sealed record CursorBubbleProjection(
    IReadOnlyList<SessionTurn> Turns,
    IReadOnlyList<SessionSourceProvenance> Sources,
    string? Model,
    string? Mode);

internal sealed class CursorDesktopBubbleProjector
{
    private readonly CursorJsonReader _json;
    private readonly CursorIdentityBuilder _identity;

    public CursorDesktopBubbleProjector(
        CursorJsonReader json,
        CursorIdentityBuilder identity)
    {
        _json = json ?? throw new ArgumentNullException(nameof(json));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public CursorBubbleProjection Project(
        string databasePath,
        string composerId,
        JsonElement composer,
        IReadOnlyDictionary<string, CursorSqliteJsonRecord> bubbles,
        int maximumRecords,
        CursorDiscoveryGuard guard,
        CursorWarningCollector warnings,
        CancellationToken cancellationToken)
    {
        List<SessionSourceProvenance> sources = bubbles.Values
            .Select(record => Provenance(
                databasePath,
                record,
                composerId))
            .ToList();
        List<CursorBubbleTurnBuilder> turns = [];
        CursorBubbleTurnBuilder? current = null;
        int turnNumber = 0;
        int order = 0;
        string? model = Model(composer);
        string? mode = Mode(composer);

        IReadOnlyList<JsonElement> headers = ConversationHeaders(composer);
        maximumRecords = Math.Max(0, maximumRecords);
        if (headers.Count > maximumRecords)
        {
            guard.MarkIncomplete();
            warnings.Add(
                $"Cursor composer '{composerId}' has {headers.Count} conversation " +
                $"headers; only the configured {maximumRecords} records were read.");
            headers = headers.Take(maximumRecords).ToArray();
        }

        if (headers.Count == 0)
        {
            return new CursorBubbleProjection([], sources, model, mode);
        }

        foreach (JsonElement header in headers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? bubbleId = _json.String(
                header,
                "bubbleId",
                "bubble_id",
                "id");
            JsonElement bubble = header;
            CursorSqliteJsonRecord? record = null;
            if (!string.IsNullOrWhiteSpace(bubbleId) &&
                bubbles.TryGetValue(bubbleId, out CursorSqliteJsonRecord? found))
            {
                record = found;
                bubble = found.Json;
            }

            int? type = BubbleType(header) ?? BubbleType(bubble);
            string? role = _json.String(header, "role") ??
                _json.String(bubble, "role");
            bool isUser = type == 1 ||
                string.Equals(role, "user", StringComparison.OrdinalIgnoreCase);
            bool isAssistant = type == 2 ||
                string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);
            if (!isUser && !isAssistant)
            {
                continue;
            }

            string effectiveBubbleId = bubbleId ??
                _json.String(bubble, "bubbleId", "bubble_id", "id") ??
                $"header:{order}";
            string databaseKey = record?.Key ??
                $"bubbleId:{composerId}:{effectiveBubbleId}";
            string raw = record?.RawContent ?? header.GetRawText();
            SessionSourceProvenance provenance = new(
                SessionSourceKind.CursorDesktopSqlite,
                databasePath,
                "cursor-desktop-bubble-json",
                raw,
                RecordId: effectiveBubbleId,
                ParentRecordId: RequestId(header, bubble),
                DatabaseKey: databaseKey);
            if (record is null)
            {
                sources.Add(provenance);
            }

            DateTimeOffset? timestamp = Timestamp(bubble) ?? Timestamp(header);
            string? requestId = RequestId(header, bubble);
            model = Model(bubble) ?? model;
            mode = Mode(bubble) ?? mode;

            if (isUser)
            {
                string prompt = _json.NormalizeUserPrompt(ContentText(bubble));
                if (current is not null)
                {
                    turns.Add(current);
                }

                turnNumber++;
                string turnId = !string.IsNullOrWhiteSpace(requestId)
                    ? NativeTurnId(composerId, requestId)
                    : _identity.TranscriptTurnId(composerId, null, turnNumber);
                current = new CursorBubbleTurnBuilder(
                    turnId,
                    turnNumber,
                    prompt,
                    timestamp);
                current.Events.Add(new SessionEventRecord
                {
                    Id = EventId(
                        databasePath,
                        composerId,
                        order,
                        "user"),
                    NativeName = "user",
                    Provider = HookProvider.Cursor,
                    Surface = HookSurface.CursorIde,
                    Role = ObservationRole.PromptSubmitted,
                    EventKind = CanonicalEventKind.PromptSubmitted,
                    Direction = ObservationDirection.Input,
                    Evidence = InferenceEvidence.Observed,
                    TimestampUtc = timestamp,
                    Order = order++,
                    TurnId = turnId,
                    ParentId = requestId,
                    PromptText = prompt,
                    Text = prompt,
                    Model = model,
                    Mode = mode,
                    ExcludeFromSummary = true,
                    Provenance = provenance
                });
                continue;
            }

            current ??= new CursorBubbleTurnBuilder(
                !string.IsNullOrWhiteSpace(requestId)
                    ? NativeTurnId(composerId, requestId)
                    : _identity.TranscriptTurnId(
                        composerId,
                        null,
                        ++turnNumber),
                turnNumber,
                string.Empty,
                timestamp);

            order = AddAssistantBubble(
                databasePath,
                composerId,
                effectiveBubbleId,
                bubble,
                current,
                order,
                timestamp,
                requestId,
                provenance,
                model,
                mode);
        }

        if (current is not null)
        {
            turns.Add(current);
        }

        IReadOnlyList<SessionTurn> built = MarkParallelToolCalls(
            turns.Select(item => item.Build()).ToArray());
        return new CursorBubbleProjection(built, sources, model, mode);
    }

    private int AddAssistantBubble(
        string databasePath,
        string composerId,
        string bubbleId,
        JsonElement bubble,
        CursorBubbleTurnBuilder turn,
        int order,
        DateTimeOffset? timestamp,
        string? requestId,
        SessionSourceProvenance provenance,
        string? model,
        string? mode)
    {
        string? modelCallId = _json.String(
            bubble,
            "modelCallId",
            "model_call_id",
            "messageId",
            "message_id");
        string assistantStepId = modelCallId ??
            $"cursor-bubble-step:{composerId}:{bubbleId}";
        order = AddThinkingBlocks(
            databasePath,
            composerId,
            bubble,
            turn,
            order,
            timestamp,
            modelCallId,
            assistantStepId,
            provenance,
            model,
            mode);

        if (_json.TryGetProperty(
            bubble,
            "toolFormerData",
            out JsonElement toolData) &&
            toolData.ValueKind == JsonValueKind.Object)
        {
            string? nativeToolName = _json.String(
                toolData,
                "name",
                "toolName",
                "tool_name");
            string nativeName = nativeToolName ?? "toolFormerData";
            string? toolCallId = _json.String(
                toolData,
                "toolCallId",
                "tool_call_id",
                "toolUseId",
                "tool_use_id",
                "id");
            string? toolModelCallId = _json.String(
                toolData,
                "modelCallId",
                "model_call_id") ??
                modelCallId;
            string toolAssistantStepId = toolModelCallId ?? assistantStepId;
            string? status = _json.String(toolData, "status", "state");
            string? arguments = _json.JsonText(
                toolData,
                "rawArgs",
                "arguments",
                "input",
                "params");
            JsonElement argumentJson = ParseArguments(arguments);
            (string? mcpServer, string? mcpTool) = McpIdentity(
                nativeToolName,
                toolData,
                argumentJson);
            CanonicalToolKind classifiedTool =
                ToolClassifier.Classify(nativeToolName);
            bool isMcp = mcpServer is not null ||
                mcpTool is not null ||
                classifiedTool == CanonicalToolKind.Mcp ||
                string.Equals(
                    nativeToolName,
                    "CallDynamicTool",
                    StringComparison.Ordinal);

            turn.Events.Add(new SessionEventRecord
            {
                Id = EventId(
                    databasePath,
                    composerId,
                    order,
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
                TimestampUtc = timestamp,
                Order = order++,
                TurnId = turn.Id,
                ParentId = toolModelCallId,
                AssistantStepId = toolAssistantStepId,
                ToolCallId = toolCallId,
                ToolName = nativeToolName,
                McpServerName = mcpServer,
                McpToolName = mcpTool,
                Text = arguments,
                Model = Model(toolData) ?? model,
                Mode = Mode(toolData) ?? mode,
                Status = status,
                TargetPaths = argumentJson.ValueKind is
                    JsonValueKind.Undefined or JsonValueKind.Null
                        ? []
                        : _json.TargetPaths(argumentJson),
                ExcludeFromSummary = true,
                Provenance = provenance
            });

            bool isFailure = IsFailureStatus(status);
            bool isSuccess = IsSuccessStatus(status);
            string? result = ResultText(bubble, toolData);
            if (isFailure || isSuccess || !string.IsNullOrWhiteSpace(result))
            {
                turn.Events.Add(new SessionEventRecord
                {
                    Id = EventId(
                        databasePath,
                        composerId,
                        order,
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
                    ToolKind = isMcp
                        ? CanonicalToolKind.Mcp
                        : classifiedTool,
                    Direction = ObservationDirection.Output,
                    Tone = isFailure
                        ? ObservationTone.Failure
                        : isMcp
                            ? ObservationTone.Mcp
                            : ObservationTone.Normal,
                    Evidence = InferenceEvidence.Observed,
                    TimestampUtc = timestamp,
                    Order = order++,
                    TurnId = turn.Id,
                    ParentId = toolModelCallId,
                    AssistantStepId = toolAssistantStepId,
                    ToolCallId = toolCallId,
                    ToolName = nativeToolName,
                    McpServerName = mcpServer,
                    McpToolName = mcpTool,
                    Text = result,
                    Model = Model(toolData) ?? model,
                    Mode = Mode(toolData) ?? mode,
                    Status = status,
                    DurationMs = _json.Double(
                        toolData,
                        "durationMs",
                        "duration_ms",
                        "duration"),
                    IsFailure = isFailure,
                    IsAborted = string.Equals(
                        status,
                        "rejected",
                        StringComparison.OrdinalIgnoreCase),
                    ExcludeFromSummary = true,
                    Provenance = provenance
                });
            }

            turn.EndedAtUtc = Maximum(turn.EndedAtUtc, timestamp);
            return order;
        }

        string? text = ContentText(bubble);
        bool thought = _json.Boolean(bubble, "isThought", "is_thought") == true ||
            !string.IsNullOrWhiteSpace(_json.String(bubble, "thinking")) ||
            string.Equals(
                _json.String(bubble, "bubbleType", "kind"),
                "thought",
                StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(text))
        {
            turn.Events.Add(new SessionEventRecord
            {
                Id = EventId(
                    databasePath,
                    composerId,
                    order,
                    thought ? "thought" : "assistant"),
                NativeName = thought ? "thought" : "assistant",
                Provider = HookProvider.Cursor,
                Surface = HookSurface.CursorIde,
                Role = thought
                    ? ObservationRole.AgentThought
                    : ObservationRole.AgentResponse,
                EventKind = thought
                    ? CanonicalEventKind.AssistantThought
                    : CanonicalEventKind.AssistantMessage,
                Direction = ObservationDirection.Output,
                Tone = thought ? ObservationTone.Thought : ObservationTone.Normal,
                Evidence = InferenceEvidence.Observed,
                TimestampUtc = timestamp,
                Order = order++,
                TurnId = turn.Id,
                ParentId = modelCallId,
                AssistantStepId = assistantStepId,
                Text = text,
                Model = model,
                Mode = mode,
                DurationMs = thought
                    ? _json.Double(bubble, "durationMs", "duration_ms")
                    : null,
                ExcludeFromSummary = true,
                Provenance = provenance
            });
        }

        turn.EndedAtUtc = Maximum(turn.EndedAtUtc, timestamp);
        return order;
    }

    private int AddThinkingBlocks(
        string databasePath,
        string composerId,
        JsonElement bubble,
        CursorBubbleTurnBuilder turn,
        int order,
        DateTimeOffset? timestamp,
        string? modelCallId,
        string assistantStepId,
        SessionSourceProvenance provenance,
        string? model,
        string? mode)
    {
        if (!_json.TryGetProperty(
            bubble,
            "allThinkingBlocks",
            out JsonElement thinkingBlocks) ||
            thinkingBlocks.ValueKind != JsonValueKind.Array)
        {
            return order;
        }

        foreach (JsonElement block in thinkingBlocks.EnumerateArray())
        {
            string? text = ContentText(block);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            turn.Events.Add(new SessionEventRecord
            {
                Id = EventId(
                    databasePath,
                    composerId,
                    order,
                    "thought"),
                NativeName = _json.String(block, "type", "kind") ?? "thought",
                Provider = HookProvider.Cursor,
                Surface = HookSurface.CursorIde,
                Role = ObservationRole.AgentThought,
                EventKind = CanonicalEventKind.AssistantThought,
                Direction = ObservationDirection.Output,
                Tone = ObservationTone.Thought,
                Evidence = InferenceEvidence.Observed,
                TimestampUtc = Timestamp(block) ?? timestamp,
                Order = order++,
                TurnId = turn.Id,
                ParentId = modelCallId,
                AssistantStepId = assistantStepId,
                Text = text,
                Model = Model(block) ?? model,
                Mode = Mode(block) ?? mode,
                DurationMs = _json.Double(block, "durationMs", "duration_ms"),
                ExcludeFromSummary = true,
                Provenance = provenance
            });
        }

        return order;
    }

    private IReadOnlyList<JsonElement> ConversationHeaders(JsonElement composer)
    {
        foreach (string propertyName in new[]
                 {
                     "fullConversationHeadersOnly",
                     "conversationHeaders",
                     "fullConversation",
                     "conversation"
                 })
        {
            if (_json.TryGetProperty(
                composer,
                propertyName,
                out JsonElement headers) &&
                headers.ValueKind == JsonValueKind.Array)
            {
                return headers.EnumerateArray().ToArray();
            }
        }

        return [];
    }

    private int? BubbleType(JsonElement element)
    {
        int? numeric = _json.Integer(element, "type", "bubbleType");
        if (numeric is not null)
        {
            return numeric;
        }

        string? text = _json.String(element, "type", "bubbleType");
        return text?.ToLowerInvariant() switch
        {
            "user" => 1,
            "assistant" => 2,
            "ai" => 2,
            _ => null
        };
    }

    private string? RequestId(params JsonElement[] elements)
    {
        foreach (JsonElement element in elements)
        {
            string? value = _json.String(
                element,
                "requestId",
                "request_id",
                "generationId",
                "generation_id");
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private string? ContentText(JsonElement element, int depth = 0)
    {
        if (depth > 8)
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
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
            return values.Length == 0 ? null : string.Join("\n", values);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string name in new[]
                 {
                     "text", "richText", "thinking", "content", "message", "output"
                 })
        {
            if (_json.TryGetProperty(element, name, out JsonElement value) &&
                ContentText(value, depth + 1) is string text &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private string? ResultText(JsonElement bubble, JsonElement toolData)
    {
        foreach ((JsonElement element, string[] names) in new[]
                 {
                     (toolData, new[] { "result", "output", "error", "content" }),
                     (bubble, new[] { "result", "output", "error", "text" })
                 })
        {
            string? value = _json.JsonText(element, names);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private JsonElement ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments) ||
            !_json.TryParse(arguments, out JsonDocument? document, out _) ||
            document is null)
        {
            return default;
        }

        using (document)
        {
            return document.RootElement.Clone();
        }
    }

    private (string? Server, string? Tool) McpIdentity(
        string? nativeToolName,
        JsonElement toolData,
        JsonElement arguments)
    {
        string? server = _json.String(
            toolData,
            "mcpServerName",
            "mcp_server_name",
            "serverName",
            "server_name");
        string? tool = _json.String(
            toolData,
            "mcpToolName",
            "mcp_tool_name");
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            server ??= _json.String(
                arguments,
                "namespace",
                "server",
                "serverName",
                "server_name");
            tool ??= _json.String(
                arguments,
                "toolName",
                "tool_name");
        }

        if (server is null &&
            !string.Equals(
                nativeToolName,
                "CallDynamicTool",
                StringComparison.Ordinal))
        {
            return (null, null);
        }

        return (server, tool);
    }

    private string? Model(JsonElement element)
    {
        string? direct = _json.String(
            element,
            "model",
            "modelName",
            "model_name",
            "modelId",
            "model_id");
        if (direct is not null)
        {
            return direct;
        }

        foreach (string containerName in new[]
                 {
                     "modelDetails", "modelInfo", "modelConfig"
                 })
        {
            if (_json.TryGetProperty(
                element,
                containerName,
                out JsonElement nested))
            {
                direct = _json.String(
                    nested,
                    "model",
                    "modelName",
                    "name",
                    "id");
                if (direct is not null)
                {
                    return direct;
                }
            }
        }

        return null;
    }

    private string? Mode(JsonElement element) =>
        _json.String(
            element,
            "unifiedMode",
            "mode",
            "composerMode",
            "composer_mode");

    private DateTimeOffset? Timestamp(JsonElement element) =>
        _json.Timestamp(
            element,
            "createdAt",
            "created_at",
            "timestamp",
            "time",
            "lastUpdatedAt");

    private SessionSourceProvenance Provenance(
        string databasePath,
        CursorSqliteJsonRecord record,
        string composerId) =>
        new(
            SessionSourceKind.CursorDesktopSqlite,
            databasePath,
            "cursor-desktop-bubble-json",
            record.RawContent,
            RecordId: _json.String(
                record.Json,
                "bubbleId",
                "bubble_id",
                "id"),
            ParentRecordId: composerId,
            DatabaseKey: record.Key);

    private IReadOnlyList<SessionTurn> MarkParallelToolCalls(
        IReadOnlyList<SessionTurn> turns)
    {
        List<SessionTurn> result = [];
        foreach (SessionTurn turn in turns)
        {
            HashSet<string> parallelSteps = turn.Events
                .Where(item =>
                    item.Role == ObservationRole.ToolRequest &&
                    !string.IsNullOrWhiteSpace(item.AssistantStepId))
                .GroupBy(item => item.AssistantStepId!, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.Ordinal);

            if (parallelSteps.Count == 0)
            {
                result.Add(turn);
                continue;
            }

            SessionEventRecord[] events = turn.Events
                .Select(item => item.AssistantStepId is string step &&
                    parallelSteps.Contains(step) &&
                    item.Role == ObservationRole.ToolRequest
                        ? item with
                        {
                            IsParallelCandidate = true,
                            ParallelGroupId = step
                        }
                        : item)
                .ToArray();
            result.Add(turn with { Events = events });
        }

        return result;
    }

    private string NativeTurnId(string composerId, string requestId) =>
        $"cursor:{Uri.EscapeDataString(composerId)}:request:" +
        Uri.EscapeDataString(requestId);

    private string EventId(
        string databasePath,
        string composerId,
        int order,
        string nativeName) =>
        _identity.EventId(
            databasePath + "#" + composerId,
            order,
            0,
            nativeName);

    private bool IsFailureStatus(string? status) =>
        status is not null &&
        (status.Equals("error", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("failure", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("rejected", StringComparison.OrdinalIgnoreCase));

    private bool IsSuccessStatus(string? status) =>
        status is not null &&
        (status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("complete", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("success", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("succeeded", StringComparison.OrdinalIgnoreCase));

    private DateTimeOffset? Maximum(
        DateTimeOffset? first,
        DateTimeOffset? second) =>
        first is null ? second :
        second is null ? first :
        first >= second ? first : second;

    private sealed class CursorBubbleTurnBuilder
    {
        public CursorBubbleTurnBuilder(
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
            InferenceEvidence.Observed,
            Events.ToArray());
    }
}
