using System.Text.Json;
using HarnessSpy.Core.Models;

namespace HarnessSpy.Agent.Abstractions;

public sealed record AgentCapabilities(
    bool IsAvailable,
    bool SupportsLocal,
    bool SupportsCloud,
    bool SupportsResume,
    bool SupportsStreaming,
    bool SupportsReasoning,
    bool SupportsTools,
    bool SupportsHooks,
    bool SupportsApprovals,
    bool SupportsSubagents,
    bool SupportsTasks,
    bool SupportsUsage,
    bool SupportsPersistence,
    bool SupportsCancellation)
{
    public static AgentCapabilities Unavailable { get; } = new(
        false, false, false, false, false, false, false,
        false, false, false, false, false, false, false);
}

public sealed record AgentProviderHealth(
    bool IsHealthy,
    string Status,
    string? ProviderVersion = null,
    string? RuntimeVersion = null);

public sealed record AgentCreateRequest(
    string WorkingDirectory,
    string? Model,
    AgentRuntimeKind Runtime,
    string HarnessInvocationId,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed record AgentResumeRequest(
    string ProviderAgentId,
    string? WorkingDirectory,
    string HarnessInvocationId);

public sealed record AgentRef(
    HookProvider Provider,
    AgentRuntimeKind Runtime,
    string ProviderAgentId,
    string HarnessInvocationId);

public sealed record RunRef(
    AgentRef Agent,
    string ProviderRunId,
    string HarnessCommandId);

public sealed record SendReceipt(
    string HarnessCommandId,
    string? ProviderMessageId,
    RunRef? Run);

public sealed record ResumeCursor(string Value);

public sealed record AgentCommandResult(
    bool Succeeded,
    string? ErrorCode = null,
    string? Message = null,
    bool IsRetryable = false);

public enum AgentNotificationKind
{
    Unknown,
    Session,
    Interaction,
    ModelTurn,
    AssistantDelta,
    AssistantMessage,
    ReasoningDelta,
    Reasoning,
    Tool,
    Subagent,
    Task,
    Hook,
    Approval,
    Status,
    Usage,
    Error
}

public sealed record AgentNotification(
    Guid EventId,
    HookProvider Provider,
    HookSurface Surface,
    AgentRuntimeKind Runtime,
    ObservationSourceKind SourceKind,
    long Sequence,
    DateTimeOffset ObservedAtUtc,
    AgentNotificationKind Kind,
    string NativeEventName,
    string? ProviderEventId,
    string HarnessInvocationId,
    string? ProviderAgentId,
    string? ProviderSessionId,
    string? ProviderRunId,
    string? InteractionId,
    string? ModelTurnId,
    string? MessageId,
    string? ToolCallId,
    string? SubagentId,
    string? TaskId,
    string? HookId,
    string? RequestId,
    bool IsEphemeral,
    bool IsFinal,
    CorrelationQuality CorrelationQuality,
    JsonElement RawPayload);

public interface IAgentProvider : IAsyncDisposable
{
    HookProvider Provider { get; }

    AgentCapabilities Capabilities { get; }

    Task<AgentProviderHealth> GetHealthAsync(CancellationToken cancellationToken);

    Task<AgentRef> CreateAsync(
        AgentCreateRequest request,
        CancellationToken cancellationToken);

    Task<AgentRef> ResumeAsync(
        AgentResumeRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentRef>> ListAsync(CancellationToken cancellationToken);

    Task<IAgentSession> OpenSessionAsync(
        AgentRef agent,
        CancellationToken cancellationToken);
}

public interface IAgentSession : IAsyncDisposable
{
    AgentRef Agent { get; }

    Task<SendReceipt> SendAsync(
        string prompt,
        CancellationToken cancellationToken);

    Task<AgentCommandResult> InterruptAsync(CancellationToken cancellationToken);

    Task<AgentCommandResult> CancelAsync(
        RunRef run,
        CancellationToken cancellationToken);

    Task<AgentCommandResult> ResolveRequestAsync(
        string requestId,
        JsonElement response,
        CancellationToken cancellationToken);

    IAsyncEnumerable<AgentNotification> ObserveAsync(
        ResumeCursor? cursor,
        CancellationToken cancellationToken);
}

public sealed class UnavailableAgentProvider(HookProvider provider) : IAgentProvider
{
    public HookProvider Provider { get; } = provider;

    public AgentCapabilities Capabilities => AgentCapabilities.Unavailable;

    public Task<AgentProviderHealth> GetHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AgentProviderHealth(false, "SDK provider is not installed."));

    public Task<AgentRef> CreateAsync(
        AgentCreateRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<AgentRef>(Unavailable());

    public Task<AgentRef> ResumeAsync(
        AgentResumeRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<AgentRef>(Unavailable());

    public Task<IReadOnlyList<AgentRef>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgentRef>>([]);

    public Task<IAgentSession> OpenSessionAsync(
        AgentRef agent,
        CancellationToken cancellationToken) =>
        Task.FromException<IAgentSession>(Unavailable());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static InvalidOperationException Unavailable() =>
        new("The agent SDK provider is not installed in this build.");
}
