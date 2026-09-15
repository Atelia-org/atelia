using System.Text;
using Atelia.SessionJournal;
using Atelia.Galatea.Prompts;
using Atelia.MemoPod;
using Atelia.Galatea.Server.CharacterMemory;
using Microsoft.Data.Sqlite;
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
                ["m1:" + (index + 1).ToString("x8", System.Globalization.CultureInfo.InvariantCulture)]));
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
        Assert.Null(pending.NoticeBody);
        Assert.Equal("saved note", Assert.Single(pending.Facts!.Memos).ExactText);
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
        SessionInputContent rendered = RenderReceiptObservation(pending);
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
        SessionInputContent rendered = variant switch {
            "missing" => PlayerTurnObservationEnvelope.Wrap(new PlayerTurnObservation("next")),
            "different" => RenderReceiptObservation(pending with { Facts = new CharacterNoteReceiptFacts(pending.SourceActionAddress,
                [new(pending.SourceActionAddress, 0, CharacterNoteDefaultPodV1.PodId, MemoId.Parse("m1:00000001"), "unrelated receipt")]) }),
            _ => SessionInputContent.Text("not a structured Observation"),
        };
        Assert.Throws<InvalidDataException>(() => fixture.Store.BindReceiptDelivery(
            pending.SourceActionAddress, pending.StateRevision, Address(96), rendered));
        Assert.Equal(pending, fixture.Store.ReadPendingReceiptDelivery());
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("oversized")]
    [InlineData("invalid-utf8")]
    [InlineData("capture-revision")]
    public void ReceiptDelivery_StrictOpenRejectsInvalidPayloadOrCaptureRelation(string variant) {
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(97)));
        fixture.DisposeStore();
        ExecuteSql(System.IO.Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName),
            variant switch {
                "blank" => "UPDATE note_receipt_delivery SET receipt_format = 'legacy-text', notice_body = '   ';",
                "oversized" => "UPDATE note_receipt_delivery SET receipt_format = 'legacy-text', notice_body = hex(zeroblob(262145));",
                "invalid-utf8" => "UPDATE note_receipt_delivery SET receipt_format = 'legacy-text', notice_body = CAST(X'80' AS TEXT);",
                _ => "UPDATE note_receipt_delivery SET created_revision = created_revision - 1;",
            });
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner));
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("ObservationBound")]
    [InlineData("Delivered")]
    public void ReceiptDelivery_OldWordingColdReopensWithoutChangingPayloadOrRevisions(
        string stateName) {
        CharacterNoteReceiptDeliveryState state = Enum.Parse<CharacterNoteReceiptDeliveryState>(stateName);
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(101)));
        CharacterNoteReceiptDeliverySnapshot receipt = fixture.Store.ReadPendingReceiptDelivery()!;
        if (state != CharacterNoteReceiptDeliveryState.Pending) {
            receipt = fixture.Store.BindReceiptDelivery(receipt.SourceActionAddress,
                receipt.StateRevision, Address(102), RenderReceiptObservation(receipt));
        }
        if (state == CharacterNoteReceiptDeliveryState.Delivered) {
            receipt = fixture.Store.CompleteReceiptDelivery(receipt.SourceActionAddress,
                receipt.StateRevision, Address(103));
        }
        CharacterMemoryStatusSnapshot status = fixture.Store.ReadStatusSnapshot();
        string oldBody = HistoricalNoteReceiptFixture.OldWording(CharacterNoteSaveReceipt.CreateDurable(receipt.Facts!.Memos).Notice.Body);
        CharacterNoteReceiptDeliverySnapshot historical = receipt with {
            NoticeBody = oldBody, Facts = null, BoundInput = null,
            RenderedObservation = state == CharacterNoteReceiptDeliveryState.ObservationBound
                ? PlayerTurnObservationEnvelope.Wrap(new PlayerTurnObservation("next", notices: [new PlayerTurnNotice.NoteSaveReceipt(oldBody)])) : null,
        };
        fixture.DisposeStore();
        HistoricalNoteReceiptFixture.WriteFrozenNotice(fixture.DirectoryPath, historical);
        using CharacterMemorySqliteStore reopened = CharacterMemorySqliteStore.OpenExisting(
            fixture.DirectoryPath, fixture.Owner);
        CharacterNoteReceiptDeliverySnapshot actual = reopened.ReadReceiptDeliveryExact(receipt.SourceActionAddress)!;
        Assert.Equal(historical, actual);
        Assert.Equal(Encoding.UTF8.GetBytes(oldBody), Encoding.UTF8.GetBytes(actual.NoticeBody!));
        Assert.Equal(status, reopened.ReadStatusSnapshot());
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
            "UPDATE note_receipt_delivery SET bound_input = CAST('not a structured receipt Observation' AS BLOB);");
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner));
    }

    [Fact]
    public void ReceiptDelivery_FenceHeavyExactTextRemainsSemanticAndFitsMaximalReply() {
        using var fixture = new ReadyStore();
        CharacterMemorySettleRequest settle = PrepareReceiptBatch(fixture, Address(98), new string('~', 64 * 1024));
        _ = fixture.Store.SettleApplied(settle);
        CharacterNoteReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        Assert.Null(pending.NoticeBody);
        PlayerTurnNotice.NoteSaveReceipt selected = CharacterNoteSaveReceipt.SelectForObservation(pending);
        Assert.Equal(new string('~', 64 * 1024), Assert.Single(selected.Selection!.ExactTexts));
        Assert.Equal("m1:00000001", Assert.Single(selected.Selection.MemoIds).Value);
        Assert.True(GalateaObservationContent.FitsEveryValidPlayerText([
            new PlayerTurnNotice.Reply(new string('~', PlayerTurnObservationEnvelope.MaximumReplyUtf8Bytes),
                new GalateaSenderSnapshot("delegate", "codex", "Codex"), "dispatch"), selected,
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

    private static SessionInputContent RenderReceiptObservation(CharacterNoteReceiptDeliverySnapshot receipt) =>
        GalateaObservationContent.Create(new GalateaFreshInput.PlayerAction("next", GalateaDelegateTestConfiguration.PlayerSender),
            new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero), new GalateaSenderSnapshot("character", "alice", "Alice"),
            [CharacterNoteSaveReceipt.SelectForObservation(receipt)]);

}

// Synthetic legacy payload only. Production does not recognize renderer versions.
internal static class HistoricalNoteReceiptFixture {
    internal static string OldWording(string current) {
        const string currentPrefix = "Galatea runtime 已将以下 1 条 Note 内容成功保存到默认MemoPod。\n\n"
            + "本回执只证明以下Note内容已保存；不承诺分类、metadata补全或召回。\n\n已保存的 Note 内容：";
        const string oldPrefix = "Galatea runtime 已将以下 1 条 Note 原文成功保存到默认MemoPod。\n\n"
            + "本回执只证明以下原文已保存；不承诺分类、metadata补全或召回。\n\n已保存的 Note 原文：";
        Assert.StartsWith(currentPrefix, current, StringComparison.Ordinal);
        return oldPrefix + current[currentPrefix.Length..];
    }

    internal static void WriteFrozenNotice(string directory, CharacterNoteReceiptDeliverySnapshot historical) {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = System.IO.Path.Combine(directory, CharacterMemorySqliteStore.DatabaseFileName),
            Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE note_receipt_delivery SET receipt_format = 'legacy-text', notice_body = $body, rendered_observation = $observation, bound_input = NULL
            WHERE source_action_address = $source;
            """;
        command.Parameters.AddWithValue("$body", historical.NoticeBody!);
        command.Parameters.AddWithValue("$observation", (object?)historical.RenderedObservation ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", historical.SourceActionAddress);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
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
        using CharacterMemorySqliteStore migrated = CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner(), upgradeLegacyFormat: true);
        Assert.Equal(4, ReadUserVersion(fixture.DatabasePath));
        Assert.Null(migrated.ReadPendingReceiptDelivery());
    }

    [Fact]
    public void V2ReceiptMigration_PreservesOldAuthorityAndDoesNotBackfillApplied() {
        using var fixture = V1Store.Create(valid: true);
        LeaveExactV2(fixture);
        using CharacterMemorySqliteStore migrated = CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner(), upgradeLegacyFormat: true);
        Assert.Equal(4, ReadUserVersion(fixture.DatabasePath));
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
            }), upgradeLegacyFormat: true);
        Assert.Equal(1, fired);
        Assert.Equal(4, ReadUserVersion(fixture.DatabasePath));
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
            }), upgradeLegacyFormat: true));
        Assert.Equal(2, ReadUserVersion(fixture.DatabasePath));
        Assert.False(TableExists(fixture.DatabasePath, "note_receipt_delivery"));
    }

    private static void LeaveExactV2(V1Store fixture) {
        Assert.Throws<IOException>(() => CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner(),
            new CharacterMemoryStoreTestHooks(BeforeCommit: operation => {
                if (operation == "migrate-character-memory-v2-to-v3") {
                    throw new IOException("stop before V3 commit");
                }
            }), upgradeLegacyFormat: true));
        Assert.Equal(2, ReadUserVersion(fixture.DatabasePath));
        Assert.False(TableExists(fixture.DatabasePath, "note_receipt_delivery"));
    }
}
