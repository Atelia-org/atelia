using System.Threading.Channels;
using Atelia.Completion;
using Atelia.Diagnostics;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

/// <summary>
/// Host-local, best-effort scheduler for durable character-mail outboxes.
/// The outbox and target proof gate provide correctness; this type only
/// improves liveness and never waits for a busy target writer.
/// </summary>
internal sealed class GalateaCharacterMailRelay : BackgroundService {
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);
    private readonly GalateaHostService _host;
    private readonly GalateaAcceptedTurnRunner _runner;
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _stopped = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    // Deliberately process-local: durable global ordering is not a product
    // requirement, while one sender's FIFO is.
    private readonly Dictionary<string, string> _lastSourceByTarget = new(
        StringComparer.Ordinal);
    private int _stopping;

    public GalateaCharacterMailRelay(
        GalateaHostService host,
        GalateaAcceptedTurnRunner runner
    ) {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _host.RegisterCharacterMailRelay(this);
    }

    internal void BeginShutdown() {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) { return; }
        _signals.Writer.TryComplete();
        _shutdown.Cancel();
    }

    internal async Task DrainAsync() {
        BeginShutdown();
        await _stopped.Task.ConfigureAwait(false);
    }

    internal bool Signal() => Volatile.Read(ref _stopping) == 0
        && _signals.Writer.TryWrite(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, _shutdown.Token);
        CancellationToken ct = linked.Token;
        try {
            if (_host.MaintenanceMode) { return; }
            _ = Signal();
            using var timer = new PeriodicTimer(SweepInterval, _host.TimeProvider);
            Task<bool> signalWait = _signals.Reader.WaitToReadAsync(
                ct).AsTask();
            Task<bool> tickWait = timer.WaitForNextTickAsync(
                ct).AsTask();
            while (!ct.IsCancellationRequested
                && Volatile.Read(ref _stopping) == 0) {
                Task completed = await Task.WhenAny(signalWait, tickWait)
                    .ConfigureAwait(false);
                if (ReferenceEquals(completed, signalWait)) {
                    if (!await signalWait.ConfigureAwait(false)) { return; }
                    while (_signals.Reader.TryRead(out _)) { }
                    signalWait = _signals.Reader.WaitToReadAsync(
                        ct).AsTask();
                }
                if (ReferenceEquals(completed, tickWait)) {
                    if (!await tickWait.ConfigureAwait(false)) { return; }
                    tickWait = timer.WaitForNextTickAsync(ct).AsTask();
                }
                if (_host.IsStopping) { return; }
                await SweepAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            ct.IsCancellationRequested
            || Volatile.Read(ref _stopping) != 0) { }
        finally {
            _stopped.TrySetResult();
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken) {
        foreach (GalateaCharacterRecipient target in _host.CharacterRecipients) {
            cancellationToken.ThrowIfCancellationRequested();
            if (_host.IsStopping) { return; }
            try {
                IReadOnlyList<GalateaInternalMailSourceOutbox> rows = _host
                    .DelegationSupervisor
                    .ReadInternalMailOutboxesForTarget(target.CharacterId);
                GalateaInternalMailSourceOutbox? candidate = ChooseCandidate(
                    target.CharacterId, rows);
                if (candidate is not null) {
                    await TryAdvanceAsync(target, candidate, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested || _host.IsStopping) {
                return;
            }
            catch (Exception exception) when (
                GalateaExceptionClassifier.IsNonFatal(exception)) {
                DebugUtil.Warning("Galatea.CharacterMail",
                    "Character-mail relay sweep deferred: target="
                    + target.CharacterId + ", exceptionType="
                    + exception.GetType().FullName + ".");
            }
        }
    }

    private GalateaInternalMailSourceOutbox? ChooseCandidate(
        string targetUserId,
        IReadOnlyList<GalateaInternalMailSourceOutbox> rows
    ) {
        GalateaInternalMailSourceOutbox[] valid = rows
            // A stale Pending row is deliberately inert, but a stale Bound
            // row must still enter the target gate and block its writers.
            .Where(value => value.Outbox.State
                == GalateaInternalMailState.ObservationBound
                || value.Outbox.State == GalateaInternalMailState.Pending
                    && _host.IsCurrentInternalMailTarget(value))
            .ToArray();
        GalateaInternalMailSourceOutbox? bound = valid
            .Where(static value => value.Outbox.State
                == GalateaInternalMailState.ObservationBound)
            .OrderBy(static value => value.Outbox.CaptureSequence)
            .ThenBy(static value => value.Outbox.ArtifactOrdinal)
            .FirstOrDefault();
        if (bound is not null) { return bound; }

        GalateaInternalMailSourceOutbox[] heads = valid
            .Where(static value => value.Outbox.State
                == GalateaInternalMailState.Pending)
            .GroupBy(static value => value.SourceCharacterId, StringComparer.Ordinal)
            .Select(static queue => queue.OrderBy(value => value.Outbox.CaptureSequence)
                .ThenBy(value => value.Outbox.ArtifactOrdinal).First())
            .OrderBy(static value => value.SourceCharacterId, StringComparer.Ordinal)
            .ToArray();
        if (heads.Length == 0) { return null; }
        if (!_lastSourceByTarget.TryGetValue(targetUserId, out string? last)) {
            _lastSourceByTarget[targetUserId] = heads[0].SourceCharacterId;
            return heads[0];
        }
        int index = Array.FindIndex(heads, value => string.Compare(
            value.SourceCharacterId, last, StringComparison.Ordinal) > 0);
        GalateaInternalMailSourceOutbox selected = heads[index < 0 ? 0 : index];
        _lastSourceByTarget[targetUserId] = selected.SourceCharacterId;
        return selected;
    }

    private async Task TryAdvanceAsync(
        GalateaCharacterRecipient target,
        GalateaInternalMailSourceOutbox selected,
        CancellationToken cancellationToken
    ) {
        CharacterSessionHost session = await _host.GetSessionAsync(
            target.CharacterId, cancellationToken).ConfigureAwait(false);
        if (!session.TurnLock.Wait(0)) { return; }

        GalateaLiveTurn? liveTurn = null;
        bool transferred = false;
        try {
            if (_host.IsStopping || _host.MaintenanceMode) { return; }
            GalateaCharacterMailDeliveryReconciler.Reconcile(
                _host.DelegationSupervisor, session, cancellationToken);
            if (selected.Outbox.State
                != GalateaInternalMailState.Pending
                || session.AutomaticReplyFailed
                || session.AutomaticAdmissionFailed) {
                return;
            }
            SessionRuntimeRecoveryRequirements recovery = session.Engine
                .InspectRuntimeRecoveryRequirements(cancellationToken);
            if (recovery is not SessionRuntimeRecoveryRequirements
                    .NoRuntimeRequired { Phase: SessionExecutionPhase.Idle }) {
                return;
            }
            if (!_host.TryGetFreshConnection(session.Character, null,
                    out CompletionConnectionConfig connection)) {
                throw new InvalidDataException(
                    "Character-mail target connection is unavailable.");
            }
            await _host.PrepareFreshTurnAdmissionAsync(
                session, recovery, cancellationToken).ConfigureAwait(false);

            GalateaInternalMailOutboxSnapshot current = selected.Store
                .ReadInternalMailOutboxesForTarget(target.CharacterId)
                .SingleOrDefault(value => string.Equals(value.DispatchId,
                    selected.Outbox.DispatchId, StringComparison.Ordinal))
                ?? throw new GalateaDelegationStoreConflictException(
                    "Character-mail outbox row disappeared before admission.");
            if (current.State != GalateaInternalMailState.Pending) { return; }
            var source = selected with { Outbox = current };
            MailboxMessage message =
                GalateaCharacterMailDeliveryReconciler.RestoreMessage(source);
            liveTurn = _host.StartInboundMailTurn(
                session,
                message,
                new GalateaTurnOptions(connection.Id),
                new GalateaInternalMailDeliveryBinding(
                    source.Store, current.DispatchId, current.Revision),
                sender: new GalateaSenderSnapshot("character", source.SourceCharacterId, current.FromCharacterName)
            );
            _ = _runner.Start(session, liveTurn);
            transferred = true;
        }
        finally {
            if (!transferred) {
                try {
                    if (liveTurn is not null) {
                        if (liveTurn.Status == "running") {
                            liveTurn.PublishError(GalateaSseErrorCode.InternalFailure);
                        }
                        _host.FinishTurn(session, liveTurn);
                        liveTurn.Complete();
                    }
                }
                finally {
                    session.TurnLock.Release();
                }
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken) {
        BeginShutdown();
        return base.StopAsync(cancellationToken);
    }
}
