using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// The minimum non-mutating raw-authority surface used by Derived
/// integrations. This view is intentionally not a general mirror of all
/// SessionJournal read APIs. It neither owns nor independently extends the
/// usable engine lifetime; every operation fails after its owner is disposed.
/// </summary>
public sealed class SessionJournalReadView {
    private readonly SessionJournalEngine _owner;

    internal SessionJournalReadView(SessionJournalEngine owner) {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public string Path {
        get {
            EnsureOwnerAlive();
            return _owner.Path;
        }
    }

    public string BranchName {
        get {
            EnsureOwnerAlive();
            return _owner.BranchName;
        }
    }

    public RefId BranchRefId {
        get {
            EnsureOwnerAlive();
            return _owner.BranchRefId;
        }
    }

    public EventAddress? ReadCurrentHead() {
        EnsureOwnerAlive();
        return _owner.ReadCurrentHead();
    }

    /// <summary>
    /// Captures the end-exclusive physical tail frontier of the underlying
    /// EventJournal. This is not the selected branch head: it includes
    /// selected and orphan EventFrames already present in the events store.
    /// </summary>
    public EventJournalPhysicalAppendFrontier
        ReadPhysicalAppendFrontier() {
        EnsureOwnerAlive();
        return _owner.ReadPhysicalAppendFrontierForReadView();
    }

    public SessionExecutionBoundaryInspection InspectExecutionBoundary(
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _owner.InspectExecutionBoundary(cancellationToken);
    }

    public SessionExpectedObservationTurnReadResult
        ProveExpectedObservationTurnAtSelectedHead(
        SessionExpectedObservationTurnRequest request,
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _owner.ProveExpectedObservationTurnAtSelectedHead(
            request,
            cancellationToken
        );
    }

    public SessionCurrentLineagePrefix ReadCurrentLineagePrefix(
        int maxHeaderCount,
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _owner.ReadCurrentLineagePrefix(
            maxHeaderCount,
            cancellationToken
        );
    }

    public SessionCurrentLineagePrefix ReadLineagePrefixAt(
        EventAddress capturedHead,
        int maxHeaderCount,
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _owner.ReadLineagePrefixAt(
            capturedHead,
            maxHeaderCount,
            cancellationToken
        );
    }

    public SessionCreatedPlanningSeedReadResult
        ReadSessionCreatedPlanningSeedAtBounded(
        EventAddress capturedHead,
        int maxRawEventCount,
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _owner.ReadSessionCreatedPlanningSeedAtBounded(
            capturedHead,
            maxRawEventCount,
            cancellationToken
        );
    }

    public SessionHistoryPlanningWindowReadResult
        ReadHistoryPlanningWindowAtBounded(
        EventAddress capturedHead,
        EventAddress startExclusive,
        int maxRawEventCount,
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _owner.ReadHistoryPlanningWindowAtBounded(
            capturedHead,
            startExclusive,
            maxRawEventCount,
            cancellationToken
        );
    }

    public SessionHistoryPlanningWindowReadResult
        ReadHistoryPlanningWindowAtBounded(
        EventAddress capturedHead,
        SessionHistoryPlanningSeed planningSeed,
        int maxRawEventCount,
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _owner.ReadHistoryPlanningWindowAtBounded(
            capturedHead,
            planningSeed,
            maxRawEventCount,
            cancellationToken
        );
    }

    public SessionHistoryPlanningWindowProofResult
        ProveHistoryPlanningWindowAtBounded(
        EventAddress capturedHead,
        EventAddress startExclusive,
        int maxRawEventCount,
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _owner.ProveHistoryPlanningWindowAtBounded(
            capturedHead,
            startExclusive,
            maxRawEventCount,
            cancellationToken
        );
    }

    public SessionHistoryPlanningWindowProofResult
        ProveHistoryPlanningWindowInPrefix(
        SessionCurrentLineagePrefix prefix,
        EventAddress capturedHead,
        EventAddress startExclusive,
        int maxRawEventCount
    ) {
        EnsureOwnerAlive();
        return _owner.ProveHistoryPlanningWindowInPrefix(
            prefix,
            capturedHead,
            startExclusive,
            maxRawEventCount
        );
    }

    public SessionHistoryPlanningWindow MaterializeHistoryPlanningWindow(
        SessionHistoryPlanningWindowProof proof,
        SessionHistoryPlanningSeed planningSeed,
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _owner.MaterializeHistoryPlanningWindow(
            proof,
            planningSeed,
            cancellationToken
        );
    }

    public SessionHistoryPlanningSeed MaterializeHistoryPlanningSeed(
        SessionGoverningSetupProof proof,
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _owner.MaterializeHistoryPlanningSeed(
            proof,
            cancellationToken
        );
    }

    /// <summary>
    /// Rehydrates an exact durable setup boundary without searching toward
    /// the selected-lineage root.
    /// </summary>
    public SessionHistoryPlanningSeed CreateHistoryPlanningSeed(
        EventAddress startExclusive,
        SessionContextAnchorSetupReferences setups,
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _owner.CreateHistoryPlanningSeed(
            startExclusive,
            setups,
            cancellationToken
        );
    }

    public SessionGoverningSetupProofResult ProveGoverningSetupAtBounded(
        EventAddress boundary,
        SessionContextAnchorSetupReferences expectedSetups,
        int maxHeaderCount,
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _owner.ProveGoverningSetupAtBounded(
            boundary,
            expectedSetups,
            maxHeaderCount,
            cancellationToken
        );
    }

    public SessionGoverningSetupProofResult ProveGoverningSetupInPrefix(
        SessionCurrentLineagePrefix prefix,
        EventAddress boundary,
        SessionContextAnchorSetupReferences expectedSetups
    ) {
        EnsureOwnerAlive();
        return _owner.ProveGoverningSetupInPrefix(
            prefix,
            boundary,
            expectedSetups
        );
    }

    public SessionGoverningSetupProof ProveGoverningSetupTransition(
        SessionHistoryPlanningWindowProof proof,
        SessionGoverningSetupProof startProof,
        SessionContextAnchorSetupReferences expectedEndSetups
    ) {
        EnsureOwnerAlive();
        return _owner.ProveGoverningSetupTransition(
            proof,
            startProof,
            expectedEndSetups
        );
    }

    public void ValidateGoverningSetupPayloads(
        IEnumerable<SessionGoverningSetupProof> proofs,
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        _owner.ValidateGoverningSetupPayloads(
            proofs,
            cancellationToken
        );
    }

    public SessionGoverningSetup ResolveGoverningSetup(
        EventAddress head,
        CancellationToken cancellationToken = default
    ) {
        EnsureOwnerAlive();
        return _owner.ResolveGoverningSetup(head, cancellationToken);
    }

    private void EnsureOwnerAlive()
        => _owner.EnsureNotDisposedForReadView();
}

public sealed partial class SessionJournalEngine {
    internal EventJournalPhysicalAppendFrontier
        ReadPhysicalAppendFrontierForReadView() {
        ThrowIfDisposed();
        return _journal.ReadPhysicalAppendFrontier();
    }
}
