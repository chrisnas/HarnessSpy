using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CursorSpy.Hook;

public interface IHookDiagnostics
{
    // Returns the full path of the payload file that was written, or null if the
    // payload could not be persisted. Callers forward this path so the viewer can
    // associate a live (pipe-delivered) observation with its on-disk file.
    Task<string?> SavePayloadAsync(string sessionId, string hookEventName, string rawPayload, CancellationToken cancellationToken);

    Task LogErrorAsync(string context, Exception exception, CancellationToken cancellationToken);
}

public sealed class NullHookDiagnostics : IHookDiagnostics
{
    public static NullHookDiagnostics Instance { get; } = new();

    public Task<string?> SavePayloadAsync(string sessionId, string hookEventName, string rawPayload, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public Task LogErrorAsync(string context, Exception exception, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class FileHookDiagnostics(string? directory = null) : IHookDiagnostics
{
    public const string ErrorLogFileName = "cursorspy-hook-errors.log";
    public const string PayloadFilePrefix = "hp_";

    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _directory = directory ?? AppContext.BaseDirectory;

    public async Task<string?> SavePayloadAsync(string sessionId, string hookEventName, string rawPayload, CancellationToken cancellationToken)
    {
        try
        {
            string payloadsDirectory = Path.Combine(_directory, "Payloads");
            Directory.CreateDirectory(payloadsDirectory);

            string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            string fileName = $"{PayloadFilePrefix}{Sanitize(sessionId, "unknownSession")}_{Sanitize(hookEventName, "unknownHook")}_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{uniqueSuffix}.json";
            string path = Path.Combine(payloadsDirectory, fileName);

            await File.WriteAllTextAsync(path, ToReadableJson(rawPayload), cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch
        {
            // Diagnostics are best-effort and must never affect Cursor.
            return null;
        }
    }

    // Cursor sends payloads with strict escapes (e.g. \u0027, \u0060, \u2014). Re-serialize
    // with the relaxed encoder so saved files are human-readable, falling back to the raw
    // string if it is not valid JSON so we never lose the diagnostic record.
    private static string ToReadableJson(string rawPayload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(rawPayload);
            return JsonSerializer.Serialize(document.RootElement, ReadableJson);
        }
        catch (JsonException)
        {
            return rawPayload;
        }
    }

    public async Task LogErrorAsync(string context, Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_directory);

            string path = Path.Combine(_directory, ErrorLogFileName);
            string entry =
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {context}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}{Environment.NewLine}";

            await AppendWithRetryAsync(path, entry, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics are best-effort and must never affect Cursor.
        }
    }

    private static async Task AppendWithRetryAsync(string path, string content, CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        byte[] bytes = Encoding.UTF8.GetBytes(content);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                // FileShare.ReadWrite lets concurrent hook processes append to the shared log.
                await using FileStream stream = new(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);

                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string Sanitize(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        char[] buffer = value.ToCharArray();
        char[] invalid = Path.GetInvalidFileNameChars();

        for (int i = 0; i < buffer.Length; i++)
        {
            if (Array.IndexOf(invalid, buffer[i]) >= 0)
            {
                buffer[i] = '_';
            }
        }

        return new string(buffer);
    }
}
