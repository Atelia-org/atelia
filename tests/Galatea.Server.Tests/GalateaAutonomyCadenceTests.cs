using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaAutonomyCadenceTests {
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(10);

    [Fact]
    public void CustomPositiveIntervalControlsDueAndCompletedTurnReset() {
        var clock = new ManualTimeProvider();
        var cadence = new GalateaAutonomyCadence(clock, TimeSpan.FromMinutes(1));
        _ = cadence.ObservePulse();

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(GalateaAutonomyCadencePulseResult.Waiting, cadence.ObservePulse());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(GalateaAutonomyCadencePulseResult.AutonomousActivationDue, cadence.ObservePulse());
        Assert.True(cadence.TryClaimAutonomousActivationStarted(out GalateaAutonomyCadenceClaim? claim));
        Assert.NotNull(claim);
        Assert.True(cadence.SettleMainTurn(
            new GalateaAutonomyCadenceTurnSettlement(),
            isAutonomousActivation: true,
            completed: true,
            autonomousClaim: claim
        ));
        Assert.Equal(
            (clock.GetUtcNow() + TimeSpan.FromMinutes(1)).ToUnixTimeMilliseconds(),
            cadence.ProjectStatus().NextActivationAtUnixTimeMilliseconds
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveIntervalIsRejected(int minutes) {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GalateaAutonomyCadence(
                new ManualTimeProvider(),
                TimeSpan.FromMinutes(minutes)
            )
        );
    }

    [Fact]
    public void ArmStartsDeadlineOnceWithoutRearmingOnRepeatedAttach() {
        var clock = new ManualTimeProvider();
        var cadence = new GalateaAutonomyCadence(clock, DefaultInterval);
        cadence.Arm();
        long? expectedDue = cadence.ProjectStatus()
            .NextActivationAtUnixTimeMilliseconds;
        Assert.NotNull(expectedDue);

        clock.Advance(TimeSpan.FromMinutes(5));
        cadence.Arm();

        Assert.Equal(GalateaAutonomyCadencePulseResult.Waiting,
            cadence.ObservePulse());
        Assert.Equal(expectedDue, cadence.ProjectStatus()
            .NextActivationAtUnixTimeMilliseconds);

        clock.Advance(TimeSpan.FromMinutes(25));
        Assert.Equal(GalateaAutonomyCadencePulseResult.AutonomousActivationDue,
            cadence.ObservePulse());
        Assert.True(cadence.TryClaimAutonomousActivationStarted(out _));
        Assert.False(cadence.TryClaimAutonomousActivationStarted(out _));
    }

    [Fact]
    public void FirstPulseArmsAndContinuousPulsesDoNotMoveDeadline() {
        var clock = new ManualTimeProvider();
        var cadence = new GalateaAutonomyCadence(clock, DefaultInterval);

        GalateaAutonomyCadenceStatus initial =
            cadence.ProjectStatus();
        Assert.Equal(GalateaAutonomyCadence.WaitingState,
            initial.State);
        Assert.Null(initial.NextActivationAtUnixTimeMilliseconds);
        Assert.Null(initial.LastActivationAtUnixTimeMilliseconds);
        Assert.Null(initial.Code);

        Assert.Equal(
            GalateaAutonomyCadencePulseResult.Rearmed,
            cadence.ObservePulse()
        );
        DateTimeOffset expectedDue = clock.GetUtcNow()
            + DefaultInterval;
        Assert.Equal(
            expectedDue.ToUnixTimeMilliseconds(),
            cadence.ProjectStatus().NextActivationAtUnixTimeMilliseconds
        );

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(
            GalateaAutonomyCadencePulseResult.Waiting,
            cadence.ObservePulse()
        );
        Assert.Equal(
            expectedDue.ToUnixTimeMilliseconds(),
            cadence.ProjectStatus().NextActivationAtUnixTimeMilliseconds
        );
    }

    [Fact]
    public void DelayedPulsesKeepDeadlineAndMonotonicRegressionRearms() {
        var exactClock = new ManualTimeProvider();
        var exactCadence = new GalateaAutonomyCadence(exactClock, DefaultInterval);
        _ = exactCadence.ObservePulse();

        long? expectedDue = exactCadence.ProjectStatus()
            .NextActivationAtUnixTimeMilliseconds;
        exactClock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(
            GalateaAutonomyCadencePulseResult.Waiting,
            exactCadence.ObservePulse()
        );
        Assert.Equal(expectedDue, exactCadence.ProjectStatus()
            .NextActivationAtUnixTimeMilliseconds);

        var overClock = new ManualTimeProvider();
        var overCadence = new GalateaAutonomyCadence(overClock, DefaultInterval);
        _ = overCadence.ObservePulse();
        long? overExpectedDue = overCadence.ProjectStatus()
            .NextActivationAtUnixTimeMilliseconds;
        overClock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(
            GalateaAutonomyCadencePulseResult.Waiting,
            overCadence.ObservePulse()
        );
        Assert.Equal(
            overExpectedDue,
            overCadence.ProjectStatus().NextActivationAtUnixTimeMilliseconds
        );

        overClock.RegressMonotonic(TimeSpan.FromSeconds(1));
        Assert.Equal(
            GalateaAutonomyCadencePulseResult.Rearmed,
            overCadence.ObservePulse()
        );
        Assert.Equal(
            (overClock.GetUtcNow()
                + DefaultInterval)
                .ToUnixTimeMilliseconds(),
            overCadence.ProjectStatus().NextActivationAtUnixTimeMilliseconds
        );
    }

    [Fact]
    public void WallClockRegressionDoesNotAdvanceMonotonicDueDecision() {
        var clock = new ManualTimeProvider();
        var cadence = new GalateaAutonomyCadence(clock, DefaultInterval);
        _ = cadence.ObservePulse();

        clock.AdvanceMonotonic(TimeSpan.FromSeconds(10));
        clock.AdvanceWall(TimeSpan.FromHours(-2));

        Assert.Equal(
            GalateaAutonomyCadencePulseResult.Waiting,
            cadence.ObservePulse()
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
        var cadence = new GalateaAutonomyCadence(clock, DefaultInterval);
        _ = cadence.ObservePulse();

        for (int pulse = 1; pulse < 60; pulse++) {
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(
                GalateaAutonomyCadencePulseResult.Waiting,
                cadence.ObservePulse()
            );
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(
            GalateaAutonomyCadencePulseResult
                .AutonomousActivationDue,
            cadence.ObservePulse()
        );
        Assert.True(cadence.TryClaimAutonomousActivationStarted(
            out GalateaAutonomyCadenceClaim? claim
        ));
        Assert.NotNull(claim);
        Assert.False(cadence.TryClaimAutonomousActivationStarted(out _));
        GalateaAutonomyCadenceStatus claimed =
            cadence.ProjectStatus();
        Assert.Null(claimed.NextActivationAtUnixTimeMilliseconds);
        Assert.Equal(
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            claimed.LastActivationAtUnixTimeMilliseconds
        );

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(
            GalateaAutonomyCadencePulseResult.Waiting,
            cadence.ObservePulse()
        );
        Assert.Null(cadence.ProjectStatus()
            .NextActivationAtUnixTimeMilliseconds);
    }

    [Fact]
    public void CompletedMainTurnOnlyResetsPreviouslyArmedState() {
        var clock = new ManualTimeProvider();
        var cadence = new GalateaAutonomyCadence(clock, DefaultInterval);

        Assert.True(cadence.SettleMainTurn(
            new GalateaAutonomyCadenceTurnSettlement(),
            isAutonomousActivation: false,
            completed: true
        ));
        Assert.Null(cadence.ProjectStatus()
            .NextActivationAtUnixTimeMilliseconds);

        _ = cadence.ObservePulse();
        clock.Advance(TimeSpan.FromMinutes(2));
        var settlement =
            new GalateaAutonomyCadenceTurnSettlement();
        Assert.True(cadence.SettleMainTurn(
            settlement,
            isAutonomousActivation: false,
            completed: true
        ));
        DateTimeOffset expected = clock.GetUtcNow()
            + DefaultInterval;
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
        var cadence = new GalateaAutonomyCadence(clock, DefaultInterval);
        GalateaAutonomyCadenceClaim claim = ClaimAtExactDue(
            cadence,
            clock
        );

        Assert.True(cadence.SettleMainTurn(
            new GalateaAutonomyCadenceTurnSettlement(),
            isAutonomousActivation: true,
            completed: false,
            autonomousClaim: claim
        ));
        GalateaAutonomyCadenceStatus paused =
            cadence.ProjectStatus();
        Assert.Equal(GalateaAutonomyCadence.PausedState,
            paused.State);
        Assert.Equal(GalateaAutonomyCadence.PausedCode,
            paused.Code);
        Assert.Null(paused.NextActivationAtUnixTimeMilliseconds);
        Assert.Equal(
            clock.GetUtcNow().ToUnixTimeMilliseconds(),
            paused.LastActivationAtUnixTimeMilliseconds
        );

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(
            GalateaAutonomyCadencePulseResult.AutonomyPaused,
            cadence.ObservePulse()
        );
        Assert.Equal(GalateaAutonomyCadence.PausedState,
            cadence.ProjectStatus().State);

        Assert.True(cadence.SettleMainTurn(
            new GalateaAutonomyCadenceTurnSettlement(),
            isAutonomousActivation: false,
            completed: true
        ));
        GalateaAutonomyCadenceStatus resumed =
            cadence.ProjectStatus();
        Assert.Equal(GalateaAutonomyCadence.WaitingState,
            resumed.State);
        Assert.Null(resumed.Code);
        Assert.Equal(
            (clock.GetUtcNow()
                + DefaultInterval)
                .ToUnixTimeMilliseconds(),
            resumed.NextActivationAtUnixTimeMilliseconds
        );
    }

    [Fact]
    public void NewInstanceAfterRestartAlwaysLateRearms() {
        var clock = new ManualTimeProvider();
        var beforeRestart = new GalateaAutonomyCadence(clock, DefaultInterval);
        _ = beforeRestart.ObservePulse();
        clock.Advance(TimeSpan.FromMinutes(10));

        var afterRestart = new GalateaAutonomyCadence(clock, DefaultInterval);
        Assert.Equal(
            GalateaAutonomyCadencePulseResult.Rearmed,
            afterRestart.ObservePulse()
        );
        GalateaAutonomyCadenceStatus status =
            afterRestart.ProjectStatus();
        Assert.Equal(
            (clock.GetUtcNow()
                + DefaultInterval)
                .ToUnixTimeMilliseconds(),
            status.NextActivationAtUnixTimeMilliseconds
        );
        Assert.Null(status.LastActivationAtUnixTimeMilliseconds);
    }

    [Fact]
    public void CurrentUnsettledClaimRollsBackExactDueAndLastActivation() {
        var clock = new ManualTimeProvider();
        var cadence = new GalateaAutonomyCadence(clock, DefaultInterval);
        GalateaAutonomyCadenceClaim firstClaim = ClaimAtExactDue(
            cadence,
            clock
        );
        Assert.True(cadence.SettleMainTurn(
            new GalateaAutonomyCadenceTurnSettlement(),
            isAutonomousActivation: true,
            completed: true,
            autonomousClaim: firstClaim
        ));
        long previousLast = cadence.ProjectStatus()
            .LastActivationAtUnixTimeMilliseconds!.Value;
        GalateaAutonomyCadenceClaim secondClaim = ClaimAtExactDue(
            cadence,
            clock
        );
        Assert.NotEqual(
            previousLast,
            cadence.ProjectStatus().LastActivationAtUnixTimeMilliseconds
        );

        var rollbackSettlement =
            new GalateaAutonomyCadenceTurnSettlement();
        Assert.True(cadence.TryRollbackAutonomousActivationClaim(
            secondClaim,
            rollbackSettlement
        ));
        Assert.True(rollbackSettlement.IsSettled);
        GalateaAutonomyCadenceStatus restored =
            cadence.ProjectStatus();
        Assert.Equal(GalateaAutonomyCadence.WaitingState,
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
            GalateaAutonomyCadencePulseResult
                .AutonomousActivationDue,
            cadence.ObservePulse()
        );
        Assert.True(cadence.TryClaimAutonomousActivationStarted(out _));
    }

    [Fact]
    public void RollbackValidationFailuresAreZeroMutation() {
        var clock = new ManualTimeProvider();
        var cadence = new GalateaAutonomyCadence(clock, DefaultInterval);
        GalateaAutonomyCadenceClaim current = ClaimAtExactDue(
            cadence,
            clock
        );
        GalateaAutonomyCadenceStatus before =
            cadence.ProjectStatus();

        var wrong = new GalateaAutonomyCadenceClaim(
            previousDueFromTimestamp: -1,
            previousLastAutonomousActivationTimestamp: null,
            claimedAtTimestamp: -1
        );
        var wrongSettlement =
            new GalateaAutonomyCadenceTurnSettlement();
        Assert.False(cadence.TryRollbackAutonomousActivationClaim(
            wrong,
            wrongSettlement
        ));
        Assert.Equal(before, cadence.ProjectStatus());
        Assert.False(wrong.IsSettled);
        Assert.False(wrongSettlement.IsSettled);
        Assert.False(current.IsSettled);

        var alreadySettled = new GalateaAutonomyCadenceClaim(
            previousDueFromTimestamp: -2,
            previousLastAutonomousActivationTimestamp: null,
            claimedAtTimestamp: -2
        );
        Assert.True(alreadySettled.TrySettle());
        var freshSettlement =
            new GalateaAutonomyCadenceTurnSettlement();
        Assert.False(cadence.TryRollbackAutonomousActivationClaim(
            alreadySettled,
            freshSettlement
        ));
        Assert.Equal(before, cadence.ProjectStatus());
        Assert.True(alreadySettled.IsSettled);
        Assert.False(freshSettlement.IsSettled);
        Assert.False(current.IsSettled);

        var preconsumedSettlement =
            new GalateaAutonomyCadenceTurnSettlement();
        Assert.True(preconsumedSettlement.TrySettle());
        Assert.False(cadence.TryRollbackAutonomousActivationClaim(
            current,
            preconsumedSettlement
        ));
        Assert.Equal(before, cadence.ProjectStatus());
        Assert.True(preconsumedSettlement.IsSettled);
        Assert.False(current.IsSettled);

        var correctSettlement =
            new GalateaAutonomyCadenceTurnSettlement();
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
        var cadence = new GalateaAutonomyCadence(clock, DefaultInterval);
        GalateaAutonomyCadenceClaim current = ClaimAtExactDue(
            cadence,
            clock
        );
        GalateaAutonomyCadenceStatus before =
            cadence.ProjectStatus();
        var wrong = new GalateaAutonomyCadenceClaim(
            previousDueFromTimestamp: -1,
            previousLastAutonomousActivationTimestamp: null,
            claimedAtTimestamp: -1
        );
        var wrongSettlement =
            new GalateaAutonomyCadenceTurnSettlement();

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
            new GalateaAutonomyCadenceTurnSettlement();
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
            new GalateaAutonomyCadenceTurnSettlement();
        Assert.True(cadence.SettleMainTurn(
            correctSettlement,
            isAutonomousActivation: true,
            completed: false,
            autonomousClaim: current
        ));
        Assert.True(correctSettlement.IsSettled);
        Assert.True(current.IsSettled);
        Assert.Equal(GalateaAutonomyCadence.PausedState,
            cadence.ProjectStatus().State);
    }

    private static GalateaAutonomyCadenceClaim ClaimAtExactDue(
        GalateaAutonomyCadence cadence,
        ManualTimeProvider clock
    ) {
        _ = cadence.ObservePulse();
        for (int pulse = 0; pulse < 60; pulse++) {
            clock.Advance(TimeSpan.FromSeconds(10));
            GalateaAutonomyCadencePulseResult result =
                cadence.ObservePulse();
            if (pulse < 59) {
                Assert.Equal(
                    GalateaAutonomyCadencePulseResult.Waiting,
                    result
                );
            }
            else {
                Assert.Equal(
                    GalateaAutonomyCadencePulseResult
                        .AutonomousActivationDue,
                    result
                );
                Assert.True(cadence.TryClaimAutonomousActivationStarted(
                    out GalateaAutonomyCadenceClaim? claim
                ));
                return Assert.IsType<
                    GalateaAutonomyCadenceClaim>(claim);
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
