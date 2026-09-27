using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;

namespace HarnessSpy.Wpf.ViewModels;

internal sealed class SessionFileAccessCollector
{
    private readonly FileAccessPathNormalizer _pathNormalizer = new();

    public IReadOnlyList<FileAccessRow> Collect(
        IEnumerable<SessionEventRecord> events,
        params CanonicalToolKind[] kinds)
    {
        HashSet<CanonicalToolKind> acceptedKinds = [.. kinds];
        Dictionary<string, LogicalFileAccess> accesses =
            new(StringComparer.Ordinal);

        foreach (SessionEventRecord item in events)
        {
            if (!acceptedKinds.Contains(item.ToolKind))
            {
                continue;
            }

            string key = string.IsNullOrWhiteSpace(item.ToolCallId)
                ? $"event:{item.Id}"
                : $"call:{item.ToolCallId}";
            if (!accesses.TryGetValue(key, out LogicalFileAccess? access))
            {
                access = new LogicalFileAccess();
                accesses[key] = access;
            }

            access.Observe(item);
        }

        return accesses.Values
            .Where(static access => access.ShouldInclude)
            .SelectMany(static access => access.Paths)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(_pathNormalizer.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(static path => new FileAccessRow { FullPath = path })
            .ToArray();
    }

    private sealed class LogicalFileAccess
    {
        private readonly HashSet<string> _paths =
            new(StringComparer.OrdinalIgnoreCase);
        private bool _hasRequest;
        private bool _hasSuccess;
        private bool _hasTerminalOutcome;

        public IReadOnlyCollection<string> Paths => _paths;

        public bool ShouldInclude =>
            _hasSuccess || (_hasRequest && !_hasTerminalOutcome);

        public void Observe(SessionEventRecord item)
        {
            foreach (string path in item.TargetPaths)
            {
                _paths.Add(path);
            }

            if (item.Role == ObservationRole.ToolRequest ||
                item.EventKind == CanonicalEventKind.ToolRequested)
            {
                _hasRequest = true;
            }

            if (item.Role is ObservationRole.ToolSuccess or
                    ObservationRole.FileAccess ||
                item.EventKind == CanonicalEventKind.ToolSucceeded)
            {
                _hasSuccess = true;
                _hasTerminalOutcome = true;
            }
            else if (item.Role == ObservationRole.ToolFailure ||
                     item.EventKind == CanonicalEventKind.ToolFailed)
            {
                _hasTerminalOutcome = true;
            }
        }
    }
}
