using System.Threading.Channels;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sources;

namespace HarnessSpy.Core.Services;

// Single serialized funnel for hooks, replay, and transcript rows. Everything
// is processed on one loop so reconciliation state needs no cross-source locks.
// Hooks are emitted immediately (they are the ordering authority); transcript
// paths carried by a hook are discovered, backfilled once, and tailed, with
// every accepted row durably captured before its projection is emitted.
public sealed class ObservationIngestionCoordinator : IAsyncDisposable
{
    private readonly TranscriptSessionRegistry _registry;
    private readonly TranscriptCaptureStore _capture;
    private readonly TranscriptBindingJournal _journal;
    private readonly ObservationReconciler _reconciler = new();
    private readonly Func<ObservationChange, CancellationToken, Task> _onChange;
    private readonly bool _enableTranscripts;
    private readonly TimeSpan _pollInterval;

    private readonly Channel<WorkItem> _work = Channel.CreateUnbounded<WorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private CancellationTokenSource? _shutdown;
    private Task? _processLoop;
    private Task? _pollLoop;

    public ObservationIngestionCoordinator(
        TranscriptSessionRegistry registry,
        TranscriptCaptureStore capture,
        TranscriptBindingJournal journal,
        Func<ObservationChange, CancellationToken, Task> onChange,
        bool enableTranscripts,
        TimeSpan? pollInterval = null)
    {
        _registry = registry;
        _capture = capture;
        _journal = journal;
        _onChange = onChange;
        _enableTranscripts = enableTranscripts;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
    }

    public void Start(CancellationToken cancellationToken)
    {
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processLoop = Task.Run(() => ProcessLoopAsync(_shutdown.Token));
        if (_enableTranscripts)
        {
            _pollLoop = Task.Run(() => PollLoopAsync(_shutdown.Token));
        }
    }

    public ValueTask IngestHookAsync(HookObservation hook, CancellationToken cancellationToken) =>
        _work.Writer.WriteAsync(new WorkItem(hook, null, null), cancellationToken);

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (WorkItem item in _work.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.Hook is HookObservation hook)
                {
                    await ProcessHookAsync(hook, cancellationToken).ConfigureAwait(false);
                }
                else if (item.Binding is TranscriptFileBinding binding && item.Line is TranscriptRawLine line)
                {
                    await ProcessTranscriptLineAsync(binding, line, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ProcessHookAsync(HookObservation hook, CancellationToken cancellationToken)
    {
        if (_enableTranscripts)
        {
            foreach (TranscriptFileBinding discovered in _registry.RegisterFromHook(hook))
            {
                // Backfill immediately, especially for short-lived subagent files.
                await DrainBindingAsync(discovered, cancellationToken).ConfigureAwait(false);
                WriteManifest(discovered);
            }
        }

        foreach (ObservationChange change in _reconciler.Reconcile(hook))
        {
            await _onChange(change, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessTranscriptLineAsync(
        TranscriptFileBinding binding,
        TranscriptRawLine line,
        CancellationToken cancellationToken)
    {
        _capture.AppendSourceRow(
            binding.ScopedSessionId,
            binding.SourceId,
            line,
            binding.NormalizedPath,
            binding.DialectId,
            binding.Role,
            TranscriptCompleteness.Complete);

        TranscriptRowScanner.RowMeta meta = TranscriptRowScanner.Read(line.Raw);
        if (meta.TurnId is string turnId)
        {
            binding.LastTurnId = turnId;
        }

        TranscriptLine transcriptLine = new(
            line.Raw,
            binding.NormalizedPath,
            line.ByteOffset,
            line.LineNumber,
            line.FileGeneration,
            binding.Role,
            binding.Provider,
            binding.Surface,
            binding.DialectId,
            binding.ScopedSessionId,
            binding.NativeSessionId,
            binding.AgentId,
            CapturedPath: binding.NormalizedPath,
            // Order transcript nodes by the row's own timestamp when it carries
            // one (Claude ISO-8601, Copilot epoch ms); otherwise fall back to
            // capture time so rows without a native clock still stay grouped.
            ObservedAtUtc: meta.Timestamp,
            // Carry the last-seen turn id forward to rows that omit it.
            TurnHint: binding.LastTurnId);

        ITranscriptDialectParser parser = TranscriptDialectParserRegistry.Resolve(binding.DialectId);
        foreach (HookObservation observation in parser.Parse(transcriptLine))
        {
            foreach (ObservationChange change in _reconciler.Reconcile(observation))
            {
                _journal.Record(binding.ScopedSessionId, change);
                await _onChange(change, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                foreach (TranscriptFileBinding binding in _registry.ActiveFiles())
                {
                    await DrainBindingAsync(binding, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Reads all currently-complete rows from a file and enqueues them. Guarded
    // per cursor so discovery backfill and the poll loop never race the offset.
    private async Task DrainBindingAsync(TranscriptFileBinding binding, CancellationToken cancellationToken)
    {
        IReadOnlyList<TranscriptRawLine> lines;
        lock (binding.Cursor)
        {
            lines = JsonLineFileTailer.ReadNewLines(binding.Cursor);
        }

        foreach (TranscriptRawLine line in lines)
        {
            await _work.Writer.WriteAsync(new WorkItem(null, binding, line), cancellationToken).ConfigureAwait(false);
        }
    }

    private void WriteManifest(TranscriptFileBinding binding)
    {
        _capture.WriteManifest(binding.ScopedSessionId, new TranscriptSessionManifest(
            binding.ScopedSessionId,
            binding.DialectId,
            binding.Cursor.NormalizedPath,
            ParserVersion: 1,
            TranscriptBindingJournal.ReconcilerVersion,
            binding.CaptureState,
            [binding.SourceId],
            binding.NativeSessionId));
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown?.Cancel();
        _work.Writer.TryComplete();

        await AwaitQuietly(_pollLoop).ConfigureAwait(false);
        await AwaitQuietly(_processLoop).ConfigureAwait(false);
        _shutdown?.Dispose();
    }

    private static async Task AwaitQuietly(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private readonly record struct WorkItem(
        HookObservation? Hook,
        TranscriptFileBinding? Binding,
        TranscriptRawLine? Line);
}
