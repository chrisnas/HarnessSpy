using System.IO;
using System.Text;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Plans;

public sealed record SessionPlanFileReadResult(
    bool Exists,
    string? Content,
    DateTimeOffset LastWriteTimeUtc,
    long Length,
    SessionSourceProvenance? Provenance,
    bool IsComplete,
    IReadOnlyList<string> Warnings);

// Reads a provider-owned plan markdown file read-only and bounded by the shared
// discovery limits. Never follows reparse points and never blocks a provider
// that keeps the file open for writing.
public sealed class SessionPlanFileReader
{
    private readonly SessionDiscoveryLimits _limits;

    public SessionPlanFileReader(SessionDiscoveryLimits limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public async Task<SessionPlanFileReadResult> ReadAsync(
        string path,
        SessionSourceKind sourceKind,
        string dialectId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FileInfo file = new(path);
        if (!IsSafeRegularFile(file, out string? safetyReason))
        {
            return new SessionPlanFileReadResult(
                Exists: file.Exists,
                Content: null,
                LastWriteTimeUtc: default,
                Length: 0,
                Provenance: null,
                IsComplete: false,
                Warnings: safetyReason is null ? [] : [safetyReason]);
        }

        long maximumFileBytes = Math.Max(0, _limits.MaximumFileBytes);
        long snapshotLength = file.Length;
        if (snapshotLength > maximumFileBytes)
        {
            return new SessionPlanFileReadResult(
                Exists: true,
                Content: null,
                LastWriteTimeUtc: ToUtc(file.LastWriteTimeUtc),
                Length: snapshotLength,
                Provenance: null,
                IsComplete: false,
                Warnings:
                [
                    $"Skipped plan file '{path}' because its {snapshotLength} " +
                    $"bytes exceed the {maximumFileBytes}-byte limit."
                ]);
        }

        List<string> warnings = [];
        bool isComplete = true;
        byte[] bytes = new byte[(int)Math.Min(snapshotLength, int.MaxValue)];
        int totalRead = 0;
        try
        {
            await using FileStream stream = new(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (totalRead < bytes.Length)
            {
                int read = await stream
                    .ReadAsync(bytes.AsMemory(totalRead), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            System.Security.SecurityException)
        {
            return new SessionPlanFileReadResult(
                Exists: true,
                Content: null,
                LastWriteTimeUtc: ToUtc(file.LastWriteTimeUtc),
                Length: snapshotLength,
                Provenance: null,
                IsComplete: false,
                Warnings: [$"Could not read plan file '{path}': {exception.Message}"]);
        }

        if (totalRead != bytes.Length)
        {
            isComplete = false;
            warnings.Add(
                $"Plan file '{path}' changed while it was read; the available " +
                "snapshot was used.");
        }

        string content = DecodeUtf8(
            bytes.AsSpan(0, totalRead),
            out bool hadInvalidEncoding);
        if (hadInvalidEncoding)
        {
            isComplete = false;
            warnings.Add(
                $"Plan file '{path}' contains invalid UTF-8; replacement " +
                "characters were used.");
        }

        SessionSourceProvenance provenance = new(
            sourceKind,
            file.FullName,
            "markdown",
            content,
            ContractVersion: dialectId);

        return new SessionPlanFileReadResult(
            Exists: true,
            Content: content,
            LastWriteTimeUtc: ToUtc(file.LastWriteTimeUtc),
            Length: snapshotLength,
            Provenance: provenance,
            IsComplete: isComplete,
            Warnings: warnings);
    }

    private static bool IsSafeRegularFile(FileInfo file, out string? reason)
    {
        try
        {
            if (!file.Exists)
            {
                reason = null;
                return false;
            }

            FileAttributes attributes = file.Attributes;
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                reason = $"Skipped plan path '{file.FullName}' because it is a directory.";
                return false;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                reason =
                    $"Skipped plan file '{file.FullName}' because it is a symbolic link.";
                return false;
            }

            reason = null;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            System.Security.SecurityException)
        {
            reason = $"Skipped plan file '{file.FullName}': {exception.Message}";
            return false;
        }
    }

    private static string DecodeUtf8(
        ReadOnlySpan<byte> bytes,
        out bool hadInvalidEncoding)
    {
        UTF8Encoding strict = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        try
        {
            hadInvalidEncoding = false;
            return strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            hadInvalidEncoding = true;
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
