using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.MemoPod;
using Atelia.MdJson;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterNoteSaveReceiptTests {
    private static readonly string Source = EventAddressTextCodec.Format(new EventAddress(SizedPtr.Create(4, 4), 1, AddressHint.None));

    [Fact]
    public void HistoricalSemanticReceiptPreservesCompleteExactTextAndSavedOrder() {
        string exactText = new string('~', 64 * 1024);
        SessionInputContent input = HistoricalInput(new {
            kind = "note-save-receipt",
            sender = new { kind = "runtime", id = "galatea", name = "Galatea runtime" },
            receipt = new {
                sourceActionAddress = Source,
                podId = CharacterNoteDefaultPodV1.PodIdText,
                saved = new[] { new { ordinal = 0, memoId = "m1:00000001" }, new { ordinal = 1, memoId = "m1:00000002" } },
                exactTexts = new[] { exactText, "historical second note" }
            }
        });
        byte[] before = input.ToUtf8Json();
        PlayerTurnNotice.NoteSaveReceipt notice = Assert.IsType<PlayerTurnNotice.NoteSaveReceipt>(
            Assert.Single(GalateaObservationContent.ReadPlayerTurn(input).Notices));
        Assert.Equal(new[] { "m1:00000001", "m1:00000002" }, notice.Selection!.MemoIds.Select(id => id.Value));
        Assert.Equal(new[] { exactText, "historical second note" }, notice.Selection.ExactTexts);
        JsonElement projected = MdJsonSerializer.Read(GalateaInputProjector.Instance.Project(input));
        Assert.Equal(exactText, projected.GetProperty("notices")[0].GetProperty("receipt").GetProperty("exactTexts")[0].GetString());
        Assert.Equal(before, input.ToUtf8Json());
    }

    [Fact]
    public void HistoricalOpaqueReceiptPreservesFrozenWordingAndCompleteBody() {
        const string oldBody = "Galatea runtime 已将以下 1 条 Note 原文成功保存到默认MemoPod。\n\n"
            + "本回执只证明以下原文已保存；不承诺分类、metadata补全或召回。\n\n"
            + "已保存的 Note 原文：\n\n1.\n~~~~character-note-exact-text\n第一行\n最后一行\n~~~~";
        SessionInputContent input = HistoricalInput(new { kind = "legacy-note-save-receipt", body = oldBody, sourceActionAddress = Source });
        byte[] before = input.ToUtf8Json();
        PlayerTurnNotice.NoteSaveReceipt notice = Assert.IsType<PlayerTurnNotice.NoteSaveReceipt>(
            Assert.Single(GalateaObservationContent.ReadPlayerTurn(input).Notices));
        Assert.True(notice.IsLegacyDurable);
        Assert.Equal(Source, notice.LegacySourceActionAddress);
        Assert.Equal(oldBody, notice.Body);
        Assert.Contains(oldBody, PlayerTurnObservationEnvelope.FormatForDisplay(GalateaObservationContent.ReadPlayerTurn(input)), StringComparison.Ordinal);
        _ = GalateaInputProjector.Instance.Project(input);
        Assert.Equal(before, input.ToUtf8Json());
    }

    [Fact]
    public void CurrentNoteReceiptCarriesPreviewAndCanOmitAllPreviewsWithoutChangingFrozenBatch() {
        string exact = new string('~', 64 * 1024);
        var batch = new NoteReceiptBatch(Source, CharacterNoteDefaultPodV1.PodId,
            [new NoteReceiptItem(MemoId.Parse("m1:00000001"), ActionReceiptPreview.Create(exact)),
                new NoteReceiptItem(MemoId.Parse("m1:00000002"), "second")]);
        string frozen = ActionReceiptBatchCodec.SerializeFrozen(batch);
        NoteReceiptBatch compact = Assert.IsType<NoteReceiptBatch>(batch.Compact());
        Assert.All(compact.Items, item => Assert.Null(item.Preview));
        Assert.Equal(batch.Items.Select(item => item.MemoId), compact.Items.Select(item => item.MemoId));
        Assert.True(batch.MatchesProjection(compact));
        Assert.Equal(frozen, ActionReceiptBatchCodec.SerializeFrozen(batch));
        Assert.DoesNotContain(exact, frozen, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ActionReceiptBatchCodec.SerializeFrozen(compact));
    }

    private static SessionInputContent HistoricalInput(object notice) {
        SessionInputContent basis = GalateaObservationContent.Create(
            new GalateaFreshInput.PlayerAction("historical continuation", GalateaDelegateTestConfiguration.PlayerSender),
            new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero), new GalateaSenderSnapshot("character", "alice", "Alice"), connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"));
        JsonObject raw = JsonNode.Parse(basis.JsonValue.GetRawText())!.AsObject();
        raw.Remove("connectionState");
        raw["notices"] = new JsonArray(JsonSerializer.SerializeToNode(notice));
        return SessionInputContent.Structured(GalateaObservationContent.V1SchemaId, JsonSerializer.SerializeToElement(raw));
    }
}
