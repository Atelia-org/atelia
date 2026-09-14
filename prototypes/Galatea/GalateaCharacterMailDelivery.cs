using Atelia.EventJournal;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

/// <summary>
/// Process-local capability for the one relay-owned internal mailbox turn.
/// It deliberately contains a store object rather than any public HTTP data.
/// </summary>
internal sealed class GalateaInternalMailDeliveryBinding(
    GalateaDelegationSqliteStore senderStore,
    string dispatchId,
    long expectedRowRevision
) {
    private readonly GalateaDelegationSqliteStore _senderStore =
        senderStore ?? throw new ArgumentNullException(nameof(senderStore));
    private readonly string _dispatchId = dispatchId
        ?? throw new ArgumentNullException(nameof(dispatchId));
    private readonly long _expectedRowRevision = expectedRowRevision;

    internal void BindObservationBase(
        SessionJournalEngine engine,
        EventAddress exactBaseHead,
        string renderedObservation
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(renderedObservation);
        if (engine.ReadView.ReadCurrentHead() != exactBaseHead) {
            throw new GalateaTurnException(
                "Character-mail target head changed before binding.",
                "character-mail-stale-session-head"
            );
        }
        _ = _senderStore.BindInternalMailObservation(
            _dispatchId,
            _expectedRowRevision,
            EventAddressTextCodec.Format(exactBaseHead),
            renderedObservation
        );
    }
}

/// <summary>
/// Target-side durable writer gate for character mail.  This is intentionally
/// separate from Codex reply leases: a source SQLite row is settled only from
/// the target Journal's exact Observation proof.
/// </summary>
internal static class GalateaCharacterMailDeliveryReconciler {
    internal static void Reconcile(
        GalateaDelegationSupervisor supervisor,
        UserSessionHost target,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<GalateaInternalMailSourceOutbox> unsettled = supervisor
            .ReadInternalMailOutboxesForTarget(target.User.UserId)
            .Where(static value => value.Outbox.State is
                GalateaInternalMailState.ObservationBound
                or GalateaInternalMailState.Quarantined)
            .ToArray();
        GalateaInternalMailSourceOutbox? quarantined = unsettled
            .FirstOrDefault(static value => value.Outbox.State
                == GalateaInternalMailState.Quarantined);
        if (quarantined is not null) {
            throw Blocked("character-mail-quarantined",
                "Character mail delivery is quarantined.");
        }
        GalateaInternalMailSourceOutbox[] bound = unsettled
            .Where(static value => value.Outbox.State
                == GalateaInternalMailState.ObservationBound)
            .ToArray();
        if (bound.Length > 1) {
            throw Blocked("character-mail-multiple-bound",
                "More than one character mail Observation is bound for one target.");
        }
        if (bound.Length == 0) { return; }

        GalateaInternalMailSourceOutbox source = bound[0];
        GalateaInternalMailOutboxSnapshot outbox = source.Outbox;
        string currentRepositoryId =
            GalateaDelegationSupervisor.CreateSessionRepositoryId(
                target.User.SessionDir);
        if (!string.Equals(outbox.TargetUserId, target.User.UserId,
                StringComparison.Ordinal)
            || !string.Equals(outbox.TargetSessionRepositoryId,
                currentRepositoryId, StringComparison.Ordinal)) {
            throw Blocked("character-mail-target-locator-mismatch",
                "A bound character mail targets a different session repository.");
        }
        MailboxMessage message = RestoreMessage(source);
        if (!string.Equals(message.To, target.User.CharacterName.Value,
                StringComparison.Ordinal)) {
            throw Blocked("character-mail-target-recipient-mismatch",
                "A bound character mail recipient does not match the target character.");
        }
        EventAddress head = target.Engine.ReadView.ReadCurrentHead()
            ?? throw Blocked("character-mail-empty-journal",
                "A bound character mail requires a non-empty target Journal.");
        var request = new SessionExpectedObservationTurnRequest(
            head,
            EventAddressTextCodec.Parse(outbox.ExpectedSessionHead
                ?? throw new InvalidDataException(
                    "A bound character mail has no expected session head.")),
            outbox.RenderedObservation
                ?? throw new InvalidDataException(
                    "A bound character mail has no rendered Observation."),
            ExpectedObservationAddress: null
        );
        SessionExpectedObservationTurnReadResult proof = target.Engine.ReadView
            .ProveExpectedObservationTurnAtSelectedHead(
                request, cancellationToken);
        switch (proof) {
            case SessionExpectedObservationTurnReadResult.NotAppended:
                Reset(source, outbox);
                return;
            case SessionExpectedObservationTurnReadResult.InProgress progress:
                Complete(source, outbox, progress.Evidence.ObservationAddress);
                return;
            case SessionExpectedObservationTurnReadResult.Terminal terminal:
                Complete(source, outbox, terminal.Evidence.ObservationAddress);
                return;
            case SessionExpectedObservationTurnReadResult.Retryable:
                throw Blocked("character-mail-proof-retryable",
                    "Character mail Journal head changed during proof.");
            case SessionExpectedObservationTurnReadResult.Conflict:
                Quarantine(source, outbox, "OBSERVATION_CONFLICT");
                throw Blocked("character-mail-proof-conflict",
                    "Character mail Observation conflicts with target Journal evidence.");
            case SessionExpectedObservationTurnReadResult.Corruption:
                Quarantine(source, outbox, "OBSERVATION_CORRUPTION");
                throw Blocked("character-mail-proof-corruption",
                    "Character mail Observation proof found Journal corruption.");
            case SessionExpectedObservationTurnReadResult.Abandoned:
                throw Blocked("character-mail-proof-abandoned",
                    "A bound character mail Observation was abandoned unexpectedly.");
            case SessionExpectedObservationTurnReadResult.LimitExceeded:
                throw Blocked("character-mail-proof-limit-exceeded",
                    "Character mail Observation proof exceeded its read limit.");
            case SessionExpectedObservationTurnReadResult.UnsupportedSchema:
                throw Blocked("character-mail-session-schema-unsupported",
                    "Character mail Observation proof uses an unsupported schema.");
            default:
                throw new InvalidDataException(
                    "Unknown character-mail Observation proof result.");
        }
    }

    internal static MailboxMessage RestoreMessage(
        GalateaInternalMailSourceOutbox source
    ) {
        ArgumentNullException.ThrowIfNull(source);
        GalateaInternalMailOutboxSnapshot outbox = source.Outbox;
        GalateaDelegationStateSnapshot snapshot = source.Store.ReadSnapshot();
        GalateaOutboundMailSnapshot mail = snapshot.Mails.Single(value =>
            string.Equals(value.DispatchId, outbox.DispatchId,
                StringComparison.Ordinal));
        return MailboxMessage.FromCanonicalEnvelope(
            outbox.MessageId,
            outbox.FromCharacterName,
            mail.Recipient,
            mail.Subject,
            mail.Body ?? throw new InvalidDataException(
                "An internal mail artifact has no body.")
        );
    }

    private static void Reset(
        GalateaInternalMailSourceOutbox source,
        GalateaInternalMailOutboxSnapshot outbox
    ) {
        try {
            _ = source.Store.ResetInternalMailObservation(
                outbox.DispatchId, outbox.Revision);
        }
        catch (GalateaDelegationStoreConflictException exception) {
            throw Blocked("character-mail-state-changed", exception.Message);
        }
    }

    private static void Complete(
        GalateaInternalMailSourceOutbox source,
        GalateaInternalMailOutboxSnapshot outbox,
        EventAddress address
    ) {
        try {
            _ = source.Store.CompleteInternalMailObservation(
                outbox.DispatchId, outbox.Revision,
                EventAddressTextCodec.Format(address));
        }
        catch (GalateaDelegationStoreConflictException exception) {
            throw Blocked("character-mail-state-changed", exception.Message);
        }
    }

    private static void Quarantine(
        GalateaInternalMailSourceOutbox source,
        GalateaInternalMailOutboxSnapshot outbox,
        string code
    ) {
        try {
            _ = source.Store.QuarantineInternalMailObservation(
                outbox.DispatchId, outbox.Revision, code);
        }
        catch (GalateaDelegationStoreConflictException exception) {
            throw Blocked("character-mail-state-changed", exception.Message);
        }
    }

    private static GalateaTurnException Blocked(string code, string message) =>
        new(message, code);
}
