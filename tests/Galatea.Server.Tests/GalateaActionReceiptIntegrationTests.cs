using System.Security.Cryptography;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaActionReceiptIntegrationTests {
    private static GalateaSenderSnapshot Character { get; } = new("character", "user", "Galatea");

    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 15)]
    [InlineData(2, 14)]
    public async Task SixteenReadyReplies_ClaimsActualFifoPrefixBesideSelectedReceiptBatches(int receiptCount, int expectedReplies) {
        using var fixture = await ActionReceiptDeliveryFixture.CreateAsync();
        for (int index = 0; index < 16; index++) { ProduceReadyReply(fixture.Mail, index, "reply-" + index); }
        GalateaReplyNoticeSnapshot[] ready = Ready(fixture.Mail);
        GalateaFreshAdmissionPlan plan = GalateaFreshAdmissionPlan.Compose(
            new GalateaFreshInput.PlayerAction("continue", GalateaDelegateTestConfiguration.PlayerSender),
            ActionReceiptDeliveryFixture.Timestamp, Character,
            mailReceipt: receiptCount == 2 ? fixture.MailPending : null,
            noteReceipt: receiptCount >= 1 ? fixture.NotePending : null, readyNotices: ready, connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"));
        var reconciler = new GalateaDurableReplyLeaseReconciler(fixture.Mail);
        GalateaDurableReplyLease lease = Assert.IsType<GalateaDurableReplyLeaseBeginResult.Created>(
            reconciler.BeginMembership("continue", plan.ReplyMembers)).Lease;
        Assert.Equal(16, plan.Notices.Count);
        Assert.Equal(receiptCount, plan.Notices.Take(receiptCount).OfType<PlayerTurnNotice.ActionReceipt>().Count());
        Assert.Equal(ready.Take(expectedReplies).Select(row => row.NoticeId), plan.ReplyMembers.Select(member => member.NoticeId));
        Assert.Equal(ready.Skip(expectedReplies).Select(row => row.NoticeId), Ready(fixture.Mail).Select(row => row.NoticeId));
        Assert.Equal(expectedReplies, lease.ReadNotices().Count);
        SessionInputContent input = plan.PreliminaryInput;
        EventAddress head = fixture.Engine.ReadCurrentHead()!.Value;
        _ = lease.BindObservationBase(fixture.Engine, head, input);
        if (plan.MailReceipt is { } mail) { ActionReceiptDelivery.Bind(fixture.Mail, fixture.Engine, mail, head, input); }
        if (plan.NoteReceipt is { } note) { ActionReceiptDelivery.Bind(fixture.Memory.ReceiptDeliveryStore, fixture.Engine, note, head, input); }
        EventAddress appended = fixture.Engine.AppendObservation(input);
        fixture.Engine.AppendImportedAgentAction(new ActionMessage([new ActionBlock.Text("received")]), ActionReceiptDeliveryFixture.Invocation);
        fixture.ReconcileBoth();
        Assert.IsType<GalateaDurableReplyLeaseReconcileResult.Consumed>(reconciler.ReconcileActiveLease(fixture.Engine));
        Assert.Equal(receiptCount, GalateaObservationContent.ReadPlayerTurn(input).Notices.OfType<PlayerTurnNotice.ActionReceipt>().Count());
        if (receiptCount >= 1) { Assert.Equal(EventAddressTextCodec.Format(appended), fixture.NoteExact.ObservationAddress); }
        if (receiptCount == 2) { Assert.Equal(EventAddressTextCodec.Format(appended), fixture.MailExact.ObservationAddress); }
        Assert.Equal(16 - expectedReplies, Ready(fixture.Mail).Length);
    }

    [Fact]
    public async Task LaterReadyReply_DoesNotEnterFrozenPlanOrClaimMembership() {
        using var fixture = await ActionReceiptDeliveryFixture.CreateAsync();
        ProduceReadyReply(fixture.Mail, 0, "first reply");
        GalateaFreshAdmissionPlan plan = Assert.IsType<GalateaFreshAdmissionPlan>(GalateaFreshAdmissionPlan.ComposeReadyReply(
            ActionReceiptDeliveryFixture.Timestamp, Character, new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"), fixture.MailPending, fixture.NotePending, Ready(fixture.Mail)));
        SessionInputContent frozen = plan.PreliminaryInput;
        ProduceReadyReply(fixture.Mail, 1, "LATE_READY_MUST_WAIT");
        GalateaDurableReplyLease lease = Assert.IsType<GalateaDurableReplyLeaseBeginResult.Created>(
            new GalateaDurableReplyLeaseReconciler(fixture.Mail).BeginMembership(
                PlayerTurnObservationEnvelope.DelegateReplyLeasePlayerTextDiscriminator, plan.ReplyMembers)).Lease;
        Assert.Equal("first reply", Assert.IsType<PlayerTurnNotice.Reply>(Assert.Single(lease.ReadNotices())).Body);
        Assert.Equal("LATE_READY_MUST_WAIT", Assert.Single(Ready(fixture.Mail)).Body);
        Assert.Equal(frozen, plan.WithRecalls([]));
        Assert.DoesNotContain("LATE_READY_MUST_WAIT", GalateaInputProjector.Instance.Project(frozen), StringComparison.Ordinal);
        lease.RollbackBeforeEffect();
    }

    [Fact]
    public async Task HeartbeatReceiptsDoNotClaimReadyAndInboundMailHasNoEnrichment() {
        using var fixture = await ActionReceiptDeliveryFixture.CreateAsync();
        ProduceReadyReply(fixture.Mail, 0, "ready reply");
        GalateaFreshAdmissionPlan heartbeat = GalateaFreshAdmissionPlan.Compose(
            new GalateaFreshInput.HeartbeatActivation(new GalateaCharacterName("Galatea"), 10),
            ActionReceiptDeliveryFixture.Timestamp, Character,
            mailReceipt: fixture.MailPending, noteReceipt: fixture.NotePending, readyNotices: Ready(fixture.Mail), connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"));
        Assert.Empty(heartbeat.ReplyMembers);
        Assert.Equal(2, heartbeat.Notices.OfType<PlayerTurnNotice.ActionReceipt>().Count());
        Assert.Equal(PlayerTurnObservationTriggerKind.HeartbeatActivation, heartbeat.PreliminaryObservation!.TriggerKind);
        Assert.Single(Ready(fixture.Mail));
        Assert.Null(fixture.Mail.ReadSnapshot().ActiveLease);
        Assert.Null(GalateaFreshAdmissionPlan.ComposeReadyReply(
            ActionReceiptDeliveryFixture.Timestamp, Character, new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"), fixture.MailPending, fixture.NotePending, []));

        MailboxMessage message = MailboxMessage.CreateInbound(new GalateaCharacterName("Galatea"), "visitor", null, "incoming body");
        GalateaFreshAdmissionPlan inbound = GalateaFreshAdmissionPlan.Compose(new GalateaFreshInput.InboundMail(message,
                new GalateaInboundMailOrigin.PlayerInjection(GalateaDelegateTestConfiguration.PlayerSender)),
            ActionReceiptDeliveryFixture.Timestamp, Character,
            mailReceipt: fixture.MailPending, noteReceipt: fixture.NotePending, readyNotices: Ready(fixture.Mail), connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"));
        Assert.Empty(inbound.Notices);
        Assert.Empty(inbound.ReplyMembers);
        Assert.Null(inbound.MailReceipt);
        Assert.Null(inbound.NoteReceipt);
        Assert.Null(inbound.PreliminaryObservation);
        Assert.Equal("incoming body", GalateaObservationContent.ReadMailboxContent(inbound.PreliminaryInput).Body);
        Assert.Single(Ready(fixture.Mail));
    }

    [Fact]
    public async Task CurrentPreviewProjectionOmitsMiddleAndKeepsFullBusinessBodies() {
        using var fixture = await ActionReceiptDeliveryFixture.CreateAsync();
        GalateaFreshAdmissionPlan plan = GalateaFreshAdmissionPlan.Compose(
            new GalateaFreshInput.PlayerAction("continue", GalateaDelegateTestConfiguration.PlayerSender),
            ActionReceiptDeliveryFixture.Timestamp, Character,
            mailReceipt: fixture.MailPending, noteReceipt: fixture.NotePending, connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"));
        string projected = GalateaInputProjector.Instance.Project(plan.PreliminaryInput);
        Assert.DoesNotContain(ActionReceiptDeliveryFixture.NoteMiddle, projected, StringComparison.Ordinal);
        Assert.DoesNotContain(ActionReceiptDeliveryFixture.MailMiddle, projected, StringComparison.Ordinal);
        Assert.Contains(ActionReceiptPreview.Create(ActionReceiptDeliveryFixture.NoteText), projected, StringComparison.Ordinal);
        Assert.Contains(ActionReceiptPreview.Create(ActionReceiptDeliveryFixture.MailText), projected, StringComparison.Ordinal);
        Assert.Equal(ActionReceiptDeliveryFixture.NoteText, fixture.SavedNote.ExactText);
        Assert.Equal(ActionReceiptDeliveryFixture.MailText, Assert.Single(fixture.Mail.ReadSnapshot().Mails).Body);
    }

    internal static GalateaReplyNoticeSnapshot[] Ready(GalateaDelegationSqliteStore store) => store.ReadSnapshot().Notices
        .Where(row => row.State == GalateaReplyNoticeState.Ready).OrderBy(row => row.CompletionSequence).ToArray();

    internal static void ProduceReadyReply(GalateaDelegationSqliteStore store, int ordinal, string body) {
        string visible = "Synthetic task " + ordinal;
        GalateaDelegationCaptureResult capture = store.CaptureActionBatch(new(
            $"ej1:{1000 + ordinal:x16}0000000100000000",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(visible))).ToLowerInvariant(),
            Encoding.UTF8.GetByteCount(visible), "fixture-mail-v1",
            [new SendMailIntent("Codex", null, visible, null, "evidence")],
            GalateaDelegationTestInputs.Sender(store, "Galatea")));
        string dispatch = Assert.Single(capture.DispatchIds);
        GalateaDelegationStateSnapshot snapshot = store.ReadSnapshot();
        if (snapshot.Route.State == GalateaDelegationRouteState.Unbound) {
            GalateaRouteBindingSnapshot binding = store.BeginThreadBinding("binding", snapshot.Route.Revision,
                dispatch, snapshot.Mails.Single(row => row.DispatchId == dispatch).Revision);
            store.CompleteThreadBinding(binding.BindingOperationId!, "thread", binding.Revision);
            snapshot = store.ReadSnapshot();
        }
        GalateaOutboundMailSnapshot started = store.StartQueuedMail(dispatch,
            snapshot.Mails.Single(row => row.DispatchId == dispatch).Revision, snapshot.Route.Revision,
            GalateaDelegationTestInputs.Commitment(store, dispatch));
        store.RecordCompletedMail(dispatch, started.Revision, "thread", "turn-" + ordinal, body);
    }
}
