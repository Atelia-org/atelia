using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaMailboxStatusTests {
    [Fact]
    public void Projection_UsesDocumentedPriorityAndKeepsCountsIndependent() {
        GalateaMailboxStatusAggregate allSignals = Baseline() with {
            RouteState = GalateaDelegationRouteState.Quarantined,
            RouteQuarantineCode = "ROUTE_BAD",
            RouteHasActiveMail = true,
            ActiveMailState = GalateaDurableMailState.Accepted,
            ActiveMailAttemptCount = 7,
            ActiveMailLastCode = "ACCEPTED_TURN_NOT_VISIBLE",
            ActiveMailNextRetryAtUnixTimeMilliseconds = 1234,
            ActiveLeaseQuarantined = true,
            ActiveStateMailCount = 1,
            QueuedCount = 3,
            ReadyNoticeCount = 2
        };

        GalateaMailboxStatusProjection status =
            GalateaDelegationSqliteStore.ProjectMailboxStatus(allSignals);

        Assert.Equal(GalateaMailboxStatusState.Quarantined, status.State);
        Assert.Equal(3, status.QueuedCount);
        Assert.Equal(2, status.ReadyNoticeCount);
        Assert.Equal(7, status.AttemptCount);
        Assert.Equal("ROUTE_BAD", status.Code);
        Assert.Null(status.NextRetryAtUnixTimeMilliseconds);
    }

    [Fact]
    public void Projection_DistinguishesAcceptedHistoryFromOrdinaryBackoff() {
        GalateaMailboxStatusProjection history =
            GalateaDelegationSqliteStore.ProjectMailboxStatus(
                Baseline() with {
                    ActiveMailState = GalateaDurableMailState.Accepted,
                    RouteHasActiveMail = true,
                    ActiveStateMailCount = 1,
                    ActiveMailAttemptCount = 4,
                    ActiveMailLastCode = "ACCEPTED_TURN_NOT_VISIBLE",
                    ActiveMailNextRetryAtUnixTimeMilliseconds = 8000
                }
            );
        GalateaMailboxStatusProjection backoff =
            GalateaDelegationSqliteStore.ProjectMailboxStatus(
                Baseline() with {
                    ActiveMailState = GalateaDurableMailState.OutcomeUnknown,
                    RouteHasActiveMail = true,
                    ActiveStateMailCount = 1,
                    ActiveMailAttemptCount = 2,
                    ActiveMailLastCode = "TEMPORARY",
                    ActiveMailNextRetryAtUnixTimeMilliseconds = 4000
                }
            );

        Assert.Equal(
            GalateaMailboxStatusState.AcceptedHistoryUnavailable,
            history.State
        );
        Assert.Equal(GalateaMailboxStatusState.Backoff, backoff.State);
        Assert.Equal("TEMPORARY", backoff.Code);
    }

    [Fact]
    public void Projection_ActiveMailWithoutBackoffIsRunning() {
        foreach (GalateaDurableMailState mailState in new[] {
            GalateaDurableMailState.Started,
            GalateaDurableMailState.OutcomeUnknown,
            GalateaDurableMailState.Accepted
        }) {
            GalateaMailboxStatusProjection status =
                GalateaDelegationSqliteStore.ProjectMailboxStatus(
                    Baseline() with {
                        RouteHasActiveMail = true,
                        ActiveMailState = mailState,
                        ActiveStateMailCount = 1
                    }
                );

            Assert.Equal(
                GalateaMailboxStatusState.ActiveRunning,
                status.State
            );
        }
    }

    [Fact]
    public void Projection_ReadyReplyPrecedesQueuedAndNoMailIsEmpty() {
        GalateaMailboxStatusProjection ready =
            GalateaDelegationSqliteStore.ProjectMailboxStatus(
                Baseline() with { QueuedCount = 2, ReadyNoticeCount = 1 }
            );
        GalateaMailboxStatusProjection queued =
            GalateaDelegationSqliteStore.ProjectMailboxStatus(
                Baseline() with { QueuedCount = 2 }
            );
        GalateaMailboxStatusProjection empty =
            GalateaDelegationSqliteStore.ProjectMailboxStatus(Baseline());

        Assert.Equal(GalateaMailboxStatusState.ReadyReply, ready.State);
        Assert.Equal(GalateaMailboxStatusState.Queued, queued.State);
        Assert.Equal(GalateaMailboxStatusState.NoMail, empty.State);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void Projection_RejectsInconsistentActiveMailAuthority(
        bool routeHasActiveMail,
        int activeStateMailCount
    ) {
        GalateaMailboxStatusAggregate inconsistent = Baseline() with {
            RouteHasActiveMail = routeHasActiveMail,
            ActiveMailState = routeHasActiveMail
                ? GalateaDurableMailState.Started
                : null,
            ActiveStateMailCount = activeStateMailCount
        };

        Assert.Throws<InvalidDataException>(() =>
            GalateaDelegationSqliteStore.ProjectMailboxStatus(inconsistent)
        );
    }

    [Fact]
    public void DtoHasOnlyThePublicAggregateFields() {
        Assert.Equal(
            [
                "AttemptCount", "Code", "NextRetryAtUnixTimeMilliseconds",
                "QueuedCount", "ReadyNoticeCount", "State"
            ],
            typeof(GalateaMailboxStatusDto).GetProperties()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
        GalateaMailboxStatusProjection unavailable =
            GalateaMailboxStatusProjection.Unavailable("STORE_BAD");
        GalateaMailboxStatusDto dto =
            GalateaMailboxStatusDto.FromProjection(unavailable);
        Assert.Equal("unavailable", dto.State);
        Assert.Equal("STORE_BAD", dto.Code);
    }

    private static GalateaMailboxStatusAggregate Baseline() => new(
        GalateaDelegationRouteState.Bound,
        RouteQuarantineCode: null,
        RouteAttemptCount: 0,
        RouteLastCode: null,
        RouteNextRetryAtUnixTimeMilliseconds: null,
        RouteHasActiveMail: false,
        ActiveMailState: null,
        ActiveMailTerminalCode: null,
        ActiveMailAttemptCount: 0,
        ActiveMailLastCode: null,
        ActiveMailNextRetryAtUnixTimeMilliseconds: null,
        ActiveLeaseQuarantined: false,
        ActiveStateMailCount: 0,
        QueuedCount: 0,
        ReadyNoticeCount: 0
    );
}
