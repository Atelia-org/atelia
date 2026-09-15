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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReceiptSelectionRetainsEverySavedIdAndChoosesWholeTextsOnly(bool large) {
        string source = EventAddressTextCodec.Format(new Atelia.EventJournal.EventAddress(Atelia.Data.SizedPtr.Create(4, 4), 1, Atelia.EventJournal.AddressHint.None));
        CharacterNoteAppliedMemo[] memos = Enumerable.Range(0, large ? 16 : 1).Select(i =>
            new CharacterNoteAppliedMemo(source, i, CharacterNoteDefaultPodV1.PodId,
                MemoId.Parse("m1:" + (i + 1).ToString("x8")), new string(large ? '\u0001' : 'x', large ? 16 * 1024 : 128))).ToArray();
        var facts = new CharacterNoteReceiptFacts(source, memos);
        var receipt = new CharacterNoteReceiptDeliverySnapshot(source, CharacterNoteReceiptDeliveryState.Pending,
            null, 3, 3, null, null, null, facts);
        PlayerTurnNotice.NoteSaveReceipt selected = CharacterNoteSaveReceipt.SelectForObservation(receipt);
        Assert.Null(receipt.NoticeBody);
        Assert.Equal(memos.Select(m => m.MemoId), selected.Selection!.MemoIds);
        Assert.Equal(large ? 0 : memos.Length, selected.Selection.ExactTexts.Count);
        SessionInputContent input = GalateaObservationContent.Create(new GalateaFreshInput.HeartbeatActivation(new GalateaCharacterName("Alice")), Time, Character, [selected]);
        PlayerTurnNotice.NoteSaveReceipt reopened = Assert.IsType<PlayerTurnNotice.NoteSaveReceipt>(
            Assert.Single(GalateaObservationContent.ReadPlayerTurn(input).Notices));
        Assert.Equal(selected.Selection.MemoIds, reopened.Selection!.MemoIds);
        Assert.Equal(selected.Selection.ExactTexts, reopened.Selection.ExactTexts);
        Assert.Throws<InvalidOperationException>(() => selected.Body);
    }

    [Fact]
    public void TypedInputPreservesSourcesAndUiEnrichmentWhileUndoRestoresOnlyAction() {
        PlayerTurnRecall gist = PlayerTurnRecall.FromText(new RecallEntry(RecallType.MemoGist, "memory-source"), "revision-17", "remember the garden");
        var reply = new PlayerTurnNotice.Reply("remote original", new("delegate", "codex", "Codex"), "dispatch", "thread", "turn", "notice");
        SessionInputContent input = GalateaObservationContent.Create(new GalateaFreshInput.PlayerAction("open the door", GalateaDelegateTestConfiguration.PlayerSender), Time, Character, [reply], [gist]);
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
            Time, Character, [reply]);
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
            [new PlayerTurnNotice.NoteSaveReceipt("fresh rendered text is not content")]));
        SessionInputContent imported = GalateaObservationContent.Create(fresh, Time, Character,
            [PlayerTurnNotice.NoteSaveReceipt.FromLegacyDurable("unchanged historical wording")]);
        Assert.Equal("legacy-note-save-receipt", imported.JsonValue.GetProperty("notices")[0].GetProperty("kind").GetString());
        Assert.Equal("unchanged historical wording", Assert.Single(GalateaObservationContent.ReadPlayerTurn(imported).Notices).Body);
    }
}
