using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Services;

public enum ObservationChangeKind
{
    // Add a new canonical or transcript-only node.
    Add,

    // Attach transcript evidence to an existing canonical hook node.
    AttachEvidence,

    // A hook arrived that matches an already-added transcript-only node; make
    // the hook canonical while keeping the transcript evidence.
    PromotePrimary,

    // Add a navigable relationship between two existing nodes.
    AddLink
}

// The result of reconciling one observation against everything seen so far.
// The shared view model applies these instead of blindly adding nodes so an
// action observed by both a hook and a transcript appears exactly once.
public sealed record ObservationChange(
    ObservationChangeKind Kind,
    HookObservation Observation,
    Guid? TargetEventId = null,
    TranscriptRelationshipKind Relationship = TranscriptRelationshipKind.EvidenceOf);
