using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HarnessSpy.Core.Sessions.Copilot;

internal enum CopilotSessionLayout
{
    Current,
    Legacy
}

internal sealed class CopilotSessionFileSource
{
    public CopilotSessionFileSource(
        string nativeSessionId,
        string eventsPath,
        string? workspacePath,
        CopilotSessionLayout layout,
        string? planPath = null)
    {
        NativeSessionId = nativeSessionId;
        EventsPath = eventsPath;
        WorkspacePath = workspacePath;
        Layout = layout;
        PlanPath = planPath;
    }

    public string NativeSessionId { get; }

    public string EventsPath { get; }

    public string? WorkspacePath { get; }

    public CopilotSessionLayout Layout { get; }

    public string? PlanPath { get; }
}

internal sealed class CopilotSourceDiscoveryResult
{
    public CopilotSourceDiscoveryResult(
        IReadOnlyList<CopilotSessionFileSource> sources,
        bool isComplete,
        IReadOnlyList<string> warnings)
    {
        Sources = sources;
        IsComplete = isComplete;
        Warnings = warnings;
    }

    public static CopilotSourceDiscoveryResult Empty { get; } =
        new([], true, []);

    public IReadOnlyList<CopilotSessionFileSource> Sources { get; }

    public bool IsComplete { get; }

    public IReadOnlyList<string> Warnings { get; }
}

internal sealed class CopilotSessionReadResult
{
    public CopilotSessionReadResult(
        SessionCatalogEntry? session,
        bool isComplete,
        IReadOnlyList<string> warnings,
        Plans.SessionPlanCatalogFragment? planFragment = null)
    {
        Session = session;
        IsComplete = isComplete;
        Warnings = warnings;
        PlanFragment = planFragment ?? Plans.SessionPlanCatalogFragment.Empty;
    }

    public SessionCatalogEntry? Session { get; }

    public bool IsComplete { get; }

    public IReadOnlyList<string> Warnings { get; }

    public Plans.SessionPlanCatalogFragment PlanFragment { get; }
}

internal sealed class CopilotWarningCollector
{
    private const int MaximumWarnings = 200;

    private readonly List<string> _warnings = [];
    private int _suppressedCount;

    public void Add(string warning)
    {
        if (_warnings.Count < MaximumWarnings)
        {
            _warnings.Add(warning);
        }
        else
        {
            _suppressedCount++;
        }
    }

    public void AddRange(IEnumerable<string> warnings)
    {
        foreach (string warning in warnings)
        {
            Add(warning);
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        return _suppressedCount == 0
            ? _warnings.ToArray()
            :
            [
                .. _warnings,
                $"{_suppressedCount} additional Copilot session warnings were suppressed."
            ];
    }
}

internal sealed class CopilotHomeResolver
{
    private readonly SessionDiscoveryContext _context;
    private readonly Regex _environmentVariablePattern =
        new("%(?<name>[^%]+)%", RegexOptions.CultureInvariant);

    public CopilotHomeResolver(SessionDiscoveryContext context)
    {
        _context = context;
    }

    public string Resolve()
    {
        string? configured = _context.GetEnvironmentVariable("COPILOT_HOME");
        string candidate = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(_context.UserProfile, ".copilot")
            : configured.Trim().Trim('"');

        candidate = _environmentVariablePattern.Replace(
            candidate,
            match => _context.GetEnvironmentVariable(match.Groups["name"].Value) ??
                match.Value);

        if (candidate.Equals("~", StringComparison.Ordinal))
        {
            candidate = _context.UserProfile;
        }
        else if (candidate.StartsWith(
            $"~{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal) ||
            candidate.StartsWith("~/", StringComparison.Ordinal))
        {
            candidate = Path.Combine(
                _context.UserProfile,
                candidate[2..].Replace(
                    Path.AltDirectorySeparatorChar,
                    Path.DirectorySeparatorChar));
        }

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException or
            System.Security.SecurityException)
        {
            return candidate;
        }
    }
}

internal sealed class CopilotFileSystemGuard
{
    public bool IsSafeDirectory(DirectoryInfo directory, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(directory);

        try
        {
            directory.Refresh();
            if (!directory.Exists)
            {
                reason = "the directory no longer exists";
                return false;
            }

            if ((directory.Attributes & FileAttributes.Directory) == 0)
            {
                reason = "the path is not a directory";
                return false;
            }

            if (IsLink(directory))
            {
                reason = "symbolic links and reparse points are not followed";
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
            reason = exception.Message;
            return false;
        }
    }

    public bool IsSafeFile(FileInfo file, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(file);

        try
        {
            file.Refresh();
            if (!file.Exists)
            {
                reason = "the file no longer exists";
                return false;
            }

            if ((file.Attributes & FileAttributes.Directory) != 0)
            {
                reason = "the path is not a regular file";
                return false;
            }

            if (IsLink(file))
            {
                reason = "symbolic links and reparse points are not followed";
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
            reason = exception.Message;
            return false;
        }
    }

    private static bool IsLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        try
        {
            return entry.LinkTarget is not null;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }
}

internal sealed class CopilotTimestampParser
{
    private static readonly DateTimeOffset _unixEpoch = DateTimeOffset.UnixEpoch;

    public DateTimeOffset? Parse(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => Parse(value.GetString()),
            JsonValueKind.Number when value.TryGetDecimal(out decimal number) =>
                ParseNumeric(number),
            _ => null
        };
    }

    public DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (decimal.TryParse(
            trimmed,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out decimal numeric))
        {
            return ParseNumeric(numeric);
        }

        if (DateTimeOffset.TryParse(
            trimmed,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces |
            DateTimeStyles.AssumeUniversal |
            DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset timestamp))
        {
            return timestamp.ToUniversalTime();
        }

        return null;
    }

    public DateTimeOffset? Parse(object? value)
    {
        return value switch
        {
            null => null,
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            DateTime timestamp => new DateTimeOffset(
                timestamp.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
                    : timestamp.ToUniversalTime()),
            IFormattable formattable => Parse(
                formattable.ToString(null, CultureInfo.InvariantCulture)),
            _ => Parse(value.ToString())
        };
    }

    private static DateTimeOffset? ParseNumeric(decimal value)
    {
        decimal absolute = Math.Abs(value);
        decimal seconds = absolute switch
        {
            >= 100_000_000_000_000_000m => value / 1_000_000_000m,
            >= 100_000_000_000_000m => value / 1_000_000m,
            >= 100_000_000_000m => value / 1_000m,
            _ => value
        };

        try
        {
            decimal ticks = seconds * TimeSpan.TicksPerSecond;
            if (ticks > long.MaxValue || ticks < long.MinValue)
            {
                return null;
            }

            return _unixEpoch.AddTicks(decimal.ToInt64(decimal.Truncate(ticks)));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

internal sealed class CopilotJsonValueReader
{
    public bool TryGetProperty(
        JsonElement element,
        out JsonElement value,
        params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out value))
            {
                return true;
            }
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (names.Any(name => property.Name.Equals(
                name,
                StringComparison.OrdinalIgnoreCase)))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public string? String(JsonElement element, params string[] names)
    {
        return TryGetProperty(element, out JsonElement value, names)
            ? String(value)
            : null;
    }

    public string? String(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => NullIfWhiteSpace(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    public string? Text(JsonElement element, params string[] names)
    {
        return TryGetProperty(element, out JsonElement value, names)
            ? Text(value)
            : null;
    }

    public string? Text(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return NullIfEmpty(value.GetString());

            case JsonValueKind.Array:
            {
                List<string> parts = [];
                foreach (JsonElement item in value.EnumerateArray())
                {
                    string? text = Text(item);
                    if (!string.IsNullOrEmpty(text))
                    {
                        parts.Add(text);
                    }
                }

                return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
            }

            case JsonValueKind.Object:
                return Text(value, "text", "content", "message", "deltaContent");

            default:
                return null;
        }
    }

    public bool? Boolean(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out JsonElement value, names))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(
                value.GetString(),
                out bool parsed) => parsed,
            _ => null
        };
    }

    public long? Integer(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out JsonElement value, names))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out long integer))
        {
            return integer;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out decimal number) &&
            number == decimal.Truncate(number) &&
            number is >= long.MinValue and <= long.MaxValue)
        {
            return decimal.ToInt64(number);
        }

        return value.ValueKind == JsonValueKind.String &&
            long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out integer)
                    ? integer
                    : null;
    }

    public double? Number(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out JsonElement value, names))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out double number) &&
            double.IsFinite(number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
            double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number) &&
            double.IsFinite(number)
                    ? number
                    : null;
    }

    public JsonElement? Object(
        JsonElement element,
        params string[] names)
    {
        return TryGetProperty(element, out JsonElement value, names) &&
            value.ValueKind == JsonValueKind.Object
                ? value
                : null;
    }

    public JsonElement? Array(
        JsonElement element,
        params string[] names)
    {
        return TryGetProperty(element, out JsonElement value, names) &&
            value.ValueKind == JsonValueKind.Array
                ? value
                : null;
    }

    public string ScalarText(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number or
            JsonValueKind.True or
            JsonValueKind.False => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;
}

internal sealed class CopilotUtf8Decoder
{
    private readonly UTF8Encoding _strictEncoding = new(false, true);
    private readonly UTF8Encoding _replacementEncoding = new(false, false);

    public string Decode(
        ReadOnlySpan<byte> bytes,
        out bool hadInvalidEncoding)
    {
        try
        {
            hadInvalidEncoding = false;
            return _strictEncoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            hadInvalidEncoding = true;
            return _replacementEncoding.GetString(bytes);
        }
    }
}
