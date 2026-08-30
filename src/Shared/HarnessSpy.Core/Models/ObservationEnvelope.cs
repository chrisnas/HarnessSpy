using System.Text.Json;

namespace HarnessSpy.Core.Models;

public sealed record ObservationEnvelope(
    int IngressVersion,
    Guid EventId,
    DateTimeOffset ObservedAtUtc,
    HookProvider Provider,
    HookSurface ConfiguredSurface,
    HookSurface DetectedSurface,
    ObservationSourceKind SourceKind,
    string? ConfiguredEventName,
    string? RawEventName,
    string? SourceConfigurationId,
    string? SourceFilePath,
    string ParseStatus,
    JsonElement Payload,
    int? SpawningProcessId = null,
    string? SpawningProcessName = null)
{
    public const int CurrentIngressVersion = 2;
}
