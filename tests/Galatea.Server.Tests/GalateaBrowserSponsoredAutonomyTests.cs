using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaBrowserSponsoredAutonomyTests {
    [Fact]
    public void FirstPulseArmsAndContinuousPulsesDoNotMoveDeadline() {
        var clock = new ManualTimeProvider();
        var cadence = new GalateaBrowserSponsoredAutonomy(clock);

        GalateaBrowserSponsoredAutonomyStatus initial =
            cadence.ProjectStatus();
        Assert.Equal(GalateaBrowserSponsoredAutonomy.WaitingState,
            initial.State);
        Assert.Null(initial.NextActivationAtUnixTimeMilliseconds);
        Assert.Null(initial.LastActivationAtUnixTimeMilliseconds);
        Assert.Null(initial.Code);

        Assert.Equal(
            GalateaBrowserSponsoredAutonomyPulseResult.Rearmed,
            cadence.ObserveSponsorPulse()
        );
        DateTimeOffset expectedDue = clock.GetUtcNow()
            + GalateaBrowserSponsoredAutonomy.IdleInterval;
        Assert.Equal(
            expectedDue.ToUnixTimeMilliseconds(),
            cadence.ProjectStatus().NextActivationAtUnixTimeMilliseconds
        );

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(
            GalateaBrowserSponsoredAutonomyPulseResult.Waiting,
            cadence.ObserveSponsorPulse()
        );
        Assert.Equal(
            expectedDue.ToUnixTimeMilliseconds(),
            cadence.ProjectStatus().NextActivationAtUnixTimeMilliseconds
        );
    }

    [Fact]
    public void ExactSponsorGapStaysContinuousButLongerGapRearms() {
        var exactClock = new ManualTimeProvider();
        var exactCadence = new GalateaBrowserSponsoredAutonomy(exactClock);
        _ = exactCadence.ObserveSponsorPulse();

        exactClock.Advance(
            GalateaBrowserSponsoredAutonomy.SponsorContinuityGap
        );
        Assert.Equal(
            GalateaBrowserSponsoredAutonomyPulseResult.Waiting,
            exactCadence.ObserveSponsorPulse()
        );

        var overClock = new ManualTimeProvider();
        var overCadence = new GalateaBrowserSponsoredAutonomy(overClock);
        _ = overCadence.ObserveSponsorPulse();
        overClock.Advance(
            GalateaBrowserSponsoredAutonomy.SponsorContinuityGap
                + TimeSpan.FromTicks(1)
        );
        Assert.Equal(
            GalateaBrowserSponsoredAutonomyPulseResult.Rearmed,
            overCadence.ObserveSponsorPulse()
        );
        Assert.Equal(
            (overClock.GetUtcNow()
                + GalateaBrowserSponsoredAutonomy.IdleInterval)
                .ToUnixTimeMilliseconds(),
            overCadence.ProjectStatus().NextActivationAtUnixTimeMilliseconds
        );

        overClock.RegressMonotonic(TimeSpan.FromSeconds(1));
        Assert.Equal(
            GalateaBrowserSponsoredAutonomyPulseResult.Rearmed,
            overCadence.ObserveSponsorPulse()
        );
        Assert.Equal(
            (overClock.GetUtcNow()
                + GalateaBrowserSponsoredAutonomy.IdleInterval)
                .ToUnixTimeMilliseconds(),
            overCadence.ProjectStatus().NextActivationAtUnixTimeMilliseconds
        );
    }

    [Fact]
    public void WallClockRegressionDoesNotAdvanceMonotonicDueDecision() {
        var clock = new ManualTimeProvider();
        var cadence = new GalateaBrowserSponsoredAutonomy(clock);
        _ = cadence.ObserveSponsorPulse();

        clock.AdvanceMonotonic(TimeSpan.FromSeconds(10));
        clock.AdvanceWall(TimeSpan.FromHours(-2));

        Assert.Equal(
            GalateaBrowserSponsoredAutonomyPulseResult.Waiting,
            cadence.ObserveSponsorPulse()
        );
        Assert.Equal(
            (clock.GetUtcNow() + TimeSpan.FromMinutes(9)
                + TimeSpan.FromSeconds(50)).ToUnixTimeMilliseconds(),
            cadence.ProjectStatus().NextActivationAtUnixTimeMilliseconds
        );
    }

    [Fact]
    public void ExactDueClaimsOnceAndNeverCatchesUp() {
        var clock = new ManualTimeProvider();
        var cadence = new GalateaBrowserSponsoredAutonomy(clock);
        _ = cadence.ObserveSponsorPulse();

        for (int pulse = 1; pulse < 60; pulse++) {
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(
                GalateaBrowserSponsoredAutonomyPulseResult.Waiting,
                cadence.ObserveSponsorPulse()
            );
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(
            GalateaBrowserSponsoredAutonomyPulseResult
                .AutonomousActivationDue,
            cadence.ObserveSponsorPulse()
        );
        Assert.True(cadence.TryClaimAutonomousActivationStarted(
            out GalateaBrowserSponsoredAutonomyClaim? claim
        ));
        Assert.NotNull(claim);
        Assert.False(cadence.TryClaimAutonomousActivationStarted(out _));
        GalateaBrowserSponsoredAutonomyStatus claimed =
            cadence.ProjectStatus();
        Assert.Null(claimed.NextActivationAtUnixTimeMilliseconds);
        Assert.Equal(
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            claimed.LastActivationAtUnixTimeMilliseconds
        );

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(
            GalateaBrowserSponsoredAutonomyPulseResult.Waiting,
            cadence.ObserveSponsorPulse()
        );
        Assert.Null(cadence.ProjectStatus()
            .NextActivationAtUnixTimeMilliseconds);
    }

    [Fact]
    public void CompletedMainTurnOnlyResetsPreviouslySponsoredState() {
        var clock = new ManualTimeProvider();
        var cadence = new GalateaBrowserSponsoredAutonomy(clock);

        Assert.True(cadence.SettleMainTurn(
            new GalateaBrowserSponsoredAutonomyTurnSettlement(),
            isAutonomousActivation: false,
            completed: true
        ));
        Assert.Null(cadence.ProjectStatus()
            .NextActivationAtUnixTimeMilliseconds);

        _ = cadence.ObserveSponsorPulse();
        clock.Advance(TimeSpan.FromMinutes(2));
        var settlement =
            new GalateaBrowserSponsoredAutonomyTurnSettlement();
        Assert.True(cadence.SettleMainTurn(
            settlement,
            isAutonomousActivation: false,
            completed: true
        ));
        DateTimeOffset expected = clock.GetUtcNow()
            + GalateaBrowserSponsoredAutonomy.IdleInterval;
        Assert.Equal(
            expected.ToUnixTimeMilliseconds(),
            cadence.ProjectStatus().NextActivationAtUnixTimeMilliseconds
        );

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(cadence.SettleMainTurn(
            settlement,
            isAutonomousActivation: false,
            completed: true
        ));
        Assert.Equal(
            expected.ToUnixTimeMilliseconds(),
            cadence.ProjectStatus().NextActivationAtUnixTimeMilliseconds
        );
    }

    [Fact]
    public void AutonomousNonCompletedOutcomePausesUntilCompletedMainTurn() {
        var clock = new ManualTimeProvider();
        var cadence = new GalateaBrowserSponsoredAutonomy(clock);
        GalateaBrowserSponsoredAutonomyClaim claim = ClaimAtExactDue(
            cadence,
            clock
        );

        Assert.True(cadence.SettleMainTurn(
            new GalateaBrowserSponsoredAutonomyTurnSettlement(),
            isAutonomousActivation: true,
            completed: false,
            autonomousClaim: claim
        ));
        GalateaBrowserSponsoredAutonomyStatus paused =
            cadence.ProjectStatus();
        Assert.Equal(GalateaBrowserSponsoredAutonomy.PausedState,
            paused.State);
        Assert.Equal(GalateaBrowserSponsoredAutonomy.PausedCode,
            paused.Code);
        Assert.Null(paused.NextActivationAtUnixTimeMilliseconds);
        Assert.Equal(
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            paused.LastActivationAtUnixTimeMilliseconds
        );

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(
            GalateaBrowserSponsoredAutonomyPulseResult.AutonomyPaused,
            cadence.ObserveSponsorPulse()
        );
        Assert.Equal(GalateaBrowserSponsoredAutonomy.PausedState,
            cadence.ProjectStatus().State);

        Assert.True(cadence.SettleMainTurn(
            new GalateaBrowserSponsoredAutonomyTurnSettlement(),
            isAutonomousActivation: false,
            completed: true
        ));
        GalateaBrowserSponsoredAutonomyStatus resumed =
            cadence.ProjectStatus();
        Assert.Equal(GalateaBrowserSponsoredAutonomy.WaitingState,
            resumed.State);
        Assert.Null(resumed.Code);
        Assert.Equal(
            (clock.GetUtcNow()
                + GalateaBrowserSponsoredAutonomy.IdleInterval)
                .ToUnixTimeMilliseconds(),
            resumed.NextActivationAtUnixTimeMilliseconds
        );
    }

    [Fact]
    public void NewInstanceAfterRestartAlwaysLateRearms() {
        var clock = new ManualTimeProvider();
        var beforeRestart = new GalateaBrowserSponsoredAutonomy(clock);
        _ = beforeRestart.ObserveSponsorPulse();
        clock.Advance(TimeSpan.FromMinutes(10));

        var afterRestart = new GalateaBrowserSponsoredAutonomy(clock);
        Assert.Equal(
            GalateaBrowserSponsoredAutonomyPulseResult.Rearmed,
            afterRestart.ObserveSponsorPulse()
        );
        GalateaBrowserSponsoredAutonomyStatus status =
            afterRestart.ProjectStatus();
        Assert.Equal(
            (clock.GetUtcNow()
                + GalateaBrowserSponsoredAutonomy.IdleInterval)
                .ToUnixTimeMilliseconds(),
            status.NextActivationAtUnixTimeMilliseconds
        );
        Assert.Null(status.LastActivationAtUnixTimeMilliseconds);
    }

    [Fact]
    public void CurrentUnsettledClaimRollsBackExactDueAndLastActivation() {
        var clock = new ManualTimeProvider();
        var cadence = new GalateaBrowserSponsoredAutonomy(clock);
        GalateaBrowserSponsoredAutonomyClaim firstClaim = ClaimAtExactDue(
            cadence,
            clock
        );
        Assert.True(cadence.SettleMainTurn(
            new GalateaBrowserSponsoredAutonomyTurnSettlement(),
            isAutonomousActivation: true,
            completed: true,
            autonomousClaim: firstClaim
        ));
        long previousLast = cadence.ProjectStatus()
            .LastActivationAtUnixTimeMilliseconds!.Value;
        GalateaBrowserSponsoredAutonomyClaim secondClaim = ClaimAtExactDue(
            cadence,
            clock
        );
        Assert.NotEqual(
            previousLast,
            cadence.ProjectStatus().LastActivationAtUnixTimeMilliseconds
        );

        var rollbackSettlement =
            new GalateaBrowserSponsoredAutonomyTurnSettlement();
        Assert.True(cadence.TryRollbackAutonomousActivationClaim(
            secondClaim,
            rollbackSettlement
        ));
        Assert.True(rollbackSettlement.IsSettled);
        GalateaBrowserSponsoredAutonomyStatus restored =
            cadence.ProjectStatus();
        Assert.Equal(GalateaBrowserSponsoredAutonomy.WaitingState,
            restored.State);
        Assert.Equal(previousLast,
            restored.LastActivationAtUnixTimeMilliseconds);
        Assert.Equal(
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            restored.NextActivationAtUnixTimeMilliseconds
        );
        Assert.False(cadence.TryRollbackAutonomousActivationClaim(
            secondClaim,
            rollbackSettlement
        ));
        Assert.Equal(
            GalateaBrowserSponsoredAutonomyPulseResult
                .AutonomousActivationDue,
            cadence.ObserveSponsorPulse()
        );
        Assert.True(cadence.TryClaimAutonomousActivationStarted(out _));
    }

    [Fact]
    public void RollbackValidationFailuresAreZeroMutation() {
        var clock = new ManualTimeProvider();
        var cadence = new GalateaBrowserSponsoredAutonomy(clock);
        GalateaBrowserSponsoredAutonomyClaim current = ClaimAtExactDue(
            cadence,
            clock
        );
        GalateaBrowserSponsoredAutonomyStatus before =
            cadence.ProjectStatus();

        var wrong = new GalateaBrowserSponsoredAutonomyClaim(
            previousDueFromTimestamp: -1,
            previousLastAutonomousActivationTimestamp: null,
            claimedAtTimestamp: -1
        );
        var wrongSettlement =
            new GalateaBrowserSponsoredAutonomyTurnSettlement();
        Assert.False(cadence.TryRollbackAutonomousActivationClaim(
            wrong,
            wrongSettlement
        ));
        Assert.Equal(before, cadence.ProjectStatus());
        Assert.False(wrong.IsSettled);
        Assert.False(wrongSettlement.IsSettled);
        Assert.False(current.IsSettled);

        var alreadySettled = new GalateaBrowserSponsoredAutonomyClaim(
            previousDueFromTimestamp: -2,
            previousLastAutonomousActivationTimestamp: null,
            claimedAtTimestamp: -2
        );
        Assert.True(alreadySettled.TrySettle());
        var freshSettlement =
            new GalateaBrowserSponsoredAutonomyTurnSettlement();
        Assert.False(cadence.TryRollbackAutonomousActivationClaim(
            alreadySettled,
            freshSettlement
        ));
        Assert.Equal(before, cadence.ProjectStatus());
        Assert.True(alreadySettled.IsSettled);
        Assert.False(freshSettlement.IsSettled);
        Assert.False(current.IsSettled);

        var preconsumedSettlement =
            new GalateaBrowserSponsoredAutonomyTurnSettlement();
        Assert.True(preconsumedSettlement.TrySettle());
        Assert.False(cadence.TryRollbackAutonomousActivationClaim(
            current,
            preconsumedSettlement
        ));
        Assert.Equal(before, cadence.ProjectStatus());
        Assert.True(preconsumedSettlement.IsSettled);
        Assert.False(current.IsSettled);

        var correctSettlement =
            new GalateaBrowserSponsoredAutonomyTurnSettlement();
        Assert.True(cadence.TryRollbackAutonomousActivationClaim(
            current,
            correctSettlement
        ));
        Assert.True(current.IsSettled);
        Assert.True(correctSettlement.IsSettled);
    }

    [Fact]
    public void TerminalSettlementValidationFailuresAreZeroMutation() {
        var clock = new ManualTimeProvider();
        var cadence = new GalateaBrowserSponsoredAutonomy(clock);
        GalateaBrowserSponsoredAutonomyClaim current = ClaimAtExactDue(
            cadence,
            clock
        );
        GalateaBrowserSponsoredAutonomyStatus before =
            cadence.ProjectStatus();
        var wrong = new GalateaBrowserSponsoredAutonomyClaim(
            previousDueFromTimestamp: -1,
            previousLastAutonomousActivationTimestamp: null,
            claimedAtTimestamp: -1
        );
        var wrongSettlement =
            new GalateaBrowserSponsoredAutonomyTurnSettlement();

        Assert.False(cadence.SettleMainTurn(
            wrongSettlement,
            isAutonomousActivation: true,
            completed: false,
            autonomousClaim: wrong
        ));
        Assert.Equal(before, cadence.ProjectStatus());
        Assert.False(wrongSettlement.IsSettled);
        Assert.False(wrong.IsSettled);
        Assert.False(current.IsSettled);

        var preconsumedSettlement =
            new GalateaBrowserSponsoredAutonomyTurnSettlement();
        Assert.True(preconsumedSettlement.TrySettle());
        Assert.False(cadence.SettleMainTurn(
            preconsumedSettlement,
            isAutonomousActivation: true,
            completed: false,
            autonomousClaim: current
        ));
        Assert.Equal(before, cadence.ProjectStatus());
        Assert.True(preconsumedSettlement.IsSettled);
        Assert.False(current.IsSettled);

        var correctSettlement =
            new GalateaBrowserSponsoredAutonomyTurnSettlement();
        Assert.True(cadence.SettleMainTurn(
            correctSettlement,
            isAutonomousActivation: true,
            completed: false,
            autonomousClaim: current
        ));
        Assert.True(correctSettlement.IsSettled);
        Assert.True(current.IsSettled);
        Assert.Equal(GalateaBrowserSponsoredAutonomy.PausedState,
            cadence.ProjectStatus().State);
    }

    private static GalateaBrowserSponsoredAutonomyClaim ClaimAtExactDue(
        GalateaBrowserSponsoredAutonomy cadence,
        ManualTimeProvider clock
    ) {
        _ = cadence.ObserveSponsorPulse();
        for (int pulse = 0; pulse < 60; pulse++) {
            clock.Advance(TimeSpan.FromSeconds(10));
            GalateaBrowserSponsoredAutonomyPulseResult result =
                cadence.ObserveSponsorPulse();
            if (pulse < 59) {
                Assert.Equal(
                    GalateaBrowserSponsoredAutonomyPulseResult.Waiting,
                    result
                );
            }
            else {
                Assert.Equal(
                    GalateaBrowserSponsoredAutonomyPulseResult
                        .AutonomousActivationDue,
                    result
                );
                Assert.True(cadence.TryClaimAutonomousActivationStarted(
                    out GalateaBrowserSponsoredAutonomyClaim? claim
                ));
                return Assert.IsType<
                    GalateaBrowserSponsoredAutonomyClaim>(claim);
            }
        }
        throw new InvalidOperationException("Exact due was not reached.");
    }

    private sealed class ManualTimeProvider : TimeProvider {
        private long _timestamp;
        private DateTimeOffset _utcNow = new(
            2030,
            1,
            2,
            3,
            4,
            5,
            TimeSpan.Zero
        );

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan value) {
            AdvanceMonotonic(value);
            AdvanceWall(value);
        }

        internal void AdvanceMonotonic(TimeSpan value) {
            _timestamp = checked(_timestamp + value.Ticks);
        }

        internal void AdvanceWall(TimeSpan value) {
            _utcNow += value;
        }

        internal void RegressMonotonic(TimeSpan value) {
            _timestamp = checked(_timestamp - value.Ticks);
        }
    }
}
