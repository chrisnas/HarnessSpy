using System.Text;
using HarnessSpy.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace HarnessSpy.Core.Services;

public sealed record SessionRawSourceContent(
    string Content,
    bool IsTruncated,
    string? Warning = null);

public interface ISessionRawSourceResolver
{
    Task<SessionRawSourceContent> LoadAsync(
        SessionSourceProvenance provenance,
        int maximumBytes,
        CancellationToken cancellationToken = default);
}

public sealed class SessionRawSourceResolver : ISessionRawSourceResolver
{
    private readonly SessionRawSnapshotCache? _snapshotCache;
    private readonly Func<bool>? _snapshotCacheEnabled;
    private readonly UTF8Encoding _utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    public SessionRawSourceResolver(
        SessionRawSnapshotCache? snapshotCache = null,
        Func<bool>? snapshotCacheEnabled = null)
    {
        _snapshotCache = snapshotCache;
        _snapshotCacheEnabled = snapshotCacheEnabled;
    }

    public async Task<SessionRawSourceContent> LoadAsync(
        SessionSourceProvenance provenance,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        if (provenance.RawContent is string inline)
        {
            return FromString(inline, maximumBytes);
        }

        SessionRawSourceReference? source = provenance.RawSource;
        if (source is null)
        {
            return new SessionRawSourceContent(
                string.Empty,
                false,
                "No reloadable raw source is associated with this record.");
        }

        bool useSnapshotCache =
            _snapshotCache is not null &&
            _snapshotCacheEnabled?.Invoke() == true;
        if (useSnapshotCache)
        {
            SessionRawSourceContent? cached = await _snapshotCache!
                .TryLoadAsync(
                    source,
                    maximumBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }
        }

        SessionRawSourceContent result = source.Kind switch
        {
            SessionRawSourceKind.JsonLine =>
                await LoadJsonLineAsync(
                    source,
                    maximumBytes,
                    cancellationToken).ConfigureAwait(false),
            SessionRawSourceKind.File =>
                await LoadFileAsync(
                    source,
                    maximumBytes,
                    cancellationToken).ConfigureAwait(false),
            SessionRawSourceKind.SqliteValue =>
                await LoadSqliteValueAsync(
                    source,
                    maximumBytes,
                    cancellationToken).ConfigureAwait(false),
            _ => new SessionRawSourceContent(
                string.Empty,
                false,
                "The raw source kind is not supported.")
        };
        if (useSnapshotCache &&
            !result.IsTruncated &&
            result.Warning is null)
        {
            await _snapshotCache!.StoreAsync(
                source,
                result.Content,
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<SessionRawSourceContent> LoadJsonLineAsync(
        SessionRawSourceReference source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (source.ByteOffset is not long offset)
        {
            return new SessionRawSourceContent(
                string.Empty,
                false,
                "The JSONL source has no byte offset.");
        }

        string? warning = SourceChangeWarning(source);
        await using FileStream stream = OpenRead(source.Path);
        if (offset > stream.Length)
        {
            return new SessionRawSourceContent(
                string.Empty,
                false,
                "The recorded byte offset is beyond the current file.");
        }

        stream.Seek(offset, SeekOrigin.Begin);
        int prefixAllowance = offset == 0 ? 3 : 0;
        int requested = source.ByteLength is int byteLength
            ? Math.Min(
                byteLength + prefixAllowance,
                maximumBytes + 1 + prefixAllowance)
            : maximumBytes + 1 + prefixAllowance;
        byte[] bytes = new byte[requested];
        int read = 0;
        while (read < requested)
        {
            int count = await stream.ReadAsync(
                bytes.AsMemory(read, requested - read),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            int newline = Array.IndexOf(bytes, (byte)'\n', read, count);
            if (newline >= 0)
            {
                read = newline;
                break;
            }

            read += count;
        }

        if (read > 0 && bytes[read - 1] == (byte)'\r')
        {
            read--;
        }

        int bomLength = source.ByteOffset == 0 &&
            read >= 3 &&
            bytes[0] == 0xef &&
            bytes[1] == 0xbb &&
            bytes[2] == 0xbf
                ? 3
                : 0;
        int available = Math.Max(0, read - bomLength);
        bool truncated =
            source.ByteLength is int knownLength
                ? knownLength > maximumBytes
                : available > maximumBytes;
        int decodeLength = Math.Min(available, maximumBytes);
        return new SessionRawSourceContent(
            _utf8.GetString(
                bytes,
                bomLength,
                decodeLength),
            truncated,
            warning);
    }

    private async Task<SessionRawSourceContent> LoadFileAsync(
        SessionRawSourceReference source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        string? warning = SourceChangeWarning(source);
        await using FileStream stream = OpenRead(source.Path);
        int requested = (int)Math.Min(
            stream.Length,
            maximumBytes + 1L);
        byte[] bytes = new byte[requested];
        int read = 0;
        while (read < requested)
        {
            int count = await stream.ReadAsync(
                bytes.AsMemory(read, requested - read),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        bool truncated = stream.Length > maximumBytes;
        int decodeLength = Math.Min(read, maximumBytes);
        int bomLength = decodeLength >= 3 &&
            bytes[0] == 0xef &&
            bytes[1] == 0xbb &&
            bytes[2] == 0xbf
                ? 3
                : 0;
        return new SessionRawSourceContent(
            _utf8.GetString(
                bytes,
                bomLength,
                decodeLength - bomLength),
            truncated,
            warning);
    }

    private async Task<SessionRawSourceContent> LoadSqliteValueAsync(
        SessionRawSourceReference source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.DatabaseTable) ||
            string.IsNullOrWhiteSpace(source.DatabaseKey))
        {
            return new SessionRawSourceContent(
                string.Empty,
                false,
                "The SQLite source has no table or key.");
        }

        string? warning = SourceChangeWarning(source);
        SqliteConnectionStringBuilder connectionString = new()
        {
            DataSource = source.Path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 2
        };
        await using SqliteConnection connection = new(
            connectionString.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        switch (source.DatabaseTable)
        {
            case "cursorDiskKV":
            case "ItemTable":
                command.CommandText =
                    $"SELECT value FROM \"{source.DatabaseTable}\" " +
                    "WHERE key = $key LIMIT 1";
                break;
            case "composerHeaders":
                command.CommandText =
                    "SELECT value FROM \"composerHeaders\" " +
                    "WHERE composerId = $key LIMIT 1";
                break;
            default:
                return new SessionRawSourceContent(
                    string.Empty,
                    false,
                    $"SQLite table '{source.DatabaseTable}' is not allowed.");
        }

        command.Parameters.AddWithValue("$key", source.DatabaseKey);
        object? value = await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (value is null || value is DBNull)
        {
            return new SessionRawSourceContent(
                string.Empty,
                false,
                "The recorded SQLite value is no longer available.");
        }

        string content = value switch
        {
            byte[] bytes => _utf8.GetString(bytes),
            _ => Convert.ToString(
                value,
                System.Globalization.CultureInfo.InvariantCulture) ??
                string.Empty
        };
        SessionRawSourceContent bounded = FromString(content, maximumBytes);
        return bounded with { Warning = warning };
    }

    private SessionRawSourceContent FromString(
        string content,
        int maximumBytes)
    {
        int byteCount = _utf8.GetByteCount(content);
        if (byteCount <= maximumBytes)
        {
            return new SessionRawSourceContent(content, false);
        }

        int characterCount = 0;
        int bytes = 0;
        foreach (Rune rune in content.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumBytes)
            {
                break;
            }

            bytes += rune.Utf8SequenceLength;
            characterCount += rune.Utf16SequenceLength;
        }

        return new SessionRawSourceContent(
            content[..characterCount],
            true);
    }

    private FileStream OpenRead(string path) =>
        new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 64 * 1024,
                Options =
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan
            });

    private string? SourceChangeWarning(SessionRawSourceReference source)
    {
        try
        {
            FileInfo file = new(source.Path);
            if (!file.Exists)
            {
                return "The source file is no longer available.";
            }

            bool lengthChanged =
                source.SourceLength is long expectedLength &&
                file.Length != expectedLength;
            bool timestampChanged =
                source.SourceLastWriteTimeUtc is DateTimeOffset expectedWrite &&
                new DateTimeOffset(
                    DateTime.SpecifyKind(
                        file.LastWriteTimeUtc,
                        DateTimeKind.Utc)) != expectedWrite;
            return lengthChanged || timestampChanged
                ? "The source changed since this catalog entry was scanned."
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return $"The source could not be inspected: {exception.Message}";
        }
    }
}
