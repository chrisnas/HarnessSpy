using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HarnessSpy.Core.Sessions.Plans;

// Normalizes plan markdown so identical content produces one stable hash while
// semantic markdown detail is preserved. Trailing spaces (markdown line breaks),
// indentation, and internal blank lines are kept; only the byte-order mark,
// Unicode form, line-ending style, and a trailing newline run are normalized.
public sealed class SessionPlanContentNormalizer
{
    public string Normalize(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        string value = markdown;
        if (value.Length > 0 && value[0] == '\uFEFF')
        {
            value = value[1..];
        }

        value = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        // Leading/trailing newline runs are not semantic (a plan body written
        // after YAML front matter gains a leading blank line the CreatePlan
        // input does not have); drop them so the bodies hash the same. Trailing
        // spaces on a content line and internal blank lines are kept.
        value = value.Trim('\n');

        if (!value.IsNormalized(NormalizationForm.FormC))
        {
            value = value.Normalize(NormalizationForm.FormC);
        }

        return value;
    }

    public string Hash(string normalized)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }

    // Returns a snapshot for the raw content, or null when the content is empty
    // after normalization.
    public SessionPlanContentSnapshot? Snapshot(string? markdown)
    {
        string normalized = Normalize(markdown);
        if (normalized.Length == 0)
        {
            return null;
        }

        return new SessionPlanContentSnapshot(normalized, Hash(normalized));
    }

    public string ShortHash(string hash) =>
        hash.Length <= 12
            ? hash
            : hash[..12];

    public string DescribeSequence(int sequence) =>
        sequence.ToString(CultureInfo.InvariantCulture);
}
