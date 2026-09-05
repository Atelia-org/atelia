using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

internal abstract record GalateaDurableReplyLeaseBeginResult {
    private GalateaDurableReplyLeaseBeginResult() { }

    internal sealed record Empty : GalateaDurableReplyLeaseBeginResult;

    internal sealed record Created(GalateaDurableReplyLease Lease)
        : GalateaDurableReplyLeaseBeginResult;
}

internal enum GalateaDurableReplyLeaseRetryReason {
    StoreStateChanged,
    SelectedHeadChanged
}

internal abstract record GalateaDurableReplyLeaseReconcileResult {
    private GalateaDurableReplyLeaseReconcileResult() { }

    internal sealed record None
        : GalateaDurableReplyLeaseReconcileResult;

    internal sealed record RolledBack(string LeaseId)
        : GalateaDurableReplyLeaseReconcileResult;

    internal sealed record Retained(
        GalateaDurableReplyLease Lease,
        EventAddress ObservationAddress,
        SessionExecutionPhase Phase
    ) : GalateaDurableReplyLeaseReconcileResult;

    internal sealed record Consumed(
        string LeaseId,
        EventAddress TerminalActionAddress
    ) : GalateaDurableReplyLeaseReconcileResult;

    internal sealed record Quarantined(
        string LeaseId,
        SessionExpectedObservationConflictReason? ConflictReason
    ) : GalateaDurableReplyLeaseReconcileResult;

    internal sealed record Retryable(
        GalateaDurableReplyLeaseRetryReason Reason,
        EventAddress? ExpectedSelectedHead = null,
        EventAddress? ObservedSelectedHead = null
    ) : GalateaDurableReplyLeaseReconcileResult;

    internal sealed record LimitExceeded(SessionCompletedTurnsLimit Limit)
        : GalateaDurableReplyLeaseReconcileResult;

    internal sealed record UnsupportedSchema(string Detail)
        : GalateaDurableReplyLeaseReconcileResult;

    internal sealed record Corruption(string Detail)
        : GalateaDurableReplyLeaseReconcileResult;
}

internal sealed class GalateaDurableReplyLeaseHeadMismatchException(
    EventAddress expectedHead,
    EventAddress? observedHead
) : InvalidOperationException(
    "The durable reply lease fresh base is not the current SessionJournal head."
) {
    internal EventAddress ExpectedHead { get; } = expectedHead;
    internal EventAddress? ObservedHead { get; } = observedHead;
}

/// <summary>
/// A convenience handle for one durable lease. SQLite remains authoritative:
/// every operation re-reads and validates the active row before transition.
/// CLR lifetime has no settlement meaning.
/// </summary>
internal sealed class GalateaDurableReplyLease {
    private readonly GalateaDelegationSqliteStore _store;
    private long _revision;

    internal GalateaDurableReplyLease(
        GalateaDelegationSqliteStore store,
        string leaseId,
        long revision
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        LeaseId = leaseId;
        _revision = revision;
    }

    internal string LeaseId { get; }
    internal long Revision => Interlocked.Read(ref _revision);

    internal IReadOnlyList<PlayerTurnNotice> ReadNotices() {
        GalateaDelegationStateSnapshot storeSnapshot = _store.ReadSnapshot();
        GalateaReplyLeaseSnapshot lease = RequireCurrent(storeSnapshot);
        return ProjectNotices(lease, storeSnapshot.Notices);
    }

    /// <summary>
    /// The caller must own the per-user SessionJournal/store serialization
    /// boundary from this exact-head check through the following SendAsync.
    /// </summary>
    internal GalateaReplyLeaseSnapshot BindObservationBase(
        SessionJournalEngine engine,
        EventAddress exactBaseHead,
        string canonicalRenderedObservation
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            canonicalRenderedObservation
        );
        GalateaReplyLeaseSnapshot current = RequireCurrent(
            _store.ReadSnapshot(),
            GalateaReplyLeaseState.CutoffFrozen
        );
        EventAddress? observedHead = engine.ReadView.ReadCurrentHead();
        if (observedHead != exactBaseHead) {
            throw new GalateaDurableReplyLeaseHeadMismatchException(
                exactBaseHead,
                observedHead
            );
        }
        GalateaReplyLeaseSnapshot bound =
            _store.BindReplyLeaseObservationBase(
                LeaseId,
                current.Revision,
                EventAddressTextCodec.Format(exactBaseHead),
                canonicalRenderedObservation
            );
        Interlocked.Exchange(ref _revision, bound.Revision);
        return bound;
    }

    internal GalateaReplyLeaseSnapshot RecordObservationCommitted(
        EventAddress observationAddress
    ) {
        GalateaReplyLeaseSnapshot current = RequireCurrent(
            _store.ReadSnapshot(),
            GalateaReplyLeaseState.ObservationBound
        );
        GalateaReplyLeaseSnapshot committed =
            _store.RecordLeaseObservationCommitted(
                LeaseId,
                current.Revision,
                EventAddressTextCodec.Format(observationAddress)
            );
        Interlocked.Exchange(ref _revision, committed.Revision);
        return committed;
    }

    internal void Consume(EventAddress terminalActionAddress) {
        GalateaReplyLeaseSnapshot current = RequireCurrent(
            _store.ReadSnapshot(),
            GalateaReplyLeaseState.ObservationCommitted
        );
        _store.ConsumeReplyLease(
            LeaseId,
            current.Revision,
            EventAddressTextCodec.Format(terminalActionAddress)
        );
    }

    internal void RollbackBeforeEffect() {
        GalateaReplyLeaseSnapshot current = RequireCurrent(
            _store.ReadSnapshot(),
            GalateaReplyLeaseState.CutoffFrozen,
            GalateaReplyLeaseState.ObservationBound
        );
        _store.RollbackReplyLease(LeaseId, current.Revision);
    }

    internal void RollbackAfterExactAbandon(
        EventAddress exactBaseHead,
        EventAddress exactObservationAddress
    ) {
        GalateaReplyLeaseSnapshot current = RequireCurrent(
            _store.ReadSnapshot(),
            GalateaReplyLeaseState.ObservationCommitted
        );
        _store.RollbackReplyLeaseAfterExactAbandon(
            LeaseId,
            current.Revision,
            EventAddressTextCodec.Format(exactBaseHead),
            EventAddressTextCodec.Format(exactObservationAddress)
        );
    }

    internal void Quarantine() {
        GalateaReplyLeaseSnapshot current = RequireCurrent(
            _store.ReadSnapshot()
        );
        _store.QuarantineReplyLease(LeaseId, current.Revision);
        Interlocked.Exchange(
            ref _revision,
            checked(current.Revision + 1)
        );
    }

    private GalateaReplyLeaseSnapshot RequireCurrent(
        GalateaDelegationStateSnapshot snapshot,
        params GalateaReplyLeaseState[] allowedStates
    ) {
        GalateaReplyLeaseSnapshot current = snapshot.ActiveLease
            ?? throw new GalateaDelegationStoreConflictException(
                "The durable reply lease is no longer active."
            );
        if (!string.Equals(
                current.LeaseId,
                LeaseId,
                StringComparison.Ordinal)
            || current.Revision != Revision
            || allowedStates.Length > 0
                && !allowedStates.Contains(current.State)) {
            throw new GalateaDelegationStoreConflictException(
                "The durable reply lease handle is stale."
            );
        }
        return current;
    }

    internal static IReadOnlyList<PlayerTurnNotice> ProjectNotices(
        GalateaReplyLeaseSnapshot lease,
        IReadOnlyList<GalateaReplyNoticeSnapshot> notices
    ) => Array.AsReadOnly(lease.NoticeIds.Select(noticeId => {
        GalateaReplyNoticeSnapshot notice = notices.Single(value =>
            string.Equals(
                value.NoticeId,
                noticeId,
                StringComparison.Ordinal
            )
        );
        return notice.Kind switch {
            GalateaReplyNoticeKind.Reply =>
                (PlayerTurnNotice)new PlayerTurnNotice.Reply(
                    notice.Body
                ),
            GalateaReplyNoticeKind.DeliveryFailure =>
                new PlayerTurnNotice.DeliveryFailure(notice.Body),
            _ => throw new InvalidDataException(
                "The durable reply notice kind is invalid."
            )
        };
    }).ToArray());
}

/// <summary>
/// Durable cutoff/reconciliation layer. SessionJournal raw evidence decides
/// settlement; method returns and exception text never do.
/// </summary>
internal sealed class GalateaDurableReplyLeaseReconciler {
    private const string LeaseIdPrefix = "galatea-reply-lease-";

    private readonly GalateaDelegationSqliteStore _store;

    internal GalateaDurableReplyLeaseReconciler(
        GalateaDelegationSqliteStore store
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    internal GalateaDurableReplyLeaseBeginResult BeginCutoff(
        string playerText
    ) {
        _ = new PlayerTurnObservation(playerText);
        GalateaDelegationStateSnapshot snapshot = _store.ReadSnapshot();
        if (snapshot.ActiveLease is not null) {
            throw new GalateaDelegationStoreConflictException(
                "A durable reply lease is already active."
            );
        }
        GalateaReplyNoticeSnapshot[] available = snapshot.Notices
            .Where(static notice =>
                notice.State == GalateaReplyNoticeState.Ready)
            .OrderBy(static notice => notice.CompletionSequence)
            .ToArray();
        if (available.Length == 0) {
            return new GalateaDurableReplyLeaseBeginResult.Empty();
        }

        var selected = new List<GalateaReplyNoticeSnapshot>(Math.Min(
            available.Length,
            PlayerTurnObservationEnvelope.MaximumNoticeCount
        ));
        foreach (GalateaReplyNoticeSnapshot notice in available) {
            if (selected.Count
                == PlayerTurnObservationEnvelope.MaximumNoticeCount) {
                break;
            }
            PlayerTurnNotice[] proposed = [
                .. selected.Select(ProjectReadyNotice),
                ProjectReadyNotice(notice)
            ];
            if (!PlayerTurnObservationEnvelope
                    .FitsEveryValidPlayerText(proposed)) {
                break;
            }
            selected.Add(notice);
        }
        if (selected.Count == 0) {
            throw new InvalidDataException(
                "The earliest Ready notice cannot fit a durable player-turn Observation."
            );
        }

        string leaseId = LeaseIdPrefix + Guid.NewGuid().ToString("N");
        GalateaReplyLeaseSnapshot lease =
            _store.BeginReplyLeaseMembership(
                leaseId,
                playerText,
                selected.Select(static notice =>
                    new GalateaReplyLeaseMember(
                        notice.NoticeId,
                        notice.Revision
                    )
                ).ToArray()
            );
        return new GalateaDurableReplyLeaseBeginResult.Created(
            new GalateaDurableReplyLease(
                _store,
                lease.LeaseId,
                lease.Revision
            )
        );
    }

    /// <summary>
    /// The caller must own the per-user SessionJournal/store serialization
    /// boundary for the complete raw-proof and SQLite transition.
    /// </summary>
    internal GalateaDurableReplyLeaseReconcileResult ReconcileActiveLease(
        SessionJournalEngine engine,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        cancellationToken.ThrowIfCancellationRequested();
        try {
            GalateaReplyLeaseSnapshot? snapshot =
                _store.ReadSnapshot().ActiveLease;
            if (snapshot is null) {
                return new GalateaDurableReplyLeaseReconcileResult.None();
            }
            var lease = new GalateaDurableReplyLease(
                _store,
                snapshot.LeaseId,
                snapshot.Revision
            );
            if (snapshot.State == GalateaReplyLeaseState.Quarantined) {
                return new GalateaDurableReplyLeaseReconcileResult
                    .Quarantined(snapshot.LeaseId, ConflictReason: null);
            }
            if (snapshot.State == GalateaReplyLeaseState.CutoffFrozen) {
                lease.RollbackBeforeEffect();
                return new GalateaDurableReplyLeaseReconcileResult
                    .RolledBack(snapshot.LeaseId);
            }

            EventAddress? selectedHead = engine.ReadView.ReadCurrentHead();
            if (selectedHead is not { } head) {
                return new GalateaDurableReplyLeaseReconcileResult.Corruption(
                    "A bound durable reply lease requires a non-empty SessionJournal."
                );
            }
            EventAddress baseHead = EventAddressTextCodec.Parse(
                snapshot.ExpectedSessionHead
                    ?? throw new InvalidDataException(
                        "A bound durable reply lease has no fresh base head."
                    )
            );
            EventAddress? expectedObservation =
                snapshot.ObservationAddress is { } address
                    ? EventAddressTextCodec.Parse(address)
                    : null;
            var request = new SessionExpectedObservationTurnRequest(
                head,
                baseHead,
                snapshot.RenderedObservation
                    ?? throw new InvalidDataException(
                        "A bound durable reply lease has no rendered Observation."
                    ),
                expectedObservation
            );
            SessionExpectedObservationTurnReadResult evidence = engine
                .ReadView.ProveExpectedObservationTurnAtSelectedHead(
                    request,
                    cancellationToken
                );
            return ReconcileEvidence(
                lease,
                snapshot,
                baseHead,
                evidence
            );
        }
        catch (GalateaDelegationStoreConflictException) {
            return new GalateaDurableReplyLeaseReconcileResult.Retryable(
                GalateaDurableReplyLeaseRetryReason.StoreStateChanged
            );
        }
        catch (InvalidDataException exception) {
            return new GalateaDurableReplyLeaseReconcileResult.Corruption(
                exception.Message
            );
        }
    }

    private static GalateaDurableReplyLeaseReconcileResult ReconcileEvidence(
        GalateaDurableReplyLease lease,
        GalateaReplyLeaseSnapshot snapshot,
        EventAddress baseHead,
        SessionExpectedObservationTurnReadResult evidence
    ) => evidence switch {
        SessionExpectedObservationTurnReadResult.NotAppended
            when snapshot.State
                == GalateaReplyLeaseState.ObservationBound =>
            RollbackBeforeEffect(lease),
        SessionExpectedObservationTurnReadResult.InProgress inProgress
            when snapshot.State
                == GalateaReplyLeaseState.ObservationBound =>
            RecordAndRetain(lease, inProgress),
        SessionExpectedObservationTurnReadResult.InProgress inProgress
            when snapshot.State
                == GalateaReplyLeaseState.ObservationCommitted =>
            RetainCommitted(lease, inProgress),
        SessionExpectedObservationTurnReadResult.Terminal terminal
            when snapshot.State
                == GalateaReplyLeaseState.ObservationBound =>
            RecordAndConsume(lease, terminal),
        SessionExpectedObservationTurnReadResult.Terminal terminal
            when snapshot.State
                == GalateaReplyLeaseState.ObservationCommitted =>
            ConsumeCommitted(lease, terminal),
        SessionExpectedObservationTurnReadResult.Abandoned abandoned
            when snapshot.State
                == GalateaReplyLeaseState.ObservationCommitted =>
            RollbackAbandoned(lease, baseHead, abandoned),
        SessionExpectedObservationTurnReadResult.Conflict conflict =>
            Quarantine(lease, conflict.Reason),
        SessionExpectedObservationTurnReadResult.Retryable retryable =>
            new GalateaDurableReplyLeaseReconcileResult.Retryable(
                GalateaDurableReplyLeaseRetryReason.SelectedHeadChanged,
                retryable.ExpectedSelectedHead,
                retryable.ObservedSelectedHead
            ),
        SessionExpectedObservationTurnReadResult.LimitExceeded limit =>
            new GalateaDurableReplyLeaseReconcileResult.LimitExceeded(
                limit.Limit
            ),
        SessionExpectedObservationTurnReadResult.UnsupportedSchema schema =>
            new GalateaDurableReplyLeaseReconcileResult.UnsupportedSchema(
                schema.Detail
            ),
        SessionExpectedObservationTurnReadResult.Corruption corruption =>
            new GalateaDurableReplyLeaseReconcileResult.Corruption(
                corruption.Detail
            ),
        _ => Quarantine(lease, conflictReason: null)
    };

    private static GalateaDurableReplyLeaseReconcileResult
        RollbackBeforeEffect(GalateaDurableReplyLease lease) {
        string leaseId = lease.LeaseId;
        lease.RollbackBeforeEffect();
        return new GalateaDurableReplyLeaseReconcileResult.RolledBack(
            leaseId
        );
    }

    private static GalateaDurableReplyLeaseReconcileResult RecordAndRetain(
        GalateaDurableReplyLease lease,
        SessionExpectedObservationTurnReadResult.InProgress inProgress
    ) {
        lease.RecordObservationCommitted(
            inProgress.Evidence.ObservationAddress
        );
        return RetainCommitted(lease, inProgress);
    }

    private static GalateaDurableReplyLeaseReconcileResult RetainCommitted(
        GalateaDurableReplyLease lease,
        SessionExpectedObservationTurnReadResult.InProgress inProgress
    ) => new GalateaDurableReplyLeaseReconcileResult.Retained(
        lease,
        inProgress.Evidence.ObservationAddress,
        inProgress.Evidence.Boundary.Phase
    );

    private static GalateaDurableReplyLeaseReconcileResult RecordAndConsume(
        GalateaDurableReplyLease lease,
        SessionExpectedObservationTurnReadResult.Terminal terminal
    ) {
        lease.RecordObservationCommitted(
            terminal.Evidence.ObservationAddress
        );
        return ConsumeCommitted(lease, terminal);
    }

    private static GalateaDurableReplyLeaseReconcileResult ConsumeCommitted(
        GalateaDurableReplyLease lease,
        SessionExpectedObservationTurnReadResult.Terminal terminal
    ) {
        string leaseId = lease.LeaseId;
        lease.Consume(terminal.TerminalAction.Address);
        return new GalateaDurableReplyLeaseReconcileResult.Consumed(
            leaseId,
            terminal.TerminalAction.Address
        );
    }

    private static GalateaDurableReplyLeaseReconcileResult RollbackAbandoned(
        GalateaDurableReplyLease lease,
        EventAddress baseHead,
        SessionExpectedObservationTurnReadResult.Abandoned abandoned
    ) {
        string leaseId = lease.LeaseId;
        lease.RollbackAfterExactAbandon(
            baseHead,
            abandoned.Evidence.ObservationAddress
        );
        return new GalateaDurableReplyLeaseReconcileResult.RolledBack(
            leaseId
        );
    }

    private static GalateaDurableReplyLeaseReconcileResult Quarantine(
        GalateaDurableReplyLease lease,
        SessionExpectedObservationConflictReason? conflictReason
    ) {
        lease.Quarantine();
        return new GalateaDurableReplyLeaseReconcileResult.Quarantined(
            lease.LeaseId,
            conflictReason
        );
    }

    private static PlayerTurnNotice ProjectReadyNotice(
        GalateaReplyNoticeSnapshot notice
    ) => notice.Kind switch {
        GalateaReplyNoticeKind.Reply =>
            new PlayerTurnNotice.Reply(notice.Body),
        GalateaReplyNoticeKind.DeliveryFailure =>
            new PlayerTurnNotice.DeliveryFailure(notice.Body),
        _ => throw new InvalidDataException(
            "The durable reply notice kind is invalid."
        )
    };
}
