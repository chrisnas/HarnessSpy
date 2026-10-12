using HarnessSpy.Core.Runtimes.Cursor;

namespace HarnessSpy.Core.Sessions.Cursor;

internal sealed class CursorToolRequestIndex
{
    private readonly IReadOnlyList<SessionEventRecord> _requests;
    private readonly bool[] _used;
    private readonly Dictionary<string, Queue<int>> _byToolCallId =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<int>> _byCorrelationKey =
        new(StringComparer.Ordinal);

    public CursorToolRequestIndex(
        IReadOnlyList<SessionEventRecord> requests)
    {
        _requests = requests;
        _used = new bool[requests.Count];

        for (int index = 0; index < requests.Count; index++)
        {
            SessionEventRecord request = requests[index];
            if (!string.IsNullOrWhiteSpace(request.ToolCallId))
            {
                Enqueue(_byToolCallId, request.ToolCallId, index);
            }

            Enqueue(
                _byCorrelationKey,
                CorrelationKey(request),
                index);
        }
    }

    public bool TryTakeByToolCallId(
        string toolCallId,
        out SessionEventRecord? request) =>
        TryTake(_byToolCallId, toolCallId, out request);

    public bool TryTakeByCorrelationKey(
        string correlationKey,
        out SessionEventRecord? request) =>
        TryTake(_byCorrelationKey, correlationKey, out request);

    private bool TryTake(
        IReadOnlyDictionary<string, Queue<int>> index,
        string key,
        out SessionEventRecord? request)
    {
        if (!index.TryGetValue(key, out Queue<int>? candidates))
        {
            request = null;
            return false;
        }

        while (candidates.TryDequeue(out int candidateIndex))
        {
            if (_used[candidateIndex])
            {
                continue;
            }

            _used[candidateIndex] = true;
            request = _requests[candidateIndex];
            return true;
        }

        request = null;
        return false;
    }

    private void Enqueue(
        IDictionary<string, Queue<int>> index,
        string key,
        int requestIndex)
    {
        if (!index.TryGetValue(key, out Queue<int>? candidates))
        {
            candidates = new Queue<int>();
            index[key] = candidates;
        }

        candidates.Enqueue(requestIndex);
    }

    private string CorrelationKey(SessionEventRecord item) =>
        CursorToolSemantics.CorrelationKey(
            item.ToolKind,
            item.ToolName ?? item.NativeName,
            item.McpServerName,
            item.McpToolName);
}
