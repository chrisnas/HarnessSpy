using System.Text.Json;

namespace HarnessSpy.Core.Models;

public sealed record HarnessOutcome(
    string Mode,
    string EffectKind,
    JsonElement Response,
    string Summary,
    bool RequiresUserAction,
    IReadOnlyList<HarnessOutcomeChange> Changes);

public sealed record HarnessOutcomeChange(
    string Target,
    string? Before = null,
    string? After = null);
