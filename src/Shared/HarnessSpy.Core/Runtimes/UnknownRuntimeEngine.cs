using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Runtimes;

// Fallback engine for unrecognised providers, surfaces, or events. It never
// discards anything: the observation appears as a generic, session-scoped node
// with its exact native name and all raw fields visible.
internal sealed class UnknownRuntimeEngine : HarnessRuntimeEngineBase
{
    public static UnknownRuntimeEngine Instance { get; } = new();

    public override string HarnessId => HarnessIds.Unknown;

    public override ObservationInterpretation Interpret(ObservationContext context)
    {
        string nativeEvent =
            context.PayloadEventName ??
            context.ConfiguredEventName ??
            "unknownHook";

        string? sessionId = RuntimeJson.String(
            context.Payload,
            "conversation_id",
            "session_id",
            "sessionId");
        string? turnId = RuntimeJson.String(
            context.Payload,
            "generation_id",
            "prompt_id",
            "turn_id",
            "turnId");

        return new ObservationInterpretation
        {
            NativeEventName = nativeEvent,
            Scope = turnId is null ? ObservationScope.Session : ObservationScope.Turn,
            Role = ObservationRole.Generic,
            EventKind = CanonicalEventKind.ProviderSpecific,
            CorrelationQuality = CorrelationQuality.Heuristic,
            SessionId = sessionId,
            TurnId = turnId,
            ToolName = RuntimeJson.String(context.Payload, "tool_name", "toolName"),
            DetailFieldSpecs = [FieldSpec.AllTopLevel]
        };
    }
}
