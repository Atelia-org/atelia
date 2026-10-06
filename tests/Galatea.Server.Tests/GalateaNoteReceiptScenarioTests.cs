using System.Net;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.SessionJournal;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Galatea.Server.Tests;

[Trait("Category", "GalateaLab")]
public sealed class GalateaNoteReceiptScenarioTests(ITestOutputHelper output) {
    [Fact]
    public async Task HostedNoteReceiptAcrossColdReopensSavesOnceAndDeliversOnceWithoutPlayer() {
        var clock = new GalateaLabClock();
        var firstFactory = new GalateaNoteReceiptFixture.Factory(epoch: 1);
        await using var lab = GalateaScenarioLab.Create("hosted-note-receipt", firstFactory,
            connections: [GalateaNoteReceiptFixture.MainConnection, GalateaNoteReceiptFixture.HelperConnection],
            timeProvider: clock, reportArtifact: output.WriteLine,
            characterNoteExtractorConnectionId: GalateaNoteReceiptFixture.HelperConnection.Id,
            autonomyCharacterIds: ["alice"], enableServerAgentHostedService: true);

        var first = await GalateaNoteReceiptFixture.StartEpochAsync(lab, clock, firstFactory);
        await GalateaNoteReceiptFixture.AdvanceHeartbeatAsync(first);
        var pending = await GalateaNoteReceiptFixture.ReadStateAsync(first);
        Assert.Single(pending.Turns);
        Assert.Equal(GalateaNoteReceiptFixture.NoteText, pending.Note.ExactText);
        Assert.Equal(ActionReceiptDeliveryState.Pending, pending.Receipt.State);
        Assert.Equal(EventAddressTextCodec.Format(pending.Turns[0].RequireTerminalAction().Address), pending.Receipt.SourceActionAddress);
        Assert.Equal(pending.Receipt.CreatedRevision, pending.Receipt.StateRevision);
        Assert.Equal(1, firstFactory.SaveIntents);
        Assert.Equal(1, firstFactory.DerivedCalls);
        await lab.StopAsync();

        // Missed downtime ticks are not replayed. A new host gets a fresh full
        // cadence interval, using freshly constructed external dependencies.
        clock.Advance(TimeSpan.FromHours(1));
        var secondFactory = new GalateaNoteReceiptFixture.Factory(epoch: 2);
        await lab.ReopenAsync(secondFactory);
        var second = await GalateaNoteReceiptFixture.StartEpochAsync(lab, clock, secondFactory);
        var reopenedPending = await GalateaNoteReceiptFixture.ReadStateAsync(second);
        Assert.Equal(pending.Receipt, reopenedPending.Receipt);
        Assert.Equal(pending.Note, reopenedPending.Note);
        await GalateaNoteReceiptFixture.AdvanceHeartbeatAsync(second);
        var delivered = await GalateaNoteReceiptFixture.ReadStateAsync(second);
        Assert.Equal(2, delivered.Turns.Count);
        Assert.Equal(pending.Note, delivered.Note);
        Assert.Equal(pending.PodIdentity, delivered.PodIdentity);
        Assert.Equal(pending.Receipt.SourceActionAddress, delivered.Receipt.SourceActionAddress);
        Assert.Equal(pending.Receipt.CreatedRevision, delivered.Receipt.CreatedRevision);
        Assert.Null(delivered.Receipt.FrozenBatch);
        Assert.Equal(ActionReceiptDeliveryState.Delivered, delivered.Receipt.State);
        Assert.True(delivered.Receipt.StateRevision > pending.Receipt.StateRevision);
        Assert.Null(delivered.Receipt.BoundInput);
        Assert.Equal(EventAddressTextCodec.Format(delivered.Turns[0].ObservationAddress), delivered.Receipt.ObservationAddress);
        Assert.Equal(GalateaInputProjector.Instance.Project(delivered.Turns[0].ObservationContent), secondFactory.CurrentObservation);
        PlayerTurnObservation providerObservation = GalateaObservationContent.ReadPlayerTurn(delivered.Turns[0].ObservationContent);
        var received = Assert.Single(providerObservation.Notices.OfType<PlayerTurnNotice.ActionReceipt>());
        var batch = Assert.IsType<NoteReceiptBatch>(received.Batch);
        Assert.Equal(pending.Receipt.FrozenBatch, batch);
        Assert.Equal(ActionReceiptPreview.Create(GalateaNoteReceiptFixture.NoteText), Assert.Single(batch.Items).Preview);
        Assert.DoesNotContain(GalateaNoteReceiptFixture.UniqueMiddle, secondFactory.CurrentObservation!, StringComparison.Ordinal);
        Assert.Equal(0, secondFactory.SaveIntents);
        Assert.Equal(0, secondFactory.DerivedCalls);
        await lab.StopAsync();

        clock.Advance(TimeSpan.FromHours(1));
        var thirdFactory = new GalateaNoteReceiptFixture.Factory(epoch: 3);
        await lab.ReopenAsync(thirdFactory);
        var third = await GalateaNoteReceiptFixture.StartEpochAsync(lab, clock, thirdFactory);
        Assert.Equal(delivered.Receipt, (await GalateaNoteReceiptFixture.ReadStateAsync(third)).Receipt);
        // The real Host must attach a ledger containing a Delivered
        // receipt before the browser can read its current turn.
        using (HttpClient http = lab.Host.CreateClient()) {
            using HttpResponseMessage login = await GalateaTestHost.LoginAsync(http);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            using HttpResponseMessage currentResponse = await http.GetAsync("/api/v1/characters/alice/chat/turns/current");
            Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
        }
        await GalateaNoteReceiptFixture.AdvanceHeartbeatAsync(third);
        var final = await GalateaNoteReceiptFixture.ReadStateAsync(third);
        Assert.Equal(3, final.Turns.Count);
        Assert.Equal(delivered.Receipt, final.Receipt); // Includes StateRevision.
        Assert.Equal(pending.Note, final.Note); // Includes MemoId and ExactText.
        Assert.Equal(pending.PodIdentity, final.PodIdentity);
        Assert.Equal(GalateaInputProjector.Instance.Project(final.Turns[0].ObservationContent), thirdFactory.CurrentObservation);
        PlayerTurnObservation current = GalateaObservationContent.ReadPlayerTurn(final.Turns[0].ObservationContent);
        Assert.Empty(current.Notices.OfType<PlayerTurnNotice.ActionReceipt>());
        int receipts = 0;
        foreach (var turn in final.Turns) {
            PlayerTurnObservation observation = GalateaObservationContent.ReadPlayerTurn(turn.ObservationContent);
            Assert.Equal(PlayerTurnObservationTriggerKind.HeartbeatActivation, observation.TriggerKind);
            Assert.Empty(observation.Recalls); // Memo recall is explicitly out of scope.
            receipts += observation.Notices.OfType<PlayerTurnNotice.ActionReceipt>().Count();
        }
        Assert.Equal(1, receipts);
        Assert.Equal(1, firstFactory.MainCalls);
        Assert.Equal(1, secondFactory.MainCalls);
        Assert.Equal(1, thirdFactory.MainCalls);
        Assert.Equal(1, firstFactory.ExtractorCalls);
        Assert.Equal(1, secondFactory.ExtractorCalls);
        Assert.Equal(1, thirdFactory.ExtractorCalls);
        Assert.Equal(2, firstFactory.ExtractorRounds);
        Assert.Equal(1, secondFactory.ExtractorRounds);
        Assert.Equal(1, thirdFactory.ExtractorRounds);
        Assert.Equal(0, thirdFactory.SaveIntents);
        Assert.Equal(0, thirdFactory.DerivedCalls);
        await lab.CompleteAsync();
    }
}
