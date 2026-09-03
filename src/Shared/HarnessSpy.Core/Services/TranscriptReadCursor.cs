namespace HarnessSpy.Core.Services;

// Per-file tail position. The committed byte offset is only advanced after a
// row has been durably captured and enqueued, so a crash can replay at most the
// last uncommitted rows. Line number is provenance only; seeking uses bytes.
public sealed class TranscriptReadCursor(string normalizedPath)
{
    public string NormalizedPath { get; } = normalizedPath;

    public long FileGeneration { get; private set; } = 1;

    public long CommittedOffset { get; internal set; }

    public int NextLineNumber { get; internal set; } = 1;

    public long LastObservedLength { get; internal set; }

    public DateTimeOffset LastReadUtc { get; internal set; }

    // Starts a fresh generation after a truncation/replacement so reused
    // (path, offset) keys can never collide across the boundary.
    internal void StartNewGeneration()
    {
        FileGeneration++;
        CommittedOffset = 0;
        NextLineNumber = 1;
    }
}
