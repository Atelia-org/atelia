using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.Galatea.Server.Mailbox;
using Atelia.MemoPod;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaFreshAdmissionPlanTests {
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 6, 12, 34, 56, TimeSpan.FromHours(8));
    private static readonly GalateaSenderSnapshot Character = new("character", "alice", "Alice");
    private static readonly GalateaSenderSnapshot Player = new("player", "player", "Player");

    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 15)]
    [InlineData(2, 14)]
    public void ReceiptsReserveNoticeSlotsAndKeepExactFifoRevisions(int receiptCount, int expectedReplies) {
        GalateaReplyNoticeSnapshot[] available = Enumerable.Range(1, 17)
            .Select(index => Ready(index, "reply-" + index)).Reverse().ToArray();
        ActionReceiptDeliverySnapshot? mail = receiptCount == 2 ? Mail() : null;
        ActionReceiptDeliverySnapshot? note = receiptCount >= 1 ? Note() : null;
        GalateaFreshAdmissionPlan plan = GalateaFreshAdmissionPlan.ComposeReadyReply(
            Timestamp, Character, null, mail, note, available)!;

        Assert.Equal(16, plan.Notices.Count);
        Assert.Equal(expectedReplies, plan.ReplyMembers.Count);
        Assert.Equal(Enumerable.Range(1, expectedReplies).Select(index => Ready(index, "").NoticeId),
            plan.ReplyMembers.Select(member => member.NoticeId));
        Assert.Equal(Enumerable.Range(1, expectedReplies).Select(index => (long)index + 10),
            plan.ReplyMembers.Select(member => member.ExpectedRevision));
        Assert.All(plan.Notices.Take(receiptCount), notice => Assert.IsType<PlayerTurnNotice.ActionReceipt>(notice));
        if (receiptCount == 2) {
            Assert.IsType<MailReceiptBatch>(Assert.IsType<PlayerTurnNotice.ActionReceipt>(plan.Notices[0]).Batch);
            Assert.IsType<NoteReceiptBatch>(Assert.IsType<PlayerTurnNotice.ActionReceipt>(plan.Notices[1]).Batch);
        }
    }

    [Fact]
    public void FullPreviewsMustCoexistWithEarliestReplyBeforeTheyAreSelected() {
        string preview = ActionReceiptPreview.Create(string.Concat(Enumerable.Repeat("😀", 64)));
        ActionReceiptDeliverySnapshot mail = Mail(preview, itemCount: 64);
        ActionReceiptDeliverySnapshot note = Note(preview, itemCount: 16);
        GalateaReplyNoticeSnapshot earliest = Ready(1, new string('\u0001', 160_000));
        // Receipt-only full input fits, so selection must check the reply too.
        GalateaFreshAdmissionPlan receiptsOnly = GalateaFreshAdmissionPlan.Compose(
            new GalateaFreshInput.PlayerAction("p", Player), Timestamp, Character,
            mailReceipt: mail, noteReceipt: note);
        Assert.All(receiptsOnly.Notices.OfType<PlayerTurnNotice.ActionReceipt>(), receipt => Assert.True(receipt.Batch.HasPreviews));

        GalateaFreshAdmissionPlan plan = GalateaFreshAdmissionPlan.ComposeReadyReply(
            Timestamp, Character, null, mail, note, [earliest, Ready(2, "later")])!;

        Assert.Equal(2, plan.ReplyMembers.Count);
        Assert.All(plan.Notices.OfType<PlayerTurnNotice.ActionReceipt>(), receipt => {
            Assert.False(receipt.Batch.HasPreviews);
            ActionReceiptBatch frozen = receipt.Batch.Kind == "mail" ? mail.FrozenBatch! : note.FrozenBatch!;
            Assert.True(frozen.MatchesProjection(receipt.Batch));
        });
        Assert.True(mail.FrozenBatch!.HasPreviews);
        Assert.True(note.FrozenBatch!.HasPreviews);
        Assert.Equal(Timestamp, plan.PreliminaryObservation!.ExternalLocalTimestamp);
    }

    [Fact]
    public void ActualPlayerTextAndConnectionStateDeterminePrefixWithoutWorstCaseReservation() {
        GalateaReplyNoticeSnapshot[] available = Enumerable.Range(1, 4)
            .Select(index => Ready(index, new string('x', 256 * 1024))).ToArray();
        var state = new GalateaConnectionStateSnapshot(null, "default", "default", null,
            "默认配置", "默认配置");
        GalateaFreshAdmissionPlan shortPlan = GalateaFreshAdmissionPlan.Compose(
            new GalateaFreshInput.PlayerAction("short", Player), Timestamp, Character,
            state, Mail(), Note(), available);
        GalateaFreshAdmissionPlan escapedPlan = GalateaFreshAdmissionPlan.Compose(
            new GalateaFreshInput.PlayerAction(new string('\u0001', 64 * 1024), Player),
            Timestamp, Character, state, Mail(), Note(), available);

        Assert.Equal(3, shortPlan.ReplyMembers.Count);
        Assert.Equal(2, escapedPlan.ReplyMembers.Count);
        Assert.Same(state, shortPlan.ConnectionState);
        Assert.Equal(state, GalateaObservationContent.ReadConnectionState(shortPlan.PreliminaryInput));
    }

    [Fact]
    public void RecallCannotChangeFrozenReceiptProjectionOrReplyMembership() {
        GalateaFreshAdmissionPlan plan = GalateaFreshAdmissionPlan.Compose(
            new GalateaFreshInput.PlayerAction("p", Player), Timestamp, Character,
            mailReceipt: Mail(), noteReceipt: Note(), readyNotices: [Ready(1, "reply")]);
        var recall = new PlayerTurnRecall(new RecallEntry(RecallType.MemoExactText,
            GalateaMemoRecallSourceIdCodec.Format(MemoPodId.Parse("00000000000000000000000000000001"),
                MemoId.Parse("m1:00000002"))), "identity", "title", "body");

        Assert.True(plan.FitsRecalls([recall]));
        PlayerTurnObservation enriched = GalateaObservationContent.ReadPlayerTurn(plan.WithRecalls([recall]));
        Assert.Equal(plan.Notices.Select(GalateaObservationContent.NoticeJson).Select(value => System.Text.Json.JsonSerializer.Serialize(value)),
            enriched.Notices.Select(GalateaObservationContent.NoticeJson).Select(value => System.Text.Json.JsonSerializer.Serialize(value)));
        Assert.Single(plan.ReplyMembers);
        Assert.Empty(plan.PreliminaryObservation!.Recalls);
        Assert.Single(enriched.Recalls);
        Assert.Equal(Timestamp, enriched.ExternalLocalTimestamp);
    }

    [Fact]
    public void HeartbeatDoesNotClaimNewReadyAndInboundDoesNotEnrich() {
        GalateaReplyNoticeSnapshot[] ready = [Ready(1, "new reply")];
        GalateaFreshAdmissionPlan heartbeat = GalateaFreshAdmissionPlan.Compose(
            new GalateaFreshInput.HeartbeatActivation(new GalateaCharacterName("Alice"), 10),
            Timestamp, Character, mailReceipt: Mail(), noteReceipt: Note(), readyNotices: ready);
        Assert.Empty(heartbeat.ReplyMembers);
        Assert.Equal(2, heartbeat.Notices.Count);
        Assert.Equal(PlayerTurnObservationTriggerKind.HeartbeatActivation,
            heartbeat.PreliminaryObservation!.TriggerKind);

        var message = MailboxMessage.FromCanonicalEnvelope(new string('a', 32), "Player", "Alice", null, "mail");
        GalateaFreshAdmissionPlan inbound = GalateaFreshAdmissionPlan.Compose(
            new GalateaFreshInput.InboundMail(message, InjectedBy: Player), Timestamp, Character,
            mailReceipt: Mail(), noteReceipt: Note(), readyNotices: ready);
        Assert.Empty(inbound.Notices);
        Assert.Empty(inbound.ReplyMembers);
        Assert.Null(inbound.MailReceipt);
        Assert.Null(inbound.NoteReceipt);
        Assert.Null(inbound.PreliminaryObservation);
    }

    [Fact]
    public void EmptyReadyDoesNotManufactureReceiptOnlyReplyTurn() {
        Assert.Null(GalateaFreshAdmissionPlan.ComposeReadyReply(Timestamp, Character,
            null, Mail(), Note(), []));
    }

    [Fact]
    public void UnrenderableEarliestReplyDoesNotSkipToLaterReply() {
        GalateaTurnException failure = Assert.Throws<GalateaTurnException>(() =>
            GalateaFreshAdmissionPlan.ComposeReadyReply(Timestamp, Character,
                null, Mail(), Note(), [Ready(1, new string('\u0001', 256 * 1024)), Ready(2, "small")]));
        Assert.Equal("fresh-input-budget-exceeded", failure.FailureReason);
    }

    private static GalateaReplyNoticeSnapshot Ready(int ordinal, string body) => new(
        Dispatch(ordinal), Dispatch(ordinal), GalateaReplyNoticeKind.Reply, body,
        null, null, ordinal, GalateaReplyNoticeState.Ready, null, ordinal + 10,
        "semantic-notice-v1", new GalateaSenderSnapshot("delegate", "codex", "Codex"));

    private static ActionReceiptDeliverySnapshot Mail(string preview = "mail", int itemCount = 1) => Pending(
        new MailReceiptBatch(Address(1), Enumerable.Range(1, itemCount).Select(index =>
            new MailReceiptItem(Dispatch(index), "accepted", preview, preview)).ToArray()), created: 99);

    private static ActionReceiptDeliverySnapshot Note(string preview = "note", int itemCount = 1) => Pending(
        new NoteReceiptBatch(Address(2), MemoPodId.Parse("00000000000000000000000000000001"),
            Enumerable.Range(1, itemCount).Select(index =>
                new NoteReceiptItem(MemoId.Parse($"m1:{index:x8}"), preview)).ToArray()), created: 1);

    private static ActionReceiptDeliverySnapshot Pending(ActionReceiptBatch batch, long created) => new(
        batch.SourceActionAddress, ActionReceiptDeliveryState.Pending, created, 0, batch,
        null, null, null);
    private static string Dispatch(int ordinal) => "gd1-" + ordinal.ToString("x64");
    private static string Address(int value) => $"ej1:{value:x16}0000000100000000";
}
