using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Runtimes;

namespace HarnessSpy.Core.Sources;

// Shared plumbing for the provider transcript parsers: safe row parsing, native
// fragment emission, and provenance wiring. Subclasses only decide which native
// fragments a row yields and how to interpret them; the base keeps the raw
// payload untouched and never throws on a malformed row.
internal abstract class TranscriptDialectParserBase : ITranscriptDialectParser
{
    public abstract string DialectId { get; }

    public IReadOnlyList<HookObservation> Parse(TranscriptLine line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line.Raw);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            try
            {
                return ParseRow(line, document.RootElement);
            }
            catch (JsonException)
            {
                return [];
            }
            catch (InvalidOperationException)
            {
                return [];
            }
        }
    }

    protected abstract IReadOnlyList<HookObservation> ParseRow(TranscriptLine line, JsonElement row);

    // Wraps a synthetic native payload and its interpretation into a
    // transcript-sourced observation. The payload is a JsonObject the parser
    // shaped from the raw row; it is serialized once and stored untouched.
    protected HookObservation Emit(
        TranscriptLine line,
        JsonObject payload,
        ObservationInterpretation interpretation,
        ObservationProvenance provenance)
    {
        using JsonDocument document = JsonDocument.Parse(payload.ToJsonString());
        return HookObservation.CreateTranscriptFragment(
            line.Provider,
            line.Surface,
            document.RootElement,
            interpretation.NativeEventName,
            line.EffectiveObservedAtUtc,
            interpretation,
            provenance);
    }

    protected static string? Text(JsonElement element, params string[] names) =>
        RuntimeJson.String(element, names);

    protected static CanonicalToolKind ToolKind(string? nativeToolName) =>
        ToolClassifier.Classify(nativeToolName);

    // Copies a native block element into a fresh JsonObject so the fragment
    // payload owns its memory once the parse document is disposed.
    protected static JsonObject CloneToObject(JsonElement element)
    {
        JsonNode? node = JsonNode.Parse(element.GetRawText());
        return node as JsonObject ?? new JsonObject { ["value"] = element.GetRawText() };
    }
}
