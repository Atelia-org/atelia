using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server;

internal sealed partial class GalateaDelegationSqliteStore {
    internal GalateaReplyLeaseSnapshot BeginReplyLeaseMembership(
        string leaseId,
        string playerText,
        IReadOnlyList<GalateaReplyLeaseMember> members
    ) {
        RequireWireIdentity(leaseId, nameof(leaseId));
        _ = new PlayerTurnObservation(playerText);
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count is < 1
                or > GalateaDelegationStateBounds.MaximumReplyNoticeCount
            || members.Select(static value => value.NoticeId)
                .Distinct(StringComparer.Ordinal).Count() != members.Count) {
            throw new ArgumentException(
                "Reply lease membership must contain 1..16 unique notices.",
                nameof(members)
            );
        }
        foreach (GalateaReplyLeaseMember member in members) {
            ArgumentNullException.ThrowIfNull(member);
            RequireDispatchId(member.NoticeId);
            ArgumentOutOfRangeException.ThrowIfNegative(member.ExpectedRevision);
        }

        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(
                "begin-reply-lease-membership",
                (connection, transaction) => {
                    if (ReadActiveLease(connection, transaction) is not null) {
                        throw Conflict("A reply lease is already active.");
                    }
                    List<GalateaReplyNoticeSnapshot> notices = ReadNotices(
                        connection,
                        transaction
                    );
                    var selected = new List<GalateaReplyNoticeSnapshot>(members.Count);
                    long previousSequence = 0;
                    for (int ordinal = 0; ordinal < members.Count; ordinal++) {
                        GalateaReplyLeaseMember member = members[ordinal];
                        GalateaReplyNoticeSnapshot notice = notices
                            .SingleOrDefault(value => string.Equals(
                                value.NoticeId,
                                member.NoticeId,
                                StringComparison.Ordinal))
                            ?? throw Conflict("A selected reply notice is missing.");
                        if (notice.State != GalateaReplyNoticeState.Ready
                            || notice.Revision != member.ExpectedRevision
                            || notice.CompletionSequence <= previousSequence) {
                            throw Conflict(
                                "Reply lease membership state, revision, or order changed."
                            );
                        }
                        previousSequence = notice.CompletionSequence;
                        selected.Add(notice);
                    }
                    RequireReadyPrefix(notices, selected);
                    RequireRenderableLease(selected);
                    _ = IncrementStoreRevision(connection, transaction);
                    using (SqliteCommand insertLease = connection.CreateCommand()) {
                        insertLease.Transaction = transaction;
                        insertLease.CommandText = """
                            INSERT INTO reply_lease(
                                lease_id, state, active_slot, player_text,
                                expected_session_head, rendered_observation,
                                observation_utf8_bytes, observation_sha256,
                                completion_frontier, observation_address,
                                revision
                            ) VALUES (
                                $lease, 'CutoffFrozen', 1, $playerText,
                                NULL, NULL, NULL, NULL, $frontier,
                                NULL, 0
                            );
                            """;
                        insertLease.Parameters.AddWithValue("$lease", leaseId);
                        insertLease.Parameters.AddWithValue(
                            "$playerText",
                            playerText
                        );
                        insertLease.Parameters.AddWithValue(
                            "$frontier",
                            selected[^1].CompletionSequence
                        );
                        insertLease.ExecuteNonQuery();
                    }
                    for (int ordinal = 0; ordinal < selected.Count; ordinal++) {
                        GalateaReplyNoticeSnapshot notice = selected[ordinal];
                        using (SqliteCommand item = connection.CreateCommand()) {
                            item.Transaction = transaction;
                            item.CommandText = """
                                INSERT INTO reply_lease_item(
                                    lease_id, ordinal, notice_id
                                ) VALUES ($lease, $ordinal, $notice);
                                """;
                            item.Parameters.AddWithValue("$lease", leaseId);
                            item.Parameters.AddWithValue("$ordinal", ordinal);
                            item.Parameters.AddWithValue("$notice", notice.NoticeId);
                            item.ExecuteNonQuery();
                        }
                        using SqliteCommand update = connection.CreateCommand();
                        update.Transaction = transaction;
                        update.CommandText = """
                            UPDATE reply_notice
                            SET state = 'Leased', revision = revision + 1
                            WHERE notice_id = $notice AND state = 'Ready'
                              AND revision = $revision;
                            """;
                        update.Parameters.AddWithValue("$notice", notice.NoticeId);
                        update.Parameters.AddWithValue("$revision", notice.Revision);
                        RequireOne(update.ExecuteNonQuery(), "reply notice lease");
                    }
                    return new GalateaReplyLeaseSnapshot(
                        leaseId,
                        GalateaReplyLeaseState.CutoffFrozen,
                        playerText,
                        ExpectedSessionHead: null,
                        RenderedObservation: null,
                        ObservationUtf8Bytes: null,
                        ObservationSha256: null,
                        selected[^1].CompletionSequence,
                        ObservationAddress: null,
                        Revision: 0,
                        GalateaDelegationStateSnapshot.Freeze(
                            selected.Select(static value => value.NoticeId)
                        )
                    );
                },
                (snapshot, result) => LeaseMatches(
                    snapshot.ActiveLease,
                    result
                )
            );
        }
    }

    internal GalateaReplyLeaseSnapshot BindReplyLeaseObservationBase(
        string leaseId,
        long expectedLeaseRevision,
        string expectedSessionHead,
        string renderedObservation
    ) {
        RequireWireIdentity(leaseId, nameof(leaseId));
        RequireEventAddress(expectedSessionHead, nameof(expectedSessionHead));
        RequireText(
            renderedObservation,
            GalateaDelegationStateBounds.MaximumObservationUtf8Bytes,
            nameof(renderedObservation),
            allowLineBreaks: true
        );
        int bytes = StrictUtf8.GetByteCount(renderedObservation);
        string digest = ComputeSha256(renderedObservation);
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(
                "bind-reply-lease-observation-base",
                (connection, transaction) => {
                    GalateaReplyLeaseSnapshot lease = RequireActiveLease(
                        connection,
                        transaction,
                        leaseId,
                        GalateaReplyLeaseState.CutoffFrozen,
                        expectedLeaseRevision
                    );
                    if (!PlayerTurnObservationEnvelope.TryUnwrap(
                            renderedObservation,
                            out PlayerTurnObservation parsed)
                        || parsed.ExternalLocalTimestamp is not { }
                            externalLocalTimestamp) {
                        throw Conflict(
                            "Rendered Observation must use the timestamped canonical shape."
                        );
                    }
                    string canonical = RenderLeaseObservation(
                        connection,
                        transaction,
                        lease,
                        externalLocalTimestamp
                    );
                    if (!string.Equals(
                            renderedObservation,
                            canonical,
                            StringComparison.Ordinal)) {
                        throw Conflict(
                            "Rendered Observation does not exactly match the frozen cutoff."
                        );
                    }
                    _ = IncrementStoreRevision(connection, transaction);
                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE reply_lease
                        SET state = 'ObservationBound',
                            expected_session_head = $head,
                            rendered_observation = $observation,
                            observation_utf8_bytes = $bytes,
                            observation_sha256 = $digest,
                            revision = revision + 1
                        WHERE lease_id = $lease
                          AND state = 'CutoffFrozen'
                          AND active_slot = 1
                          AND revision = $revision;
                        """;
                    update.Parameters.AddWithValue("$head", expectedSessionHead);
                    update.Parameters.AddWithValue("$observation", renderedObservation);
                    update.Parameters.AddWithValue("$bytes", bytes);
                    update.Parameters.AddWithValue("$digest", digest);
                    update.Parameters.AddWithValue("$lease", leaseId);
                    update.Parameters.AddWithValue("$revision", expectedLeaseRevision);
                    RequireOne(update.ExecuteNonQuery(), "reply lease Observation bind");
                    return lease with {
                        State = GalateaReplyLeaseState.ObservationBound,
                        ExpectedSessionHead = expectedSessionHead,
                        RenderedObservation = renderedObservation,
                        ObservationUtf8Bytes = bytes,
                        ObservationSha256 = digest,
                        Revision = checked(lease.Revision + 1)
                    };
                },
                (snapshot, result) => LeaseMatches(
                    snapshot.ActiveLease,
                    result
                )
            );
        }
    }

    internal GalateaReplyLeaseSnapshot RecordLeaseObservationCommitted(
        string leaseId,
        long expectedLeaseRevision,
        string observationAddress
    ) {
        RequireWireIdentity(leaseId, nameof(leaseId));
        RequireEventAddress(observationAddress, nameof(observationAddress));
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(
                "record-lease-observation-committed",
                (connection, transaction) => {
                    GalateaReplyLeaseSnapshot lease = RequireActiveLease(
                        connection,
                        transaction,
                        leaseId,
                        GalateaReplyLeaseState.ObservationBound,
                        expectedLeaseRevision
                    );
                    _ = IncrementStoreRevision(connection, transaction);
                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE reply_lease
                        SET state = 'ObservationCommitted',
                            observation_address = $address,
                            revision = revision + 1
                        WHERE lease_id = $lease
                          AND state = 'ObservationBound'
                          AND active_slot = 1
                          AND revision = $revision;
                        """;
                    update.Parameters.AddWithValue("$address", observationAddress);
                    update.Parameters.AddWithValue("$lease", leaseId);
                    update.Parameters.AddWithValue("$revision", expectedLeaseRevision);
                    RequireOne(update.ExecuteNonQuery(), "reply lease Observation commit");
                    return lease with {
                        State = GalateaReplyLeaseState.ObservationCommitted,
                        ObservationAddress = observationAddress,
                        Revision = checked(lease.Revision + 1)
                    };
                },
                (snapshot, result) => LeaseMatches(
                    snapshot.ActiveLease,
                    result
                )
            );
        }
    }

    internal void ConsumeReplyLease(
        string leaseId,
        long expectedLeaseRevision,
        string terminalActionAddress
    ) {
        RequireWireIdentity(leaseId, nameof(leaseId));
        RequireEventAddress(
            terminalActionAddress,
            nameof(terminalActionAddress)
        );
        lock (_gate) {
            ThrowIfNotWritable();
            ExecuteWrite(
                "consume-reply-lease",
                (connection, transaction) => {
                    GalateaReplyLeaseSnapshot lease = RequireActiveLease(
                        connection,
                        transaction,
                        leaseId,
                        GalateaReplyLeaseState.ObservationCommitted,
                        expectedLeaseRevision
                    );
                    ConsumeLeaseRows(
                        connection,
                        transaction,
                        lease,
                        terminalActionAddress
                    );
                    return GalateaDelegationStateSnapshot.Freeze(
                        lease.NoticeIds
                    );
                },
                (snapshot, result) => snapshot.ActiveLease is null
                    && snapshot.Notices
                        .Where(value => result.Contains(value.NoticeId))
                        .All(value => value.State
                                == GalateaReplyNoticeState.Consumed
                            && string.Equals(
                                value.ConsumedActionAddress,
                                terminalActionAddress,
                                StringComparison.Ordinal))
            );
        }
    }

    internal void RollbackReplyLease(
        string leaseId,
        long expectedLeaseRevision
    ) {
        RequireWireIdentity(leaseId, nameof(leaseId));
        lock (_gate) {
            ThrowIfNotWritable();
            ExecuteWrite(
                "rollback-reply-lease",
                (connection, transaction) => {
                    GalateaReplyLeaseSnapshot lease = ReadActiveLease(
                            connection,
                            transaction)
                        ?? throw Conflict("No reply lease is active.");
                    if (!string.Equals(lease.LeaseId, leaseId,
                            StringComparison.Ordinal)
                        || lease.Revision != expectedLeaseRevision
                        || lease.State is not (
                            GalateaReplyLeaseState.CutoffFrozen
                            or GalateaReplyLeaseState.ObservationBound)) {
                        throw Conflict("The reply lease cannot roll back from this state.");
                    }
                    _ = RollbackLeaseRows(
                        connection,
                        transaction,
                        lease
                    );
                    return GalateaDelegationStateSnapshot.Freeze(
                        lease.NoticeIds
                    );
                },
                (snapshot, result) => snapshot.ActiveLease is null
                    && snapshot.Notices.Where(value =>
                        result.Contains(value.NoticeId)).All(value =>
                            value.State == GalateaReplyNoticeState.Ready)
            );
        }
    }

    internal void RollbackReplyLeaseAfterExactAbandon(
        string leaseId,
        long expectedLeaseRevision,
        string expectedBaseHead,
        string expectedObservationAddress
    ) {
        RequireWireIdentity(leaseId, nameof(leaseId));
        RequireEventAddress(expectedBaseHead, nameof(expectedBaseHead));
        RequireEventAddress(
            expectedObservationAddress,
            nameof(expectedObservationAddress)
        );
        lock (_gate) {
            ThrowIfNotWritable();
            ExecuteWrite(
                "rollback-reply-lease-after-exact-abandon",
                (connection, transaction) => {
                    GalateaReplyLeaseSnapshot lease = ReadActiveLease(
                            connection,
                            transaction)
                        ?? throw Conflict("No reply lease is active.");
                    List<GalateaReplyNoticeSnapshot> notices = ReadNotices(
                        connection,
                        transaction
                    );
                    ValidateActiveLease(lease, notices);
                    if (!string.Equals(
                            lease.LeaseId,
                            leaseId,
                            StringComparison.Ordinal)
                        || lease.Revision != expectedLeaseRevision
                        || lease.State
                            != GalateaReplyLeaseState.ObservationCommitted
                        || !string.Equals(
                            lease.ExpectedSessionHead,
                            expectedBaseHead,
                            StringComparison.Ordinal)
                        || !string.Equals(
                            lease.ObservationAddress,
                            expectedObservationAddress,
                            StringComparison.Ordinal)) {
                        throw Conflict(
                            "The reply lease does not match the exact abandoned Observation."
                        );
                    }
                    long storeRevision = RollbackLeaseRows(
                        connection,
                        transaction,
                        lease
                    );
                    return new ExactAbandonRollbackReceipt(
                        storeRevision,
                        GalateaDelegationStateSnapshot.Freeze(
                            lease.NoticeIds.Select(noticeId => {
                                GalateaReplyNoticeSnapshot notice = notices
                                    .Single(value => string.Equals(
                                        value.NoticeId,
                                        noticeId,
                                        StringComparison.Ordinal
                                    ));
                                return new ExactAbandonNoticeReceipt(
                                    noticeId,
                                    checked(notice.Revision + 1)
                                );
                            })
                        )
                    );
                },
                IsExactAbandonRollbackPublished
            );
        }
    }

    internal void QuarantineReplyLease(
        string leaseId,
        long expectedLeaseRevision
    ) {
        RequireWireIdentity(leaseId, nameof(leaseId));
        lock (_gate) {
            ThrowIfNotWritable();
            ExecuteWrite(
                "quarantine-reply-lease",
                (connection, transaction) => {
                    GalateaReplyLeaseSnapshot lease = ReadActiveLease(
                            connection,
                            transaction)
                        ?? throw Conflict("No reply lease is active.");
                    if (!string.Equals(lease.LeaseId, leaseId,
                            StringComparison.Ordinal)
                        || lease.Revision != expectedLeaseRevision) {
                        throw Conflict("The reply lease identity changed.");
                    }
                    _ = IncrementStoreRevision(connection, transaction);
                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE reply_lease
                        SET state = 'Quarantined', revision = revision + 1
                        WHERE lease_id = $lease AND active_slot = 1
                          AND revision = $revision;
                        """;
                    update.Parameters.AddWithValue("$lease", leaseId);
                    update.Parameters.AddWithValue("$revision", expectedLeaseRevision);
                    RequireOne(update.ExecuteNonQuery(), "reply lease quarantine");
                    return leaseId;
                },
                (snapshot, result) => snapshot.ActiveLease is {
                        State: GalateaReplyLeaseState.Quarantined
                    } active
                    && string.Equals(active.LeaseId, result,
                        StringComparison.Ordinal)
            );
        }
    }

    private static void ConsumeLeaseRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GalateaReplyLeaseSnapshot lease,
        string terminalActionAddress
    ) {
        _ = IncrementStoreRevision(connection, transaction);
        using (SqliteCommand updateNotices = connection.CreateCommand()) {
            updateNotices.Transaction = transaction;
            updateNotices.CommandText = """
                UPDATE reply_notice
                SET state = 'Consumed',
                    consumed_action_address = $action,
                    revision = revision + 1
                WHERE state = 'Leased' AND notice_id IN (
                    SELECT notice_id FROM reply_lease_item
                    WHERE lease_id = $lease
                );
                """;
            updateNotices.Parameters.AddWithValue(
                "$action",
                terminalActionAddress
            );
            updateNotices.Parameters.AddWithValue("$lease", lease.LeaseId);
            if (updateNotices.ExecuteNonQuery() != lease.NoticeIds.Count) {
                throw Conflict("Reply lease notice membership changed.");
            }
        }
        DeleteLeaseAttemptRows(
            connection,
            transaction,
            lease,
            "reply lease consume"
        );
    }

    private static long RollbackLeaseRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GalateaReplyLeaseSnapshot lease
    ) {
        long storeRevision = IncrementStoreRevision(
            connection,
            transaction
        );
        using (SqliteCommand updateNotices = connection.CreateCommand()) {
            updateNotices.Transaction = transaction;
            updateNotices.CommandText = """
                UPDATE reply_notice
                SET state = 'Ready', revision = revision + 1
                WHERE state = 'Leased' AND notice_id IN (
                    SELECT notice_id FROM reply_lease_item
                    WHERE lease_id = $lease
                );
                """;
            updateNotices.Parameters.AddWithValue("$lease", lease.LeaseId);
            if (updateNotices.ExecuteNonQuery() != lease.NoticeIds.Count) {
                throw Conflict("Reply lease notice membership changed.");
            }
        }
        DeleteLeaseAttemptRows(
            connection,
            transaction,
            lease,
            "reply lease rollback"
        );
        return storeRevision;
    }

    private static bool IsExactAbandonRollbackPublished(
        GalateaDelegationStateSnapshot snapshot,
        ExactAbandonRollbackReceipt expected
    ) {
        if (snapshot.ActiveLease is not null
            || snapshot.StoreRevision != expected.StoreRevision) {
            return false;
        }
        foreach (ExactAbandonNoticeReceipt expectedNotice
                 in expected.Notices) {
            GalateaReplyNoticeSnapshot? notice = snapshot.Notices
                .SingleOrDefault(value => string.Equals(
                    value.NoticeId,
                    expectedNotice.NoticeId,
                    StringComparison.Ordinal
                ));
            if (notice is null
                || notice.State != GalateaReplyNoticeState.Ready
                || notice.ConsumedActionAddress is not null
                || notice.Revision != expectedNotice.Revision) {
                return false;
            }
        }
        return true;
    }

    private sealed record ExactAbandonRollbackReceipt(
        long StoreRevision,
        IReadOnlyList<ExactAbandonNoticeReceipt> Notices
    );

    private sealed record ExactAbandonNoticeReceipt(
        string NoticeId,
        long Revision
    );

    private static void DeleteLeaseAttemptRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GalateaReplyLeaseSnapshot lease,
        string operation
    ) {
        using (SqliteCommand deleteItems = connection.CreateCommand()) {
            deleteItems.Transaction = transaction;
            deleteItems.CommandText = """
                DELETE FROM reply_lease_item WHERE lease_id = $lease;
                """;
            deleteItems.Parameters.AddWithValue("$lease", lease.LeaseId);
            if (deleteItems.ExecuteNonQuery() != lease.NoticeIds.Count) {
                throw Conflict("Reply lease item membership changed.");
            }
        }
        using SqliteCommand deleteLease = connection.CreateCommand();
        deleteLease.Transaction = transaction;
        deleteLease.CommandText = """
            DELETE FROM reply_lease
            WHERE lease_id = $lease AND active_slot = 1
              AND revision = $revision;
            """;
        deleteLease.Parameters.AddWithValue("$lease", lease.LeaseId);
        deleteLease.Parameters.AddWithValue("$revision", lease.Revision);
        RequireOne(deleteLease.ExecuteNonQuery(), operation);
    }

    private static GalateaReplyLeaseSnapshot RequireActiveLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string leaseId,
        GalateaReplyLeaseState expectedState,
        long expectedRevision
    ) {
        GalateaReplyLeaseSnapshot lease = ReadActiveLease(
                connection,
                transaction)
            ?? throw Conflict("No reply lease is active.");
        if (!string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
            || lease.State != expectedState
            || lease.Revision != expectedRevision) {
            throw Conflict("The reply lease expected state/revision changed.");
        }
        return lease;
    }

    private static void RequireReadyPrefix(
        IReadOnlyList<GalateaReplyNoticeSnapshot> all,
        IReadOnlyList<GalateaReplyNoticeSnapshot> selected
    ) {
        string[] earliest = all
            .Where(static value => value.State
                == GalateaReplyNoticeState.Ready)
            .OrderBy(static value => value.CompletionSequence)
            .Take(selected.Count)
            .Select(static value => value.NoticeId)
            .ToArray();
        if (!earliest.SequenceEqual(
                selected.Select(static value => value.NoticeId),
                StringComparer.Ordinal)) {
            throw Conflict("Reply lease membership is not the earliest Ready prefix.");
        }
    }

    private static void RequireRenderableLease(
        IReadOnlyList<GalateaReplyNoticeSnapshot> notices
    ) {
        PlayerTurnNotice[] ready = notices.Select(static notice =>
            notice.Kind switch {
                GalateaReplyNoticeKind.Reply =>
                    (PlayerTurnNotice)new PlayerTurnNotice.Reply(
                        notice.Body
                    ),
                GalateaReplyNoticeKind.DeliveryFailure =>
                    new PlayerTurnNotice.DeliveryFailure(notice.Body),
                _ => throw Corrupt("Reply notice kind is unknown.")
            }
        ).ToArray();
        if (!PlayerTurnObservationEnvelope.FitsEveryValidPlayerText(ready)) {
            throw new InvalidOperationException(
                "The reply lease prefix cannot fit every valid player text."
            );
        }
    }

    private static string RenderLeaseObservation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GalateaReplyLeaseSnapshot lease,
        DateTimeOffset externalLocalTimestamp
    ) {
        IReadOnlyList<GalateaReplyNoticeSnapshot> notices = ReadNotices(
            connection,
            transaction
        );
        PlayerTurnNotice[] ready = lease.NoticeIds.Select(noticeId => {
            GalateaReplyNoticeSnapshot notice = notices.Single(value =>
                string.Equals(
                    value.NoticeId,
                    noticeId,
                    StringComparison.Ordinal
                ));
            return notice.Kind switch {
                GalateaReplyNoticeKind.Reply =>
                    (PlayerTurnNotice)new PlayerTurnNotice.Reply(
                        notice.Body
                    ),
                GalateaReplyNoticeKind.DeliveryFailure =>
                    new PlayerTurnNotice.DeliveryFailure(notice.Body),
                _ => throw Corrupt("Reply notice kind is unknown.")
            };
        }).ToArray();
        return PlayerTurnObservationEnvelope.Wrap(
            new PlayerTurnObservation(
                lease.PlayerText,
                externalLocalTimestamp,
                ready
            )
        );
    }

    private static bool LeaseMatches(
        GalateaReplyLeaseSnapshot? actual,
        GalateaReplyLeaseSnapshot expected
    ) => actual is not null
        && actual with { NoticeIds = expected.NoticeIds } == expected
        && actual.NoticeIds.SequenceEqual(
            expected.NoticeIds,
            StringComparer.Ordinal
        );
}
