using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server;

internal sealed partial class GalateaDelegationSqliteStore {
    internal IReadOnlyList<GalateaInternalMailOutboxSnapshot>
        ReadInternalMailOutboxesForTarget(string targetUserId) {
        RequireBoundedText(targetUserId, nameof(targetUserId));
        lock (_gate) {
            ThrowIfDisposed();
            using SqliteConnection connection = OpenVerifiedConnection();
            using SqliteTransaction transaction =
                connection.BeginTransaction(deferred: true);
            IReadOnlyList<GalateaInternalMailOutboxSnapshot> result =
                GalateaDelegationStateSnapshot.Freeze(
                    ReadInternalMailOutboxes(connection, transaction)
                        .Where(value => string.Equals(value.TargetUserId,
                            targetUserId, StringComparison.Ordinal))
                );
            transaction.Commit();
            return result;
        }
    }

    internal GalateaInternalMailOutboxSnapshot BindInternalMailObservation(
        string dispatchId,
        long expectedRowRevision,
        string exactBaseHead,
        string renderedObservation
    ) => UpdateInternalMailObservation(
        "bind-internal-mail-observation", dispatchId, expectedRowRevision,
        GalateaInternalMailState.Pending, "ObservationBound",
        exactBaseHead, renderedObservation, observationAddress: null,
        quarantineCode: null
    );

    internal GalateaInternalMailOutboxSnapshot ResetInternalMailObservation(
        string dispatchId,
        long expectedRowRevision
    ) => UpdateInternalMailObservation(
        "reset-internal-mail-observation", dispatchId, expectedRowRevision,
        GalateaInternalMailState.ObservationBound, "Pending",
        expectedSessionHead: null, renderedObservation: null,
        observationAddress: null, quarantineCode: null
    );

    internal GalateaInternalMailOutboxSnapshot CompleteInternalMailObservation(
        string dispatchId,
        long expectedRowRevision,
        string observationAddress
    ) {
        RequireEventAddress(observationAddress, nameof(observationAddress));
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(
                "complete-internal-mail-observation",
                (connection, transaction) => {
                    GalateaInternalMailOutboxSnapshot current =
                        ReadInternalMailOutboxRequired(connection, transaction,
                            dispatchId);
                    RequireInternalMail(current,
                        GalateaInternalMailState.ObservationBound,
                        expectedRowRevision);
                    _ = IncrementStoreRevision(connection, transaction);
                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE internal_mail_outbox
                        SET state = 'Delivered', observation_address = $address,
                            revision = revision + 1
                        WHERE dispatch_id = $dispatch
                          AND state = 'ObservationBound'
                          AND revision = $revision;
                        """;
                    update.Parameters.AddWithValue("$address", observationAddress);
                    update.Parameters.AddWithValue("$dispatch", dispatchId);
                    update.Parameters.AddWithValue("$revision", expectedRowRevision);
                    RequireOne(update.ExecuteNonQuery(),
                        "internal mail delivery completion");
                    return current with {
                        State = GalateaInternalMailState.Delivered,
                        ObservationAddress = observationAddress,
                        Revision = checked(current.Revision + 1)
                    };
                },
                (snapshot, result) => snapshot.InternalMailOutboxes.Contains(result)
            );
        }
    }

    internal GalateaInternalMailOutboxSnapshot QuarantineInternalMailObservation(
        string dispatchId,
        long expectedRowRevision,
        string quarantineCode
    ) {
        RequireFailureToken(quarantineCode, nameof(quarantineCode));
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(
                "quarantine-internal-mail-observation",
                (connection, transaction) => {
                    GalateaInternalMailOutboxSnapshot current =
                        ReadInternalMailOutboxRequired(connection, transaction,
                            dispatchId);
                    if (current.State is not GalateaInternalMailState.ObservationBound
                        || current.Revision != expectedRowRevision) {
                        throw Conflict("Internal mail outbox CAS did not match a bound row.");
                    }
                    _ = IncrementStoreRevision(connection, transaction);
                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE internal_mail_outbox
                        SET state = 'Quarantined', quarantine_code = $code,
                            revision = revision + 1
                        WHERE dispatch_id = $dispatch
                          AND state = 'ObservationBound'
                          AND revision = $revision;
                        """;
                    update.Parameters.AddWithValue("$code", quarantineCode);
                    update.Parameters.AddWithValue("$dispatch", dispatchId);
                    update.Parameters.AddWithValue("$revision", expectedRowRevision);
                    RequireOne(update.ExecuteNonQuery(),
                        "internal mail quarantine");
                    return current with {
                        State = GalateaInternalMailState.Quarantined,
                        QuarantineCode = quarantineCode,
                        Revision = checked(current.Revision + 1)
                    };
                },
                (snapshot, result) => snapshot.InternalMailOutboxes.Contains(result)
            );
        }
    }

    private GalateaInternalMailOutboxSnapshot UpdateInternalMailObservation(
        string operation,
        string dispatchId,
        long expectedRowRevision,
        GalateaInternalMailState expectedState,
        string state,
        string? expectedSessionHead,
        string? renderedObservation,
        string? observationAddress,
        string? quarantineCode
    ) {
        RequireDispatchId(dispatchId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRowRevision);
        if (expectedSessionHead is not null) {
            RequireEventAddress(expectedSessionHead, nameof(expectedSessionHead));
        }
        if (renderedObservation is not null) {
            RequireText(renderedObservation,
                GalateaDelegationStateBounds.MaximumObservationUtf8Bytes,
                nameof(renderedObservation), allowLineBreaks: true);
        }
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(operation, (connection, transaction) => {
                GalateaInternalMailOutboxSnapshot current =
                    ReadInternalMailOutboxRequired(connection, transaction,
                        dispatchId);
                RequireInternalMail(current, expectedState, expectedRowRevision);
                _ = IncrementStoreRevision(connection, transaction);
                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE internal_mail_outbox
                    SET state = $state,
                        expected_session_head = $head,
                        rendered_observation = $observation,
                        observation_address = $address,
                        quarantine_code = $quarantine,
                        revision = revision + 1
                    WHERE dispatch_id = $dispatch
                      AND state = $expectedState
                      AND revision = $revision;
                    """;
                update.Parameters.AddWithValue("$state", state);
                update.Parameters.AddWithValue("$head", (object?)expectedSessionHead ?? DBNull.Value);
                update.Parameters.AddWithValue("$observation", (object?)renderedObservation ?? DBNull.Value);
                update.Parameters.AddWithValue("$address", (object?)observationAddress ?? DBNull.Value);
                update.Parameters.AddWithValue("$quarantine", (object?)quarantineCode ?? DBNull.Value);
                update.Parameters.AddWithValue("$dispatch", dispatchId);
                update.Parameters.AddWithValue("$expectedState", expectedState.ToString());
                update.Parameters.AddWithValue("$revision", expectedRowRevision);
                RequireOne(update.ExecuteNonQuery(), "internal mail outbox CAS");
                return current with {
                    State = ParseExact<GalateaInternalMailState>(state),
                    ExpectedSessionHead = expectedSessionHead,
                    RenderedObservation = renderedObservation,
                    ObservationAddress = observationAddress,
                    QuarantineCode = quarantineCode,
                    Revision = checked(current.Revision + 1)
                };
            }, (snapshot, result) => snapshot.InternalMailOutboxes.Contains(result));
        }
    }

    private static GalateaInternalMailOutboxSnapshot
        ReadInternalMailOutboxRequired(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string dispatchId
    ) => ReadInternalMailOutboxes(connection, transaction).SingleOrDefault(value =>
            string.Equals(value.DispatchId, dispatchId, StringComparison.Ordinal))
        ?? throw Conflict("The requested internal mail outbox row does not exist.");

    private static void RequireInternalMail(
        GalateaInternalMailOutboxSnapshot current,
        GalateaInternalMailState expectedState,
        long expectedRowRevision
    ) {
        if (current.State != expectedState
            || current.Revision != expectedRowRevision) {
            throw Conflict("Internal mail outbox CAS did not match.");
        }
    }
}
