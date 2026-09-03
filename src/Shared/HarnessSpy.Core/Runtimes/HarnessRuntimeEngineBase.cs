using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Runtimes;

internal abstract class HarnessRuntimeEngineBase : IHarnessRuntimeEngine
{
    public abstract string HarnessId { get; }

    public abstract ObservationInterpretation Interpret(ObservationContext context);

    // Provider-neutral mapping from an exact native tool name to a coarse tool
    // kind. Never renames the tool; only classifies it for trait purposes.
    protected static CanonicalToolKind ToolKind(string? nativeToolName) =>
        ToolClassifier.Classify(nativeToolName);
}
