using Atelia.Galatea.Prompts;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

/// <summary>
/// Process-local admission facts. Content is chosen once under TurnLock;
/// later setup reconciliation and recall do not read or reselect deliveries.
/// </summary>
internal sealed class GalateaFreshAdmissionPlan {
    private GalateaFreshAdmissionPlan(
        GalateaFreshInput freshInput,
        DateTimeOffset timestamp,
        GalateaSenderSnapshot character,
        GalateaConnectionStateSnapshot? connectionState,
        ActionReceiptDeliverySnapshot? mailReceipt,
        ActionReceiptDeliverySnapshot? noteReceipt,
        IReadOnlyList<GalateaReplyLeaseMember> replyMembers,
        IReadOnlyList<PlayerTurnNotice> notices,
        SessionInputContent preliminaryInput
    ) {
        FreshInput = freshInput;
        Timestamp = timestamp;
        Character = character;
        ConnectionState = connectionState;
        MailReceipt = mailReceipt;
        NoteReceipt = noteReceipt;
        ReplyMembers = Array.AsReadOnly(replyMembers.ToArray());
        Notices = Array.AsReadOnly(notices.ToArray());
        PreliminaryInput = preliminaryInput;
    }

    internal GalateaFreshInput FreshInput { get; }
    internal DateTimeOffset Timestamp { get; }
    internal GalateaSenderSnapshot Character { get; }
    internal GalateaConnectionStateSnapshot? ConnectionState { get; }
    internal ActionReceiptDeliverySnapshot? MailReceipt { get; }
    internal ActionReceiptDeliverySnapshot? NoteReceipt { get; }
    internal IReadOnlyList<GalateaReplyLeaseMember> ReplyMembers { get; }
    internal IReadOnlyList<PlayerTurnNotice> Notices { get; }
    internal SessionInputContent PreliminaryInput { get; }

    internal PlayerTurnObservation? PreliminaryObservation =>
        FreshInput is GalateaFreshInput.InboundMail
            ? null : GalateaObservationContent.ReadPlayerTurn(PreliminaryInput);

    internal static GalateaFreshAdmissionPlan Compose(
        GalateaFreshInput fresh,
        DateTimeOffset timestamp,
        GalateaSenderSnapshot character,
        GalateaConnectionStateSnapshot? connectionState = null,
        ActionReceiptDeliverySnapshot? mailReceipt = null,
        ActionReceiptDeliverySnapshot? noteReceipt = null,
        IReadOnlyList<GalateaReplyNoticeSnapshot>? readyNotices = null
    ) {
        ArgumentNullException.ThrowIfNull(fresh);
        if (fresh is GalateaFreshInput.DelegateReply) {
            throw new ArgumentException("Use ComposeReadyReply to select a fresh reply trigger.", nameof(fresh));
        }
        if (fresh is GalateaFreshInput.PlayerAction { Notices.Count: > 0 }) {
            throw new ArgumentException("The fresh composer owns admission notice selection.", nameof(fresh));
        }
        bool acceptsReplies = fresh is GalateaFreshInput.PlayerAction;
        if (fresh is GalateaFreshInput.InboundMail) {
            // Mail keeps its existing contract: no new enrichment or reply lease.
            mailReceipt = null;
            noteReceipt = null;
        }
        return ComposeCore(
            notices => fresh is GalateaFreshInput.PlayerAction player
                ? new GalateaFreshInput.PlayerAction(player.Text, player.Sender, notices)
                : fresh,
            timestamp, character, connectionState, mailReceipt, noteReceipt,
            acceptsReplies ? readyNotices ?? [] : [], requiresReply: false)!;
    }

    internal static GalateaFreshAdmissionPlan? ComposeReadyReply(
        DateTimeOffset timestamp,
        GalateaSenderSnapshot character,
        GalateaConnectionStateSnapshot? connectionState,
        ActionReceiptDeliverySnapshot? mailReceipt,
        ActionReceiptDeliverySnapshot? noteReceipt,
        IReadOnlyList<GalateaReplyNoticeSnapshot> readyNotices
    ) => ComposeCore(
        notices => new GalateaFreshInput.DelegateReply(notices),
        timestamp, character, connectionState, mailReceipt, noteReceipt,
        readyNotices, requiresReply: true);

    private static GalateaFreshAdmissionPlan? ComposeCore(
        Func<IReadOnlyList<PlayerTurnNotice>, GalateaFreshInput> createFresh,
        DateTimeOffset timestamp,
        GalateaSenderSnapshot character,
        GalateaConnectionStateSnapshot? connectionState,
        ActionReceiptDeliverySnapshot? mailReceipt,
        ActionReceiptDeliverySnapshot? noteReceipt,
        IReadOnlyList<GalateaReplyNoticeSnapshot> readyNotices,
        bool requiresReply
    ) {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(readyNotices);
        GalateaReplyNoticeSnapshot[] available = readyNotices
            .Where(static notice => notice.State == GalateaReplyNoticeState.Ready)
            .OrderBy(static notice => notice.CompletionSequence)
            .ToArray();
        if (requiresReply && available.Length == 0) { return null; }
        RequirePending(mailReceipt, "mail");
        RequirePending(noteReceipt, "note-save");
        ActionReceiptDeliverySnapshot[] receipts = new[] { mailReceipt, noteReceipt }
            .OfType<ActionReceiptDeliverySnapshot>().ToArray();

        // Full previews are preferred only when the earliest Ready notice can
        // coexist. Otherwise retry the entire selected receipt set as all-null.
        foreach (bool compact in receipts.Length == 0 ? new[] { false } : [false, true]) {
            PlayerTurnNotice[] receiptNotices = receipts.Select(receipt =>
                (PlayerTurnNotice)new PlayerTurnNotice.ActionReceipt(compact
                    ? receipt.FrozenBatch!.Compact() : receipt.FrozenBatch!)).ToArray();
            var selected = new List<GalateaReplyNoticeSnapshot>();
            int requiredCount = available.Length == 0 ? 0 : 1;
            PlayerTurnNotice[] notices = [.. receiptNotices,
                .. available.Take(requiredCount).Select(GalateaDurableNoticeContent.Project)];
            if (!TryCreate(createFresh, timestamp, character, connectionState, notices,
                    out GalateaFreshInput? fresh, out SessionInputContent? input)) { continue; }
            selected.AddRange(available.Take(requiredCount));
            foreach (GalateaReplyNoticeSnapshot next in available.Skip(requiredCount)) {
                if (notices.Length == PlayerTurnObservationEnvelope.MaximumNoticeCount) { break; }
                PlayerTurnNotice[] proposed = [.. notices, GalateaDurableNoticeContent.Project(next)];
                if (!TryCreate(createFresh, timestamp, character, connectionState,
                        proposed, out GalateaFreshInput? proposedFresh,
                        out SessionInputContent? proposedInput)) { break; }
                selected.Add(next);
                notices = proposed;
                fresh = proposedFresh;
                input = proposedInput;
            }
            return new GalateaFreshAdmissionPlan(fresh!, timestamp, character,
                connectionState, mailReceipt, noteReceipt,
                selected.Select(static notice => new GalateaReplyLeaseMember(
                    notice.NoticeId, notice.Revision)).ToArray(), notices, input!);
        }
        throw new GalateaTurnException(
            "The frozen fresh input and earliest Ready reply cannot fit the Observation budget.",
            "fresh-input-budget-exceeded");
    }

    internal SessionInputContent WithRecalls(IReadOnlyList<PlayerTurnRecall> recalls) =>
        GalateaObservationContent.Create(FreshInput, Timestamp, Character,
            Notices, recalls, ConnectionState);

    internal bool FitsRecalls(IReadOnlyList<PlayerTurnRecall> recalls) {
        try { _ = WithRecalls(recalls); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static bool TryCreate(
        Func<IReadOnlyList<PlayerTurnNotice>, GalateaFreshInput> createFresh, DateTimeOffset timestamp,
        GalateaSenderSnapshot character, GalateaConnectionStateSnapshot? connectionState,
        IReadOnlyList<PlayerTurnNotice> notices, out GalateaFreshInput? fresh,
        out SessionInputContent? input
    ) {
        fresh = null;
        try {
            fresh = createFresh(notices);
            input = GalateaObservationContent.Create(fresh, timestamp,
                character, notices, [], connectionState);
            return true;
        }
        catch (ArgumentOutOfRangeException) { input = null; return false; }
    }

    private static void RequirePending(ActionReceiptDeliverySnapshot? receipt, string kind) {
        if (receipt is null) { return; }
        if (receipt.State != ActionReceiptDeliveryState.Pending
            || receipt.FrozenBatch is not { } batch || batch.Kind != kind
            || batch.SourceActionAddress != receipt.SourceActionAddress) {
            throw new InvalidDataException("Fresh admission requires the exact Pending receipt batch.");
        }
        batch.RequireFrozen();
    }
}
