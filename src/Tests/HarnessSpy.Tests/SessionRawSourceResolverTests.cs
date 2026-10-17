using System.Text;
using HarnessSpy.Core.Services;
using HarnessSpy.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace HarnessSpy.Tests;

public sealed class SessionRawSourceResolverTests
{
    private readonly SessionRawSourceResolver _resolver = new();

    [Fact]
    public async Task LoadsBoundedJsonLineAtByteOffset()
    {
        string path = TemporaryPath(".jsonl");
        const string first = "{\"id\":1}";
        const string second = "{\"message\":\"caf\u00e9\"}";
        await File.WriteAllTextAsync(
            path,
            first + "\n" + second + "\n",
            new UTF8Encoding(false));
        FileInfo file = new(path);
        SessionSourceProvenance provenance = new(
            SessionSourceKind.CursorTranscriptJsonl,
            path,
            "jsonl",
            LineNumber: 2,
            ByteOffset: Encoding.UTF8.GetByteCount(first + "\n"),
            RawSource: new SessionRawSourceReference(
                SessionRawSourceKind.JsonLine,
                path,
                Encoding.UTF8.GetByteCount(first + "\n"),
                Encoding.UTF8.GetByteCount(second),
                SourceLastWriteTimeUtc: Utc(file.LastWriteTimeUtc),
                SourceLength: file.Length));
        try
        {
            SessionRawSourceContent result = await _resolver.LoadAsync(
                provenance,
                1024);

            Assert.Equal(second, result.Content);
            Assert.False(result.IsTruncated);
            Assert.Null(result.Warning);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadsBoundedFileAndReportsTruncation()
    {
        string path = TemporaryPath(".md");
        await File.WriteAllTextAsync(
            path,
            "0123456789",
            new UTF8Encoding(false));
        SessionSourceProvenance provenance = new(
            SessionSourceKind.CursorPlanMarkdown,
            path,
            "markdown",
            RawSource: new SessionRawSourceReference(
                SessionRawSourceKind.File,
                path));
        try
        {
            SessionRawSourceContent result = await _resolver.LoadAsync(
                provenance,
                4);

            Assert.Equal("0123", result.Content);
            Assert.True(result.IsTruncated);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadsCursorSqliteValueByTableAndKey()
    {
        string path = TemporaryPath(".vscdb");
        await using (SqliteConnection connection = new(
                         $"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value TEXT);
                INSERT INTO cursorDiskKV (key, value)
                VALUES ('bubbleId:session:b1', '{"text":"result"}');
                """;
            await command.ExecuteNonQueryAsync();
        }

        SessionSourceProvenance provenance = new(
            SessionSourceKind.CursorDesktopSqlite,
            path,
            "json",
            DatabaseKey: "bubbleId:session:b1",
            RawSource: new SessionRawSourceReference(
                SessionRawSourceKind.SqliteValue,
                path,
                DatabaseTable: "cursorDiskKV",
                DatabaseKey: "bubbleId:session:b1"));
        try
        {
            SessionRawSourceContent result = await _resolver.LoadAsync(
                provenance,
                1024);

            Assert.Equal("""{"text":"result"}""", result.Content);
            Assert.False(result.IsTruncated);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SnapshotCacheServesSourceAfterFileIsRemoved()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"HarnessSpy-RawCache-{Guid.NewGuid():N}");
        string path = TemporaryPath(".jsonl");
        const string content = """{"cached":true}""";
        await File.WriteAllTextAsync(
            path,
            content + "\n",
            new UTF8Encoding(false));
        SessionSourceProvenance provenance = new(
            SessionSourceKind.CursorTranscriptJsonl,
            path,
            "jsonl",
            ByteOffset: 0,
            RawSource: new SessionRawSourceReference(
                SessionRawSourceKind.JsonLine,
                path,
                ByteOffset: 0,
                ByteLength: Encoding.UTF8.GetByteCount(content)));
        SessionRawSourceResolver resolver = new(
            new SessionRawSnapshotCache(root, maximumBytes: 1024 * 1024),
            () => true);
        try
        {
            SessionRawSourceContent initial = await resolver.LoadAsync(
                provenance,
                1024);
            File.Delete(path);

            SessionRawSourceContent cached = await resolver.LoadAsync(
                provenance,
                1024);

            Assert.Equal(content, initial.Content);
            Assert.Equal(content, cached.Content);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string TemporaryPath(string extension) =>
        Path.Combine(
            Path.GetTempPath(),
            $"HarnessSpy-RawSource-{Guid.NewGuid():N}{extension}");

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
