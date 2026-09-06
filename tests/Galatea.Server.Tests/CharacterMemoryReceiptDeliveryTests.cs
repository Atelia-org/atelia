using Atelia.Galatea.Server.CharacterMemory;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed partial class CharacterMemorySqliteStoreTests {
    [Fact]
    public void ReceiptDelivery_DoesNotDropPendingBatchesAtFormerFifoLimit() {
        using var fixture = new ReadyStore();
        string previous = State('p');
        for (int index = 0; index < 17; index++) {
            string source = Address(110 + index);
            CharacterMemoryCaptureSnapshot capture = fixture.Store.CaptureNew(Capture(source, ["saved note"])).Capture!;
            string next = State((char)('a' + index));
            _ = fixture.Store.PlanApply(new(source, capture.ExtractionCommitment, previous, next,
                ["m1:" + (index + 1).ToString("X8", System.Globalization.CultureInfo.InvariantCulture)]));
            _ = fixture.Store.SettleApplied(new(source, capture.ExtractionCommitment, next));
            previous = next;
        }
        Assert.Equal(Address(110), fixture.Store.ReadPendingReceiptDelivery()!.SourceActionAddress);
        for (int index = 0; index < 17; index++) {
            Assert.Equal(CharacterNoteReceiptDeliveryState.Pending,
                fixture.Store.ReadReceiptDeliveryExact(Address(110 + index))!.State);
        }
    }

    [Fact]
    public void ReceiptDelivery_IsCreatedAtomicallyWithAppliedAndSurvivesReopen() {
        using var fixture = new ReadyStore();
        CharacterMemorySettleRequest settle = PrepareReceiptBatch(fixture, Address(90));
        Assert.Null(fixture.Store.ReadPendingReceiptDelivery());
        CharacterMemorySettleResult result = fixture.Store.SettleApplied(settle);
        CharacterNoteReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        Assert.Equal(CharacterNoteReceiptDeliveryState.Pending, pending.State);
        Assert.Equal(result.StoreRevision, pending.CreatedRevision);
        Assert.Contains("saved note", pending.NoticeBody, StringComparison.Ordinal);
        fixture.DisposeStore();
        using CharacterMemorySqliteStore reopened = CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner);
        Assert.Equal(pending, reopened.ReadPendingReceiptDelivery());
        Assert.Equal(CharacterMemorySettleDisposition.AlreadyApplied, reopened.SettleApplied(settle).Disposition);
        Assert.Equal(pending, reopened.ReadPendingReceiptDelivery());
    }

    [Fact]
    public void ReceiptDelivery_BindRollbackAndDeliveredAreRevisionFencedAndDurable() {
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(91)));
        CharacterNoteReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        string rendered = RenderReceiptObservation(pending);
        CharacterNoteReceiptDeliverySnapshot bound = fixture.Store.BindReceiptDelivery(
            pending.SourceActionAddress, pending.StateRevision, Address(92), rendered);
        Assert.Null(fixture.Store.ReadPendingReceiptDelivery());
        Assert.Equal(bound, fixture.Store.ReadBoundReceiptDelivery());
        Assert.Throws<CharacterMemoryStoreConflictException>(() => fixture.Store.RollbackReceiptDelivery(
            bound.SourceActionAddress, pending.StateRevision));
        CharacterNoteReceiptDeliverySnapshot rolled = fixture.Store.RollbackReceiptDelivery(
            bound.SourceActionAddress, bound.StateRevision);
        Assert.Equal(pending.NoticeBody, rolled.NoticeBody);
        Assert.Null(rolled.RenderedObservation);
        bound = fixture.Store.BindReceiptDelivery(rolled.SourceActionAddress, rolled.StateRevision, Address(92), rendered);
        fixture.DisposeStore();
        using CharacterMemorySqliteStore reopened = CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner);
        Assert.Equal(bound, reopened.ReadBoundReceiptDelivery());
        CharacterNoteReceiptDeliverySnapshot delivered = reopened.CompleteReceiptDelivery(
            bound.SourceActionAddress, bound.StateRevision, Address(93));
        Assert.Equal(CharacterNoteReceiptDeliveryState.Delivered, delivered.State);
        Assert.Null(delivered.RenderedObservation);
        Assert.Equal(Address(92), delivered.ExpectedSessionHead);
        Assert.Equal(Address(93), delivered.ObservationAddress);
        Assert.Null(reopened.ReadPendingReceiptDelivery());
        Assert.Null(reopened.ReadBoundReceiptDelivery());
        Assert.Throws<CharacterMemoryStoreConflictException>(() => reopened.RollbackReceiptDelivery(
            delivered.SourceActionAddress, delivered.StateRevision));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReceiptDelivery_SettleCommitBoundaryCannotLoseOrFabricateNotification(bool afterCommit) {
        int fired = 0;
        void Fail(string operation) {
            if (operation == "settle-note-apply" && Interlocked.Exchange(ref fired, 1) == 0) {
                throw new IOException("injected settlement boundary");
            }
        }
        var hooks = afterCommit
            ? new CharacterMemoryStoreTestHooks(AfterCommitBeforeReturn: Fail)
            : new CharacterMemoryStoreTestHooks(BeforeCommit: Fail);
        using var fixture = new ReadyStore(hooks);
        CharacterMemorySettleRequest settle = PrepareReceiptBatch(fixture, Address(94));
        if (afterCommit) {
            _ = fixture.Store.SettleApplied(settle);
            Assert.NotNull(fixture.Store.ReadPendingReceiptDelivery());
        }
        else {
            Assert.Throws<IOException>(() => fixture.Store.SettleApplied(settle));
            Assert.Equal(CharacterMemoryCaptureState.Planned, fixture.Store.ReadCaptureExact(settle.SourceActionAddress)!.State);
            Assert.Null(fixture.Store.ReadPendingReceiptDelivery());
            _ = fixture.Store.SettleApplied(settle);
            Assert.NotNull(fixture.Store.ReadPendingReceiptDelivery());
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("different")]
    [InlineData("noncanonical")]
    public void ReceiptDelivery_BindRejectsUnrelatedOrNoncanonicalObservation(string variant) {
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(95)));
        CharacterNoteReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        string rendered = variant switch {
            "missing" => PlayerTurnObservationEnvelope.Wrap(new PlayerTurnObservation("next")),
            "different" => RenderReceiptObservation(pending with { NoticeBody = "unrelated receipt" }),
            _ => RenderReceiptObservation(pending) + "\n",
        };
        Assert.Throws<InvalidDataException>(() => fixture.Store.BindReceiptDelivery(
            pending.SourceActionAddress, pending.StateRevision, Address(96), rendered));
        Assert.Equal(pending, fixture.Store.ReadPendingReceiptDelivery());
    }

    [Fact]
    public void ReceiptDelivery_StrictOpenRejectsTamperedPayload() {
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(97)));
        fixture.DisposeStore();
        ExecuteSql(System.IO.Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName),
            "UPDATE note_receipt_delivery SET notice_body = 'fabricated saved note';");
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner));
    }

    [Fact]
    public void ReceiptDelivery_StrictOpenRejectsBoundObservationWithoutFrozenReceipt() {
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(99)));
        CharacterNoteReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        _ = fixture.Store.BindReceiptDelivery(pending.SourceActionAddress, pending.StateRevision,
            Address(100), RenderReceiptObservation(pending));
        fixture.DisposeStore();
        ExecuteSql(System.IO.Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName),
            "UPDATE note_receipt_delivery SET rendered_observation = 'not a canonical receipt Observation';");
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner));
    }

    [Fact]
    public void ReceiptDelivery_PathologicalExactTextUsesBoundedIdsAndFitsMaximalReply() {
        using var fixture = new ReadyStore();
        CharacterMemorySettleRequest settle = PrepareReceiptBatch(fixture, Address(98), new string('~', 64 * 1024));
        _ = fixture.Store.SettleApplied(settle);
        CharacterNoteReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        Assert.Contains("Memo: m1:00000001", pending.NoticeBody, StringComparison.Ordinal);
        Assert.Contains(pending.SourceActionAddress, pending.NoticeBody, StringComparison.Ordinal);
        Assert.True(PlayerTurnObservationEnvelope.FitsEveryValidPlayerText([
            new PlayerTurnNotice.Reply(new string('~', PlayerTurnObservationEnvelope.MaximumReplyUtf8Bytes)),
            new PlayerTurnNotice.NoteSaveReceipt(pending.NoticeBody),
        ]));
    }

    private static CharacterMemorySettleRequest PrepareReceiptBatch(
        ReadyStore fixture, string source, string text = "saved note"
    ) {
        CharacterMemoryCaptureSnapshot captured = fixture.Store.CaptureNew(Capture(source, [text])).Capture!;
        _ = fixture.Store.PlanApply(new CharacterMemoryPlanRequest(source, captured.ExtractionCommitment,
            State('p'), State('t'), ["m1:00000001"]));
        return new(source, captured.ExtractionCommitment, State('t'));
    }

    private static string RenderReceiptObservation(CharacterNoteReceiptDeliverySnapshot receipt) =>
        PlayerTurnObservationEnvelope.Wrap(new PlayerTurnObservation("next", notices: [
            new PlayerTurnNotice.NoteSaveReceipt(receipt.NoticeBody),
        ]));
}

public sealed partial class CharacterMemorySqliteStoreTestsV2 {
    [Fact]
    public void V2ReceiptMigration_AcceptsNormalizedSchemaWhitespace() {
        using var fixture = V1Store.Create(valid: true);
        LeaveExactV2(fixture);
        ExecuteSql(fixture.DatabasePath, """
            PRAGMA writable_schema = ON;
            UPDATE sqlite_schema SET sql = replace(sql, 'schema_version = 2', 'schema_version  =    2')
            WHERE name = 'character_memory_meta';
            PRAGMA writable_schema = OFF;
            PRAGMA schema_version = 999;
            """);
        using CharacterMemorySqliteStore migrated = CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner());
        Assert.Equal(3, ReadUserVersion(fixture.DatabasePath));
        Assert.Null(migrated.ReadPendingReceiptDelivery());
    }

    [Fact]
    public void V2ReceiptMigration_PreservesOldAuthorityAndDoesNotBackfillApplied() {
        using var fixture = V1Store.Create(valid: true);
        LeaveExactV2(fixture);
        using CharacterMemorySqliteStore migrated = CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner());
        Assert.Equal(3, ReadUserVersion(fixture.DatabasePath));
        Assert.Equal(8, migrated.ReadStatusSnapshot().StoreRevision);
        Assert.Equal(CharacterMemoryCaptureState.Applied, migrated.ReadCaptureExact(Address(60))!.State);
        Assert.Null(migrated.ReadReceiptDeliveryExact(Address(60)));
        Assert.Null(migrated.ReadPendingReceiptDelivery());
        Assert.Equal(CharacterMemorySettleDisposition.AlreadyApplied, migrated.SettleApplied(new(
            Address(60), migrated.ReadCaptureExact(Address(60))!.ExtractionCommitment, State('a'))).Disposition);
        Assert.Null(migrated.ReadPendingReceiptDelivery());
        CharacterMemoryCaptureSnapshot newer = migrated.ReadCaptureExact(Address(63))!;
        _ = migrated.PlanApply(new(Address(63), newer.ExtractionCommitment,
            State('a'), State('t'), ["m1:00000002"]));
        _ = migrated.SettleApplied(new(Address(63), newer.ExtractionCommitment, State('t')));
        Assert.Equal(Address(63), migrated.ReadPendingReceiptDelivery()!.SourceActionAddress);
    }

    [Fact]
    public void V2ReceiptMigration_AfterCommitResponseLossStrictlyReopensV3() {
        using var fixture = V1Store.Create(valid: true);
        LeaveExactV2(fixture);
        int fired = 0;
        using CharacterMemorySqliteStore migrated = CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner(),
            new CharacterMemoryStoreTestHooks(AfterCommitBeforeReturn: operation => {
                if (operation == "migrate-character-memory-v2-to-v3" && Interlocked.Exchange(ref fired, 1) == 0) {
                    throw new IOException("simulated receipt migration response loss");
                }
            }));
        Assert.Equal(1, fired);
        Assert.Equal(3, ReadUserVersion(fixture.DatabasePath));
        Assert.Null(migrated.ReadPendingReceiptDelivery());
    }

    [Fact]
    public void V2ReceiptMigration_RevalidatesAuthorityUnderWriteLock() {
        using var fixture = V1Store.Create(valid: true);
        LeaveExactV2(fixture);
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner(),
            new CharacterMemoryStoreTestHooks(AfterValidationBeforeTransaction: operation => {
                if (operation == "migrate-character-memory-v2-to-v3") {
                    ExecuteSql(fixture.DatabasePath, "UPDATE character_memory_meta SET store_revision = store_revision + 1;");
                }
            })));
        Assert.Equal(2, ReadUserVersion(fixture.DatabasePath));
        Assert.False(TableExists(fixture.DatabasePath, "note_receipt_delivery"));
    }

    private static void LeaveExactV2(V1Store fixture) {
        Assert.Throws<IOException>(() => CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner(),
            new CharacterMemoryStoreTestHooks(BeforeCommit: operation => {
                if (operation == "migrate-character-memory-v2-to-v3") {
                    throw new IOException("stop before V3 commit");
                }
            })));
        Assert.Equal(2, ReadUserVersion(fixture.DatabasePath));
        Assert.False(TableExists(fixture.DatabasePath, "note_receipt_delivery"));
    }
}
