using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid.Cadence;

public abstract record RecapGridCadenceTimelineSealOpenResult {
    private RecapGridCadenceTimelineSealOpenResult() { }

    public sealed record Opened(
        RecapGridCadenceTimelineSealOperation Operation)
        : RecapGridCadenceTimelineSealOpenResult;
    public sealed record Busy(string Component)
        : RecapGridCadenceTimelineSealOpenResult;
    public sealed record UnsupportedSchema(
        string Component,
        int SchemaVersion
    ) : RecapGridCadenceTimelineSealOpenResult;
    public sealed record Disposed(string Component)
        : RecapGridCadenceTimelineSealOpenResult;
    public sealed record Invalid(
        string Component,
        string Code,
        string Detail
    ) : RecapGridCadenceTimelineSealOpenResult;
}

public abstract record RecapGridCadenceOfflineAuditCaptureResult {
    private RecapGridCadenceOfflineAuditCaptureResult() { }

    public sealed record Available(RecapGridCadenceOfflineAudit Audit)
        : RecapGridCadenceOfflineAuditCaptureResult;
    public sealed record LimitExceeded(
        int MaximumEvents,
        long ObservedEvents)
        : RecapGridCadenceOfflineAuditCaptureResult;
    public sealed record Busy
        : RecapGridCadenceOfflineAuditCaptureResult;
    public sealed record RawHeadChanged(
        EventAddress Expected,
        EventAddress? Observed)
        : RecapGridCadenceOfflineAuditCaptureResult;
    public sealed record Disposed
        : RecapGridCadenceOfflineAuditCaptureResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridCadenceOfflineAuditCaptureResult;
}

public sealed class RecapGridCadenceOfflineAudit : IDisposable {
    private SessionSelectedLineageAuditSnapshot? _snapshot;
    private readonly SessionSelectedLineageDerivedAuditToken _token;

    internal RecapGridCadenceOfflineAudit(
        SessionSelectedLineageAuditSnapshot snapshot,
        SessionSelectedLineageDerivedAuditToken token
    ) {
        _snapshot = snapshot;
        _token = token;
    }

    internal SessionSelectedLineageAuditSnapshot RequireSnapshot(
        SessionSelectedLineageDerivedAuditToken token
    ) {
        if (!ReferenceEquals(token, _token) || !token.IsActive) {
            throw new InvalidOperationException(
                "The offline audit belongs to another or disposed Cadence seal operation.");
        }
        return Volatile.Read(ref _snapshot)
            ?? throw new ObjectDisposedException(nameof(RecapGridCadenceOfflineAudit));
    }

    public void Dispose() =>
        Interlocked.Exchange(ref _snapshot, null)?.Dispose();
}

public abstract record RecapGridCadenceOfflineBuilderOpenResult {
    private RecapGridCadenceOfflineBuilderOpenResult() { }

    public sealed record Opened(RecapGridCadenceOfflineBuilder Builder)
        : RecapGridCadenceOfflineBuilderOpenResult;
    public sealed record Busy
        : RecapGridCadenceOfflineBuilderOpenResult;
    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : RecapGridCadenceOfflineBuilderOpenResult;
    public sealed record RawHeadChanged(
        EventAddress Expected,
        EventAddress? Observed)
        : RecapGridCadenceOfflineBuilderOpenResult;
    public sealed record Invalid(string Code, string Detail)
        : RecapGridCadenceOfflineBuilderOpenResult;
}

public sealed class RecapGridCadenceOfflineBuilder : IDisposable {
    private readonly SessionJournalEngine _owner;
    private readonly SessionSelectedLineageDerivedAuditToken _token;
    private readonly HistoryTimelineOfflineBuilder _builder;
    private readonly bool _derivedSidecarAudit;
    private SessionSelectedLineageForwardCursor? _cursor;

    internal RecapGridCadenceOfflineBuilder(
        SessionJournalEngine owner,
        SessionSelectedLineageDerivedAuditToken token,
        HistoryTimelineOfflineBuilder builder,
        SessionSelectedLineageForwardCursor cursor,
        bool derivedSidecarAudit
    ) {
        _owner = owner;
        _token = token;
        _builder = builder;
        _cursor = cursor;
        _derivedSidecarAudit = derivedSidecarAudit;
    }

    public HistoryTimelineOfflineStepResult BuildNextRow(
        TimelineHeadRef expectedWholeHead,
        CancellationToken cancellationToken = default
    ) {
        if (!_token.IsActive || Volatile.Read(ref _cursor) is null) {
            return new HistoryTimelineOfflineStepResult.Invalid(
                "CadenceSealOperationDisposed",
                "The Cadence offline builder is disposed or its seal operation ended.");
        }
        try {
            return _derivedSidecarAudit
                ? _owner.ExecuteDerivedSelectedLineageAudit(
                    _token,
                    "RecapGridCadence.BuildOfflineTimelineRow",
                    () => _builder.BuildNextRow(
                        expectedWholeHead,
                        cancellationToken))
                : _builder.BuildNextRow(
                    expectedWholeHead,
                    cancellationToken);
        }
        catch (SessionJournalConcurrentMutationException) {
            return new HistoryTimelineOfflineStepResult.BackendBusy();
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return new HistoryTimelineOfflineStepResult.Invalid(
                "CadenceOfflineAuditInvalid",
                exception.Message);
        }
    }

    public HistoryTimelineOfflineStepResult ProbeNextRow(
        TimelineHeadRef expectedWholeHead,
        CancellationToken cancellationToken = default
    ) {
        if (!_token.IsActive || Volatile.Read(ref _cursor) is null) {
            return new HistoryTimelineOfflineStepResult.Invalid(
                "CadenceSealOperationDisposed",
                "The Cadence offline builder is disposed or its seal operation ended.");
        }
        try {
            return _derivedSidecarAudit
                ? _owner.ExecuteDerivedSelectedLineageAudit(
                    _token,
                    "RecapGridCadence.ProbeOfflineTimelineRow",
                    () => _builder.ProbeNextRow(
                        expectedWholeHead,
                        cancellationToken))
                : _builder.ProbeNextRow(
                    expectedWholeHead,
                    cancellationToken);
        }
        catch (SessionJournalConcurrentMutationException) {
            return new HistoryTimelineOfflineStepResult.BackendBusy();
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return new HistoryTimelineOfflineStepResult.Invalid(
                "CadenceOfflineAuditInvalid",
                exception.Message);
        }
    }

    public void Dispose() =>
        Interlocked.Exchange(ref _cursor, null)?.Dispose();
}

public sealed class RecapGridCadenceTimelineSealOperation : IDisposable {
    private CadenceLifetime.Operation? _cadenceLease;
    private readonly HistoryRecentReserveAuthorityToken _authorityToken;
    private readonly SessionSelectedLineageDerivedAuditToken _auditToken;
    private readonly SessionJournalEngine _mutableOwner;
    private readonly HistoryTimelineCoordinator _coordinator;
    private readonly HistoryTimelineReader _reader;
    private readonly HistoryRecentReservePolicy _reservePolicy;

    internal RecapGridCadenceTimelineSealOperation(
        CadenceLifetime.Operation cadenceLease,
        HistoryRecentReserveAuthorityToken authorityToken,
        SessionSelectedLineageDerivedAuditToken auditToken,
        SessionJournalEngine mutableOwner,
        HistoryTimelineCoordinator coordinator,
        HistoryTimelineReader reader,
        HistoryRecentReservePolicy reservePolicy,
        TimelineHeadRef headAtOpen
    ) {
        _cadenceLease = cadenceLease;
        _authorityToken = authorityToken;
        _auditToken = auditToken;
        _mutableOwner = mutableOwner;
        _coordinator = coordinator;
        _reader = reader;
        _reservePolicy = reservePolicy;
        HeadAtOpen = headAtOpen;
    }

    public TimelineHeadRef HeadAtOpen { get; }

    public HistoryTimelinePlanResult PlanNextRow(
        TimelineHeadRef expectedWholeHead,
        OnlineSelectedRawCapture capture,
        CancellationToken cancellationToken = default
    ) {
        if (!_authorityToken.IsActive) {
            return new HistoryTimelinePlanResult.Invalid(
                "CadenceSealOperationDisposed",
                "The Cadence Timeline seal operation is disposed.");
        }
        return _coordinator.PlanNextRow(
            expectedWholeHead,
            capture,
            _reservePolicy,
            cancellationToken);
    }

    public HistoryTimelineCommitResult CommitRow(
        HistoryRowCommitCandidate candidate
    ) {
        if (!_authorityToken.IsActive) {
            return new HistoryTimelineCommitResult.Invalid(
                "CadenceSealOperationDisposed",
                "The Cadence Timeline seal operation is disposed.");
        }
        if (!candidate.ReserveProof.IsBoundToAuthority(_authorityToken)) {
            return new HistoryTimelineCommitResult.Invalid(
                "CadenceSealAuthorityMismatch",
                "The commit candidate was issued by another Cadence seal operation.");
        }
        return _coordinator.CommitRow(candidate);
    }

    public RecapGridCadenceOfflineAuditCaptureResult CaptureOfflineAudit(
        int maximumEvents,
        CancellationToken cancellationToken = default
    ) {
        if (!_authorityToken.IsActive || !_auditToken.IsActive) {
            return new RecapGridCadenceOfflineAuditCaptureResult.Disposed();
        }
        try {
            return _mutableOwner
                .CaptureSelectedLineageAuditSnapshotForDerivedSidecar(
                    _auditToken,
                    maximumEvents,
                    cancellationToken) switch {
                SessionSelectedLineageAuditSnapshotCaptureResult.Available available
                    => new RecapGridCadenceOfflineAuditCaptureResult.Available(
                        new RecapGridCadenceOfflineAudit(
                            available.Snapshot,
                            _auditToken)),
                SessionSelectedLineageAuditSnapshotCaptureResult.LimitExceeded limit
                    => new RecapGridCadenceOfflineAuditCaptureResult.LimitExceeded(
                        limit.MaximumEvents,
                        limit.ObservedEvents),
                SessionSelectedLineageAuditSnapshotCaptureResult.Busy
                    => new RecapGridCadenceOfflineAuditCaptureResult.Busy(),
                SessionSelectedLineageAuditSnapshotCaptureResult.RawHeadChanged changed
                    => new RecapGridCadenceOfflineAuditCaptureResult.RawHeadChanged(
                        changed.Expected,
                        changed.Observed),
                _ => new RecapGridCadenceOfflineAuditCaptureResult.Invalid(
                    "CadenceOfflineAuditOutcomeInvalid",
                    "SessionJournal returned an unknown offline audit outcome.")
            };
        }
        catch (SessionJournalConcurrentMutationException) {
            return new RecapGridCadenceOfflineAuditCaptureResult.Busy();
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return new RecapGridCadenceOfflineAuditCaptureResult.Invalid(
                "CadenceOfflineAuditInvalid",
                exception.Message);
        }
    }

    public HistoryTimelineReconcileResult ReconcileSelectedPathOffline(
        TimelineHeadRef expectedWholeHead,
        RecapGridCadenceOfflineAudit audit,
        CancellationToken cancellationToken = default
    ) {
        if (!_authorityToken.IsActive || !_auditToken.IsActive) {
            return new HistoryTimelineReconcileResult.Invalid(
                "CadenceSealOperationDisposed",
                "The Cadence Timeline seal operation is disposed.");
        }
        try {
            SessionSelectedLineageAuditSnapshot snapshot =
                audit.RequireSnapshot(_auditToken);
            return _mutableOwner.ExecuteDerivedSelectedLineageAudit<
                HistoryTimelineReconcileResult>(
                _auditToken,
                "RecapGridCadence.ReconcileTimelineOffline",
                () => {
                    using SessionSelectedLineageForwardCursor cursor =
                        snapshot.OpenForwardCursor(cancellationToken);
                    return _coordinator.ReconcileSelectedPathOffline(
                        expectedWholeHead,
                        cursor,
                        cancellationToken);
                });
        }
        catch (SessionJournalConcurrentMutationException) {
            return new HistoryTimelineReconcileResult.BackendBusy();
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return new HistoryTimelineReconcileResult.Invalid(
                "CadenceOfflineAuditInvalid",
                exception.Message);
        }
    }

    public RecapGridCadenceOfflineBuilderOpenResult OpenOfflineBuilder(
        TimelineHeadRef expectedWholeHead,
        RecapGridCadenceOfflineAudit audit,
        CancellationToken cancellationToken = default
    ) {
        if (!_authorityToken.IsActive) {
            return new RecapGridCadenceOfflineBuilderOpenResult.Invalid(
                "CadenceSealOperationDisposed",
                "The Cadence Timeline seal operation is disposed.");
        }
        SessionSelectedLineageForwardCursor? cursor = null;
        try {
            SessionSelectedLineageAuditSnapshot snapshot =
                audit.RequireSnapshot(_auditToken);
            return _mutableOwner.ExecuteDerivedSelectedLineageAudit<
                RecapGridCadenceOfflineBuilderOpenResult>(
                _auditToken,
                "RecapGridCadence.OpenOfflineTimelineBuilder",
                () => {
                    cursor = snapshot.OpenForwardCursor(cancellationToken);
                    if (expectedWholeHead.HeadRowId is { } rowId) {
                        HistoryTimelineReaderRowResult selected = _reader
                            .ReadSelectedRow(expectedWholeHead, rowId);
                        if (selected is not HistoryTimelineReaderRowResult
                                .Selected row) {
                            cursor.Dispose();
                            cursor = null;
                            return selected switch {
                                HistoryTimelineReaderRowResult.Busy
                                    => new RecapGridCadenceOfflineBuilderOpenResult
                                        .Busy(),
                                HistoryTimelineReaderRowResult.StaleTimelineHead stale
                                    => new RecapGridCadenceOfflineBuilderOpenResult
                                        .StaleTimelineHead(stale.Actual),
                                HistoryTimelineReaderRowResult.Invalid invalid
                                    => new RecapGridCadenceOfflineBuilderOpenResult
                                        .Invalid(invalid.Code, invalid.Detail),
                                _ => new RecapGridCadenceOfflineBuilderOpenResult
                                    .Invalid(
                                        "OfflineHeadUnavailable",
                                        "The selected Timeline head row is unavailable.")
                            };
                        }
                        cursor.SeekToBoundary(
                            row.Row.Descriptor.EndInclusive,
                            row.Row.Descriptor.EndSetups,
                            cancellationToken);
                    }
                    HistoryTimelineOfflineBuilderOpenResult opened =
                        _coordinator.OpenOfflineBuilder(
                            expectedWholeHead,
                            cursor,
                            _reservePolicy);
                    if (opened is not HistoryTimelineOfflineBuilderOpenResult
                            .Opened inner) {
                        cursor.Dispose();
                        cursor = null;
                        return opened switch {
                            HistoryTimelineOfflineBuilderOpenResult.BackendBusy
                                => new RecapGridCadenceOfflineBuilderOpenResult
                                    .Busy(),
                            HistoryTimelineOfflineBuilderOpenResult
                                .StaleTimelineHead stale
                                => new RecapGridCadenceOfflineBuilderOpenResult
                                    .StaleTimelineHead(stale.Actual),
                            HistoryTimelineOfflineBuilderOpenResult
                                .RawHeadChanged changed
                                => new RecapGridCadenceOfflineBuilderOpenResult
                                    .RawHeadChanged(
                                        changed.Expected,
                                        changed.Observed),
                            HistoryTimelineOfflineBuilderOpenResult.Invalid invalid
                                => new RecapGridCadenceOfflineBuilderOpenResult
                                    .Invalid(invalid.Code, invalid.Detail),
                            _ => new RecapGridCadenceOfflineBuilderOpenResult
                                .Invalid(
                                    "OfflineBuilderOpenOutcomeInvalid",
                                    "Timeline returned an unknown offline builder outcome.")
                        };
                    }
                    var wrapper = new RecapGridCadenceOfflineBuilder(
                        _mutableOwner,
                        _auditToken,
                        inner.Builder,
                        cursor,
                        derivedSidecarAudit: true);
                    cursor = null;
                    return new RecapGridCadenceOfflineBuilderOpenResult.Opened(
                        wrapper);
                });
        }
        catch (SessionJournalConcurrentMutationException) {
            cursor?.Dispose();
            return new RecapGridCadenceOfflineBuilderOpenResult.Busy();
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            cursor?.Dispose();
            return new RecapGridCadenceOfflineBuilderOpenResult.Invalid(
                "CadenceOfflineAuditInvalid",
                exception.Message);
        }
    }

    public HistoryTimelineReconcileResult ReconcileSelectedPathOffline(
        TimelineHeadRef expectedWholeHead,
        SessionSelectedLineageAuditSnapshot snapshot,
        CancellationToken cancellationToken = default
    ) {
        if (!_authorityToken.IsActive || !_auditToken.IsActive) {
            return new HistoryTimelineReconcileResult.Invalid(
                "CadenceSealOperationDisposed",
                "The Cadence Timeline seal operation is disposed.");
        }
        try {
            using SessionSelectedLineageForwardCursor cursor =
                snapshot.OpenForwardCursor(cancellationToken);
            return _coordinator.ReconcileSelectedPathOffline(
                expectedWholeHead,
                cursor,
                cancellationToken);
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return new HistoryTimelineReconcileResult.Invalid(
                "CadenceOfflineAuditInvalid",
                exception.Message);
        }
    }

    public RecapGridCadenceOfflineBuilderOpenResult OpenOfflineBuilder(
        TimelineHeadRef expectedWholeHead,
        SessionSelectedLineageAuditSnapshot snapshot,
        CancellationToken cancellationToken = default
    ) => OpenOfflineBuilderFromSnapshot(
        expectedWholeHead,
        snapshot,
        derivedSidecarAudit: false,
        cancellationToken);

    private RecapGridCadenceOfflineBuilderOpenResult
        OpenOfflineBuilderFromSnapshot(
        TimelineHeadRef expectedWholeHead,
        SessionSelectedLineageAuditSnapshot snapshot,
        bool derivedSidecarAudit,
        CancellationToken cancellationToken
    ) {
        if (!_authorityToken.IsActive || !_auditToken.IsActive) {
            return new RecapGridCadenceOfflineBuilderOpenResult.Invalid(
                "CadenceSealOperationDisposed",
                "The Cadence Timeline seal operation is disposed.");
        }
        SessionSelectedLineageForwardCursor? cursor = null;
        try {
            cursor = snapshot.OpenForwardCursor(cancellationToken);
            if (expectedWholeHead.HeadRowId is { } rowId) {
                HistoryTimelineReaderRowResult selected = _reader
                    .ReadSelectedRow(expectedWholeHead, rowId);
                if (selected is not HistoryTimelineReaderRowResult
                        .Selected row) {
                    cursor.Dispose();
                    cursor = null;
                    return MapSelectedRowForOfflineOpen(selected);
                }
                cursor.SeekToBoundary(
                    row.Row.Descriptor.EndInclusive,
                    row.Row.Descriptor.EndSetups,
                    cancellationToken);
            }
            HistoryTimelineOfflineBuilderOpenResult opened =
                _coordinator.OpenOfflineBuilder(
                    expectedWholeHead,
                    cursor,
                    _reservePolicy);
            if (opened is not HistoryTimelineOfflineBuilderOpenResult
                    .Opened inner) {
                cursor.Dispose();
                cursor = null;
                return MapTimelineOfflineOpen(opened);
            }
            var wrapper = new RecapGridCadenceOfflineBuilder(
                _mutableOwner,
                _auditToken,
                inner.Builder,
                cursor,
                derivedSidecarAudit);
            cursor = null;
            return new RecapGridCadenceOfflineBuilderOpenResult.Opened(
                wrapper);
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            cursor?.Dispose();
            return new RecapGridCadenceOfflineBuilderOpenResult.Invalid(
                "CadenceOfflineAuditInvalid",
                exception.Message);
        }
    }

    private static RecapGridCadenceOfflineBuilderOpenResult
        MapSelectedRowForOfflineOpen(
        HistoryTimelineReaderRowResult selected
    ) => selected switch {
        HistoryTimelineReaderRowResult.Busy
            => new RecapGridCadenceOfflineBuilderOpenResult.Busy(),
        HistoryTimelineReaderRowResult.StaleTimelineHead stale
            => new RecapGridCadenceOfflineBuilderOpenResult
                .StaleTimelineHead(stale.Actual),
        HistoryTimelineReaderRowResult.Invalid invalid
            => new RecapGridCadenceOfflineBuilderOpenResult
                .Invalid(invalid.Code, invalid.Detail),
        _ => new RecapGridCadenceOfflineBuilderOpenResult.Invalid(
            "OfflineHeadUnavailable",
            "The selected Timeline head row is unavailable.")
    };

    private static RecapGridCadenceOfflineBuilderOpenResult
        MapTimelineOfflineOpen(
        HistoryTimelineOfflineBuilderOpenResult opened
    ) => opened switch {
        HistoryTimelineOfflineBuilderOpenResult.BackendBusy
            => new RecapGridCadenceOfflineBuilderOpenResult.Busy(),
        HistoryTimelineOfflineBuilderOpenResult.StaleTimelineHead stale
            => new RecapGridCadenceOfflineBuilderOpenResult
                .StaleTimelineHead(stale.Actual),
        HistoryTimelineOfflineBuilderOpenResult.RawHeadChanged changed
            => new RecapGridCadenceOfflineBuilderOpenResult.RawHeadChanged(
                changed.Expected,
                changed.Observed),
        HistoryTimelineOfflineBuilderOpenResult.Invalid invalid
            => new RecapGridCadenceOfflineBuilderOpenResult
                .Invalid(invalid.Code, invalid.Detail),
        _ => new RecapGridCadenceOfflineBuilderOpenResult.Invalid(
            "OfflineBuilderOpenOutcomeInvalid",
            "Timeline returned an unknown offline builder outcome.")
    };

    public void Dispose() {
        _authorityToken.Deactivate();
        _auditToken.Deactivate();
        Interlocked.Exchange(ref _cadenceLease, null)?.Dispose();
    }
}

internal static class RecapGridCadenceTimelineSeal {
    internal static RecapGridCadenceTimelineSealOpenResult Open(
        RecapGridCadenceHandle cadence,
        HistoryTimelineHandle timeline
    ) {
        ArgumentNullException.ThrowIfNull(cadence);
        ArgumentNullException.ThrowIfNull(timeline);
        CadenceLifetime.Operation? cadenceLease =
            cadence.Lifetime.TryEnter();
        if (cadenceLease is null) {
            return new RecapGridCadenceTimelineSealOpenResult.Disposed(
                "Cadence");
        }
        try {
            RecapGridCadenceFactory.RequireMutableOwner(
                cadence.MutableOwner,
                cadence.Paths);
            if (!string.Equals(
                    cadence.Paths.RepositoryPath,
                    timeline.Coordinator.RepositoryPath,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal)) {
                return new RecapGridCadenceTimelineSealOpenResult.Invalid(
                    "Cadence",
                    "CadenceTimelineRepositoryMismatch",
                    "Cadence and Timeline belong to different canonical repositories.");
            }
            RecapGridCadenceReadResult cadenceRead =
                cadence.Reader.ReadSnapshot();
            RecapGridCadenceSnapshot cadenceSnapshot;
            switch (cadenceRead) {
                case RecapGridCadenceReadResult.Available available:
                    cadenceSnapshot = available.Snapshot;
                    break;
                case RecapGridCadenceReadResult.Busy:
                    return new RecapGridCadenceTimelineSealOpenResult.Busy(
                        "Cadence");
                case RecapGridCadenceReadResult.UnsupportedSchema schema:
                    return new RecapGridCadenceTimelineSealOpenResult
                        .UnsupportedSchema("Cadence", schema.Version);
                case RecapGridCadenceReadResult.Disposed:
                    return new RecapGridCadenceTimelineSealOpenResult
                        .Disposed("Cadence");
                case RecapGridCadenceReadResult.Invalid invalid:
                    return new RecapGridCadenceTimelineSealOpenResult.Invalid(
                        "Cadence", invalid.Code, invalid.Detail);
                default:
                    return new RecapGridCadenceTimelineSealOpenResult.Invalid(
                        "Cadence",
                        "CadenceReadOutcomeInvalid",
                        "Cadence returned an unknown read outcome.");
            }

            HistoryTimelineSnapshotResult timelineRead =
                timeline.Reader.ReadSnapshot();
            TimelineHeadRef timelineHead;
            switch (timelineRead) {
                case HistoryTimelineSnapshotResult.Available available:
                    timelineHead = available.Head;
                    break;
                case HistoryTimelineSnapshotResult.Busy:
                    return new RecapGridCadenceTimelineSealOpenResult.Busy(
                        "Timeline");
                case HistoryTimelineSnapshotResult.UnsupportedSchema schema:
                    return new RecapGridCadenceTimelineSealOpenResult
                        .UnsupportedSchema("Timeline", schema.SchemaVersion);
                case HistoryTimelineSnapshotResult.Invalid invalid:
                    return new RecapGridCadenceTimelineSealOpenResult.Invalid(
                        "Timeline", invalid.Code, invalid.Detail);
                default:
                    return new RecapGridCadenceTimelineSealOpenResult.Invalid(
                        "Timeline",
                        "TimelineHeadUnavailable",
                        "The Timeline head is unavailable.");
            }
            if (cadenceSnapshot.Head.RefId != timelineHead.RefId) {
                return new RecapGridCadenceTimelineSealOpenResult.Invalid(
                    "Cadence",
                    "CadenceTimelineRefMismatch",
                    "Cadence and Timeline belong to different Refs.");
            }
            RecapGridCadencePolicySpec cadencePolicy =
                cadenceSnapshot.Policy;
            PartitionPolicyRevision expectedPolicy =
                PartitionPolicyRevision.Create(
                    timelineHead.TimelineId,
                    cadencePolicy.PartitionAlgorithmId,
                    cadencePolicy.HistoryLoadEstimatorId,
                    new HistoryLoadUnit(cadencePolicy.TargetHistoryLoad),
                    cadencePolicy.MaxRawEvents,
                    cadencePolicy.MaxRenderedBytes);
            if (!string.Equals(
                    expectedPolicy.PolicyDigest,
                    timelineHead.ActivePartitionPolicyDigest,
                    StringComparison.Ordinal)) {
                return new RecapGridCadenceTimelineSealOpenResult.Invalid(
                    "Cadence",
                    "CadenceTimelinePolicyMismatch",
                    "The frozen Cadence policy differs from the active Timeline partition policy.");
            }
            var token = new HistoryRecentReserveAuthorityToken();
            var auditToken = new SessionSelectedLineageDerivedAuditToken(
                cadence.MutableOwner);
            var reserve = new HistoryRecentReservePolicy(
                cadence.Paths.RepositoryPath,
                cadenceSnapshot.Head.RefId,
                cadenceSnapshot.Head.Generation,
                cadenceSnapshot.Head.DomainDigest.Value,
                expectedPolicy,
                new HistoryLoadUnit(
                    cadencePolicy.MinimumRecentHistoryLoad),
                token);
            var operation = new RecapGridCadenceTimelineSealOperation(
                cadenceLease,
                token,
                auditToken,
                cadence.MutableOwner,
                timeline.Coordinator,
                timeline.Reader,
                reserve,
                timelineHead);
            cadenceLease = null;
            return new RecapGridCadenceTimelineSealOpenResult.Opened(
                operation);
        }
        catch (CadenceBusyException) {
            return new RecapGridCadenceTimelineSealOpenResult.Busy(
                "Cadence");
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return new RecapGridCadenceTimelineSealOpenResult.Invalid(
                "Cadence",
                exception is CadenceStoreException store
                    ? store.Code
                    : "CadenceTimelineSealOpenInvalid",
                exception.Message);
        }
        finally {
            cadenceLease?.Dispose();
        }
    }
}
