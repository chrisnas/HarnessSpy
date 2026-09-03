using System.IO;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sources;

namespace HarnessSpy.Core.Services;

// A discovered transcript file to tail, with its provider context, tail cursor,
// and durable-capture state.
public sealed class TranscriptFileBinding
{
    public required string NormalizedPath { get; init; }

    public required string ScopedSessionId { get; init; }

    public string? NativeSessionId { get; init; }

    public required string DialectId { get; init; }

    public required TranscriptFileRole Role { get; init; }

    public required HookProvider Provider { get; init; }

    public required HookSurface Surface { get; init; }

    public string? AgentId { get; init; }

    public required string SourceId { get; init; }

    public required TranscriptReadCursor Cursor { get; init; }

    public EnrichmentCaptureState CaptureState { get; set; } = EnrichmentCaptureState.None;

    public bool DiscoveredWhilePresent { get; init; }

    // The most recent turn id seen in this file, carried forward to rows that
    // omit it (Claude stamps the turn id only on user rows).
    public string? LastTurnId { get; set; }
}

// Extracts transcript references from any hook (not just session start) and
// tracks the set of files to tail. Paths are only ever those supplied by an
// accepted local hook; the registry never scans arbitrary provider storage.
// Synthetic health-check sessions are excluded.
public sealed class TranscriptSessionRegistry
{
    private static readonly HashSet<string> SyntheticSessions =
        new(StringComparer.OrdinalIgnoreCase) { "process-test", "cursor-smoke" };

    private readonly Dictionary<string, TranscriptFileBinding> _byPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    // Registers any transcript paths carried by a hook. Returns the bindings
    // newly discovered by this hook (empty when nothing new or the source is
    // synthetic/unsupported).
    public IReadOnlyList<TranscriptFileBinding> RegisterFromHook(HookObservation hook)
    {
        if (hook.SessionId is string session && SyntheticSessions.Contains(session))
        {
            return [];
        }

        IReadOnlyList<TranscriptReference> references = hook.Interpretation.TranscriptReferences;
        if (references.Count == 0)
        {
            return [];
        }

        List<TranscriptFileBinding> discovered = [];
        lock (_gate)
        {
            foreach (TranscriptReference reference in references)
            {
                if (!TranscriptDialectParserRegistry.IsSupported(reference.DialectId))
                {
                    continue;
                }

                string normalized = Normalize(reference.Path);
                if (_byPath.ContainsKey(normalized))
                {
                    continue;
                }

                TranscriptFileBinding binding = new()
                {
                    NormalizedPath = normalized,
                    ScopedSessionId = hook.ProviderScopedSessionId,
                    NativeSessionId = hook.SessionId,
                    DialectId = reference.DialectId,
                    Role = reference.Role,
                    Provider = hook.Provider,
                    Surface = hook.Surface,
                    AgentId = reference.AgentId,
                    SourceId = SourceIdFor(reference),
                    Cursor = new TranscriptReadCursor(normalized),
                    DiscoveredWhilePresent = File.Exists(normalized)
                };

                binding.CaptureState = binding.DiscoveredWhilePresent
                    ? EnrichmentCaptureState.LiveCaptured
                    : EnrichmentCaptureState.MissedBeforeCapture;

                _byPath[normalized] = binding;
                discovered.Add(binding);
            }
        }

        return discovered;
    }

    public IReadOnlyList<TranscriptFileBinding> ActiveFiles()
    {
        lock (_gate)
        {
            return [.. _byPath.Values];
        }
    }

    public static bool IsSynthetic(string? sessionId) =>
        sessionId is not null && SyntheticSessions.Contains(sessionId);

    private static string SourceIdFor(TranscriptReference reference) =>
        reference.Role == TranscriptFileRole.Subagent && reference.AgentId is string agentId
            ? $"agent-{agentId}"
            : "main";

    private static string Normalize(string path)
    {
        string trimmed = path.Trim();

        // Cursor may send URI-style '/c:/...' paths; strip the leading slash.
        if (trimmed.Length >= 3 && trimmed[0] == '/' &&
            char.IsAsciiLetter(trimmed[1]) && trimmed[2] == ':')
        {
            trimmed = trimmed[1..];
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }
}
