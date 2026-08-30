using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Runtimes;

// Mutable builder shared by all runtime engines so their large per-event
// switches read clearly. Produces an immutable ObservationInterpretation.
// Scope defaults to Turn when a turn id is present and Session otherwise,
// unless a case sets an explicit scope.
internal sealed class InterpretationBuilder(string nativeEventName)
{
    private FieldSpec[] _fields = [FieldSpec.AllTopLevel];
    private bool _explicitScope;
    private ObservationScope _scope;

    public string? SessionId { get; set; }

    public string? TurnId { get; set; }

    public string? ToolName { get; set; }

    public string? McpServerName { get; set; }

    public string? TargetFilePath { get; set; }

    public IReadOnlyList<string> TargetFilePaths { get; set; } = [];

    public string? PromptText { get; set; }

    public string? AssistantText { get; set; }

    public string? Task { get; set; }

    public string? Status { get; set; }

    public string? SubagentId { get; set; }

    public string? SubagentType { get; set; }

    public CanonicalToolKind ToolKind { get; set; } = CanonicalToolKind.Unknown;

    public bool HasTokenCounts { get; set; }

    public ObservationRole Role { get; set; } = ObservationRole.Generic;

    public CanonicalEventKind EventKind { get; set; } = CanonicalEventKind.ProviderSpecific;

    public CorrelationQuality CorrelationQuality { get; set; } = CorrelationQuality.Exact;

    public ObservationDirection Direction { get; set; } = ObservationDirection.None;

    public ObservationTone Tone { get; set; } = ObservationTone.Normal;

    public string? HeaderDetail { get; set; }

    public string? HoverText { get; set; }

    public bool ShowClockTimestamp { get; set; }

    public bool OpensToolCall { get; set; }

    public bool OpensSubagent { get; set; }

    public string? ToolCallId { get; set; }

    public IReadOnlyList<string> BatchToolCallIds { get; set; } = [];

    public ToolCallMatchStrategy MatchStrategy { get; set; } = ToolCallMatchStrategy.None;

    public string? InnerExecutionKind { get; set; }

    public string? InnerExecutionOwnerTool { get; set; }

    public IReadOnlyList<string> FileAccessOwnerTools { get; set; } = [];

    public InnerExecutionCategory InnerCategory { get; set; } = InnerExecutionCategory.None;

    public bool CountsAsFailure { get; set; }

    public bool IsAbortedStop { get; set; }

    public bool ParticipatesInDerivedTurns { get; set; }

    public bool StartsDerivedTurn { get; set; }

    public ObservationScope ScopeOverride
    {
        set
        {
            _scope = value;
            _explicitScope = true;
        }
    }

    public void Fields(params FieldSpec[] specs) => _fields = specs;

    public ObservationInterpretation Build()
    {
        ObservationScope scope = _explicitScope
            ? _scope
            : TurnId is null ? ObservationScope.Session : ObservationScope.Turn;

        return new ObservationInterpretation
        {
            NativeEventName = nativeEventName,
            Scope = scope,
            Role = Role,
            EventKind = EventKind,
            ToolKind = ToolKind,
            CorrelationQuality = CorrelationQuality,
            SessionId = SessionId,
            TurnId = TurnId,
            ToolCallId = ToolCallId,
            BatchToolCallIds = BatchToolCallIds,
            SubagentId = SubagentId,
            SubagentType = SubagentType,
            ToolName = ToolName,
            McpServerName = McpServerName,
            PromptText = PromptText,
            AssistantText = AssistantText,
            TargetFilePath = TargetFilePath,
            TargetFilePaths = TargetFilePaths,
            Task = Task,
            Status = Status,
            MatchStrategy = MatchStrategy,
            InnerExecutionKind = InnerExecutionKind,
            InnerExecutionOwnerTool = InnerExecutionOwnerTool,
            FileAccessOwnerTools = FileAccessOwnerTools,
            InnerCategory = InnerCategory,
            OpensToolCall = OpensToolCall,
            OpensSubagent = OpensSubagent,
            HeaderDetail = HeaderDetail,
            Direction = Direction,
            Tone = Tone,
            HoverText = HoverText,
            ShowClockTimestamp = ShowClockTimestamp,
            DetailFieldSpecs = _fields,
            CountsAsFailure = CountsAsFailure,
            IsAbortedStop = IsAbortedStop,
            HasTokenCounts = HasTokenCounts,
            ParticipatesInDerivedTurns = ParticipatesInDerivedTurns,
            StartsDerivedTurn = StartsDerivedTurn
        };
    }
}
