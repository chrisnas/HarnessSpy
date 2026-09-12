using System.Buffers;
using System.Text;
using System.Text.Json;

namespace HarnessSpy.Core.Sessions.Cursor;

internal enum CursorTranscriptLayout
{
    NestedMain,
    NestedChild,
    LegacyFlat
}

internal sealed record CursorTranscriptSourceFile(
    string Path,
    string ProjectKey,
    string NativeSessionId,
    CursorTranscriptLayout Layout,
    string? ParentSessionId = null)
{
    public bool IsChild => Layout == CursorTranscriptLayout.NestedChild;
}

internal sealed record CursorTranscriptRow(
    string RawContent,
    int LineNumber,
    long ByteOffset,
    JsonElement Json);

internal sealed record CursorTranscriptReadResult(
    IReadOnlyList<CursorTranscriptRow> Rows,
    bool IsComplete,
    IReadOnlyList<string> Warnings);

internal sealed class CursorTranscriptFileReader
{
    private const int ReadBufferSize = 64 * 1024;

    private readonly SessionDiscoveryLimits _limits;
    private readonly CursorFileSystemInspector _fileSystem;
    private readonly UTF8Encoding _utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public CursorTranscriptFileReader(
        SessionDiscoveryLimits limits,
        CursorFileSystemInspector fileSystem)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public async Task<CursorTranscriptReadResult> ReadAsync(
        string path,
        CursorDiscoveryGuard guard,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(guard);

        List<CursorTranscriptRow> rows = [];
        CursorWarningCollector warnings = new();
        bool isComplete = true;

        if (!_fileSystem.TryGetFileInfo(
            path,
            out long fileLength,
            out _,
            out string? inspectError))
        {
            warnings.Add($"Could not inspect Cursor transcript '{path}': {inspectError}.");
            return new CursorTranscriptReadResult(rows, false, warnings.Snapshot());
        }

        long maximumFileBytes = Math.Max(0, _limits.MaximumFileBytes);
        long bytesToRead = Math.Min(fileLength, maximumFileBytes);
        bool boundedPrefix = fileLength > maximumFileBytes;
        if (boundedPrefix)
        {
            isComplete = false;
            warnings.Add(
                $"Cursor transcript '{path}' is {fileLength} bytes; only the configured " +
                $"{maximumFileBytes} byte prefix was inspected.");
        }

        if (bytesToRead == 0)
        {
            return new CursorTranscriptReadResult(rows, isComplete, warnings.Snapshot());
        }

        int maximumLineBytes = Math.Max(1, _limits.MaximumLineBytes);
        int maximumRecords = Math.Max(0, _limits.MaximumRecordsPerSession);
        if (maximumRecords == 0)
        {
            warnings.Add(
                $"Cursor transcript record limit was reached before reading '{path}'.");
            return new CursorTranscriptReadResult(rows, false, warnings.Snapshot());
        }

        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        ArrayBufferWriter<byte> lineBuffer = new(
            Math.Min(maximumLineBytes, ReadBufferSize));
        bool lineTooLong = false;
        int lineNumber = 1;
        long lineOffset = 0;
        long processedBytes = 0;
        int recordsRead = 0;

        try
        {
            await using FileStream stream = new(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    BufferSize = ReadBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });

            while (processedBytes < bytesToRead)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (guard.DeadlineExceeded)
                {
                    guard.MarkIncomplete();
                    isComplete = false;
                    warnings.Add(
                        $"Cursor discovery time limit was reached while reading '{path}'.");
                    break;
                }

                int requested = (int)Math.Min(
                    readBuffer.Length,
                    bytesToRead - processedBytes);
                int count = await stream.ReadAsync(
                    readBuffer.AsMemory(0, requested),
                    cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    if (processedBytes < bytesToRead)
                    {
                        isComplete = false;
                        warnings.Add(
                            $"Cursor transcript '{path}' was truncated while being read.");
                    }

                    break;
                }

                int segmentStart = 0;
                for (int index = 0; index < count; index++)
                {
                    if (readBuffer[index] != (byte)'\n')
                    {
                        continue;
                    }

                    AppendSegment(
                        lineBuffer,
                        readBuffer.AsSpan(segmentStart, index - segmentStart),
                        maximumLineBytes,
                        ref lineTooLong);

                    long nextOffset = processedBytes + index + 1;
                    bool shouldStop = ProcessLine(
                        path,
                        lineBuffer,
                        lineTooLong,
                        lineNumber,
                        lineOffset,
                        maximumRecords,
                        rows,
                        warnings,
                        ref recordsRead,
                        ref isComplete);

                    lineBuffer.Clear();
                    lineTooLong = false;
                    lineNumber++;
                    lineOffset = nextOffset;
                    segmentStart = index + 1;

                    if (shouldStop)
                    {
                        if (nextOffset < fileLength)
                        {
                            guard.MarkIncomplete();
                            isComplete = false;
                            warnings.Add(
                                $"Cursor transcript record limit {maximumRecords} was reached " +
                                $"in '{path}'.");
                        }

                        return new CursorTranscriptReadResult(
                            rows,
                            isComplete,
                            warnings.Snapshot());
                    }
                }

                AppendSegment(
                    lineBuffer,
                    readBuffer.AsSpan(segmentStart, count - segmentStart),
                    maximumLineBytes,
                    ref lineTooLong);
                processedBytes += count;
            }

            if (!boundedPrefix &&
                processedBytes >= bytesToRead &&
                (lineBuffer.WrittenCount > 0 || lineTooLong))
            {
                ProcessLine(
                    path,
                    lineBuffer,
                    lineTooLong,
                    lineNumber,
                    lineOffset,
                    maximumRecords,
                    rows,
                    warnings,
                    ref recordsRead,
                    ref isComplete);
            }
        }
        catch (Exception exception) when (_fileSystem.IsRecoverable(exception))
        {
            guard.MarkIncomplete();
            isComplete = false;
            warnings.Add($"Could not read Cursor transcript '{path}': {exception.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }

        return new CursorTranscriptReadResult(rows, isComplete, warnings.Snapshot());
    }

    private void AppendSegment(
        ArrayBufferWriter<byte> destination,
        ReadOnlySpan<byte> segment,
        int maximumLineBytes,
        ref bool lineTooLong)
    {
        if (lineTooLong || segment.IsEmpty)
        {
            return;
        }

        if (segment.Length > maximumLineBytes - destination.WrittenCount)
        {
            lineTooLong = true;
            destination.Clear();
            return;
        }

        segment.CopyTo(destination.GetSpan(segment.Length));
        destination.Advance(segment.Length);
    }

    private bool ProcessLine(
        string path,
        ArrayBufferWriter<byte> lineBuffer,
        bool lineTooLong,
        int lineNumber,
        long lineOffset,
        int maximumRecords,
        List<CursorTranscriptRow> rows,
        CursorWarningCollector warnings,
        ref int recordsRead,
        ref bool isComplete)
    {
        if (lineTooLong)
        {
            recordsRead++;
            isComplete = false;
            warnings.Add(
                $"Skipped Cursor transcript line {lineNumber} in '{path}' because it " +
                $"exceeds the configured {_limits.MaximumLineBytes} byte limit.");
            return recordsRead >= maximumRecords;
        }

        ReadOnlySpan<byte> bytes = lineBuffer.WrittenSpan;
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
        {
            bytes = bytes[..^1];
        }

        if (lineNumber == 1 &&
            bytes.Length >= 3 &&
            bytes[0] == 0xef &&
            bytes[1] == 0xbb &&
            bytes[2] == 0xbf)
        {
            bytes = bytes[3..];
        }

        if (bytes.IsEmpty)
        {
            return false;
        }

        recordsRead++;
        try
        {
            string raw = _utf8.GetString(bytes);
            using JsonDocument document = JsonDocument.Parse(
                raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 128
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                isComplete = false;
                warnings.Add(
                    $"Skipped non-object Cursor transcript record at " +
                    $"'{path}:{lineNumber}'.");
            }
            else
            {
                rows.Add(new CursorTranscriptRow(
                    raw,
                    lineNumber,
                    lineOffset,
                    document.RootElement.Clone()));
            }
        }
        catch (DecoderFallbackException exception)
        {
            isComplete = false;
            warnings.Add(
                $"Skipped invalid UTF-8 at '{path}:{lineNumber}': {exception.Message}");
        }
        catch (JsonException exception)
        {
            isComplete = false;
            warnings.Add(
                $"Skipped malformed Cursor JSONL at " +
                $"'{path}:{lineNumber}': {exception.Message}");
        }

        return recordsRead >= maximumRecords;
    }
}
