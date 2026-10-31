using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarnessSpy.Core.Hooks;

public interface IHookResponseProvider
{
    HookResponseDecision Create(string eventName, JsonElement payload);
}

public sealed record HookResponseDecision(
    JsonObject Response,
    HookBehaviorEffect? Effect = null)
{
    public static HookResponseDecision NoOp() => new([]);
}

public sealed record HookBehaviorEffect(
    string Mode,
    string EffectKind,
    string Summary,
    bool RequiresUserAction,
    IReadOnlyList<HookBehaviorChange> Changes);

public sealed record HookBehaviorChange(
    string Target,
    string? Before = null,
    string? After = null);
