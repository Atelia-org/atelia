using Atelia.Diagnostics;

namespace Atelia.Galatea.Server.Mailbox;

internal enum GalateaMailRecipientClass { Codex, Character, Email, Unrouted }

internal static class GalateaMailRecipientClassifier {
    // Classifies only the explicitly supplied recipient, never body substrings.
    internal static GalateaMailRecipientClass Classify(string recipient, GalateaInternalMailTarget? peer, string? senderName = null) =>
        recipient == GalateaDelegateConfigReader.CanonicalRecipient ? GalateaMailRecipientClass.Codex
        : peer is not null ? GalateaMailRecipientClass.Character
        : recipient == senderName ? GalateaMailRecipientClass.Unrouted
        : GalateaExternalMailAddress.TryParse(recipient, out _) ? GalateaMailRecipientClass.Email
        : GalateaMailRecipientClass.Unrouted;
}

// Independent of durable delegation and internal-mail delivery states.
internal enum GalateaSmtpMailState { Pending, Attempting, ProviderAccepted, DefiniteFailure, OutcomeUnknown }

internal sealed record GalateaSmtpMailOutboxSnapshot(
    string DispatchId, string SourceActionAddress, long CaptureSequence, int ArtifactOrdinal,
    string Recipient, string FromCharacterId, string SenderAccountReference,
    string? Subject, string Body, GalateaSmtpMailState State, string? ResultCode, long Revision
);

internal sealed record GalateaSmtpSendRequest(
    string DispatchId, string FromCharacterId, string SenderAccountReference,
    string Recipient, string? Subject, string Body
);

internal sealed record GalateaSmtpSendResult(GalateaSmtpMailState State, string? Code = null);

internal interface IGalateaSmtpSender {
    Task<GalateaSmtpSendResult> SendAsync(GalateaSmtpSendRequest request, CancellationToken cancellationToken);
}

internal enum GalateaOfflineSmtpBehavior { Accepted, DefiniteFailure, OutcomeUnknown, Throw }

/// <summary>No sockets, SMTP library, configuration files or credential access.</summary>
internal sealed class GalateaOfflineSmtpSender(
    GalateaOfflineSmtpBehavior behavior = GalateaOfflineSmtpBehavior.OutcomeUnknown
) : IGalateaSmtpSender {
    public Task<GalateaSmtpSendResult> SendAsync(GalateaSmtpSendRequest request, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(behavior switch {
            GalateaOfflineSmtpBehavior.Accepted => new GalateaSmtpSendResult(GalateaSmtpMailState.ProviderAccepted, "OFFLINE_ACCEPTED"),
            GalateaOfflineSmtpBehavior.DefiniteFailure => new GalateaSmtpSendResult(GalateaSmtpMailState.DefiniteFailure, "OFFLINE_REJECTED"),
            GalateaOfflineSmtpBehavior.OutcomeUnknown => new GalateaSmtpSendResult(GalateaSmtpMailState.OutcomeUnknown, "OFFLINE_UNKNOWN"),
            _ => throw new IOException("Offline sender injected exception.")
        });
    }
}

internal sealed class GalateaSmtpOutboxConsumer(IGalateaSmtpSender sender) {
    internal async Task<bool> ConsumeOneAsync(GalateaDelegationSqliteStore store, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        // Claim is committed before the boundary; uncertain claim never permits sending.
        GalateaSmtpMailOutboxSnapshot? row = store.ClaimPendingSmtpMail();
        if (row is null) { return false; }
        GalateaSmtpSendResult result;
        try {
            result = await sender.SendAsync(new(row.DispatchId, row.FromCharacterId,
                row.SenderAccountReference, row.Recipient, row.Subject, row.Body), cancellationToken).ConfigureAwait(false);
            if (result is null || result.State is not (GalateaSmtpMailState.ProviderAccepted
                or GalateaSmtpMailState.DefiniteFailure or GalateaSmtpMailState.OutcomeUnknown)
                || !IsReasonCode(result.Code)) {
                result = new(GalateaSmtpMailState.OutcomeUnknown, "INVALID_SENDER_RESULT");
            }
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            // Even cancellation/exception after claim cannot establish whether a side effect happened.
            result = new(GalateaSmtpMailState.OutcomeUnknown, "SENDER_EXCEPTION");
        }
        store.CompleteSmtpAttempt(row.DispatchId, row.Revision, result);
        return true;
    }

    internal static bool IsReasonCode(string? code) => code is { Length: > 0 and <= 96 }
        && code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
}

/// <summary>Phase 1 background driver; default composition is always the offline sender.</summary>
internal sealed class GalateaSmtpOutboxBackgroundService : BackgroundService {
    private readonly GalateaHostService _host;
    private readonly IGalateaSmtpSender _sender;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GalateaSmtpOutboxBackgroundService(GalateaHostService host, IGalateaSmtpSender sender) {
        _host = host;
        _sender = sender;
        host.RegisterSmtpOutboxConsumer(this);
    }

    internal void BeginShutdown() => _shutdown.Cancel();
    internal async Task DrainAsync() {
        BeginShutdown();
        if (ExecuteTask is not null) { await _stopped.Task.ConfigureAwait(false); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _shutdown.Token);
        CancellationToken ct = linked.Token;
        try {
            if (_host.MaintenanceMode) { return; }
            var consumer = new GalateaSmtpOutboxConsumer(_sender);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _host.TimeProvider);
            while (!ct.IsCancellationRequested && !_host.IsStopping) {
                foreach (var store in _host.DelegationSupervisor.ReadWritableSmtpStores()) {
                    if (ct.IsCancellationRequested || _host.IsStopping) { return; }
                    try { await consumer.ConsumeOneAsync(store, ct).ConfigureAwait(false); }
                    catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
                        // Content-free; the Attempting row remains inert until reopen recovery.
                        DebugUtil.Warning("Galatea.Smtp", "SMTP outbox consumption deferred.");
                    }
                }
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) { return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { _stopped.TrySetResult(); }
    }
}
