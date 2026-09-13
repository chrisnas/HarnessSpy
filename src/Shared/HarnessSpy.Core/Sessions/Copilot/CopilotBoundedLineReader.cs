using System.Buffers;

namespace HarnessSpy.Core.Sessions.Copilot;

internal sealed class CopilotBoundedLine
{
    public CopilotBoundedLine(
        string? content,
        long byteOffset,
        bool isTerminated,
        bool isTooLong,
        bool hasInvalidEncoding)
    {
        Content = content;
        ByteOffset = byteOffset;
        IsTerminated = isTerminated;
        IsTooLong = isTooLong;
        HasInvalidEncoding = hasInvalidEncoding;
    }

    public string? Content { get; }

    public long ByteOffset { get; }

    public bool IsTerminated { get; }

    public bool IsTooLong { get; }

    public bool HasInvalidEncoding { get; }
}

/// <summary>
/// Reads a fixed snapshot of an actively appended JSONL file while bounding
/// each physical line before decoding it.
/// </summary>
internal sealed class CopilotBoundedLineReader : IAsyncDisposable
{
    private const int DefaultBufferSize = 64 * 1024;

    private readonly FileStream _stream;
    private readonly int _maximumLineBytes;
    private readonly long _snapshotLength;
    private readonly byte[] _readBuffer;
    private readonly ArrayBufferWriter<byte> _lineBuffer;
    private readonly CopilotUtf8Decoder _decoder;

    private int _readBufferOffset;
    private int _readBufferLength;
    private long _bytesReadFromStream;
    private long _bytesConsumed;
    private long _lineStartOffset;
    private bool _discardingOversizedLine;
    private bool _isFirstLine = true;

    public CopilotBoundedLineReader(
        FileStream stream,
        int maximumLineBytes,
        long snapshotLength)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _stream = stream;
        _maximumLineBytes = Math.Max(0, maximumLineBytes);
        _snapshotLength = Math.Max(0, snapshotLength);
        _readBuffer = new byte[DefaultBufferSize];
        _lineBuffer = new ArrayBufferWriter<byte>(
            Math.Min(Math.Max(_maximumLineBytes, 256), DefaultBufferSize));
        _decoder = new CopilotUtf8Decoder();
    }

    public bool WasTruncated { get; private set; }

    public async ValueTask<CopilotBoundedLine?> ReadLineAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_readBufferOffset >= _readBufferLength)
            {
                bool readMore = await RefillAsync(cancellationToken).ConfigureAwait(false);
                if (!readMore)
                {
                    if (_lineBuffer.WrittenCount == 0 && !_discardingOversizedLine)
                    {
                        return null;
                    }

                    return CompleteLine(isTerminated: false);
                }
            }

            ReadOnlySpan<byte> available =
                _readBuffer.AsSpan(
                    _readBufferOffset,
                    _readBufferLength - _readBufferOffset);
            int newlineIndex = available.IndexOf((byte)'\n');
            int contentLength = newlineIndex >= 0
                ? newlineIndex
                : available.Length;

            AppendOrDiscard(available[..contentLength]);
            _readBufferOffset += contentLength;
            _bytesConsumed += contentLength;

            if (newlineIndex < 0)
            {
                continue;
            }

            _readBufferOffset++;
            _bytesConsumed++;
            return CompleteLine(isTerminated: true);
        }
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    private async ValueTask<bool> RefillAsync(
        CancellationToken cancellationToken)
    {
        long remaining = _snapshotLength - _bytesReadFromStream;
        if (remaining <= 0)
        {
            return false;
        }

        int requested = (int)Math.Min(_readBuffer.Length, remaining);
        int count = await _stream.ReadAsync(
            _readBuffer.AsMemory(0, requested),
            cancellationToken).ConfigureAwait(false);

        _readBufferOffset = 0;
        _readBufferLength = count;
        _bytesReadFromStream += count;
        if (count == 0 && _bytesReadFromStream < _snapshotLength)
        {
            WasTruncated = true;
        }

        return count > 0;
    }

    private void AppendOrDiscard(ReadOnlySpan<byte> bytes)
    {
        if (_discardingOversizedLine || bytes.Length == 0)
        {
            return;
        }

        if (bytes.Length > _maximumLineBytes - _lineBuffer.WrittenCount)
        {
            _lineBuffer.Clear();
            _discardingOversizedLine = true;
            return;
        }

        Span<byte> destination = _lineBuffer.GetSpan(bytes.Length);
        bytes.CopyTo(destination);
        _lineBuffer.Advance(bytes.Length);
    }

    private CopilotBoundedLine CompleteLine(bool isTerminated)
    {
        long byteOffset = _lineStartOffset;
        _lineStartOffset = _bytesConsumed;

        if (_discardingOversizedLine)
        {
            _discardingOversizedLine = false;
            _lineBuffer.Clear();
            _isFirstLine = false;
            return new CopilotBoundedLine(
                null,
                byteOffset,
                isTerminated,
                isTooLong: true,
                hasInvalidEncoding: false);
        }

        ReadOnlySpan<byte> bytes = _lineBuffer.WrittenSpan;
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
        {
            bytes = bytes[..^1];
        }

        if (_isFirstLine &&
            bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        string content = _decoder.Decode(bytes, out bool hadInvalidEncoding);
        _lineBuffer.Clear();
        _isFirstLine = false;

        return new CopilotBoundedLine(
            content,
            byteOffset,
            isTerminated,
            isTooLong: false,
            hadInvalidEncoding);
    }
}
