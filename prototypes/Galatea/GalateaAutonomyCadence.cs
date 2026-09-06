namespace Atelia.Galatea.Server;

internal enum GalateaAutonomyCadencePulseResult {
    Rearmed,
    Waiting,
    AutonomousActivationDue,
    AutonomyPaused
}

internal sealed record GalateaAutonomyCadenceStatus(
    string State,
    long? NextActivationAtUnixTimeMilliseconds,
    long? LastActivationAtUnixTimeMilliseconds,
    string? Code
);

/// <summary>
/// One caller-owned guard for settling a main turn against server-owned
/// autonomy cadence at most once. The caller must hold the corresponding
/// session TurnLock whenever this guard is used.
/// </summary>
internal sealed class GalateaAutonomyCadenceTurnSettlement {
    private bool _settled;

    internal bool IsSettled => _settled;

    internal bool TrySettle() {
        if (_settled) { return false; }
        _settled = true;
        return true;
    }

    internal void SettleAfterValidation() => _settled = true;
}

/// <summary>
/// Exact process-local compensation evidence for one claimed autonomous
/// activation. The caller must hold the corresponding session TurnLock when
/// this token is settled or rolled back.
/// </summary>
internal sealed class GalateaAutonomyCadenceClaim(
    long previousDueFromTimestamp,
    long? previousLastAutonomousActivationTimestamp,
    long claimedAtTimestamp
) {
    private bool _settled;

    internal long PreviousDueFromTimestamp { get; } =
        previousDueFromTimestamp;
    internal long? PreviousLastAutonomousActivationTimestamp { get; } =
        previousLastAutonomousActivationTimestamp;
    internal long ClaimedAtTimestamp { get; } = claimedAtTimestamp;
    internal bool IsSettled => _settled;

    internal bool TrySettle() {
        if (_settled) { return false; }
        _settled = true;
        return true;
    }

    internal void SettleAfterValidation() => _settled = true;
}

/// <summary>
/// Process-local cadence for server-owned autonomous turns. Ten minutes is
/// code-owned; a new session rearms instead of catching up missed work.
///
/// The caller must hold the corresponding session TurnLock for every method.
/// This type performs no locking and owns no timer, background work, SQLite
/// state, SessionJournal state, configuration, or browser-provided time.
/// Monotonic TimeProvider timestamps are the sole cadence authority; wall time
/// is used only to project diagnostic timestamps.
/// </summary>
internal sealed class GalateaAutonomyCadence {
    internal static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(10);

    internal const string WaitingState = "waiting";
    internal const string PausedState = "autonomy-paused";
    internal const string PausedCode = "AUTONOMOUS_TURN_FAILED";

    private readonly TimeProvider _timeProvider;
    private long? _lastPulseTimestamp;
    private long? _nextDueFromTimestamp;
    private long? _lastAutonomousActivationTimestamp;
    private GalateaAutonomyCadenceClaim? _activeClaim;
    private bool _paused;

    internal GalateaAutonomyCadence(TimeProvider timeProvider) {
        _timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    internal bool IsArmed => _lastPulseTimestamp is not null;

    /// <summary>
    /// Observes one server pulse and, when exactly due, reports that one activation may
    /// be created. The caller must hold the corresponding session TurnLock.
    /// The due state remains unconsumed until the caller successfully creates
    /// a live turn and calls <see cref="TryClaimAutonomousActivationStarted"/>.
    /// </summary>
    internal GalateaAutonomyCadencePulseResult ObservePulse() {
        long now = _timeProvider.GetTimestamp();

        if (_paused) {
            _lastPulseTimestamp = now;
            return GalateaAutonomyCadencePulseResult.AutonomyPaused;
        }

        if (_lastPulseTimestamp is not { } previous || now < previous) {
            Rearm(now);
            return GalateaAutonomyCadencePulseResult.Rearmed;
        }

        _lastPulseTimestamp = now;
        if (_nextDueFromTimestamp is not { } dueFrom) {
            return GalateaAutonomyCadencePulseResult.Waiting;
        }
        if (now < dueFrom
            || _timeProvider.GetElapsedTime(dueFrom, now) < IdleInterval) {
            return GalateaAutonomyCadencePulseResult.Waiting;
        }

        return GalateaAutonomyCadencePulseResult
            .AutonomousActivationDue;
    }

    /// <summary>
    /// Claims a due activation after its live turn has been successfully
    /// constructed. The caller must hold the corresponding session TurnLock.
    /// Returning false leaves status unchanged and records no phantom last
    /// activation.
    /// </summary>
    internal bool TryClaimAutonomousActivationStarted(
        out GalateaAutonomyCadenceClaim? claim
    ) {
        claim = null;
        if (_paused
            || _activeClaim is not null
            || _nextDueFromTimestamp is not { } dueFrom) {
            return false;
        }
        long now = _timeProvider.GetTimestamp();
        if (now < dueFrom
            || _timeProvider.GetElapsedTime(dueFrom, now) < IdleInterval) {
            return false;
        }
        var created = new GalateaAutonomyCadenceClaim(
            dueFrom,
            _lastAutonomousActivationTimestamp,
            now
        );
        _activeClaim = created;
        _nextDueFromTimestamp = null;
        _lastAutonomousActivationTimestamp = now;
        claim = created;
        return true;
    }

    /// <summary>
    /// Exactly rolls back the current, still-unsettled claim after synchronous
    /// live-turn acceptance failed before writer ownership transferred. The
    /// caller must hold the corresponding session TurnLock. No other claim and
    /// no already-settled claim may be rolled back.
    /// </summary>
    internal bool TryRollbackAutonomousActivationClaim(
        GalateaAutonomyCadenceClaim claim,
        GalateaAutonomyCadenceTurnSettlement settlement
    ) {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(settlement);
        if (settlement.IsSettled || !IsCurrentUnsettledClaim(claim)) {
            return false;
        }
        claim.SettleAfterValidation();
        settlement.SettleAfterValidation();
        _nextDueFromTimestamp = claim.PreviousDueFromTimestamp;
        _lastAutonomousActivationTimestamp =
            claim.PreviousLastAutonomousActivationTimestamp;
        _activeClaim = null;
        return true;
    }

    /// <summary>
    /// Settles one terminal main turn. A completed turn clears an autonomy
    /// pause and resets the idle interval only after this state has previously
    /// been armed. A non-completed autonomous turn pauses
    /// further empty activations and clears the due time. The caller must hold
    /// the corresponding session TurnLock.
    /// </summary>
    internal bool SettleMainTurn(
        GalateaAutonomyCadenceTurnSettlement settlement,
        bool isAutonomousActivation,
        bool completed,
        GalateaAutonomyCadenceClaim? autonomousClaim = null
    ) {
        ArgumentNullException.ThrowIfNull(settlement);
        if (settlement.IsSettled
            || isAutonomousActivation
                && (autonomousClaim is null
                    || !IsCurrentUnsettledClaim(autonomousClaim))
            || !isAutonomousActivation && autonomousClaim is not null) {
            return false;
        }

        long? completedResetFrom = completed
            && _lastPulseTimestamp is not null
                ? _timeProvider.GetTimestamp()
                : null;
        settlement.SettleAfterValidation();
        if (autonomousClaim is not null) {
            autonomousClaim.SettleAfterValidation();
            _activeClaim = null;
        }

        if (completed) {
            if (completedResetFrom is { } resetFrom) {
                _paused = false;
                _nextDueFromTimestamp = resetFrom;
            }
            return true;
        }

        if (isAutonomousActivation) {
            _paused = true;
            _nextDueFromTimestamp = null;
        }
        return true;
    }

    /// <summary>
    /// Projects process-local diagnostic status. The caller must hold the
    /// corresponding session TurnLock. The projected wall-clock values are
    /// derived from one wall-clock sample plus monotonic remaining/elapsed
    /// durations and never participate in cadence decisions.
    /// </summary>
    internal GalateaAutonomyCadenceStatus ProjectStatus() {
        long timestampNow = _timeProvider.GetTimestamp();
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        DateTimeOffset? nextAt = _nextDueFromTimestamp is { } dueFrom
            ? utcNow + RemainingUntilDue(dueFrom, timestampNow)
            : null;
        DateTimeOffset? lastActivationAt =
            _lastAutonomousActivationTimestamp is { } activatedAt
                ? utcNow - ElapsedOrZero(activatedAt, timestampNow)
                : null;
        return new GalateaAutonomyCadenceStatus(
            _paused ? PausedState : WaitingState,
            nextAt?.ToUnixTimeMilliseconds(),
            lastActivationAt?.ToUnixTimeMilliseconds(),
            _paused ? PausedCode : null
        );
    }

    internal void Arm() {
        if (_lastPulseTimestamp is null) { Rearm(_timeProvider.GetTimestamp()); }
    }

    private void Rearm(long now) {
        _lastPulseTimestamp = now;
        _nextDueFromTimestamp = now;
    }

    private TimeSpan RemainingUntilDue(long dueFrom, long now) {
        TimeSpan elapsed = ElapsedOrZero(dueFrom, now);
        return elapsed >= IdleInterval
            ? TimeSpan.Zero
            : IdleInterval - elapsed;
    }

    private TimeSpan ElapsedOrZero(long start, long end) => end < start
        ? TimeSpan.Zero
        : _timeProvider.GetElapsedTime(start, end);

    private bool IsCurrentUnsettledClaim(
        GalateaAutonomyCadenceClaim claim
    ) => !_paused
        && ReferenceEquals(_activeClaim, claim)
        && !claim.IsSettled
        && _nextDueFromTimestamp is null
        && _lastAutonomousActivationTimestamp == claim.ClaimedAtTimestamp;
}
