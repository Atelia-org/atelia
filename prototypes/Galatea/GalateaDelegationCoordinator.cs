using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Atelia.Diagnostics;
using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

internal enum GalateaDelegateCandidateState {
    Unrouted,
    Queued,
    Starting,
    Running,
    ReplyReady,
    FailureReady,
    Leased,
    Consumed,
    RetractedBeforeDispatch
}

internal sealed record GalateaDelegateCandidateSnapshot(
    string DispatchId,
    EventAddress SourceActionHead,
    int ArtifactOrdinal,
    string Recipient,
    string TurnId,
    string? TaskBody,
    GalateaDelegateCandidateState State,
    bool SourceRetracted,
    long? CompletionSequence,
    string? ThreadId
);

/// <summary>
/// Owns the process-local lifecycle of outbound delegate exchanges for one
/// Galatea session. The ledger and ready inbox deliberately share one gate so
/// capture, dispatch, Undo and reply cutoffs have a single authority.
/// </summary>
internal sealed class GalateaDelegationCoordinator : IAsyncDisposable {
    internal const int MaximumCandidateCount = 4_096;
    internal const int MaximumCandidateUtf8Bytes = 64 * 1024 * 1024;
    internal const int MaximumActionHeadTombstones = 4_096;

    private const string LogCategory = "Galatea.Delegation";
    private const string DispatchPrefix = "gd1-";
    private const int MaximumWireIdentityUtf8Bytes = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private sealed class Candidate {
        internal required string DispatchId { get; init; }
        internal required EventAddress SourceActionHead { get; init; }
        internal required int ArtifactOrdinal { get; init; }
        internal required string Recipient { get; init; }
        internal required string TurnId { get; init; }
        internal required int CapturedUtf8Bytes { get; init; }
        internal required GalateaDelegateCandidateState State { get; set; }
        internal SendMailIntent? Intent { get; set; }
        internal string? PreflightFailureCode { get; init; }
        internal bool SourceRetracted { get; set; }
        internal string? ThreadId { get; set; }
        internal string? TurnIdAtDelegate { get; set; }
        internal long? CompletionSequence { get; set; }
        internal GalateaReadyNotice? ReadyNotice { get; set; }
    }

    private readonly object _gate = new();
    private readonly string _userId;
    private readonly GalateaDelegateRouteConfig _route;
    private readonly IGalateaDelegateSidecar _sidecar;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Candidate> _candidates = [];
    private readonly Dictionary<EventAddress, List<Candidate>>
        _candidatesByActionHead = [];
    private readonly HashSet<EventAddress> _seenActionHeads = [];
    private TaskCompletionSource<bool> _inboxCapacityChanged = NewSignal();
    private Task? _pumpTask;
    private Task? _disposeTask;
    private Candidate? _active;
    private string? _threadId;
    private string? _quarantineCode;
    private long _completionSequence;
    private long _nextLeaseId;
    private long? _activeLeaseId;
    private int _candidateUtf8Bytes;
    private int _inboxCount;
    private int _inboxUtf8Bytes;
    private bool _accepting = true;
    private bool _disposed;

    internal GalateaDelegationCoordinator(
        string userId,
        GalateaDelegateRouteConfig route,
        IGalateaDelegateSidecar sidecar
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(sidecar);
        if (!string.Equals(
                route.Recipient,
                GalateaDelegateConfigReader.CanonicalRecipient,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                "The delegation coordinator requires the exact Codex route.",
                nameof(route)
            );
        }
        if (route.MaximumQueuedMails <= 0
            || route.MaximumTaskUtf8Bytes <= 0
            || route.MaximumReplyUtf8Bytes <= 0
            || route.MaximumInboxReplies <= 0
            || route.MaximumInboxUtf8Bytes
                < Math.Max(
                    route.MaximumReplyUtf8Bytes,
                    GalateaPlayerObservationEnvelope
                        .MaximumFailureUtf8Bytes
                )) {
            throw new ArgumentException(
                "The delegation route has invalid capacity bounds.",
                nameof(route)
            );
        }
        _userId = userId;
        _route = route;
        _sidecar = sidecar;
    }

    /// <summary>
    /// Atomically captures one extractor result. A false result means the
    /// durable Action head was already captured; no candidate was changed.
    /// </summary>
    internal bool TryCaptureBatch(
        string turnId,
        EventAddress sourceActionHead,
        IReadOnlyList<SendMailIntent> intents
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        ArgumentNullException.ThrowIfNull(intents);
        if (intents.Count == 0) { return true; }

        Candidate[] prepared = intents.Select((intent, ordinal) =>
            PrepareCandidate(
                turnId,
                sourceActionHead,
                ordinal,
                intent ?? throw new ArgumentException(
                    "Intent batches must not contain null items.",
                    nameof(intents)
                )
            )
        ).ToArray();
        int preparedBytes = prepared.Sum(static value =>
            value.CapturedUtf8Bytes);

        lock (_gate) {
            ObjectDisposedException.ThrowIf(!_accepting, this);
            if (_seenActionHeads.Contains(sourceActionHead)) { return false; }
            if (_seenActionHeads.Count >= MaximumActionHeadTombstones
                || _candidates.Count > MaximumCandidateCount
                    - prepared.Length
                || _candidateUtf8Bytes > MaximumCandidateUtf8Bytes
                    - preparedBytes) {
                throw new InvalidOperationException(
                    "The in-memory delegation ledger is full."
                );
            }
            int routed = prepared.Count(static value =>
                value.State == GalateaDelegateCandidateState.Queued);
            int admitted = _candidates.Count(static value =>
                value.State is GalateaDelegateCandidateState.Queued
                    or GalateaDelegateCandidateState.Starting
                    or GalateaDelegateCandidateState.Running);
            if (admitted > _route.MaximumQueuedMails - routed) {
                throw new InvalidOperationException(
                    "The Codex delegation queue is full."
                );
            }

            _seenActionHeads.Add(sourceActionHead);
            _candidates.AddRange(prepared);
            _candidatesByActionHead.Add(
                sourceActionHead,
                [.. prepared]
            );
            _candidateUtf8Bytes += preparedBytes;
            StartPumpLocked();
        }

        DebugUtil.Info(
            LogCategory,
            $"Captured outbound batch: user={Safe(_userId)}, actionHead={sourceActionHead}, candidates={prepared.Length}, routed={prepared.Count(static value => value.State == GalateaDelegateCandidateState.Queued)}"
        );
        return true;
    }

    /// <summary>
    /// Applies Undo only after SessionJournal confirms that the exact durable
    /// head moved. The pump selects Queued work under this same gate, so a
    /// queued retraction cannot race into StartAsync.
    /// </summary>
    internal void RetractSourceAction(EventAddress sourceActionHead) {
        lock (_gate) {
            if (!_candidatesByActionHead.TryGetValue(
                    sourceActionHead,
                    out List<Candidate>? candidates)) {
                return;
            }
            foreach (Candidate candidate in candidates) {
                switch (candidate.State) {
                    case GalateaDelegateCandidateState.Unrouted:
                    case GalateaDelegateCandidateState.Queued:
                        candidate.State = GalateaDelegateCandidateState
                            .RetractedBeforeDispatch;
                        ReleaseCapturedPayloadLocked(candidate);
                        break;
                    case GalateaDelegateCandidateState.Starting:
                    case GalateaDelegateCandidateState.Running:
                        candidate.SourceRetracted = true;
                        break;
                }
            }
        }
        DebugUtil.Info(
            LogCategory,
            $"Applied durable Action retraction: user={Safe(_userId)}, actionHead={sourceActionHead}"
        );
    }

    /// <summary>
    /// Freezes a completion-sequence frontier and leases the ready notices at
    /// or before it. The frontier is capped to one Observation's code-owned
    /// notice limit, leaving later ready items for the next cutoff.
    /// </summary>
    internal GalateaReadyReplyLease BeginReadyReplyCutoff() =>
        BeginReadyReplyCutoff("x");

    /// <summary>
    /// Selects the earliest prefix that is exactly renderable with the
    /// admitted player text. This keeps later completions Ready instead of
    /// leasing a set the composite Observation cannot persist.
    /// </summary>
    internal GalateaReadyReplyLease BeginReadyReplyCutoff(
        string playerText
    ) {
        _ = new GalateaPlayerObservation(playerText);
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeLeaseId is not null) {
                throw new InvalidOperationException(
                    "Only one ready-reply lease may be active."
                );
            }
            Candidate[] available = _candidates
                .Where(static candidate => candidate.State is
                    GalateaDelegateCandidateState.ReplyReady
                        or GalateaDelegateCandidateState.FailureReady)
                .OrderBy(static candidate => candidate.CompletionSequence)
                .ToArray();
            var selected = new List<Candidate>(Math.Min(
                available.Length,
                GalateaPlayerObservationEnvelope.MaximumNoticeCount
            ));
            foreach (Candidate candidate in available) {
                if (selected.Count
                    == GalateaPlayerObservationEnvelope.MaximumNoticeCount) {
                    break;
                }
                GalateaReadyNotice notice = candidate.ReadyNotice
                    ?? throw new InvalidDataException(
                        "A ready candidate has no notice."
                    );
                GalateaReadyNotice[] proposed = [
                    .. selected.Select(static value => value.ReadyNotice!),
                    notice
                ];
                if (!GalateaPlayerObservationEnvelope
                        .FitsEveryValidPlayerText(proposed)) {
                    break;
                }
                selected.Add(candidate);
            }
            Candidate[] ready = [.. selected];
            long frontier = ready.Length > 0
                ? ready[^1].CompletionSequence!.Value
                : available.Length == 0
                    ? _completionSequence
                    : checked(available[0].CompletionSequence!.Value - 1);
            long leaseId = checked(++_nextLeaseId);
            _activeLeaseId = leaseId;
            foreach (Candidate candidate in ready) {
                candidate.State = GalateaDelegateCandidateState.Leased;
            }
            return new GalateaReadyReplyLease(
                this,
                leaseId,
                frontier,
                Array.AsReadOnly(ready.Select(static candidate =>
                    candidate.ReadyNotice
                        ?? throw new InvalidDataException(
                            "A ready candidate has no notice."
                        )
                ).ToArray())
            );
        }
    }

    internal IReadOnlyList<GalateaDelegateCandidateSnapshot> Snapshot() {
        lock (_gate) {
            return Array.AsReadOnly(_candidates.Select(static candidate =>
                new GalateaDelegateCandidateSnapshot(
                    candidate.DispatchId,
                    candidate.SourceActionHead,
                    candidate.ArtifactOrdinal,
                    candidate.Recipient,
                    candidate.TurnId,
                    candidate.Intent?.Body,
                    candidate.State,
                    candidate.SourceRetracted,
                    candidate.CompletionSequence,
                    candidate.ThreadId
                )
            ).ToArray());
        }
    }

    internal string? BoundThreadIdForTest {
        get { lock (_gate) { return _threadId; } }
    }

    internal bool IsQuarantinedForTest {
        get { lock (_gate) { return _quarantineCode is not null; } }
    }

    internal Task PumpTaskForTest {
        get { lock (_gate) { return _pumpTask ?? Task.CompletedTask; } }
    }

    internal static string CreateDispatchId(
        string userId,
        EventAddress sourceActionHead,
        int artifactOrdinal
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentOutOfRangeException.ThrowIfNegative(artifactOrdinal);
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        AppendLengthPrefixed(hash, userId);
        AppendLengthPrefixed(
            hash,
            GalateaDelegateConfigReader.CanonicalRecipient
        );
        AppendLengthPrefixed(
            hash,
            EventAddressTextCodec.Format(sourceActionHead)
        );
        AppendLengthPrefixed(
            hash,
            artifactOrdinal.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            )
        );
        return DispatchPrefix
            + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public ValueTask DisposeAsync() {
        lock (_gate) {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync() {
        Task pump;
        lock (_gate) {
            _accepting = false;
            _disposed = true;
            if (_activeLeaseId is { } leaseId) {
                RollbackLeaseLocked(leaseId);
            }
            foreach (Candidate candidate in _candidates) {
                if (candidate.State is GalateaDelegateCandidateState.Unrouted
                        or GalateaDelegateCandidateState.Queued) {
                    candidate.State = GalateaDelegateCandidateState
                        .RetractedBeforeDispatch;
                    ReleaseCapturedPayloadLocked(candidate);
                }
            }
            _lifetime.Cancel();
            SignalInboxCapacityChangedLocked();
            pump = _pumpTask ?? Task.CompletedTask;
        }
        try {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException) {
            // The coordinator owns this cancellation.
        }
        finally {
            _lifetime.Dispose();
        }
    }

    private Candidate PrepareCandidate(
        string turnId,
        EventAddress sourceActionHead,
        int ordinal,
        SendMailIntent intent
    ) {
        int capturedBytes = EstimateUtf8Bytes(turnId, intent);
        bool routed = string.Equals(
            intent.Recipient,
            _route.Recipient,
            StringComparison.Ordinal
        );
        string? preflightFailure = null;
        if (routed) {
            try {
                if (string.IsNullOrWhiteSpace(intent.Body)
                    || StrictUtf8.GetByteCount(intent.Body)
                        > _route.MaximumTaskUtf8Bytes) {
                    preflightFailure = "TASK_INVALID_OR_TOO_LARGE";
                }
            }
            catch (EncoderFallbackException) {
                preflightFailure = "TASK_INVALID_UNICODE";
            }
        }
        return new Candidate {
            DispatchId = CreateDispatchId(
                _userId,
                sourceActionHead,
                ordinal
            ),
            SourceActionHead = sourceActionHead,
            ArtifactOrdinal = ordinal,
            Recipient = intent.Recipient,
            TurnId = turnId,
            CapturedUtf8Bytes = capturedBytes,
            State = routed
                ? GalateaDelegateCandidateState.Queued
                : GalateaDelegateCandidateState.Unrouted,
            Intent = intent,
            PreflightFailureCode = preflightFailure
        };
    }

    private void StartPumpLocked() {
        if (_disposed
            || _pumpTask is { IsCompleted: false }
            || !_candidates.Any(static candidate =>
                candidate.State == GalateaDelegateCandidateState.Queued)) {
            return;
        }
        _pumpTask = Task.Run(PumpAsync);
    }

    private async Task PumpAsync() {
        while (true) {
            Candidate candidate;
            string? threadId;
            string? preflightFailure;
            lock (_gate) {
                if (_disposed) { return; }
                candidate = _candidates.FirstOrDefault(static value =>
                    value.State == GalateaDelegateCandidateState.Queued
                )!;
                if (candidate is null) { return; }
                candidate.State = GalateaDelegateCandidateState.Starting;
                _active = candidate;
                threadId = _threadId;
                preflightFailure = candidate.PreflightFailureCode
                    ?? (_quarantineCode is null
                        ? null
                        : "ROUTE_QUARANTINED");
            }

            try {
                if (preflightFailure is not null) {
                    await PublishFailureAsync(
                        candidate,
                        "preflight",
                        preflightFailure
                    ).ConfigureAwait(false);
                }
                else {
                    await DispatchOneAsync(candidate, threadId)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (
                _lifetime.IsCancellationRequested) {
                return;
            }
            catch (Exception exception) when (
                GalateaExceptionClassifier.IsNonFatal(exception)) {
                await PublishFailureAsync(
                    candidate,
                    "coordinator",
                    "UNEXPECTED_COORDINATOR_FAILURE"
                ).ConfigureAwait(false);
                DebugUtil.Warning(
                    LogCategory,
                    $"Delegate coordinator contained failure: dispatchId={candidate.DispatchId}, error={Safe(exception.GetType().Name)}"
                );
            }
            finally {
                lock (_gate) {
                    if (ReferenceEquals(_active, candidate)) {
                        _active = null;
                    }
                }
            }
        }
    }

    private async Task DispatchOneAsync(
        Candidate candidate,
        string? threadId
    ) {
        SendMailIntent intent;
        lock (_gate) {
            intent = candidate.Intent
                ?? throw new InvalidDataException(
                    "Queued delegation candidate has no payload."
                );
        }
        var request = new GalateaDelegateDispatchRequest(
            candidate.DispatchId,
            threadId,
            intent.Body
        );
        Task<GalateaDelegateAcceptedHandle> startTask = _sidecar.StartAsync(
            request,
            _lifetime.Token
        );
        GalateaDelegateAcceptedHandle accepted;
        try {
            accepted = await startTask.WaitAsync(_lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested) {
            ObserveAbandonedStart(startTask);
            throw;
        }
        catch (GalateaDelegateStartException exception) {
            await PublishFailureAsync(
                candidate,
                exception.Stage,
                exception.Code
            ).ConfigureAwait(false);
            return;
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            await PublishFailureAsync(
                candidate,
                "start",
                "SIDECAR_START_FAILED"
            ).ConfigureAwait(false);
            DebugUtil.Warning(
                LogCategory,
                $"Delegate start failed: dispatchId={candidate.DispatchId}, error={Safe(exception.GetType().Name)}"
            );
            return;
        }

        if (!TryAccept(candidate, accepted, threadId, out string acceptCode)) {
            ObserveAbandonedCompletion(accepted?.Completion);
            await PublishFailureAsync(candidate, "accepted", acceptCode)
                .ConfigureAwait(false);
            return;
        }

        GalateaDelegateTerminal terminal;
        try {
            terminal = await accepted.Completion
                .WaitAsync(_lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested) {
            ObserveAbandonedCompletion(accepted.Completion);
            throw;
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            await PublishFailureAsync(
                candidate,
                "terminal",
                "SIDECAR_COMPLETION_FAILED"
            ).ConfigureAwait(false);
            DebugUtil.Warning(
                LogCategory,
                $"Delegate completion failed: dispatchId={candidate.DispatchId}, error={Safe(exception.GetType().Name)}"
            );
            return;
        }

        if (!ValidateTerminal(candidate, accepted, terminal)) {
            Quarantine("TERMINAL_IDENTITY_MISMATCH");
            await PublishFailureAsync(
                candidate,
                "terminal",
                "TERMINAL_IDENTITY_MISMATCH"
            ).ConfigureAwait(false);
            return;
        }
        switch (terminal) {
            case GalateaDelegateTerminal.Completed completed:
                if (!TryValidateFinal(completed.Final, out string finalCode)) {
                    await PublishFailureAsync(
                        candidate,
                        "final",
                        finalCode
                    ).ConfigureAwait(false);
                    return;
                }
                await PublishReadyAsync(
                    candidate,
                    new GalateaReadyNotice.Reply(completed.Final),
                    GalateaDelegateCandidateState.ReplyReady
                ).ConfigureAwait(false);
                return;
            case GalateaDelegateTerminal.Failed failed:
                await PublishFailureAsync(
                    candidate,
                    failed.Stage,
                    failed.Code
                ).ConfigureAwait(false);
                return;
            default:
                await PublishFailureAsync(
                    candidate,
                    "terminal",
                    "UNKNOWN_TERMINAL"
                ).ConfigureAwait(false);
                return;
        }
    }

    private bool TryAccept(
        Candidate candidate,
        GalateaDelegateAcceptedHandle accepted,
        string? requestedThreadId,
        out string failureCode
    ) {
        if (accepted is null
            || !string.Equals(
                accepted.DispatchId,
                candidate.DispatchId,
                StringComparison.Ordinal
            )
            || !IsWireIdentity(accepted.ThreadId)
            || !IsWireIdentity(accepted.TurnId)
            || accepted.Completion is null) {
            Quarantine("ACCEPTED_IDENTITY_MISMATCH");
            failureCode = "ACCEPTED_IDENTITY_MISMATCH";
            return false;
        }
        lock (_gate) {
            if (_disposed) {
                failureCode = "COORDINATOR_DISPOSED";
                return false;
            }
            if (requestedThreadId is null) {
                if (_threadId is null) {
                    _threadId = accepted.ThreadId;
                }
                else if (!string.Equals(
                        _threadId,
                        accepted.ThreadId,
                        StringComparison.Ordinal)) {
                    _quarantineCode = "ACCEPTED_THREAD_MISMATCH";
                    failureCode = "ACCEPTED_THREAD_MISMATCH";
                    return false;
                }
            }
            else if (!string.Equals(
                    requestedThreadId,
                    accepted.ThreadId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    _threadId,
                    accepted.ThreadId,
                    StringComparison.Ordinal)) {
                _quarantineCode = "ACCEPTED_THREAD_MISMATCH";
                failureCode = "ACCEPTED_THREAD_MISMATCH";
                return false;
            }
            candidate.State = GalateaDelegateCandidateState.Running;
            candidate.ThreadId = accepted.ThreadId;
            candidate.TurnIdAtDelegate = accepted.TurnId;
        }
        DebugUtil.Info(
            LogCategory,
            $"Delegate dispatch accepted: dispatchId={candidate.DispatchId}, reusedThread={requestedThreadId is not null}"
        );
        failureCode = string.Empty;
        return true;
    }

    private static bool ValidateTerminal(
        Candidate candidate,
        GalateaDelegateAcceptedHandle accepted,
        GalateaDelegateTerminal terminal
    ) => terminal is not null
        && string.Equals(
            terminal.DispatchId,
            candidate.DispatchId,
            StringComparison.Ordinal
        )
        && string.Equals(
            terminal.ThreadId,
            accepted.ThreadId,
            StringComparison.Ordinal
        )
        && string.Equals(
            terminal.TurnId,
            accepted.TurnId,
            StringComparison.Ordinal
        );

    private bool TryValidateFinal(string? final, out string failureCode) {
        if (string.IsNullOrWhiteSpace(final)) {
            failureCode = "FINAL_BLANK";
            return false;
        }
        try {
            int bytes = StrictUtf8.GetByteCount(final);
            int maximum = Math.Min(
                _route.MaximumReplyUtf8Bytes,
                GalateaPlayerObservationEnvelope.MaximumReplyUtf8Bytes
            );
            if (bytes > maximum) {
                failureCode = "FINAL_TOO_LARGE";
                return false;
            }
        }
        catch (EncoderFallbackException) {
            failureCode = "FINAL_INVALID_UNICODE";
            return false;
        }
        failureCode = string.Empty;
        return true;
    }

    private Task PublishFailureAsync(
        Candidate candidate,
        string? stage,
        string? code
    ) {
        string safeStage = NormalizeFailureToken(stage, "delegate");
        string safeCode = NormalizeFailureToken(code, "DELEGATE_FAILURE");
        var notice = new GalateaReadyNotice.DeliveryFailure(
            $"外界代行者 Codex 未能处理这封信（阶段：{safeStage}；错误代码：{safeCode}）。"
        );
        return PublishReadyAsync(
            candidate,
            notice,
            GalateaDelegateCandidateState.FailureReady
        );
    }

    private async Task PublishReadyAsync(
        Candidate candidate,
        GalateaReadyNotice notice,
        GalateaDelegateCandidateState readyState
    ) {
        int bytes = StrictUtf8.GetByteCount(notice.Body);
        while (true) {
            Task capacityChanged;
            lock (_gate) {
                _lifetime.Token.ThrowIfCancellationRequested();
                if (_inboxCount < _route.MaximumInboxReplies
                    && _inboxUtf8Bytes
                        <= _route.MaximumInboxUtf8Bytes - bytes) {
                    candidate.ReadyNotice = notice;
                    candidate.State = readyState;
                    candidate.CompletionSequence = checked(
                        ++_completionSequence
                    );
                    _inboxCount++;
                    _inboxUtf8Bytes += bytes;
                    ReleaseCapturedPayloadLocked(candidate);
                    DebugUtil.Info(
                        LogCategory,
                        $"Delegate terminal ready: dispatchId={candidate.DispatchId}, sequence={candidate.CompletionSequence}, kind={readyState}"
                    );
                    return;
                }
                capacityChanged = _inboxCapacityChanged.Task;
            }
            await capacityChanged.WaitAsync(_lifetime.Token)
                .ConfigureAwait(false);
        }
    }

    private void CompleteLease(long leaseId, bool commit) {
        bool startPump = false;
        lock (_gate) {
            if (_disposed) {
                if (!commit) { return; }
                throw new ObjectDisposedException(
                    nameof(GalateaDelegationCoordinator),
                    "A ready-reply lease cannot commit after coordinator shutdown."
                );
            }
            if (_activeLeaseId != leaseId) {
                throw new InvalidOperationException(
                    "The ready-reply lease is no longer active."
                );
            }
            if (commit) {
                foreach (Candidate candidate in _candidates.Where(
                    static value => value.State
                        == GalateaDelegateCandidateState.Leased)) {
                    candidate.State = GalateaDelegateCandidateState.Consumed;
                    int bytes = StrictUtf8.GetByteCount(
                        candidate.ReadyNotice!.Body
                    );
                    _inboxCount--;
                    _inboxUtf8Bytes -= bytes;
                    candidate.ReadyNotice = null;
                }
                SignalInboxCapacityChangedLocked();
                startPump = true;
            }
            else {
                RollbackLeaseLocked(leaseId);
            }
            _activeLeaseId = null;
            if (startPump) { StartPumpLocked(); }
        }
    }

    private void RollbackLeaseLocked(long leaseId) {
        if (_activeLeaseId != leaseId) { return; }
        foreach (Candidate candidate in _candidates.Where(
            static value => value.State
                == GalateaDelegateCandidateState.Leased)) {
            candidate.State = candidate.ReadyNotice
                    is GalateaReadyNotice.Reply
                ? GalateaDelegateCandidateState.ReplyReady
                : GalateaDelegateCandidateState.FailureReady;
        }
        _activeLeaseId = null;
    }

    private void Quarantine(string code) {
        lock (_gate) {
            _quarantineCode ??= code;
        }
        DebugUtil.Error(
            LogCategory,
            $"Delegate route quarantined: user={Safe(_userId)}, code={NormalizeFailureToken(code, "ROUTE_QUARANTINED")}"
        );
    }

    private void ReleaseCapturedPayloadLocked(Candidate candidate) {
        if (candidate.Intent is null) { return; }
        candidate.Intent = null;
        _candidateUtf8Bytes -= candidate.CapturedUtf8Bytes;
    }

    private void SignalInboxCapacityChangedLocked() {
        TaskCompletionSource<bool> previous = _inboxCapacityChanged;
        _inboxCapacityChanged = NewSignal();
        previous.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewSignal() => new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private static int EstimateUtf8Bytes(
        string turnId,
        SendMailIntent intent
    ) {
        try {
            return checked(
                StrictUtf8.GetByteCount(turnId)
                + StrictUtf8.GetByteCount(intent.Recipient)
                + StrictUtf8.GetByteCount(intent.Subject ?? string.Empty)
                + StrictUtf8.GetByteCount(intent.Body)
                + StrictUtf8.GetByteCount(
                    intent.InReplyToMessageId ?? string.Empty
                )
                + StrictUtf8.GetByteCount(intent.EvidenceQuote)
                + 128
            );
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Captured mail must contain valid Unicode.",
                nameof(intent),
                exception
            );
        }
    }

    private static void AppendLengthPrefixed(
        IncrementalHash hash,
        string value
    ) {
        byte[] utf8 = StrictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, utf8.Length);
        hash.AppendData(length);
        hash.AppendData(utf8);
    }

    private static bool IsWireIdentity(string? value) {
        if (string.IsNullOrWhiteSpace(value)) { return false; }
        try {
            return StrictUtf8.GetByteCount(value)
                <= MaximumWireIdentityUtf8Bytes;
        }
        catch (EncoderFallbackException) {
            return false;
        }
    }

    private static string NormalizeFailureToken(
        string? value,
        string fallback
    ) {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 64
            || value.Any(static character =>
                !(character is >= 'A' and <= 'Z'
                    or >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '_' or '-' or '.'))) {
            return fallback;
        }
        return value;
    }

    private static string Safe(string value) =>
        GalateaMailboxText.SummarizeForLog(value);

    private static void ObserveAbandonedStart(
        Task<GalateaDelegateAcceptedHandle> task
    ) {
        _ = task.ContinueWith(
            static completed => {
                if (completed.IsFaulted) {
                    _ = completed.Exception;
                }
                else if (completed.Status == TaskStatus.RanToCompletion) {
                    ObserveAbandonedCompletion(completed.Result.Completion);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private static void ObserveAbandonedCompletion(
        Task<GalateaDelegateTerminal>? task
    ) {
        if (task is null) { return; }
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    internal sealed class GalateaReadyReplyLease : IDisposable {
        private GalateaDelegationCoordinator? _owner;
        private readonly long _leaseId;

        internal GalateaReadyReplyLease(
            GalateaDelegationCoordinator owner,
            long leaseId,
            long cutoffSequence,
            IReadOnlyList<GalateaReadyNotice> notices
        ) {
            _owner = owner;
            _leaseId = leaseId;
            CutoffSequence = cutoffSequence;
            Notices = notices;
        }

        internal long CutoffSequence { get; }
        internal IReadOnlyList<GalateaReadyNotice> Notices { get; }

        internal void Commit() => Complete(commit: true);

        internal void Rollback() => Complete(commit: false);

        public void Dispose() {
            GalateaDelegationCoordinator? owner = Interlocked.Exchange(
                ref _owner,
                null
            );
            if (owner is null) { return; }
            owner.CompleteLease(_leaseId, commit: false);
        }

        private void Complete(bool commit) {
            GalateaDelegationCoordinator? owner = Interlocked.Exchange(
                ref _owner,
                null
            );
            if (owner is null) {
                if (!commit) { return; }
                throw new InvalidOperationException(
                    "The ready-reply lease is already complete."
                );
            }
            owner.CompleteLease(_leaseId, commit);
        }
    }
}
