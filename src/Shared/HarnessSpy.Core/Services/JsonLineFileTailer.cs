using System.IO;
using System.Text;

namespace HarnessSpy.Core.Services;

// One raw line read from a transcript file with its exact byte offset and line
// number, ready to become a TranscriptLine with provider context.
public sealed record TranscriptRawLine(
    string Raw,
    long ByteOffset,
    int LineNumber,
    long FileGeneration);

// Reads only the complete newline-terminated rows appended since the cursor's
// committed offset. Incomplete trailing bytes are left uncommitted and re-read
// next time, so a row half-written by the provider is never parsed early.
// Opens shared for read/write/delete so tailing never blocks the provider.
public static class JsonLineFileTailer
{
    public static IReadOnlyList<TranscriptRawLine> ReadNewLines(TranscriptReadCursor cursor)
    {
        if (!File.Exists(cursor.NormalizedPath))
        {
            return [];
        }

        byte[] tail;
        long startOffset;
        try
        {
            using FileStream stream = new(
                cursor.NormalizedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            long length = stream.Length;
            cursor.LastReadUtc = DateTimeOffset.UtcNow;

            // Truncation or replacement: the file shrank below what we already
            // consumed, so start a new generation and re-read from zero.
            if (length < cursor.CommittedOffset)
            {
                cursor.StartNewGeneration();
            }

            cursor.LastObservedLength = length;
            startOffset = cursor.CommittedOffset;
            if (length <= startOffset)
            {
                return [];
            }

            long toRead = length - startOffset;
            tail = new byte[toRead];
            stream.Seek(startOffset, SeekOrigin.Begin);
            int read = stream.Read(tail, 0, tail.Length);
            if (read < tail.Length)
            {
                Array.Resize(ref tail, read);
            }
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return SplitCompleteLines(cursor, tail, startOffset);
    }

    private static IReadOnlyList<TranscriptRawLine> SplitCompleteLines(
        TranscriptReadCursor cursor,
        byte[] tail,
        long startOffset)
    {
        List<TranscriptRawLine> lines = [];
        int lineStart = 0;

        for (int index = 0; index < tail.Length; index++)
        {
            if (tail[index] != (byte)'\n')
            {
                continue;
            }

            int end = index;
            if (end > lineStart && tail[end - 1] == (byte)'\r')
            {
                end--;
            }

            if (end > lineStart)
            {
                string raw = Encoding.UTF8.GetString(tail, lineStart, end - lineStart);

                // A provider file may begin with a UTF-8 BOM; strip it so the
                // first row is still valid JSON.
                raw = raw.TrimStart('\uFEFF');
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    lines.Add(new TranscriptRawLine(
                        raw,
                        startOffset + lineStart,
                        cursor.NextLineNumber,
                        cursor.FileGeneration));
                    cursor.NextLineNumber++;
                }
            }

            lineStart = index + 1;
        }

        // Everything up to the last consumed newline is now durable to re-read;
        // the incomplete remainder stays uncommitted.
        cursor.CommittedOffset = startOffset + lineStart;
        return lines;
    }
}
