using System.Text;
using System.Text.Json;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.MemoPod;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaSemanticMemoryInputTests {
    private static readonly DateTimeOffset Time = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly GalateaSenderSnapshot Character = new("character", "alice", "Alice");

    [Fact]
    public void HeartbeatSnapshotWritesV5AndDrivesMarkdownAndRecallEvidence() {
        SessionInputContent input = GalateaObservationContent.Create(
            new GalateaFreshInput.HeartbeatActivation(new GalateaCharacterName("Alice"), 7),
            Time, Character, connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"));

        Assert.Equal(GalateaObservationContent.V5SchemaId, input.SchemaId);
        Assert.Equal(7, input.JsonValue.GetProperty("action").GetProperty("externalIntervalMinutes").GetInt32());
        PlayerTurnObservation observed = GalateaObservationContent.ReadPlayerTurn(input);
        Assert.Equal(7, observed.HeartbeatIntervalMinutes);
        Assert.Contains("7分钟", PlayerTurnObservationEnvelope.FormatForDisplay(observed), StringComparison.Ordinal);
        string wrapped = PlayerTurnObservationEnvelope.Wrap(observed);
        Assert.True(PlayerTurnObservationEnvelope.TryUnwrap(wrapped, out PlayerTurnObservation reopened));
        Assert.Equal(7, reopened.HeartbeatIntervalMinutes);

        string queryText = GalateaMemoRecallQueryRenderer.Render(
            new GalateaCharacterName("Alice"), observed,
            new GalateaPlayerTurnRecallContext(RecallBarrier.Empty, CharacterNoteOriginBarrier.Empty), input);
        using JsonDocument query = JsonDocument.Parse(queryText);
        Assert.Equal(7, query.RootElement.GetProperty("currentTurn").GetProperty("trigger")
            .GetProperty("externalIntervalMinutes").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrozenReceiptRetainsEverySavedIdAndOnlyBoundedPreviews(bool large) {
        string source = EventAddressTextCodec.Format(new Atelia.EventJournal.EventAddress(Atelia.Data.SizedPtr.Create(4, 4), 1, Atelia.EventJournal.AddressHint.None));
        CharacterNoteAppliedMemo[] memos = Enumerable.Range(0, large ? 16 : 1).Select(i =>
            new CharacterNoteAppliedMemo(source, i, CharacterNoteDefaultPodV1.PodId,
                MemoId.Parse("m1:" + (i + 1).ToString("x8")), new string(large ? '\u0001' : 'x', large ? 16 * 1024 : 128))).ToArray();
        var batch = new NoteReceiptBatch(source, CharacterNoteDefaultPodV1.PodId,
            memos.Select(m => new NoteReceiptItem(m.MemoId, ActionReceiptPreview.Create(m.ExactText))).ToArray());
        var selected = new PlayerTurnNotice.ActionReceipt(batch);
        Assert.Equal(memos.Select(m => m.MemoId), batch.Items.Select(item => item.MemoId));
        Assert.All(batch.Items, item => Assert.True(item.Preview!.EnumerateRunes().Count() <= 56));
        SessionInputContent input = GalateaObservationContent.Create(new GalateaFreshInput.HeartbeatActivation(new GalateaCharacterName("Alice"), 10), Time, Character, [selected], connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"));
        PlayerTurnNotice.ActionReceipt reopened = Assert.IsType<PlayerTurnNotice.ActionReceipt>(
            Assert.Single(GalateaObservationContent.ReadPlayerTurn(input).Notices));
        Assert.Equal(batch, reopened.Batch);
    }

    [Fact]
    public void TypedInputPreservesSourcesAndUiEnrichmentWhileUndoRestoresOnlyAction() {
        PlayerTurnRecall gist = PlayerTurnRecall.FromText(new RecallEntry(RecallType.MemoGist, "memory-source"), "revision-17", "remember the garden");
        var reply = new PlayerTurnNotice.Reply("remote original", new("delegate", "codex", "Codex"), "dispatch", "thread", "turn", "notice");
        SessionInputContent input = GalateaObservationContent.Create(new GalateaFreshInput.PlayerAction("open the door", GalateaDelegateTestConfiguration.PlayerSender), Time, Character, [reply], [gist], connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"));
        byte[] before = input.ToUtf8Json();
        PlayerTurnObservation observed = GalateaObservationContent.ReadPlayerTurn(input);
        Assert.Equal("revision-17", Assert.Single(observed.Recalls).SourceVersion);
        Assert.Equal("notice", Assert.IsType<PlayerTurnNotice.Reply>(Assert.Single(observed.Notices)).NoticeId);
        Assert.True(PlayerTurnObservationClassifier.TryProject(input, out var projection));
        Assert.Equal("open the door", projection.RestorablePlayerText);
        Assert.Contains("remote original", projection.DisplayText);
        Assert.Contains("remember the garden", projection.DisplayText);
        _ = GalateaInputProjector.Instance.Project(input);
        Assert.Equal(before, input.ToUtf8Json());
        Assert.Throws<NotSupportedException>(() => GalateaRecallBarrierBuilder.BuildFromProviderVisibleMessages([
            new SessionInputObservationMessage(SessionInputContent.Structured("unknown.host.schema", input.JsonValue))
        ]));
    }

    [Fact]
    public void RecallHelperReceivesTheCapturedPlayerAndReplyProvenance() {
        var reply = new PlayerTurnNotice.Reply("remote evidence", new("delegate", "codex", "Codex"), "dispatch-1", "thread-1", "turn-1", "notice-1");
        SessionInputContent input = GalateaObservationContent.Create(new GalateaFreshInput.PlayerAction("recall", GalateaDelegateTestConfiguration.PlayerSender),
            Time, Character, [reply], connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"));
        string queryText = GalateaMemoRecallQueryRenderer.Render(new GalateaCharacterName("Alice"),
            GalateaObservationContent.ReadPlayerTurn(input), new GalateaPlayerTurnRecallContext(RecallBarrier.Empty, CharacterNoteOriginBarrier.Empty), input);
        using JsonDocument query = JsonDocument.Parse(queryText);
        JsonElement turn = query.RootElement.GetProperty("currentTurn");
        Assert.Equal(GalateaDelegateTestConfiguration.PlayerSender.Id, turn.GetProperty("sender").GetProperty("id").GetString());
        JsonElement notice = Assert.Single(turn.GetProperty("externalNotices").EnumerateArray());
        Assert.Equal("codex", notice.GetProperty("sender").GetProperty("id").GetString());
        Assert.Equal("dispatch-1", notice.GetProperty("dispatchId").GetString());
        Assert.Equal("thread-1", notice.GetProperty("threadId").GetString());
        Assert.Equal("turn-1", notice.GetProperty("turnId").GetString());
        Assert.Equal("notice-1", notice.GetProperty("noticeId").GetString());
        Assert.Equal("remote evidence", notice.GetProperty("body").GetString());
    }

    [Fact]
    public void NewInputRejectsUnattributedLegacyNoticeButReadsExplicitDurableLegacyReceipt() {
        var fresh = new GalateaFreshInput.PlayerAction("continue", GalateaDelegateTestConfiguration.PlayerSender);
        Assert.Throws<InvalidDataException>(() => GalateaObservationContent.Create(fresh, Time, Character,
            [new PlayerTurnNotice.NoteSaveReceipt("fresh rendered text is not content")], connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test")));
        SessionInputContent imported = GalateaObservationContent.Create(fresh, Time, Character,
            [PlayerTurnNotice.NoteSaveReceipt.FromLegacyDurable("unchanged historical wording")], connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"));
        Assert.Equal("legacy-note-save-receipt", imported.JsonValue.GetProperty("notices")[0].GetProperty("kind").GetString());
        Assert.Equal("unchanged historical wording", Assert.Single(GalateaObservationContent.ReadPlayerTurn(imported).Notices).Body);
    }
}
