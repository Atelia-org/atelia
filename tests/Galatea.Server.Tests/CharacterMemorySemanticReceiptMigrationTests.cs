using Atelia.Completion.Abstractions;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.MemoPod;
using Atelia.SessionJournal;
using System.Text.Json;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed partial class CharacterMemorySqliteStoreTests {
    [Theory]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public void ExplicitUpgradeFreezesPendingFromAppliedLedgerWithoutParsingHistoricalBody(int version, bool semantic) {
        using var fixture = new ReadyStore();
        const string exact = "12345678901234567890123456789012😀" + "middle-unshown-text" + "abcdefghijklmnop";
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(150), exact));
        ActionReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        var old = new CharacterNoteReceiptDeliverySnapshot(pending.SourceActionAddress,
            CharacterNoteReceiptDeliveryState.Pending, semantic ? null : LegacyCharacterMemoryReceiptFixture.Body,
            pending.CreatedRevision, pending.StateRevision, null, null, null);
        fixture.DisposeStore();
        string database = Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName);
        LegacyCharacterMemoryReceiptFixture.Install(database, old, version, semantic);
        byte[] original = File.ReadAllBytes(database);
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner));
        Assert.Equal(original, File.ReadAllBytes(database));
        Assert.Equal("DryRunReady", CharacterMemorySqliteStore.UpgradeExisting(fixture.DirectoryPath, fixture.Owner, false).Outcome);
        Assert.Equal(original, File.ReadAllBytes(database));
        Assert.Empty(MemoryUpgradeTestInspection.Backups(fixture.DirectoryPath));
        var result = CharacterMemorySqliteStore.UpgradeExisting(fixture.DirectoryPath, fixture.Owner, true);
        Assert.Equal(version, MemoryUpgradeTestInspection.Version(result.BackupPath!));
        using var reopened = CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner);
        ActionReceiptDeliverySnapshot converted = reopened.ReadPendingReceiptDelivery()!;
        NoteReceiptBatch batch = Assert.IsType<NoteReceiptBatch>(converted.FrozenBatch);
        Assert.Equal(old.CreatedRevision, converted.CreatedRevision);
        Assert.Equal(old.StateRevision, converted.StateRevision);
        Assert.Equal(MemoId.Parse("m1:00000001"), Assert.Single(batch.Items).MemoId);
        Assert.Equal("12345678901234567890123456789012 …（省略）… abcdefghijklmnop", batch.Items[0].Preview);
        Assert.Equal(exact, Assert.Single(reopened.ReadCaptureExact(old.SourceActionAddress)!.Notes).ExactText);
        Assert.Equal(5, MemoryUpgradeTestInspection.Version(database));
    }

    [Theory]
    [InlineData(3, "NotAppended")]
    [InlineData(3, "InProgress")]
    [InlineData(3, "Terminal")]
    [InlineData(4, "NotAppended")]
    [InlineData(4, "InProgress")]
    [InlineData(4, "Terminal")]
    public void OldBoundExactProofSettlesOrConvertsAtomicallyAndLeavesRawHistoryUnchanged(int version, string proofState) {
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(151)));
        ActionReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        string session = Path.Combine(fixture.DirectoryPath, "proof-session");
        SessionInputContent input;
        string head;
        string? observation = null;
        using (SessionJournalEngine writer = SessionJournalEngine.Create(session, new("model", "system", "fixture"))) {
            head = EventAddressTextCodec.Format(writer.ReadCurrentHead()!.Value);
            input = version == 3 ? SessionInputContent.Text(PlayerTurnObservationEnvelope.Wrap(
                new PlayerTurnObservation("historical", notices: [new PlayerTurnNotice.NoteSaveReceipt(LegacyCharacterMemoryReceiptFixture.Body)])))
                : LegacySemanticInput(pending.SourceActionAddress, "saved note");
            if (proofState != "NotAppended") {
                observation = EventAddressTextCodec.Format(writer.AppendObservation(input));
                if (proofState == "Terminal") {
                    writer.AppendImportedAgentAction(new ActionMessage([new ActionBlock.Text("already received")]),
                        new CompletionDescriptor("fixture", "fixture-v1", "model"));
                }
            }
        }
        var old = new CharacterNoteReceiptDeliverySnapshot(pending.SourceActionAddress,
            CharacterNoteReceiptDeliveryState.ObservationBound, version == 3 ? LegacyCharacterMemoryReceiptFixture.Body : null,
            pending.CreatedRevision, pending.StateRevision + 1, head,
            version == 3 ? input.TextValue : null, null, BoundInput: version == 4 ? input : null);
        fixture.DisposeStore();
        string database = Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName);
        LegacyCharacterMemoryReceiptFixture.Install(database, old, version, semantic: version == 4);
        string[] originalCells = MemoryUpgradeTestInspection.ReadLegacyReceiptCells(database);
        Dictionary<string, byte[]> journalBytes = Directory.GetFiles(session, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        using SessionJournalEngine journal = SessionJournalEngine.OpenReadOnly(session);
        byte[] original = File.ReadAllBytes(database);
        Assert.Equal("DryRunReady", CharacterMemorySqliteStore.UpgradeExisting(fixture.DirectoryPath, fixture.Owner, false, journal: journal).Outcome);
        Assert.Equal(original, File.ReadAllBytes(database));
        int ownerLockChecks = 0;
        var hooks = new CharacterMemoryStoreTestHooks(BeforeCommit: operation => {
            if (operation != "migrate-character-memory-v4-to-v5") { return; }
            Assert.True(journal.IsReadOnly);
            Assert.ThrowsAny<IOException>(() => CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner));
            ownerLockChecks++;
        });
        var result = CharacterMemorySqliteStore.UpgradeExisting(fixture.DirectoryPath, fixture.Owner, true, hooks, journal);
        Assert.Equal(1, ownerLockChecks);
        Assert.Equal(originalCells, MemoryUpgradeTestInspection.ReadLegacyReceiptCells(result.BackupPath!));
        using var reopened = CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner);
        ActionReceiptDeliverySnapshot converted = reopened.ReadReceiptDeliveryExact(old.SourceActionAddress)!;
        Assert.Null(converted.BoundInput);
        Assert.Equal(old.CreatedRevision, converted.CreatedRevision);
        Assert.True(converted.StateRevision > old.StateRevision);
        if (proofState == "NotAppended") {
            Assert.Equal(ActionReceiptDeliveryState.Pending, converted.State);
            Assert.NotNull(converted.FrozenBatch);
            Assert.Null(converted.ExpectedSessionHead);
        }
        else {
            Assert.Equal(ActionReceiptDeliveryState.Delivered, converted.State);
            Assert.Null(converted.FrozenBatch);
            Assert.Equal(head, converted.ExpectedSessionHead);
            Assert.Equal(observation, converted.ObservationAddress);
        }
        foreach ((string path, byte[] bytes) in journalBytes) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public void UnknownOldBoundProofKeepsEntireOldOwnerAndCreatesNoBackup(int version, bool apply) {
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(152)));
        ActionReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        string session = Path.Combine(fixture.DirectoryPath, "conflict-session");
        string head;
        using (SessionJournalEngine writer = SessionJournalEngine.Create(session, new("model", "system", "fixture"))) {
            head = EventAddressTextCodec.Format(writer.ReadCurrentHead()!.Value);
            writer.AppendObservation("different original input");
        }
        var old = new CharacterNoteReceiptDeliverySnapshot(pending.SourceActionAddress,
            CharacterNoteReceiptDeliveryState.ObservationBound, LegacyCharacterMemoryReceiptFixture.Body,
            pending.CreatedRevision, pending.StateRevision + 1, head,
            PlayerTurnObservationEnvelope.Wrap(new PlayerTurnObservation("historical", notices:
                [new PlayerTurnNotice.NoteSaveReceipt(LegacyCharacterMemoryReceiptFixture.Body)])), null);
        fixture.DisposeStore();
        string database = Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName);
        LegacyCharacterMemoryReceiptFixture.Install(database, old, version);
        byte[] original = File.ReadAllBytes(database);
        using SessionJournalEngine journal = SessionJournalEngine.OpenReadOnly(session);
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.UpgradeExisting(fixture.DirectoryPath, fixture.Owner, apply, journal: journal));
        Assert.Equal(original, File.ReadAllBytes(database));
        Assert.Equal(version, MemoryUpgradeTestInspection.Version(database));
        Assert.Empty(MemoryUpgradeTestInspection.Backups(fixture.DirectoryPath));
    }

    private static SessionInputContent LegacySemanticInput(string source, string exact) => SessionInputContent.Structured(
        GalateaObservationContent.V1SchemaId, JsonSerializer.SerializeToElement(new {
            v = 1, kind = "player-action", sender = new { kind = "player", id = "admin", name = "Operator" },
            externalLocalTimestamp = "2026-09-15T00:00:00.0000000+00:00", action = new { text = "next" },
            notices = new[] { new { kind = "note-save-receipt", sender = new { kind = "runtime", id = "galatea", name = "Galatea runtime" },
                receipt = new { sourceActionAddress = source, podId = CharacterNoteDefaultPodV1.PodId.Value,
                    saved = new[] { new { ordinal = 0, memoId = "m1:00000001" } }, exactTexts = new[] { exact } } } },
            recalls = Array.Empty<object>()
        }));
}
