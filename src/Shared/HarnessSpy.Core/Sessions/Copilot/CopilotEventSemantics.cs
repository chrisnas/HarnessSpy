using System.Globalization;
using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Copilot;

internal sealed class CopilotMcpIdentity
{
    public CopilotMcpIdentity(string? serverName, string? toolName)
    {
        ServerName = serverName;
        ToolName = toolName;
    }

    public string? ServerName { get; }

    public string? ToolName { get; }

    public bool IsMcp =>
        !string.IsNullOrWhiteSpace(ServerName) ||
        !string.IsNullOrWhiteSpace(ToolName);
}

internal sealed class CopilotToolSemantics
{
    private readonly CopilotJsonValueReader _json;

    public CopilotToolSemantics(CopilotJsonValueReader json)
    {
        _json = json;
    }

    public CanonicalToolKind Classify(
        string? nativeName,
        CopilotMcpIdentity? mcp)
    {
        if (mcp?.IsMcp == true)
        {
            return CanonicalToolKind.Mcp;
        }

        if (string.IsNullOrWhiteSpace(nativeName))
        {
            return CanonicalToolKind.Unknown;
        }

        string name = nativeName.Trim().ToLowerInvariant();
        if (name is "bash" or "shell" or "powershell" or "terminal" or "exec" or
            "run_command" or "run_in_terminal" ||
            name.Contains("shell", StringComparison.Ordinal) ||
            name.Contains("terminal", StringComparison.Ordinal))
        {
            return CanonicalToolKind.Shell;
        }

        if (name is "view" or "read" or "read_file" or "readfile" or "cat")
        {
            return CanonicalToolKind.FileRead;
        }

        if (name is "create" or "write" or "write_file" or "writefile")
        {
            return CanonicalToolKind.FileWrite;
        }

        if (name is "edit" or "apply_patch" or "str_replace" or "replace" or
            "multiedit")
        {
            return CanonicalToolKind.FileEdit;
        }

        if (name is "delete" or "delete_file" or "remove_file")
        {
            return CanonicalToolKind.FileDelete;
        }

        if (name is "grep" or "rg" or "search" or "search_text" or "code_search" ||
            name.Contains("grep", StringComparison.Ordinal))
        {
            return CanonicalToolKind.TextSearch;
        }

        if (name is "glob" or "find" or "find_files" or "list_files")
        {
            return CanonicalToolKind.FileSearch;
        }

        if (name.Contains("notebook", StringComparison.Ordinal))
        {
            return CanonicalToolKind.Notebook;
        }

        if (name is "task" or "agent" or "read_agent" or "subagent" ||
            name.Contains("agent", StringComparison.Ordinal))
        {
            return CanonicalToolKind.Agent;
        }

        if (name is "web" or "web_search" or "web_fetch" or "fetch" or "browse" ||
            name.StartsWith("http", StringComparison.Ordinal))
        {
            return CanonicalToolKind.Web;
        }

        if (name is "ask_user" or "user_input" or "elicitation")
        {
            return CanonicalToolKind.UserInteraction;
        }

        if (name is "skill" or "todo" or "update_todo" or "plan")
        {
            return CanonicalToolKind.Task;
        }

        return CanonicalToolKind.Unknown;
    }

    public IReadOnlyList<string> TargetPaths(JsonElement data)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        CollectPaths(data, null, paths, depth: 0);
        return paths.Take(256).ToArray();
    }

    public CopilotMcpIdentity ReadMcpIdentity(JsonElement data)
    {
        string? server = _json.String(
            data,
            "mcpServerName",
            "serverName",
            "mcp_server_name");
        string? tool = _json.String(
            data,
            "mcpToolName",
            "mcp_tool_name");

        foreach (string nestedName in new[]
        {
            "promptRequest",
            "result",
            "approval",
            "permissionRequest"
        })
        {
            JsonElement? nested = _json.Object(data, nestedName);
            if (nested is null)
            {
                continue;
            }

            server ??= _json.String(
                nested.Value,
                "mcpServerName",
                "serverName",
                "mcp_server_name");
            string? explicitMcpTool = _json.String(
                nested.Value,
                "mcpToolName",
                "mcp_tool_name");
            tool ??= explicitMcpTool;

            string? kind = _json.String(nested.Value, "kind");
            if (tool is null &&
                (nestedName == "promptRequest" ||
                 string.Equals(kind, "mcp", StringComparison.OrdinalIgnoreCase)))
            {
                tool = _json.String(nested.Value, "toolName");
            }

            JsonElement? approval = _json.Object(nested.Value, "approval");
            if (approval is not null)
            {
                server ??= _json.String(
                    approval.Value,
                    "mcpServerName",
                    "serverName");
                tool ??= _json.String(
                    approval.Value,
                    "mcpToolName",
                    "toolName");
            }
        }

        return new CopilotMcpIdentity(server, tool);
    }

    private void CollectPaths(
        JsonElement value,
        string? propertyName,
        ISet<string> paths,
        int depth)
    {
        if (depth > 8 || paths.Count >= 256)
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    CollectPaths(
                        property.Value,
                        property.Name,
                        paths,
                        depth + 1);
                }
                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in value.EnumerateArray())
                {
                    CollectPaths(item, propertyName, paths, depth + 1);
                }
                break;

            case JsonValueKind.String when IsPathProperty(propertyName):
            {
                string? path = value.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }

                break;
            }
        }
    }

    private static bool IsPathProperty(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        return propertyName.Equals("path", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("paths", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("file", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("filePath", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("file_path", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("targetPath", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("target_path", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("dumpPath", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("cwd", StringComparison.OrdinalIgnoreCase) ||
            propertyName.EndsWith("FilePath", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class CopilotUsageExtractor
{
    private readonly CopilotJsonValueReader _json;

    public CopilotUsageExtractor(CopilotJsonValueReader json)
    {
        _json = json;
    }

    public IReadOnlyList<UsageMeasurement> ExtractForEvent(
        string nativeType,
        JsonElement data,
        string sourceRecordId)
    {
        List<UsageMeasurement> measurements = [];
        UsageScope scope = nativeType.StartsWith(
            "subagent.",
            StringComparison.Ordinal)
                ? UsageScope.Turn
                : UsageScope.Request;
        UsageBehavior behavior = UsageBehavior.Delta;

        AddKnownScalars(
            data,
            string.Empty,
            scope,
            behavior,
            sourceRecordId,
            measurements);

        JsonElement? usage = _json.Object(data, "usage");
        if (usage is not null)
        {
            AddNumericTree(
                usage.Value,
                "usage",
                scope,
                behavior,
                sourceRecordId,
                measurements,
                depth: 0);
        }

        JsonElement? tokenDetails = _json.Object(data, "tokenDetails", "token_details");
        if (tokenDetails is not null)
        {
            AddNumericTree(
                tokenDetails.Value,
                "tokenDetails",
                scope,
                behavior,
                sourceRecordId,
                measurements,
                depth: 0);
        }

        AddCopilotUsageTokenDetails(
            data,
            scope,
            behavior,
            sourceRecordId,
            measurements);

        return measurements
            .DistinctBy(
                static measurement =>
                    $"{measurement.Name}|{measurement.Value}|{measurement.Unit}",
                StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<UsageMeasurement> ExtractSessionAggregate(
        JsonElement data,
        string sourceRecordId,
        UsageBehavior behavior)
    {
        List<UsageMeasurement> measurements = [];
        AddKnownScalars(
            data,
            string.Empty,
            UsageScope.Session,
            behavior,
            sourceRecordId,
            measurements);

        foreach (string rootName in new[]
        {
            "tokenDetails",
            "modelMetrics",
            "agentMetrics"
        })
        {
            if (_json.TryGetProperty(data, out JsonElement root, rootName))
            {
                AddNumericTree(
                    root,
                    rootName,
                    UsageScope.Session,
                    behavior,
                    sourceRecordId,
                    measurements,
                    depth: 0);
            }
        }

        return measurements
            .DistinctBy(
                static measurement => measurement.Name,
                StringComparer.Ordinal)
            .ToArray();
    }

    private void AddKnownScalars(
        JsonElement data,
        string prefix,
        UsageScope scope,
        UsageBehavior behavior,
        string sourceRecordId,
        ICollection<UsageMeasurement> target)
    {
        foreach (string name in new[]
        {
            "inputTokens",
            "outputTokens",
            "cacheReadTokens",
            "cacheWriteTokens",
            "reasoningTokens",
            "totalTokens",
            "currentTokens",
            "systemTokens",
            "conversationTokens",
            "toolDefinitionsTokens",
            "totalNanoAiu",
            "totalPremiumRequests",
            "totalApiDurationMs",
            "durationMs",
            "modelCallDurationMs",
            "ttftMs",
            "outputTtftMs",
            "interTokenLatencyMs",
            "endToEndLatencyMs"
        })
        {
            long? value = _json.Integer(data, name);
            if (value is null)
            {
                continue;
            }

            string qualifiedName = string.IsNullOrEmpty(prefix)
                ? name
                : $"{prefix}.{name}";
            target.Add(new UsageMeasurement(
                qualifiedName,
                value.Value,
                UnitFor(name),
                scope,
                behavior,
                sourceRecordId));
        }
    }

    private void AddNumericTree(
        JsonElement value,
        string path,
        UsageScope scope,
        UsageBehavior behavior,
        string sourceRecordId,
        ICollection<UsageMeasurement> target,
        int depth)
    {
        if (depth > 12)
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    AddNumericTree(
                        property.Value,
                        $"{path}.{property.Name}",
                        scope,
                        behavior,
                        sourceRecordId,
                        target,
                        depth + 1);
                }
                break;

            case JsonValueKind.Array:
            {
                int index = 0;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    AddNumericTree(
                        item,
                        $"{path}[{index++}]",
                        scope,
                        behavior,
                        sourceRecordId,
                        target,
                        depth + 1);
                }

                break;
            }

            case JsonValueKind.Number:
                if (TryIntegral(value, out long number))
                {
                    string leafName = path[(path.LastIndexOfAny(['.', ']']) + 1)..];
                    target.Add(new UsageMeasurement(
                        path,
                        number,
                        UnitFor(leafName),
                        scope,
                        behavior,
                        sourceRecordId));
                }
                break;
        }
    }

    private void AddCopilotUsageTokenDetails(
        JsonElement data,
        UsageScope scope,
        UsageBehavior behavior,
        string sourceRecordId,
        ICollection<UsageMeasurement> target)
    {
        JsonElement current = data;
        foreach (string propertyName in new[]
        {
            "responseChunk",
            "copilot_usage"
        })
        {
            if (!_json.TryGetProperty(current, out JsonElement nested, propertyName))
            {
                return;
            }

            current = nested;
        }

        JsonElement? details = _json.Array(current, "token_details");
        if (details is null)
        {
            return;
        }

        foreach (JsonElement item in details.Value.EnumerateArray())
        {
            string? tokenType = _json.String(item, "token_type");
            long? tokenCount = _json.Integer(item, "token_count");
            if (tokenType is null || tokenCount is null)
            {
                continue;
            }

            target.Add(new UsageMeasurement(
                tokenType,
                tokenCount.Value,
                "tokens",
                scope,
                behavior,
                sourceRecordId));
        }
    }

    private static bool TryIntegral(JsonElement value, out long number)
    {
        if (value.TryGetInt64(out number))
        {
            return true;
        }

        if (value.TryGetDecimal(out decimal decimalValue) &&
            decimalValue == decimal.Truncate(decimalValue) &&
            decimalValue is >= long.MinValue and <= long.MaxValue)
        {
            number = decimal.ToInt64(decimalValue);
            return true;
        }

        number = 0;
        return false;
    }

    private static string UnitFor(string nativeName)
    {
        if (nativeName.Contains("NanoAiu", StringComparison.OrdinalIgnoreCase))
        {
            return "nano-AIU";
        }

        if (nativeName.Contains("PremiumRequest", StringComparison.OrdinalIgnoreCase))
        {
            return "premium requests";
        }

        if (nativeName.EndsWith("Ms", StringComparison.OrdinalIgnoreCase) ||
            nativeName.Contains("Duration", StringComparison.OrdinalIgnoreCase) ||
            nativeName.Contains("Latency", StringComparison.OrdinalIgnoreCase))
        {
            return "ms";
        }

        if (nativeName.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
            nativeName is "input" or "output" or "cache_read" or "cache_write")
        {
            return "tokens";
        }

        if (nativeName.Equals("count", StringComparison.OrdinalIgnoreCase))
        {
            return "requests";
        }

        return "provider units";
    }
}

internal sealed class CopilotMetadataValueFormatter
{
    public string Format(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    public string Format(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    public string Format(bool value) =>
        value ? "true" : "false";
}
