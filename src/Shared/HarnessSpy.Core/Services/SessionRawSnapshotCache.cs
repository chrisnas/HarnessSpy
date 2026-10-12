using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using HarnessSpy.Core.Sessions;

namespace HarnessSpy.Core.Services;

public sealed class SessionRawSnapshotCache
{
    private readonly string _root;
    private readonly long _maximumBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly UTF8Encoding _utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    public SessionRawSnapshotCache(
        string? root = null,
        long maximumBytes = 256L * 1024 * 1024)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "HarnessSpy",
            "SessionViewer",
            "RawCache");
        _maximumBytes = Math.Max(0, maximumBytes);
    }

    public async Task<SessionRawSourceContent?> TryLoadAsync(
        SessionRawSourceReference source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        string path = CachePath(source);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using FileStream file = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using BrotliStream compressed = new(
                file,
                CompressionMode.Decompress,
                leaveOpen: false);
            using MemoryStream content = new(
                Math.Min(maximumBytes + 1, 1024 * 1024));
            byte[] buffer = new byte[32 * 1024];
            bool truncated = false;
            while (content.Length <= maximumBytes)
            {
                int count = await compressed.ReadAsync(
                    buffer,
                    cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                int accepted = (int)Math.Min(
                    count,
                    maximumBytes + 1L - content.Length);
                content.Write(buffer, 0, accepted);
                if (accepted < count || content.Length > maximumBytes)
                {
                    truncated = true;
                    break;
                }
            }

            int length = (int)Math.Min(content.Length, maximumBytes);
            return new SessionRawSourceContent(
                _utf8.GetString(content.GetBuffer(), 0, length),
                truncated);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException)
        {
            return null;
        }
    }

    public async Task StoreAsync(
        SessionRawSourceReference source,
        string content,
        CancellationToken cancellationToken)
    {
        if (_maximumBytes == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_root);
            string path = CachePath(source);
            string temporary = path + "." + Guid.NewGuid().ToString("N") +
                ".tmp";
            try
            {
                await using (FileStream file = new(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 32 * 1024,
                                 FileOptions.Asynchronous |
                                 FileOptions.SequentialScan))
                {
                    await using BrotliStream compressed = new(
                        file,
                        CompressionLevel.Fastest,
                        leaveOpen: false);
                    byte[] bytes = _utf8.GetBytes(content);
                    await compressed.WriteAsync(
                        bytes,
                        cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    private string CachePath(SessionRawSourceReference source)
    {
        string identity =
            $"{source.Kind}\0{source.Path}\0{source.ByteOffset}\0" +
            $"{source.ByteLength}\0{source.DatabaseTable}\0" +
            $"{source.DatabaseKey}\0{source.SourceLastWriteTimeUtc}\0" +
            $"{source.SourceLength}\0{source.ContentHash}";
        byte[] hash = SHA256.HashData(_utf8.GetBytes(identity));
        return Path.Combine(
            _root,
            Convert.ToHexStringLower(hash) + ".br");
    }

    private void Trim()
    {
        FileInfo[] files = new DirectoryInfo(_root)
            .EnumerateFiles("*.br", SearchOption.TopDirectoryOnly)
            .OrderBy(static file => file.LastAccessTimeUtc)
            .ToArray();
        long total = files.Sum(static file => file.Length);
        foreach (FileInfo file in files)
        {
            if (total <= _maximumBytes)
            {
                break;
            }

            try
            {
                long length = file.Length;
                file.Delete();
                total -= length;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
