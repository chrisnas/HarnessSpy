using System.Buffers;
using System.Text;
using System.Text.Json;

namespace HarnessSpy.Core.Sessions.Claude;

internal sealed class ClaudeTranscriptRow
{
    public ClaudeTranscriptRow(
        string path,
        string rawContent,
        int lineNumber,
        long byteOffset,
        JsonElement json)
    {
        Path = path;
        RawContent = rawContent;
        LineNumber = lineNumber;
        ByteOffset = byteOffset;
        Json = json;
    }

    public string Path { get; }

    public string RawContent { get; }

    public int LineNumber { get; }

    public long ByteOffset { get; }

    public JsonElement Json { get; }
}

internal sealed class ClaudeTranscriptReadResult
{
    public ClaudeTranscriptReadResult(
        IReadOnlyList<ClaudeTranscriptRow> rows,
        bool isComplete,
        IReadOnlyList<string> warnings,
        int recordsRead)
    {
        Rows = rows;
        IsComplete = isComplete;
        Warnings = warnings;
        RecordsRead = recordsRead;
    }

    public IReadOnlyList<ClaudeTranscriptRow> Rows { get; }

    public bool IsComplete { get; }

    public IReadOnlyList<string> Warnings { get; }

    public int RecordsRead { get; }
}

internal sealed class ClaudeTranscriptFileReader
{
    private const int ReadBufferSize = 64 * 1024;

    private readonly SessionDiscoveryLimits _limits;
    private readonly UTF8Encoding _utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public ClaudeTranscriptFileReader(SessionDiscoveryLimits limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public async Task<ClaudeTranscriptReadResult> ReadAsync(
        string path,
        int maximumRecords,
        Func<bool> deadlineExceeded,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(deadlineExceeded);

        List<ClaudeTranscriptRow> rows = [];
        List<string> warnings = [];
        bool isComplete = true;
        int recordsRead = 0;

        if (maximumRecords <= 0)
        {
            warnings.Add($"Claude transcript record limit reached before reading '{path}'.");
            return new ClaudeTranscriptReadResult(rows, false, warnings, recordsRead);
        }

        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
            {
                warnings.Add($"Claude transcript disappeared before it could be read: '{path}'.");
                return new ClaudeTranscriptReadResult(rows, false, warnings, recordsRead);
            }
        }
        catch (Exception exception) when (IsRecoverableIoException(exception))
        {
            warnings.Add($"Could not inspect Claude transcript '{path}': {exception.Message}");
            return new ClaudeTranscriptReadResult(rows, false, warnings, recordsRead);
        }

        long maximumFileBytes = Math.Max(0, _limits.MaximumFileBytes);
        long bytesToRead = Math.Min(file.Length, maximumFileBytes);
        if (file.Length > maximumFileBytes)
        {
            isComplete = false;
            warnings.Add(
                $"Claude transcript '{path}' is {file.Length} bytes; only the configured " +
                $"{maximumFileBytes} byte prefix will be inspected.");
        }

        if (bytesToRead == 0)
        {
            return new ClaudeTranscriptReadResult(rows, isComplete, warnings, recordsRead);
        }

        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        ArrayBufferWriter<byte> lineBuffer = new(
            Math.Min(Math.Max(_limits.MaximumLineBytes, 1), ReadBufferSize));
        bool lineTooLong = false;
        int lineNumber = 1;
        long lineOffset = 0;
        long processedBytes = 0;

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
                if (deadlineExceeded())
                {
                    isComplete = false;
                    warnings.Add($"Claude transcript scan deadline reached while reading '{path}'.");
                    break;
                }

                int requested = (int)Math.Min(readBuffer.Length, bytesToRead - processedBytes);
                int count = await stream.ReadAsync(
                    readBuffer.AsMemory(0, requested),
                    cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    if (processedBytes < bytesToRead)
                    {
                        isComplete = false;
                        warnings.Add($"Claude transcript '{path}' was truncated while being read.");
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
                        if (nextOffset < file.Length)
                        {
                            isComplete = false;
                            warnings.Add(
                                $"Claude transcript record limit {maximumRecords} reached in '{path}'.");
                        }

                        return new ClaudeTranscriptReadResult(
                            rows,
                            isComplete,
                            warnings,
                            recordsRead);
                    }
                }

                AppendSegment(
                    lineBuffer,
                    readBuffer.AsSpan(segmentStart, count - segmentStart),
                    ref lineTooLong);
                processedBytes += count;
            }

            if (processedBytes >= bytesToRead &&
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
        catch (Exception exception) when (IsRecoverableIoException(exception))
        {
            isComplete = false;
            warnings.Add($"Could not read Claude transcript '{path}': {exception.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }

        return new ClaudeTranscriptReadResult(rows, isComplete, warnings, recordsRead);
    }

    private void AppendSegment(
        ArrayBufferWriter<byte> destination,
        ReadOnlySpan<byte> segment,
        ref bool lineTooLong)
    {
        if (lineTooLong || segment.Length == 0)
        {
            return;
        }

        int maximumLineBytes = Math.Max(0, _limits.MaximumLineBytes);
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
        List<ClaudeTranscriptRow> rows,
        List<string> warnings,
        ref int recordsRead,
        ref bool isComplete)
    {
        if (lineTooLong)
        {
            recordsRead++;
            isComplete = false;
            warnings.Add(
                $"Skipped Claude transcript line {lineNumber} in '{path}' because it exceeds " +
                $"the configured {_limits.MaximumLineBytes} byte limit.");
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
                    $"Skipped non-object Claude transcript record at '{path}:{lineNumber}'.");
            }
            else
            {
                rows.Add(new ClaudeTranscriptRow(
                    path,
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
                $"Skipped malformed Claude JSONL at '{path}:{lineNumber}': {exception.Message}");
        }

        return recordsRead >= maximumRecords;
    }

    private static bool IsRecoverableIoException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
        NotSupportedException or System.Security.SecurityException;
}
