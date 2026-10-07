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

    internal async Task SweepAsync(CancellationToken cancellationToken) {
        foreach (GalateaCharacterRecipient target in _host.CharacterRecipients) {
            cancellationToken.ThrowIfCancellationRequested();
            if (_host.IsStopping) { return; }
            try {
                var candidates = _host.DelegationSupervisor
                    .ReadInternalMailOutboxesForTarget(target.CharacterId)
                    .Where(value => value.Outbox.State == GalateaInternalMailState.ObservationBound
                        || value.Outbox.State == GalateaInternalMailState.Pending
                            && _host.IsCurrentInternalMailTarget(value))
                    .Select(static source => Candidate.Internal(source)).ToList();
                if (_host.DelegationSupervisor.TryGetAttachedStore(target.CharacterId, out var store)) {
                    candidates.AddRange(store.ReadUnsettledExternalMails()
                        .Where(static row => row.State == GalateaExternalMailInboxState.ObservationBound)
                        .Select(row => Candidate.Imap(store, row)));
                    if (_host.Imap.Enabled && store.ReadPendingExternalMail() is { } pending
                        && IsCurrentExternalMailTarget(target, pending)) {
                        candidates.Add(Candidate.Imap(store, pending));
                    }
                }
                Candidate? candidate = ChooseCandidate(target.CharacterId, candidates);
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

    private Candidate? ChooseCandidate(string targetUserId, IReadOnlyList<Candidate> rows) {
        Candidate? bound = rows.FirstOrDefault(static row => row.IsBound);
        if (bound is not null) { return bound; }
        Candidate[] heads = rows.Where(static row => !row.IsBound)
            .GroupBy(static row => row.SourceKey, StringComparer.Ordinal)
            .Select(static queue => queue.OrderBy(row => row.Sequence)
                .ThenBy(row => row.Ordinal).First())
            .OrderBy(static row => row.SourceKey, StringComparer.Ordinal).ToArray();
        if (heads.Length == 0) { return null; }
        int index = _lastSourceByTarget.TryGetValue(targetUserId, out string? last)
            ? Array.FindIndex(heads, row => string.Compare(row.SourceKey, last,
                StringComparison.Ordinal) > 0) : 0;
        return heads[index < 0 ? 0 : index];
    }

    private static bool IsCurrentExternalMailTarget(
        GalateaCharacterRecipient target, GalateaExternalMailInboxSnapshot row
    ) => row.TargetCharacterId == target.CharacterId
        && row.TargetSessionRepositoryId == target.SessionRepositoryId
        && row.TargetCharacterName == target.CharacterName.Value;

    private async Task TryAdvanceAsync(
        GalateaCharacterRecipient target,
        Candidate selected,
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
            if (selected.IsBound
                || selected.External is not null && !_host.Imap.Enabled
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

            MailboxMessage message;
            GalateaInboundMailOrigin origin;
            if (selected.InternalSource is { } internalSource) {
                GalateaInternalMailOutboxSnapshot? current = internalSource.Store
                    .ReadInternalMailOutboxesForTarget(target.CharacterId)
                    .SingleOrDefault(row => row.DispatchId == internalSource.Outbox.DispatchId);
                if (current is null || current.State != GalateaInternalMailState.Pending) { return; }
                var source = internalSource with { Outbox = current };
                if (!_host.IsCurrentInternalMailTarget(source)) { return; }
                message = GalateaCharacterMailDeliveryReconciler.RestoreMessage(source);
                origin = new GalateaInboundMailOrigin.CharacterDelivery(
                    new GalateaSenderSnapshot("character", source.SourceCharacterId, current.FromCharacterName),
                    new GalateaInternalMailDeliveryBinding(source.Store, current.DispatchId, current.Revision));
            }
            else {
                GalateaExternalMailInboxSnapshot? current = selected.Store.ReadExternalMail(selected.External!.InboxId);
                if (current is null || current.State != GalateaExternalMailInboxState.Pending
                    || !IsCurrentExternalMailTarget(target, current)) { return; }
                message = GalateaCharacterMailDeliveryReconciler.RestoreMessage(current);
                origin = new GalateaInboundMailOrigin.ImapDelivery(
                    new GalateaImapMailDeliveryBinding(selected.Store, current.InboxId, current.Revision),
                    current.AttachmentCount);
            }
            liveTurn = _host.StartInboundMailTurn(session, message,
                new GalateaTurnOptions(connection.Id), origin);
            _ = _runner.Start(session, liveTurn);
            _lastSourceByTarget[target.CharacterId] = selected.SourceKey;
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

    // Two narrow durable adapters; the target lock, admission and runner are shared.
    private sealed record Candidate(
        string SourceKey, long Sequence, int Ordinal, bool IsBound,
        GalateaDelegationSqliteStore Store,
        GalateaInternalMailSourceOutbox? InternalSource,
        GalateaExternalMailInboxSnapshot? External
    ) {
        internal static Candidate Internal(GalateaInternalMailSourceOutbox source) =>
            new("character:" + source.SourceCharacterId, source.Outbox.CaptureSequence,
                source.Outbox.ArtifactOrdinal, source.Outbox.State == GalateaInternalMailState.ObservationBound,
                source.Store, source, null);

        internal static Candidate Imap(GalateaDelegationSqliteStore store, GalateaExternalMailInboxSnapshot row) =>
            new("imap", row.InboxId, 0, row.State == GalateaExternalMailInboxState.ObservationBound,
                store, null, row);
    }

    public override Task StopAsync(CancellationToken cancellationToken) {
        BeginShutdown();
        return base.StopAsync(cancellationToken);
    }
}
