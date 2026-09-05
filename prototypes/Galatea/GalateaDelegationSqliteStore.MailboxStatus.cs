using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server;

internal sealed partial class GalateaDelegationSqliteStore {
    private static GalateaMailboxStatusAggregate
        ReadMailboxStatusAggregate(
            SqliteConnection connection,
            SqliteTransaction transaction
        ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT route.state,
                   route.quarantine_code,
                   route.ensure_attempt_count,
                   route.ensure_last_code,
                   route.next_ensure_at_ms,
                   active.state,
                   active.terminal_code,
                   active.reconcile_attempt_count,
                   active.reconcile_last_code,
                   active.next_reconcile_at_ms,
                   EXISTS(
                       SELECT 1 FROM reply_lease
                       WHERE active_slot = 1 AND state = 'Quarantined'
                   ),
                   (
                       SELECT COUNT(*) FROM outbound_mail
                       WHERE route_class = 'Codex' AND state = 'Queued'
                   ),
                   (
                       SELECT COUNT(*) FROM reply_notice
                       WHERE state = 'Ready'
                   )
            FROM route_binding AS route
            LEFT JOIN outbound_mail AS active
              ON active.dispatch_id = route.active_dispatch_id
            WHERE route.singleton = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            throw Corrupt("route_binding singleton is missing.");
        }
        GalateaDurableMailState? activeMailState = reader.IsDBNull(5)
            ? null
            : ParseExact<GalateaDurableMailState>(reader.GetString(5));
        var aggregate = new GalateaMailboxStatusAggregate(
            ParseExact<GalateaDelegationRouteState>(reader.GetString(0)),
            ReadNullableString(reader, 1),
            reader.GetInt32(2),
            ReadNullableString(reader, 3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            activeMailState,
            ReadNullableString(reader, 6),
            reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
            ReadNullableString(reader, 8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.GetInt64(10) == 1,
            checked((int)reader.GetInt64(11)),
            checked((int)reader.GetInt64(12))
        );
        if (reader.Read()) {
            throw Corrupt("route_binding has multiple singleton rows.");
        }
        return aggregate;
    }

    internal static GalateaMailboxStatusProjection ProjectMailboxStatus(
        GalateaMailboxStatusAggregate value
    ) {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(value.QueuedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(value.ReadyNoticeCount);
        ArgumentOutOfRangeException.ThrowIfNegative(value.RouteAttemptCount);
        ArgumentOutOfRangeException.ThrowIfNegative(
            value.ActiveMailAttemptCount
        );

        if (value.RouteState == GalateaDelegationRouteState.Quarantined
            || value.ActiveMailState
                == GalateaDurableMailState.Quarantined
            || value.ActiveLeaseQuarantined) {
            string code = value.RouteState
                    == GalateaDelegationRouteState.Quarantined
                ? value.RouteQuarantineCode ?? "ROUTE_QUARANTINED"
                : value.ActiveMailState
                    == GalateaDurableMailState.Quarantined
                    ? value.ActiveMailTerminalCode ?? "MAIL_QUARANTINED"
                    : "REPLY_LEASE_QUARANTINED";
            return Build(
                GalateaMailboxStatusState.Quarantined,
                value,
                AttemptCount(value),
                code,
                NextRetry(value)
            );
        }

        if (value.ActiveMailState == GalateaDurableMailState.Accepted
            && string.Equals(
                value.ActiveMailLastCode,
                GalateaDelegateDispatchInspection.AcceptedTurnNotVisible
                    .FailureCode,
                StringComparison.Ordinal
            )) {
            return Build(
                GalateaMailboxStatusState.AcceptedHistoryUnavailable,
                value,
                value.ActiveMailAttemptCount,
                GalateaDelegateDispatchInspection.AcceptedTurnNotVisible
                    .FailureCode,
                value.ActiveMailNextRetryAtUnixTimeMilliseconds
            );
        }

        if (NextRetry(value) is { } nextRetry) {
            return Build(
                GalateaMailboxStatusState.Backoff,
                value,
                AttemptCount(value),
                LastCode(value),
                nextRetry
            );
        }

        if (value.RouteState == GalateaDelegationRouteState.Binding
            || value.ActiveMailState is
                GalateaDurableMailState.Started
                or GalateaDurableMailState.OutcomeUnknown
                or GalateaDurableMailState.Accepted) {
            return Build(
                GalateaMailboxStatusState.ActiveRunning,
                value,
                AttemptCount(value),
                LastCode(value),
                nextRetryAtUnixTimeMilliseconds: null
            );
        }

        if (value.ReadyNoticeCount > 0) {
            return Build(
                GalateaMailboxStatusState.ReadyReply,
                value,
                attemptCount: 0,
                code: null,
                nextRetryAtUnixTimeMilliseconds: null
            );
        }

        if (value.QueuedCount > 0) {
            return Build(
                GalateaMailboxStatusState.Queued,
                value,
                attemptCount: 0,
                code: null,
                nextRetryAtUnixTimeMilliseconds: null
            );
        }

        return Build(
            GalateaMailboxStatusState.NoMail,
            value,
            attemptCount: 0,
            code: null,
            nextRetryAtUnixTimeMilliseconds: null
        );
    }

    private static GalateaMailboxStatusProjection Build(
        GalateaMailboxStatusState state,
        GalateaMailboxStatusAggregate value,
        int attemptCount,
        string? code,
        long? nextRetryAtUnixTimeMilliseconds
    ) => new(
        state,
        value.QueuedCount,
        value.ReadyNoticeCount,
        attemptCount,
        code,
        nextRetryAtUnixTimeMilliseconds
    );

    private static int AttemptCount(GalateaMailboxStatusAggregate value) =>
        value.ActiveMailState is not null
            ? value.ActiveMailAttemptCount
            : value.RouteAttemptCount;

    private static string? LastCode(GalateaMailboxStatusAggregate value) =>
        value.ActiveMailState is not null
            ? value.ActiveMailLastCode
            : value.RouteLastCode;

    private static long? NextRetry(GalateaMailboxStatusAggregate value) =>
        value.ActiveMailState is not null
            ? value.ActiveMailNextRetryAtUnixTimeMilliseconds
            : value.RouteNextRetryAtUnixTimeMilliseconds;
}
