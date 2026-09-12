using System.Security.Cryptography;
using System.Text;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Sessions.Plans;

// The kind of plan activity observed inside a turn. "Referenced" never implies
// ownership: it records that a session read or pointed at a plan another
// session created.
public enum SessionPlanActivityKind
{
    Created,
    Updated,
    Referenced
}

// Why a plan artifact was (or was not) bound to a session. Kept explicit so the
// inspector can show the exact evidence and heuristic bindings are never
// presented with the same authority as an observed one.
public enum SessionPlanBindingReason
{
    Unbound,
    CopilotSessionDirectory,
    ExplicitPlanPath,
    ProviderCreateOrWriteEvent,
    CursorStructuredContentMatch,
    Ambiguous,
    Unavailable
}

// Locates a plan before the assembler has assigned a catalog id. A locator is
// either path-based (Claude/Copilot) or structured-content-based (Cursor
// CreatePlan, which does not reliably carry the resulting file path).
public sealed record SessionPlanLocator(
    string? Path = null,
    string? StructuredKey = null)
{
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Path) &&
        string.IsNullOrWhiteSpace(StructuredKey);
}

// A single, fully materialized plan body observation with its normalized hash.
public sealed record SessionPlanContentSnapshot(
    string NormalizedContent,
    string ContentHash);

// One provider-observed plan activity (create/update/reference) that has not yet
// been correlated to a catalog plan id. The provider fills the session/turn it
// belongs to; the assembler resolves it against discovered artifacts.
public sealed record SessionPlanActivity
{
    public required string Id { get; init; }

    public required SessionPlanActivityKind Kind { get; init; }

    public required HookProvider Provider { get; init; }

    public string? CatalogSessionId { get; init; }

    public string? NativeSessionId { get; init; }

    public string? TurnId { get; init; }

    public int? TurnNumber { get; init; }

    public string? SourceEventId { get; init; }

    public string? ToolCallId { get; init; }

    public int Order { get; init; }

    public DateTimeOffset? TimestampUtc { get; init; }

    public SessionPlanLocator Locator { get; init; } = new();

    // The full materialized plan body this activity produced, when the provider
    // exposes it (CreatePlan input, Write/ExitPlanMode content). Null when only
    // the fact of an edit is known.
    public SessionPlanContentSnapshot? Content { get; init; }

    // False for a failed/aborted edit; excluded from revision counting.
    public bool IsSuccessful { get; init; } = true;

    // True when the edit path/target is known but the resulting content is not
    // materialized, so it counts only toward the partial-history flag.
    public bool HasOpaqueResult { get; init; }

    // True when this activity, even if a Referenced kind, is strong enough to
    // establish that its session owns the plan (a Claude plan_mode attachment
    // that carries the plan file path). Created/Updated always establish
    // ownership regardless of this flag.
    public bool EstablishesOwnership { get; init; }

    public InferenceEvidence Evidence { get; init; } = InferenceEvidence.Derived;

    public required SessionSourceProvenance Provenance { get; init; }
}

// One reconstructed revision of a plan. Sequence 0 is the initial content; every
// later sequence is a distinct normalized-content transition.
public sealed record SessionPlanRevision
{
    public required int Sequence { get; init; }

    public required SessionPlanActivityKind Kind { get; init; }

    public string? NormalizedContentHash { get; init; }

    public string? Content { get; init; }

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public int Order { get; init; }

    public string? CatalogSessionId { get; init; }

    public string? TurnId { get; init; }

    public string? SourceEventId { get; init; }

    // False when the revision is proven to exist (a successful edit) but its
    // resulting body was not exposed by the provider.
    public bool IsMaterialized { get; init; } = true;

    public InferenceEvidence Evidence { get; init; } = InferenceEvidence.Derived;

    public SessionSourceProvenance? Provenance { get; init; }
}

// A discovered plan file plus every fact known about it. Before assembly the
// binding, revision, and activity fields are provider hints; the assembler
// finalizes them. BoundCatalogSessionId == null means the plan is an orphan.
public sealed record SessionPlanArtifact
{
    public required string CatalogPlanId { get; init; }

    public required HookProvider Provider { get; init; }

    public required HookSurface Surface { get; init; }

    public required string Title { get; init; }

    public WorkspaceContext Workspace { get; init; } = WorkspaceContext.Unknown;

    public string? PrimaryPath { get; init; }

    public IReadOnlyList<string> AlternatePaths { get; init; } = [];

    public string? CurrentMarkdown { get; init; }

    public string? CurrentContentHash { get; init; }

    // The content used for revision comparison, which can differ from the
    // displayed markdown. Cursor compares the plan body only (its front-matter
    // todo statuses change independently of the plan body); other providers set
    // this to the whole file content.
    public SessionPlanContentSnapshot? RevisionSnapshot { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public DateTimeOffset? LastModifiedAtUtc { get; init; }

    // Provider correlation hint for Cursor structured-content matching.
    public string? StructuredKey { get; init; }

    // A provider may pre-suggest the owning session (Copilot session directory).
    // The assembler validates it against the final session set before binding.
    public string? SuggestedCatalogSessionId { get; init; }

    public SessionPlanBindingReason SuggestedBindingReason { get; init; } =
        SessionPlanBindingReason.Unbound;

    public InferenceEvidence SuggestedBindingEvidence { get; init; } =
        InferenceEvidence.Unavailable;

    public string? BoundCatalogSessionId { get; init; }

    public string? BoundTurnId { get; init; }

    public SessionPlanBindingReason BindingReason { get; init; } =
        SessionPlanBindingReason.Unbound;

    public InferenceEvidence BindingEvidence { get; init; } =
        InferenceEvidence.Unavailable;

    public IReadOnlyList<SessionPlanRevision> Revisions { get; init; } = [];

    public IReadOnlyList<SessionPlanActivity> Activities { get; init; } = [];

    public int ObservedUpdateCount { get; init; }

    public bool HasIncompleteRevisionHistory { get; init; }

    public IReadOnlyList<SessionSourceProvenance> Sources { get; init; } = [];

    public IReadOnlyDictionary<string, string?> Metadata { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);
}

// The unresolved plan artifacts and activities produced by one provider scan.
public sealed record SessionPlanCatalogFragment
{
    public IReadOnlyList<SessionPlanArtifact> Artifacts { get; init; } = [];

    public IReadOnlyList<SessionPlanActivity> Activities { get; init; } = [];

    public static SessionPlanCatalogFragment Empty { get; } = new();

    public bool IsEmpty => Artifacts.Count == 0 && Activities.Count == 0;
}

// The result of a dedicated plan-file scan (Cursor/Claude plan directories).
public sealed record SessionPlanScanResult(
    SessionPlanCatalogFragment Fragment,
    bool IsComplete,
    IReadOnlyList<string> Warnings)
{
    public static SessionPlanScanResult Empty { get; } =
        new(SessionPlanCatalogFragment.Empty, true, []);
}

// Stable, provider-scoped plan identity. The native key is a value that survives
// a path move (Cursor "Save to Workspace") such as the plan filename stem, the
// Claude plan slug, or the Copilot native session id.
public static class SessionPlanIdentity
{
    public static string Create(HookProvider provider, string nativeKey)
    {
        string sanitized = Sanitize(nativeKey);
        return $"{HarnessIds.FromProvider(provider)}:plan:{sanitized}";
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            builder.Append(
                char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                    ? character
                    : '-');
        }

        string result = builder.ToString().Trim('-');
        if (result.Length == 0)
        {
            return "unknown";
        }

        // Guard against pathologically long native keys while keeping identity
        // stable by folding the overflow into a short deterministic suffix.
        if (result.Length > 96)
        {
            string suffix = ShortHash(value);
            return result[..80] + "-" + suffix;
        }

        return result;
    }

    private static string ShortHash(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes)[..12];
    }
}
