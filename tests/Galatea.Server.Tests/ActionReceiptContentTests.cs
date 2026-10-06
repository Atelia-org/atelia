using System.Text;
using System.Text.Json;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.Galatea.Input;
using Atelia.MemoPod;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class ActionReceiptContentTests {
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"mail\"")]
    [InlineData("{\"kind\":null}")]
    [InlineData("{\"kind\":42}")]
    [InlineData("{\"kind\":\"unknown\"}")]
    [InlineData("{\"kind\":\"mail\"}")]
    [InlineData("{\"kind\":\"note-save\"}")]
    public void MalformedReceiptShapeThrowsInvalidDataBeforeReadingFields(string json) {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => GalateaActionReceiptSchema.Validate(document.RootElement));
        Assert.Throws<InvalidDataException>(() => ActionReceiptBatchCodec.Read(document.RootElement));
        Assert.Throws<InvalidDataException>(() => ActionReceiptBatchCodec.ReadFrozen(json));
    }

    [Theory]
    [InlineData("mail", "mail")]
    [InlineData("mail", "note-save")]
    [InlineData("note-save", "note-save")]
    [InlineData("note-save", "mail")]
    public void DuplicateDiscriminatorIsRejectedByExactObjectContract(string kind, string duplicateKind) {
        ActionReceiptBatch batch = kind == "mail"
            ? new MailReceiptBatch(Source(1), [new(Dispatch(1), "accepted", "Codex", "preview")])
            : Note([new NoteReceiptItem(MemoId.Parse("m1:00000001"), "preview")]);
        string json = ActionReceiptBatchCodec.SerializeFrozen(batch)
            .Insert(1, "\"kind\":\"" + duplicateKind + "\",");
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => GalateaActionReceiptSchema.Validate(document.RootElement));
        Assert.Throws<InvalidDataException>(() => ActionReceiptBatchCodec.Read(document.RootElement));
        Assert.Throws<InvalidDataException>(() => ActionReceiptBatchCodec.ReadFrozen(json));
    }

    [Theory]
    [InlineData("mail")]
    [InlineData("note-save")]
    public void MissingDiscriminatorRejectsOtherwiseCompleteReceipt(string kind) {
        ActionReceiptBatch batch = kind == "mail"
            ? new MailReceiptBatch(Source(1), [new(Dispatch(1), "accepted", "Codex", "preview")])
            : Note([new NoteReceiptItem(MemoId.Parse("m1:00000001"), "preview")]);
        string json = ActionReceiptBatchCodec.SerializeFrozen(batch).Replace("\"kind\":\"" + kind + "\",", string.Empty, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => GalateaActionReceiptSchema.Validate(document.RootElement));
        Assert.Throws<InvalidDataException>(() => ActionReceiptBatchCodec.Read(document.RootElement));
        Assert.Throws<InvalidDataException>(() => ActionReceiptBatchCodec.ReadFrozen(json));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(48)]
    [InlineData(49)]
    [InlineData(2048)]
    public void PreviewCountsScalarsAndPreservesUnchangedShortText(int count) {
        string text = string.Concat(Enumerable.Range(0, count).Select(i => i % 2 == 0 ? "\U0001f642" : "\U0001f680"));
        string preview = ActionReceiptPreview.Create(text);
        if (count <= 48) { Assert.Equal(text, preview); }
        else {
            Rune[] scalars = text.EnumerateRunes().ToArray();
            string expected = string.Concat(scalars.Take(32).Select(rune => rune.ToString()))
                + " …（省略）… " + string.Concat(scalars.TakeLast(16).Select(rune => rune.ToString()));
            Assert.Equal(expected, preview);
            Assert.Equal(56, preview.EnumerateRunes().Count());
        }
        _ = new UTF8Encoding(false, true).GetBytes(preview);
    }

    [Fact]
    public void PreviewPreservesWhitespaceControlsAndSourceSpelling() {
        const string shortText = " \r\n\t\0<&amp;>e\u0301\U0001f642 ";
        Assert.Equal(shortText, ActionReceiptPreview.Create(shortText));
        string longWhitespace = new(' ', 100);
        Assert.Equal(new string(' ', 32) + " …（省略）… " + new string(' ', 16), ActionReceiptPreview.Create(longWhitespace));
        var batch = Note([new NoteReceiptItem(MemoId.Parse("m1:00000001"), "\r\n\t ")]);
        batch.RequireFrozen();
        Assert.Equal(batch, ActionReceiptBatchCodec.ReadFrozen(ActionReceiptBatchCodec.SerializeFrozen(batch)));
        Assert.Throws<EncoderFallbackException>(() => ActionReceiptPreview.Create("bad\ud800"));
        Assert.Throws<EncoderFallbackException>(() => ActionReceiptPreview.Create("bad\udfff"));
        Assert.Throws<EncoderFallbackException>(() => Note([new NoteReceiptItem(MemoId.Parse("m1:00000001"), "bad\ud800")]));
        Assert.Throws<EncoderFallbackException>(() => new MailReceiptBatch(Source(1), [new(Dispatch(1), "accepted", "bad\udfff", "valid")]));
    }

    [Fact]
    public void FrozenBatchCopiesItemsAndOnlyMatchesItsTwoCompleteProjections() {
        MailReceiptItem[] items = [new(Dispatch(1), "accepted", "Codex", "first"), new(Dispatch(2), "unrouted", "Unknown", "second")];
        var frozen = new MailReceiptBatch(Source(1), items);
        string payload = ActionReceiptBatchCodec.SerializeFrozen(frozen);
        items[0] = new(Dispatch(3), "unrouted", "changed", "changed");
        Assert.Equal(payload, ActionReceiptBatchCodec.SerializeFrozen(frozen));
        ActionReceiptBatch compact = frozen.Compact();
        Assert.True(frozen.HasPreviews);
        Assert.False(compact.HasPreviews);
        Assert.True(frozen.MatchesProjection(frozen));
        Assert.True(frozen.MatchesProjection(ActionReceiptBatchCodec.Read(compact.ToJson())));
        Assert.Equal(frozen, ActionReceiptBatchCodec.ReadFrozen(payload));
        Assert.Equal(frozen.GetHashCode(), ActionReceiptBatchCodec.ReadFrozen(payload).GetHashCode());
        Assert.Throws<InvalidDataException>(() => ActionReceiptBatchCodec.SerializeFrozen(compact));
        Assert.Throws<InvalidDataException>(() => ActionReceiptBatchCodec.ReadFrozen(compact.ToJson().GetRawText()));
        Assert.False(frozen.MatchesProjection(new MailReceiptBatch(Source(2), frozen.Items)));
        Assert.False(frozen.MatchesProjection(new MailReceiptBatch(Source(1), frozen.Items.Reverse().ToArray())));
        Assert.False(frozen.MatchesProjection(new MailReceiptBatch(Source(1), [frozen.Items[0] with { Outcome = "unrouted" }, frozen.Items[1]])));
        Assert.False(frozen.MatchesProjection(new MailReceiptBatch(Source(1), [frozen.Items[0] with { Preview = "altered" }, frozen.Items[1]])));
        Assert.False(frozen.MatchesProjection(new MailReceiptBatch(Source(1), [frozen.Items[0] with { RecipientPreview = "altered" }, frozen.Items[1]])));
        Assert.False(frozen.MatchesProjection(new MailReceiptBatch(Source(1), [frozen.Items[0]])));
        Assert.False(frozen.MatchesProjection(new MailReceiptBatch(Source(1), [frozen.Items[0] with { DispatchId = Dispatch(4) }, frozen.Items[1]])));
        Assert.False(frozen.MatchesProjection(Note([new NoteReceiptItem(MemoId.Parse("m1:00000001"), "first")])));
        Assert.Equal(payload, ActionReceiptBatchCodec.SerializeFrozen(frozen));
    }

    [Fact]
    public void NoteProjectionKeepsPodAllIdentitiesAndOrder() {
        NoteReceiptItem[] items = [new(MemoId.Parse("m1:00000001"), "first"), new(MemoId.Parse("m1:00000002"), "second")];
        NoteReceiptBatch frozen = Note(items);
        items[0] = new(MemoId.Parse("m1:00000003"), "changed");
        var compact = Assert.IsType<NoteReceiptBatch>(frozen.Compact());
        Assert.Equal(frozen.PodId, compact.PodId);
        Assert.Equal(frozen.Items.Select(item => item.MemoId), compact.Items.Select(item => item.MemoId));
        Assert.All(compact.Items, item => Assert.Null(item.Preview));
        Assert.True(frozen.MatchesProjection(compact));
        Assert.False(frozen.MatchesProjection(Note(frozen.Items.Reverse().ToArray()).Compact()));
        Assert.False(frozen.MatchesProjection(Note([frozen.Items[0] with { MemoId = MemoId.Parse("m1:00000004") }, frozen.Items[1]]).Compact()));
        Assert.False(frozen.MatchesProjection(Note([frozen.Items[0]]).Compact()));
        Assert.False(frozen.MatchesProjection(new NoteReceiptBatch(Source(2), frozen.PodId, frozen.Items).Compact()));
    }

    [Fact]
    public void FinalSerializerFitsMaximumLegalMailAndNoteBatchesWithin128KiB() {
        string preview = ActionReceiptPreview.Create(string.Concat(Enumerable.Repeat("\U0001f642", 64)));
        var mail = new MailReceiptBatch(Source(1), Enumerable.Range(1, 64)
            .Select(i => new MailReceiptItem(Dispatch(i), i % 2 == 0 ? "accepted" : "unrouted", preview, preview)).ToArray());
        NoteReceiptBatch note = Note(Enumerable.Range(1, 16)
            .Select(i => new NoteReceiptItem(MemoId.Parse("m1:" + i.ToString("x8")), preview)).ToArray());
        PlayerTurnNotice[] full = [new PlayerTurnNotice.ActionReceipt(mail), new PlayerTurnNotice.ActionReceipt(note)];
        PlayerTurnNotice[] compact = [new PlayerTurnNotice.ActionReceipt(mail.Compact()), new PlayerTurnNotice.ActionReceipt(note.Compact())];
        byte[] fullJson = JsonSerializer.SerializeToUtf8Bytes(full.Select(GalateaObservationContent.NoticeJson).ToArray());
        byte[] compactJson = JsonSerializer.SerializeToUtf8Bytes(compact.Select(GalateaObservationContent.NoticeJson).ToArray());
        Assert.InRange(fullJson.Length, 1, 128 * 1024);
        Assert.InRange(compactJson.Length, 1, fullJson.Length - 1);
        using JsonDocument encoded = JsonDocument.Parse(fullJson);
        Assert.Equal(64, encoded.RootElement[0].GetProperty("receipt").GetProperty("items").GetArrayLength());
        Assert.Equal(16, encoded.RootElement[1].GetProperty("receipt").GetProperty("items").GetArrayLength());
        Assert.Equal(preview, encoded.RootElement[0].GetProperty("receipt").GetProperty("items")[0].GetProperty("preview").GetString());
        SessionInputContent fullInput = ActionReceiptObservationTests.CreateInput(full);
        SessionInputContent compactInput = ActionReceiptObservationTests.CreateInput(compact);
        Assert.InRange(GalateaInputValidation.StrictUtf8.GetByteCount(fullInput.JsonValue.GetProperty("notices").GetRawText()), 1, 128 * 1024);
        Assert.InRange(GalateaInputValidation.StrictUtf8.GetByteCount(compactInput.JsonValue.GetProperty("notices").GetRawText()), 1, 128 * 1024);
    }

    [Fact]
    public void FrozenPayloadCodecRejectsOversizedRawJsonBeforeCanonicalization() {
        NoteReceiptBatch batch = Note([new NoteReceiptItem(MemoId.Parse("m1:00000001"), "preview")]);
        string json = ActionReceiptBatchCodec.SerializeFrozen(batch);
        int paddingToLimit = 128 * 1024 - GalateaInputValidation.StrictUtf8.GetByteCount(json);
        string atLimit = json.Insert(1, new string(' ', paddingToLimit));
        Assert.Equal(batch, ActionReceiptBatchCodec.ReadFrozen(atLimit));
        string oversized = json.Insert(1, new string(' ', paddingToLimit + 1));
        Assert.Throws<InvalidDataException>(() => ActionReceiptBatchCodec.ReadFrozen(oversized));
    }

    internal static string Source(uint sequence) => EventAddressTextCodec.Format(new EventAddress(SizedPtr.Create(4, 4), sequence, AddressHint.None));
    internal static string Dispatch(int ordinal) => "gd1-" + ordinal.ToString("x64");
    internal static NoteReceiptBatch Note(IReadOnlyList<NoteReceiptItem> items) => new(Source(1), MemoPodId.Parse(GalateaObservationLimits.DefaultNotePodId), items);
}
