using System.Text.Json;

namespace HarnessSpy.Core.Sessions.Copilot;

internal sealed class CopilotExecutionSemantics
{
    private readonly CopilotJsonValueReader _json;

    public CopilotExecutionSemantics(CopilotJsonValueReader json)
    {
        _json = json;
    }

    public string? ReadToolCallId(JsonElement data)
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

    public string? ReadResultText(JsonElement data)
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

    public string? ReadStatus(JsonElement data)
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

    public double? ReadDuration(JsonElement data)
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

    public bool IsFailure(JsonElement data)
    {
        bool? success = _json.Boolean(data, "success");
        if (success == false)
        {
            return true;
        }

        return HasNonNullProperty(data, "error") ||
            ContainsFailureWord(ReadStatus(data));
    }

    public bool IsAborted(JsonElement data)
    {
        if (_json.Boolean(data, "aborted", "cancelled", "canceled") == true)
        {
            return true;
        }

        string? status = ReadStatus(data);
        return status?.Contains("abort", StringComparison.OrdinalIgnoreCase) == true ||
            status?.Contains("cancel", StringComparison.OrdinalIgnoreCase) == true;
    }

    public bool IsDenied(string nativeType, JsonElement data) =>
        string.Equals(nativeType, "permission.denied", StringComparison.Ordinal) ||
        ContainsFailureWord(ReadStatus(data));

    private bool HasNonNullProperty(JsonElement data, string name) =>
        _json.TryGetProperty(data, out JsonElement value, name) &&
        value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    private static bool ContainsFailureWord(string? value) =>
        value?.Contains("denied", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("reject", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("fail", StringComparison.OrdinalIgnoreCase) == true ||
        value?.Contains("error", StringComparison.OrdinalIgnoreCase) == true;
}
