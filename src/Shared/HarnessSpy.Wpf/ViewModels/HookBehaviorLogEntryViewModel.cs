using System.Text.Json;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes.Cursor;

namespace HarnessSpy.Wpf.ViewModels;

public sealed class HookBehaviorLogEntryViewModel
{
    public HookBehaviorLogEntryViewModel(
        HookObservation observation,
        HarnessOutcome outcome)
    {
        EventId = observation.EventId;
        ProviderScopedSessionId = observation.ProviderScopedSessionId;
        ObservedAtUtc = observation.ObservedAtUtc;
        TimestampText = observation.ObservedAtUtc.ToLocalTime().ToString("HH:mm:ss.fff");
        HookName = observation.RawHookEventName;
        EffectLabel = outcome.EffectKind switch
        {
            "reject_prompt" or "deny_subagent" or "deny_tool" => "DENY",
            "rewrite_tool_input" => "REWRITE",
            "inject_context" => "CONTEXT",
            "auto_followup" => "FOLLOW-UP",
            _ => outcome.EffectKind.ToUpperInvariant()
        };
        ResponseJson = JsonSerializer.Serialize(
            outcome.Response,
            new JsonSerializerOptions { WriteIndented = true });
        Summary = WithoutCatPrefix(outcome.Summary);
        ChangeLines = outcome.Changes.SelectMany(FormatChange).ToArray();
    }

    public Guid EventId { get; }

    public string ProviderScopedSessionId { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public string TimestampText { get; }

    public string HookName { get; }

    public string EffectLabel { get; }

    public string ResponseJson { get; }

    public string Summary { get; }

    public IReadOnlyList<string> ChangeLines { get; }

    private static IEnumerable<string> FormatChange(HarnessOutcomeChange change)
    {
        yield return change.Target;
        if (change.Before is not null)
        {
            yield return $"  before: {WithoutCatPrefix(change.Before)}";
        }

        if (change.After is not null)
        {
            yield return $"  after:  {WithoutCatPrefix(change.After)}";
        }
    }

    private static string WithoutCatPrefix(string value) =>
        value.StartsWith(CursorCatBossText.Prefix, StringComparison.Ordinal)
            ? value[CursorCatBossText.Prefix.Length..].TrimStart('\r', '\n')
            : value;
}
