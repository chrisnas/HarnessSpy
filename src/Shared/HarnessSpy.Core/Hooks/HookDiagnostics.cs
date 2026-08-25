using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Hooks;

public interface IHookDiagnostics
{
    Task<string?> SavePayloadAsync(
        string sessionId,
        string hookEventName,
        string rawPayload,
        CancellationToken cancellationToken);

    Task<string?> SaveObservationAsync(
        HookProvider provider,
        string sessionId,
        string hookEventName,
        ObservationEnvelope envelope,
        CancellationToken cancellationToken);

    Task LogErrorAsync(
        string context,
        Exception exception,
        CancellationToken cancellationToken);
}

public sealed class NullHookDiagnostics : IHookDiagnostics
{
    public static NullHookDiagnostics Instance { get; } = new();

    public Task<string?> SavePayloadAsync(
        string sessionId,
        string hookEventName,
        string rawPayload,
        CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<string?> SaveObservationAsync(
        HookProvider provider,
        string sessionId,
        string hookEventName,
        ObservationEnvelope envelope,
        CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task LogErrorAsync(
        string context,
        Exception exception,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class FileHookDiagnostics(
    string? directory = null,
    string errorLogFileName = FileHookDiagnostics.ErrorLogFileName) : IHookDiagnostics
{
    public const string ErrorLogFileName = "harnessspy-hook-errors.log";
    public const string PayloadFilePrefix = "hs_";

    private static readonly JsonSerializerOptions ReadableJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _directory = directory ?? AppContext.BaseDirectory;

    public async Task<string?> SavePayloadAsync(
        string sessionId,
        string hookEventName,
        string rawPayload,
        CancellationToken cancellationToken)
    {
        return await SaveTextAsync(
            providerName: "legacy",
            sessionId,
            hookEventName,
            ToReadableJson(rawPayload),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> SaveObservationAsync(
        HookProvider provider,
        string sessionId,
        string hookEventName,
        ObservationEnvelope envelope,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(envelope, ReadableJson);
        return await SaveTextAsync(
            provider.ToString(),
            sessionId,
            hookEventName,
            json,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task LogErrorAsync(
        string context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, errorLogFileName);
            string entry =
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {context}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}{Environment.NewLine}";
            await AppendWithRetryAsync(path, entry, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics must never affect the observed host.
        }
    }

    public static string GetDefaultDirectory(ProviderProfile profile)
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(
            localAppData,
            "HarnessSpy",
            profile.SettingsFolderName);
    }

    private async Task<string?> SaveTextAsync(
        string providerName,
        string sessionId,
        string hookEventName,
        string content,
        CancellationToken cancellationToken)
    {
        try
        {
            string payloadsDirectory = Path.Combine(_directory, "Payloads");
            Directory.CreateDirectory(payloadsDirectory);

            string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            string fileName =
                $"{PayloadFilePrefix}{Sanitize(providerName, "unknownProvider")}_" +
                $"{Sanitize(sessionId, "unknownSession")}_" +
                $"{Sanitize(hookEventName, "unknownHook")}_" +
                $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{uniqueSuffix}.json";
            string path = Path.Combine(payloadsDirectory, fileName);
            await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch
        {
            return null;
        }
    }

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

    private static async Task AppendWithRetryAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        byte[] bytes = Encoding.UTF8.GetBytes(content);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
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
        for (int index = 0; index < buffer.Length; index++)
        {
            if (Array.IndexOf(invalid, buffer[index]) >= 0)
            {
                buffer[index] = '_';
            }
        }

        return new string(buffer);
    }
}
