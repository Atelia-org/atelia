using Microsoft.Data.Sqlite;
using Atelia.SessionJournal;
using System.Text.Json;

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
            // A target delivery gate treats an empty result as proof that no
            // sender-side blocker exists. Do not allow this narrow query to
            // bypass the store's full durable-state validation.
            GalateaDelegationStateSnapshot snapshot = ReadSnapshotCore(
                connection,
                transaction
            );
            IReadOnlyList<GalateaInternalMailOutboxSnapshot> result =
                GalateaDelegationStateSnapshot.Freeze(
                    snapshot.InternalMailOutboxes
                        .Where(value => string.Equals(value.TargetCharacterId,
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
        SessionInputContent renderedObservation
    ) => UpdateInternalMailObservation(
        "bind-internal-mail-observation", dispatchId, expectedRowRevision,
        GalateaInternalMailState.Pending, "ObservationBound",
        exactBaseHead, renderedObservation, observationAddress: null,
        quarantineCode: null
    );

    /// <summary>
    /// Releases a bound row only after exact Journal proof says the Observation
    /// was not appended. Callers must not use this as a generic retry reset.
    /// </summary>
    internal GalateaInternalMailOutboxSnapshot ResetInternalMailObservation(
        string dispatchId,
        long expectedRowRevision
    ) => UpdateInternalMailObservation(
        "reset-internal-mail-observation", dispatchId, expectedRowRevision,
        GalateaInternalMailState.ObservationBound, "Pending",
        expectedSessionHead: null, renderedObservation: null,
        observationAddress: null, quarantineCode: null
    );

    /// <summary>
    /// Marks durable delivery only from exact Journal proof of the bound
    /// Observation. Completion success is not part of this state transition.
    /// </summary>
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
        SessionInputContent? renderedObservation,
        string? observationAddress,
        string? quarantineCode
    ) {
        RequireDispatchId(dispatchId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRowRevision);
        if (expectedSessionHead is not null) {
            RequireEventAddress(expectedSessionHead, nameof(expectedSessionHead));
        }
        if (renderedObservation is not null) {
            RequireNewBoundObservation(renderedObservation);
            RequireText(EncodeBoundInput(renderedObservation),
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
                if (renderedObservation is not null) {
                    GalateaOutboundMailSnapshot mail = ReadMailRequired(connection, transaction, dispatchId);
                    ValidateInternalMailInput(renderedObservation, current, mail, _owner.CharacterId);
                }
                _ = IncrementStoreRevision(connection, transaction);
                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE internal_mail_outbox
                    SET state = $state,
                        expected_session_head = $head,
                        rendered_observation = NULL, bound_input = $observation,
                        observation_address = $address,
                        quarantine_code = $quarantine,
                        revision = revision + 1
                    WHERE dispatch_id = $dispatch
                      AND state = $expectedState
                      AND revision = $revision;
                    """;
                update.Parameters.AddWithValue("$state", state);
                update.Parameters.AddWithValue("$head", (object?)expectedSessionHead ?? DBNull.Value);
                update.Parameters.AddWithValue("$observation", renderedObservation is null ? DBNull.Value : EncodeBoundInput(renderedObservation));
                update.Parameters.AddWithValue("$address", (object?)observationAddress ?? DBNull.Value);
                update.Parameters.AddWithValue("$quarantine", (object?)quarantineCode ?? DBNull.Value);
                update.Parameters.AddWithValue("$dispatch", dispatchId);
                update.Parameters.AddWithValue("$expectedState", expectedState.ToString());
                update.Parameters.AddWithValue("$revision", expectedRowRevision);
                RequireOne(update.ExecuteNonQuery(), "internal mail outbox CAS");
                return current with {
                    State = ParseExact<GalateaInternalMailState>(state),
                    ExpectedSessionHead = expectedSessionHead,
                    RenderedObservation = null,
                    BoundInput = renderedObservation,
                    ObservationAddress = observationAddress,
                    QuarantineCode = quarantineCode,
                    Revision = checked(current.Revision + 1)
                };
            }, (snapshot, result) => snapshot.InternalMailOutboxes.Contains(result));
        }
    }

    private static void ValidateInternalMailInput(SessionInputContent content,
        GalateaInternalMailOutboxSnapshot outbox, GalateaOutboundMailSnapshot mail, string senderId) {
        JsonElement value = content.JsonValue;
        if (content.SchemaId != GalateaObservationContent.V1SchemaId || value.GetProperty("kind").GetString() != "inbound-mail") {
            throw Corrupt("Internal mail bound input must be an inbound Observation.");
        }
        GalateaSenderSnapshot sender = GalateaInputContentValidation.ReadSender(value.GetProperty("sender"));
        JsonElement action = value.GetProperty("action");
        if (sender.Kind != "character" || sender.Id != senderId || sender.Name != outbox.FromCharacterName
            || action.GetProperty("messageId").GetString() != outbox.MessageId
            || action.GetProperty("from").GetString() != outbox.FromCharacterName
            || action.GetProperty("to").GetString() != mail.Recipient
            || action.GetProperty("subject").GetString() != mail.Subject
            || action.GetProperty("body").GetString() != mail.Body
            || action.GetProperty("injectedBy").ValueKind != JsonValueKind.Null) {
            throw Corrupt("Internal mail bound input differs from captured mail facts.");
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
