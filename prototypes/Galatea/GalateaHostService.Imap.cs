using Atelia.Galatea.Server.Mailbox;

namespace Atelia.Galatea.Server;

internal sealed record GalateaImapInboundStatusDto(
    bool Configured,
    bool Enabled,
    string State,
    bool BaselineEstablished,
    uint? UidValidity,
    uint? ScannedThroughUid,
    long? BaselineAtUnixTimeMilliseconds,
    int PendingCount,
    int BoundCount,
    string? BlockedCode,
    GalateaImapPollResult? LastPoll
);

public sealed partial class GalateaHostService {
    // Never calls GetSessionAsync or opens a network connection. In particular,
    // viewing this endpoint cannot establish the initial new-mail baseline.
    internal GalateaImapInboundStatusDto ReadImapInboundStatus(string characterId) {
        if (!TryGetCharacter(characterId, out _)) {
            throw new KeyNotFoundException("Character is not configured.");
        }
        bool configured = _imap.Accounts.TryGetValue(characterId, out var account);
        bool enabled = configured && _imap.Enabled && !_maintenanceMode && !IsStopping;
        GalateaImapCheckpointSnapshot? checkpoint = null;
        var queue = new GalateaImapInboxStatus(0, 0, false);
        if (_delegationSupervisor.TryGetObservableStore(characterId, out var store)) {
            if (configured) {
                checkpoint = store.ReadImapCheckpoint(
                    GalateaImapConfig.AccountReference(characterId, account!));
            }
            queue = store.ReadImapInboxStatus();
        }
        string? blockedCode = queue.HasQuarantined
            ? "IMAP_INBOX_QUARANTINED" : checkpoint?.BlockedCode;
        string state = blockedCode is not null ? "Blocked"
            : !enabled ? "Paused" : checkpoint is null ? "BaselinePending" : "Ready";
        return new(configured, enabled, state, checkpoint is not null,
            checkpoint?.UidValidity, checkpoint?.ScannedThroughUid,
            checkpoint?.BaselineAtUnixTimeMilliseconds, queue.PendingCount,
            queue.BoundCount, blockedCode, _imapPoller?.ReadLastPoll(characterId));
    }
}
