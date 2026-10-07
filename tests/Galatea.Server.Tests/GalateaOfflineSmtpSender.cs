using Atelia.Galatea.Server.Mailbox;

namespace Atelia.Galatea.Server.Tests;

internal enum GalateaOfflineSmtpBehavior { Accepted, DefiniteFailure, OutcomeUnknown, Throw }

/// <summary>Test-only injected sender; no sockets or configuration access.</summary>
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
