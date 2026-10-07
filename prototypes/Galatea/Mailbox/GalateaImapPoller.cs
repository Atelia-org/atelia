using System.Collections.Concurrent;
using Atelia.Diagnostics;

namespace Atelia.Galatea.Server.Mailbox;

internal sealed record GalateaImapPollResult(
    long AtUnixTimeMilliseconds, string Code, int ImportedCount = 0, int RejectedCount = 0, int FilteredCount = 0
);

internal static class GalateaImapReceiver {
    internal static async Task<GalateaImapPollResult> ReceiveAsync(
        GalateaDelegationSqliteStore store, string reference, string targetName,
        IReadOnlyCollection<string> configuredSenders, IGalateaImapConnection connection,
        DateTimeOffset now, CancellationToken cancellationToken, Action? signalInboundMail = null
    ) {
        int imported = 0, rejected = 0, filtered = 0;
        GalateaImapPollResult Result(string code) => new(now.ToUnixTimeMilliseconds(), code, imported, rejected, filtered);
        GalateaImapCheckpointSnapshot? checkpoint = store.ReadImapCheckpoint(reference);
        if (checkpoint?.BlockedCode is { } blocked) { return Result(blocked); }
        if (connection.UidValidity == 0 || connection.UidNext is null or 0) { return Result("IMAP_INVALID_UID_METADATA"); }
        uint validity = connection.UidValidity;
        uint observedUidNext = connection.UidNext.Value;
        uint upper = observedUidNext - 1;
        if (checkpoint is null) {
            cancellationToken.ThrowIfCancellationRequested();
            store.EstablishImapBaseline(reference, validity, upper, now);
            return Result("IMAP_BASELINE_ESTABLISHED");
        }
        bool CheckNamespace() {
            uint? next = connection.UidNext;
            string? code = connection.UidValidity != checkpoint.UidValidity ? "IMAP_UIDVALIDITY_CHANGED"
                : next is null or 0 ? "IMAP_INVALID_UID_METADATA"
                : next.Value < observedUidNext || next.Value - 1 < checkpoint.ScannedThroughUid ? "IMAP_UIDNEXT_REGRESSED" : null;
            if (code is null) { observedUidNext = next!.Value; return true; }
            checkpoint = store.BlockImapCheckpoint(checkpoint, code);
            return false;
        }
        if (!CheckNamespace()) { return Result(checkpoint.BlockedCode!); }
        if (upper == checkpoint.ScannedThroughUid) { return Result("IMAP_READY"); }
        ulong firstWide = checked((ulong)checkpoint.ScannedThroughUid + 1);
        ulong lastWide = Math.Min(upper, checked((ulong)checkpoint.ScannedThroughUid + GalateaImapBounds.MaximumSearchUidSpan));
        uint first = checked((uint)firstWide), last = checked((uint)lastWide);
        IReadOnlyList<uint> uids;
        try { uids = await connection.SearchUidsAsync(first, last, cancellationToken).ConfigureAwait(false); }
        catch (GalateaImapReadException exception) when (exception.Code == "IMAP_INVALID_SEARCH_RESULT") {
            checkpoint = store.BlockImapCheckpoint(checkpoint, exception.Code);
            return Result(checkpoint.BlockedCode!);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!CheckNamespace()) { return Result(checkpoint.BlockedCode!); }
        if (uids.Count > GalateaImapBounds.MaximumSearchUidSpan || uids.Any(uid => uid < first || uid > last)
            || uids.Distinct().Count() != uids.Count) {
            checkpoint = store.BlockImapCheckpoint(checkpoint, "IMAP_INVALID_SEARCH_RESULT");
            return Result(checkpoint.BlockedCode!);
        }
        int processed = 0;
        foreach (uint uid in uids.Order()) {
            if (processed == GalateaImapBounds.MaximumMessagesPerPoll) { return Result("IMAP_POLL_LIMIT"); }
            cancellationToken.ThrowIfCancellationRequested();
            // SEARCH proved only the gap, never the existence/status of this UID.
            if ((ulong)uid > (ulong)checkpoint.ScannedThroughUid + 1) {
                checkpoint = store.AdvanceImapCheckpoint(checkpoint, uid - 1);
            }
            GalateaImapRawMessage raw = await connection.ReadRawAsync(uid, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CheckNamespace()) { return Result(checkpoint.BlockedCode!); }
            if (raw.Missing) {
                IReadOnlyList<uint> exact;
                try { exact = await connection.SearchUidsAsync(uid, uid, cancellationToken).ConfigureAwait(false); }
                catch (GalateaImapReadException exception) when (exception.Code == "IMAP_INVALID_SEARCH_RESULT") {
                    checkpoint = store.BlockImapCheckpoint(checkpoint, exception.Code);
                    return Result(checkpoint.BlockedCode!);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!CheckNamespace()) { return Result(checkpoint.BlockedCode!); }
                if (exact.Count > 1 || exact.Any(value => value != uid)) {
                    checkpoint = store.BlockImapCheckpoint(checkpoint, "IMAP_INVALID_SEARCH_RESULT");
                    return Result(checkpoint.BlockedCode!);
                }
                if (exact.Count != 0) { return Result("IMAP_FETCH_DEFERRED"); }
                store.RejectImapMail(checkpoint, uid, "IMAP_MESSAGE_VANISHED");
                rejected++;
            }
            else if (raw.RejectedCode is { } code) {
                store.RejectImapMail(checkpoint, uid, code);
                rejected++;
            }
            else if (raw.Bytes is null) { throw new GalateaImapReadException("IMAP_INVALID_RAW_RESULT"); }
            else {
                GalateaImapMimeEnvelope envelope = GalateaImapMimeDecoder.ReadEnvelope(raw.Bytes, cancellationToken);
                if (envelope.RejectedCode is { } envelopeCode) {
                    store.RejectImapMail(checkpoint, uid, envelopeCode);
                    rejected++;
                }
                else if (!store.DecideImapAdmission(checkpoint, uid, envelope.From!, configuredSenders)) { filtered++; }
                else {
                    GalateaImapMimeProjection projection = GalateaImapMimeDecoder.ProjectAllowed(raw.Bytes, cancellationToken);
                    if (projection.RejectedCode is { } projectionCode) {
                        store.RejectImapMail(checkpoint, uid, projectionCode);
                        rejected++;
                    }
                    else {
                        if (store.AcceptImapMail(checkpoint, uid, targetName, envelope.From!, projection.Subject,
                            projection.Body!, projection.AttachmentCount) is null) {
                            return Result("IMAP_BACKPRESSURE");
                        }
                        imported++;
                        // Signal each confirmed insert, including an earlier
                        // one when a later UID in this poll fails to fetch.
                        signalInboundMail?.Invoke();
                    }
                }
            }
            checkpoint = store.ReadImapCheckpoint(reference)
                ?? throw new GalateaImapReadException("IMAP_CHECKPOINT_MISSING");
            processed++;
        }
        // The remainder of this bounded SEARCH window contains no returned UID.
        if (checkpoint.ScannedThroughUid < last) { store.AdvanceImapCheckpoint(checkpoint, last); }
        return Result("IMAP_READY");
    }
}

internal sealed class GalateaImapPoller : BackgroundService {
    private readonly GalateaHostService _host;
    private readonly IGalateaImapTransport _transport;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly ConcurrentDictionary<string, GalateaImapPollResult> _lastPolls = new(StringComparer.Ordinal);

    public GalateaImapPoller(GalateaHostService host, IGalateaImapTransport transport) {
        _host = host;
        _transport = transport;
        host.RegisterImapPoller(this);
    }

    internal GalateaImapPollResult? ReadLastPoll(string characterId) => _lastPolls.GetValueOrDefault(characterId);
    internal void BeginShutdown() => _shutdown.Cancel();
    internal async Task DrainAsync() {
        BeginShutdown();
        if (ExecuteTask is not null) { await _stopped.Task.ConfigureAwait(false); }
        // Also drain an explicitly invoked poll (tests/operators), without
        // relying on BackgroundService registration/disposal order.
        await _pollGate.WaitAsync().ConfigureAwait(false);
        _pollGate.Release();
    }

    internal async Task<GalateaImapPollResult> PollCharacterAsync(string characterId, CancellationToken cancellationToken) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _pollGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try {
            DateTimeOffset now = _host.TimeProvider.GetUtcNow();
            GalateaImapPollResult result;
            if (_host.MaintenanceMode || _host.IsStopping || !_host.Imap.Enabled || _shutdown.IsCancellationRequested) {
                result = new(now.ToUnixTimeMilliseconds(), "IMAP_PAUSED");
            }
            else if (!_host.Imap.Accounts.TryGetValue(characterId, out var account) || account.Imap is null
                || !_host.TryGetCharacter(characterId, out var character)) {
                result = new(now.ToUnixTimeMilliseconds(), "IMAP_NOT_CONFIGURED");
            }
            else {
                try {
                    linked.CancelAfter(TimeSpan.FromSeconds(_host.Imap.TimeoutSeconds));
                    var store = await _host.GetImapStoreAsync(characterId, linked.Token).ConfigureAwait(false);
                    string reference = GalateaImapConfig.AccountReference(characterId, account);
                    var checkpoint = store.ReadImapCheckpoint(reference);
                    if (checkpoint?.BlockedCode is { } blocked) { result = new(now.ToUnixTimeMilliseconds(), blocked); }
                    else {
                        linked.Token.ThrowIfCancellationRequested();
                        try {
                            await using var connection = await _transport.OpenAsync(account, linked.Token).ConfigureAwait(false);
                            result = await GalateaImapReceiver.ReceiveAsync(store, reference, character.CharacterName.Value,
                                account.Imap.AutoDisplaySenders, connection, now, linked.Token, _host.SignalInboundMail)
                                .ConfigureAwait(false);
                        }
                        catch (GalateaImapReadException exception) when (checkpoint is not null
                            && (exception.Code is "IMAP_UIDVALIDITY_CHANGED" or "IMAP_UIDNEXT_REGRESSED")) {
                            linked.Token.ThrowIfCancellationRequested();
                            store.BlockImapCheckpoint(checkpoint, exception.Code);
                            result = new(now.ToUnixTimeMilliseconds(), exception.Code);
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                    && !_shutdown.IsCancellationRequested && !_host.IsStopping) {
                    result = new(now.ToUnixTimeMilliseconds(), "IMAP_TIMEOUT");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
                    string code = exception is GalateaImapReadException read ? read.Code : "IMAP_POLL_DEFERRED";
                    result = new(now.ToUnixTimeMilliseconds(), code);
                    DebugUtil.Warning("Galatea.Imap", $"stage={code} exceptionType={exception.GetType().FullName}");
                }
            }
            _lastPolls[characterId] = result;
            return result;
        }
        finally { _pollGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _shutdown.Token);
        CancellationToken ct = linked.Token;
        try {
            if (_host.MaintenanceMode || !_host.Imap.Enabled) { return; }
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_host.Imap.PollIntervalSeconds), _host.TimeProvider);
            while (!ct.IsCancellationRequested && !_host.IsStopping) {
                foreach (string characterId in _host.Imap.Accounts.Keys) {
                    if (ct.IsCancellationRequested || _host.IsStopping) { return; }
                    await PollCharacterAsync(characterId, ct).ConfigureAwait(false);
                }
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) { return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { _stopped.TrySetResult(); }
    }
}
