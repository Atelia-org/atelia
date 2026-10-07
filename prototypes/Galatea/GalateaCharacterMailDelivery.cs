using Atelia.EventJournal;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

/// <summary>Process-local capability shared by the two durable incoming sources.</summary>
internal abstract class GalateaMailDeliveryBinding {
    internal void BindObservationBase(
        SessionJournalEngine engine, EventAddress exactBaseHead,
        SessionInputContent observation
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(observation);
        if (engine.ReadView.ReadCurrentHead() != exactBaseHead) {
            throw new GalateaTurnException(
                "Mail target head changed before binding.",
                "character-mail-stale-session-head");
        }
        Bind(EventAddressTextCodec.Format(exactBaseHead), observation);
    }

    protected abstract void Bind(string exactBaseHead, SessionInputContent observation);
}

internal sealed class GalateaInternalMailDeliveryBinding(
    GalateaDelegationSqliteStore senderStore, string dispatchId,
    long expectedRowRevision
) : GalateaMailDeliveryBinding {
    private readonly GalateaDelegationSqliteStore _store = senderStore
        ?? throw new ArgumentNullException(nameof(senderStore));
    private readonly string _dispatchId = dispatchId
        ?? throw new ArgumentNullException(nameof(dispatchId));

    protected override void Bind(string exactBaseHead, SessionInputContent observation) =>
        _ = _store.BindInternalMailObservation(
            _dispatchId, expectedRowRevision, exactBaseHead, observation);
}

internal sealed class GalateaImapMailDeliveryBinding(
    GalateaDelegationSqliteStore targetStore, long inboxId,
    long expectedRowRevision
) : GalateaMailDeliveryBinding {
    private readonly GalateaDelegationSqliteStore _store = targetStore
        ?? throw new ArgumentNullException(nameof(targetStore));

    protected override void Bind(string exactBaseHead, SessionInputContent observation) =>
        _ = _store.BindExternalMailObservation(
            inboxId, expectedRowRevision, exactBaseHead, observation);
}

/// <summary>
/// Every target writer enters this gate. Internal outboxes and external inboxes
/// use the same exact Journal proof and share the one-bound-mail invariant.
/// </summary>
internal static class GalateaCharacterMailDeliveryReconciler {
    internal static void Reconcile(
        GalateaDelegationSupervisor supervisor, CharacterSessionHost target,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        GalateaInternalMailSourceOutbox[] internalRows = supervisor
            .ReadInternalMailOutboxesForTarget(target.Character.CharacterId)
            .Where(static source => source.Outbox.State is
                GalateaInternalMailState.ObservationBound or GalateaInternalMailState.Quarantined)
            .ToArray();
        GalateaDelegationSqliteStore? inboxStore = null;
        IReadOnlyList<GalateaExternalMailInboxSnapshot> externalRows = [];
        if (supervisor.TryGetAttachedStore(target.Character.CharacterId, out var attached)) {
            inboxStore = attached;
            externalRows = attached.ReadUnsettledExternalMails();
        }
        if (internalRows.Any(static source => source.Outbox.State == GalateaInternalMailState.Quarantined)
            || externalRows.Any(static row => row.State == GalateaExternalMailInboxState.Quarantined)) {
            throw Blocked("character-mail-quarantined", "Incoming mail delivery is quarantined.");
        }
        GalateaInternalMailSourceOutbox[] internalBound = internalRows
            .Where(static source => source.Outbox.State == GalateaInternalMailState.ObservationBound).ToArray();
        GalateaExternalMailInboxSnapshot[] externalBound = externalRows
            .Where(static row => row.State == GalateaExternalMailInboxState.ObservationBound).ToArray();
        int boundCount = internalBound.Length + externalBound.Length;
        if (boundCount > 1) {
            throw Blocked("character-mail-multiple-bound", "More than one incoming mail Observation is bound for one target.");
        }
        if (boundCount == 0) { return; }
        BoundDelivery delivery = internalBound.Length == 1
            ? Adapt(internalBound[0]) : Adapt(inboxStore!, externalBound[0]);
        ReconcileBound(target, delivery, cancellationToken);
    }

    private static void ReconcileBound(
        CharacterSessionHost target, BoundDelivery delivery,
        CancellationToken cancellationToken
    ) {
        string repositoryId = GalateaDelegationSupervisor.CreateSessionRepositoryId(target.Character.SessionDir);
        if (delivery.TargetCharacterId != target.Character.CharacterId
            || delivery.TargetRepositoryId != repositoryId) {
            throw Blocked("character-mail-target-locator-mismatch", "Bound incoming mail targets a different session repository.");
        }
        if (delivery.Message.To != target.Character.CharacterName.Value) {
            throw Blocked("character-mail-target-recipient-mismatch", "Bound incoming mail recipient does not match the target character.");
        }
        EventAddress head = target.Engine.ReadView.ReadCurrentHead()
            ?? throw Blocked("character-mail-empty-journal", "Bound incoming mail requires a non-empty target Journal.");
        var request = new SessionExpectedObservationTurnRequest(head,
            EventAddressTextCodec.Parse(delivery.ExpectedSessionHead), delivery.Observation,
            ExpectedObservationAddress: null);
        SessionExpectedObservationTurnReadResult proof = target.Engine.ReadView
            .ProveExpectedObservationTurnAtSelectedHead(request, cancellationToken);
        switch (proof) {
            case SessionExpectedObservationTurnReadResult.NotAppended:
                Transition(delivery.Reset);
                return;
            case SessionExpectedObservationTurnReadResult.InProgress progress:
                Transition(() => delivery.Complete(EventAddressTextCodec.Format(progress.Evidence.ObservationAddress)));
                return;
            case SessionExpectedObservationTurnReadResult.Terminal terminal:
                Transition(() => delivery.Complete(EventAddressTextCodec.Format(terminal.Evidence.ObservationAddress)));
                return;
            case SessionExpectedObservationTurnReadResult.Terminated terminated:
                Transition(() => delivery.Complete(EventAddressTextCodec.Format(terminated.Evidence.ObservationAddress)));
                return;
            case SessionExpectedObservationTurnReadResult.Retryable:
                throw Blocked("character-mail-proof-retryable", "Incoming mail Journal head changed during proof.");
            case SessionExpectedObservationTurnReadResult.Conflict:
                Transition(() => delivery.Quarantine("OBSERVATION_CONFLICT"));
                throw Blocked("character-mail-proof-conflict", "Incoming mail Observation conflicts with target Journal evidence.");
            case SessionExpectedObservationTurnReadResult.Corruption:
                Transition(() => delivery.Quarantine("OBSERVATION_CORRUPTION"));
                throw Blocked("character-mail-proof-corruption", "Incoming mail proof found Journal corruption.");
            case SessionExpectedObservationTurnReadResult.Abandoned:
                throw Blocked("character-mail-proof-abandoned", "Bound incoming mail Observation was abandoned unexpectedly.");
            case SessionExpectedObservationTurnReadResult.LimitExceeded:
                throw Blocked("character-mail-proof-limit-exceeded", "Incoming mail proof exceeded its read limit.");
            case SessionExpectedObservationTurnReadResult.UnsupportedSchema:
                throw Blocked("character-mail-session-schema-unsupported", "Incoming mail proof uses an unsupported schema.");
            default:
                throw new InvalidDataException("Unknown incoming mail Observation proof result.");
        }
    }

    internal static MailboxMessage RestoreMessage(GalateaInternalMailSourceOutbox source) {
        ArgumentNullException.ThrowIfNull(source);
        GalateaInternalMailOutboxSnapshot outbox = source.Outbox;
        GalateaOutboundMailSnapshot mail = source.Store.ReadSnapshot().Mails.Single(
            row => string.Equals(row.DispatchId, outbox.DispatchId, StringComparison.Ordinal));
        return MailboxMessage.FromCanonicalEnvelope(outbox.MessageId, outbox.FromCharacterName,
            mail.Recipient, mail.Subject, mail.Body ?? throw new InvalidDataException("Internal mail has no body."));
    }

    internal static MailboxMessage RestoreMessage(GalateaExternalMailInboxSnapshot row) =>
        MailboxMessage.FromCanonicalEnvelope(row.MessageId,
            row.From ?? throw new InvalidDataException("Accepted external mail has no declared From."),
            row.TargetCharacterName, row.Subject,
            row.Body ?? throw new InvalidDataException("Accepted external mail has no body."));

    private static BoundDelivery Adapt(GalateaInternalMailSourceOutbox source) {
        GalateaInternalMailOutboxSnapshot row = source.Outbox;
        return new(row.TargetCharacterId, row.TargetSessionRepositoryId, RestoreMessage(source),
            row.ExpectedSessionHead ?? throw new InvalidDataException("Bound mail has no exact base."),
            row.ObservationContent ?? throw new InvalidDataException("Bound mail has no Observation."),
            () => { _ = source.Store.ResetInternalMailObservation(row.DispatchId, row.Revision); },
            address => { _ = source.Store.CompleteInternalMailObservation(row.DispatchId, row.Revision, address); },
            code => { _ = source.Store.QuarantineInternalMailObservation(row.DispatchId, row.Revision, code); });
    }

    private static BoundDelivery Adapt(GalateaDelegationSqliteStore store, GalateaExternalMailInboxSnapshot row) =>
        new(row.TargetCharacterId, row.TargetSessionRepositoryId, RestoreMessage(row),
            row.ExpectedSessionHead ?? throw new InvalidDataException("Bound mail has no exact base."),
            row.BoundInput ?? throw new InvalidDataException("Bound mail has no Observation."),
            () => { _ = store.ResetExternalMailObservation(row.InboxId, row.Revision); },
            address => { _ = store.CompleteExternalMailObservation(row.InboxId, row.Revision, address); },
            code => { _ = store.QuarantineExternalMailObservation(row.InboxId, row.Revision, code); });

    private static void Transition(Action transition) {
        try { transition(); }
        catch (GalateaDelegationStoreConflictException) {
            throw Blocked("character-mail-state-changed", "Incoming mail delivery changed during proof settlement.");
        }
    }

    private sealed record BoundDelivery(
        string TargetCharacterId, string TargetRepositoryId, MailboxMessage Message,
        string ExpectedSessionHead, SessionInputContent Observation,
        Action Reset, Action<string> Complete, Action<string> Quarantine);

    private static GalateaTurnException Blocked(string code, string message) => new(message, code);
}
