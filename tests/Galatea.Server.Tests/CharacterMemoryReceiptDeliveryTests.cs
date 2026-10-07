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
            Assert.Equal(ActionReceiptDeliveryState.Pending,
                fixture.Store.ReadReceiptDeliveryExact(Address(110 + index))!.State);
        }
    }

    [Fact]
    public void ReceiptDelivery_IsCreatedAtomicallyWithAppliedAndSurvivesReopen() {
        using var fixture = new ReadyStore();
        CharacterMemorySettleRequest settle = PrepareReceiptBatch(fixture, Address(90));
        Assert.Null(fixture.Store.ReadPendingReceiptDelivery());
        CharacterMemorySettleResult result = fixture.Store.SettleApplied(settle);
        ActionReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        Assert.Equal(ActionReceiptDeliveryState.Pending, pending.State);
        Assert.Equal(result.StoreRevision, pending.CreatedRevision);
        NoteReceiptBatch batch = Assert.IsType<NoteReceiptBatch>(pending.FrozenBatch);
        Assert.Equal("saved note", Assert.Single(batch.Items).Preview);
        Assert.Equal("m1:00000001", Assert.Single(batch.Items).MemoId.Value);
        fixture.DisposeStore();
        using CharacterMemorySqliteStore reopened = CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner);
        Assert.Equal(pending, reopened.ReadPendingReceiptDelivery());
        Assert.Equal(CharacterMemorySettleDisposition.AlreadyApplied, reopened.SettleApplied(settle).Disposition);
        Assert.Equal(pending, reopened.ReadPendingReceiptDelivery());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReceiptDelivery_BindRollbackAndDeliveredAreRevisionFencedAndDurable(bool compact) {
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(91)));
        ActionReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        SessionInputContent input = RenderReceiptObservation(pending, compact);
        ActionReceiptDeliverySnapshot bound = fixture.Store.BindReceiptDelivery(
            pending.SourceActionAddress, pending.StateRevision, Address(92), input);
        Assert.Null(fixture.Store.ReadPendingReceiptDelivery());
        Assert.Equal(bound, fixture.Store.ReadBoundReceiptDelivery());
        Assert.Equal(pending.FrozenBatch, bound.FrozenBatch);
        Assert.Throws<CharacterMemoryStoreConflictException>(() => fixture.Store.RollbackReceiptDelivery(
            bound.SourceActionAddress, pending.StateRevision));
        ActionReceiptDeliverySnapshot rolled = fixture.Store.RollbackReceiptDelivery(bound.SourceActionAddress, bound.StateRevision);
        Assert.Equal(pending.FrozenBatch, rolled.FrozenBatch);
        Assert.Null(rolled.BoundInput);
        bound = fixture.Store.BindReceiptDelivery(rolled.SourceActionAddress, rolled.StateRevision, Address(92), input);
        fixture.DisposeStore();
        using CharacterMemorySqliteStore reopened = CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner);
        Assert.Equal(bound, reopened.ReadBoundReceiptDelivery());
        ActionReceiptDeliverySnapshot delivered = reopened.CompleteReceiptDelivery(bound.SourceActionAddress, bound.StateRevision, Address(93));
        Assert.Equal(ActionReceiptDeliveryState.Delivered, delivered.State);
        Assert.Null(delivered.FrozenBatch);
        Assert.Null(delivered.BoundInput);
        Assert.Equal(Address(92), delivered.ExpectedSessionHead);
        Assert.Equal(Address(93), delivered.ObservationAddress);
        Assert.Null(reopened.ReadPendingReceiptDelivery());
        Assert.Null(reopened.ReadBoundReceiptDelivery());
        Assert.Throws<CharacterMemoryStoreConflictException>(() => reopened.RollbackReceiptDelivery(delivered.SourceActionAddress, delivered.StateRevision));
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
        var hooks = afterCommit ? new CharacterMemoryStoreTestHooks(AfterCommitBeforeReturn: Fail)
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

    [Fact]
    public void ReceiptDelivery_UncertainCommitRequiresTheExactFrozenBatch() {
        string? database = null;
        using var fixture = new ReadyStore(new CharacterMemoryStoreTestHooks(AfterCommitBeforeReturn: operation => {
            if (operation != "settle-note-apply") { return; }
            ExecuteSql(database!, "UPDATE note_receipt_delivery SET receipt_content = CAST(replace(CAST(receipt_content AS TEXT), 'saved note', 'altered') AS BLOB);");
            throw new IOException("simulated lost commit response");
        }));
        database = Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName);
        CharacterMemorySettleRequest settle = PrepareReceiptBatch(fixture, Address(108));
        Assert.Throws<CharacterMemoryStoreCommitOutcomeException>(() => fixture.Store.SettleApplied(settle));
        Assert.Equal(CharacterMemoryCaptureState.Applied, fixture.Store.ReadCaptureExact(settle.SourceActionAddress)!.State);
    }

    [Fact]
    public void ReceiptDelivery_BindsTheCompleteBatchAsAPrefixBeforeReplies() {
        using var fixture = new ReadyStore();
        string source = Address(109);
        CharacterMemoryCaptureSnapshot captured = fixture.Store.CaptureNew(Capture(source, ["first", "second"])).Capture!;
        _ = fixture.Store.PlanApply(new(source, captured.ExtractionCommitment, State('p'), State('t'),
            ["m1:00000001", "m1:00000002"]));
        _ = fixture.Store.SettleApplied(new(source, captured.ExtractionCommitment, State('t')));
        ActionReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        NoteReceiptBatch batch = Assert.IsType<NoteReceiptBatch>(pending.FrozenBatch);
        Assert.Equal(new[] { "first", "second" }, batch.Items.Select(item => item.Preview));
        SessionInputContent input = GalateaObservationContent.Create(
            new GalateaFreshInput.PlayerAction("next", GalateaDelegateTestConfiguration.PlayerSender),
            new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero), new GalateaSenderSnapshot("character", "alice", "Alice"),
            [new PlayerTurnNotice.ActionReceipt(batch), new PlayerTurnNotice.Reply("a reply",
                new GalateaSenderSnapshot("delegate", "codex", "Codex"), "dispatch")], connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"));
        ActionReceiptDeliverySnapshot bound = fixture.Store.BindReceiptDelivery(source, pending.StateRevision, Address(110), input);
        Assert.Equal(input, bound.BoundInput);
        var reversed = new NoteReceiptBatch(source, batch.PodId, batch.Items.Reverse().ToArray());
        _ = fixture.Store.RollbackReceiptDelivery(source, bound.StateRevision);
        ActionReceiptDeliverySnapshot current = fixture.Store.ReadPendingReceiptDelivery()!;
        Assert.Throws<InvalidDataException>(() => fixture.Store.BindReceiptDelivery(source, current.StateRevision,
            Address(110), RenderReceiptObservation(current with { FrozenBatch = reversed })));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("preview")]
    [InlineData("memo-id")]
    [InlineData("source")]
    [InlineData("nonstructured")]
    public void ReceiptDelivery_BindRejectsChangedFrozenProjection(string variant) {
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(95)));
        ActionReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        NoteReceiptBatch original = Assert.IsType<NoteReceiptBatch>(pending.FrozenBatch);
        SessionInputContent input = variant switch {
            "missing" => PlayerTurnObservationEnvelope.Wrap(new PlayerTurnObservation("next")),
            "nonstructured" => SessionInputContent.Text("not a structured Observation"),
            _ => RenderReceiptObservation(pending with { FrozenBatch = new NoteReceiptBatch(
                variant == "source" ? Address(96) : original.SourceActionAddress, original.PodId,
                [new NoteReceiptItem(MemoId.Parse(variant == "memo-id" ? "m1:00000002" : "m1:00000001"),
                    variant == "preview" ? "altered preview" : original.Items[0].Preview)]) }),
        };
        Assert.Throws<InvalidDataException>(() => fixture.Store.BindReceiptDelivery(pending.SourceActionAddress,
            pending.StateRevision, Address(96), input));
        Assert.Equal(pending, fixture.Store.ReadPendingReceiptDelivery());
    }

    [Fact]
    public void ReceiptDelivery_BindCannotSkipEarlierPendingBatch() {
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(105)));
        CharacterMemoryCaptureSnapshot second = fixture.Store.CaptureNew(Capture(Address(106), ["second"])).Capture!;
        _ = fixture.Store.PlanApply(new(Address(106), second.ExtractionCommitment, State('t'), State('u'), ["m1:00000002"]));
        _ = fixture.Store.SettleApplied(new(Address(106), second.ExtractionCommitment, State('u')));
        ActionReceiptDeliverySnapshot later = fixture.Store.ReadReceiptDeliveryExact(Address(106))!;
        Assert.Throws<CharacterMemoryStoreConflictException>(() => fixture.Store.BindReceiptDelivery(later.SourceActionAddress,
            later.StateRevision, Address(107), RenderReceiptObservation(later)));
        Assert.Equal(Address(105), fixture.Store.ReadPendingReceiptDelivery()!.SourceActionAddress);
    }

    [Fact]
    public void ReceiptDelivery_ColdOpenRejectsBoundBatchThatSkippedEarlierPending() {
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(105)));
        CharacterMemoryCaptureSnapshot second = fixture.Store.CaptureNew(Capture(Address(106), ["second"])).Capture!;
        _ = fixture.Store.PlanApply(new(Address(106), second.ExtractionCommitment, State('t'), State('u'), ["m1:00000002"]));
        _ = fixture.Store.SettleApplied(new(Address(106), second.ExtractionCommitment, State('u')));
        ActionReceiptDeliverySnapshot later = fixture.Store.ReadReceiptDeliveryExact(Address(106))!;
        byte[] input = RenderReceiptObservation(later).ToUtf8Json();
        fixture.DisposeStore();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName),
            Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString())) {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE character_memory_meta SET store_revision = store_revision + 1;
                UPDATE note_receipt_delivery SET state = 'ObservationBound',
                    state_revision = (SELECT store_revision FROM character_memory_meta),
                    expected_session_head = $head, bound_input = $input WHERE source_action_address = $source;
                """;
            command.Parameters.AddWithValue("$source", later.SourceActionAddress);
            command.Parameters.AddWithValue("$head", Address(107));
            command.Parameters.AddWithValue("$input", input);
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReceiptDelivery_ColdOpenRejectsTransitionWithoutLaterRevision(bool delivered) {
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(104)));
        ActionReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        ActionReceiptDeliverySnapshot bound = fixture.Store.BindReceiptDelivery(pending.SourceActionAddress,
            pending.StateRevision, Address(105), RenderReceiptObservation(pending));
        if (delivered) { _ = fixture.Store.CompleteReceiptDelivery(bound.SourceActionAddress, bound.StateRevision, Address(106)); }
        fixture.DisposeStore();
        ExecuteSql(Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName),
            "UPDATE note_receipt_delivery SET state_revision = created_revision;");
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner));
    }

    [Theory]
    [InlineData("empty-json")]
    [InlineData("oversized")]
    [InlineData("invalid-utf8")]
    [InlineData("capture-revision")]
    [InlineData("wrong-id")]
    [InlineData("null-preview")]
    public void ReceiptDelivery_StrictOpenRejectsInvalidSnapshotOrCaptureRelation(string variant) {
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(97)));
        fixture.DisposeStore();
        ExecuteSql(Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName), variant switch {
            "empty-json" => "UPDATE note_receipt_delivery SET receipt_content = CAST('{}' AS BLOB);",
            "oversized" => "UPDATE note_receipt_delivery SET receipt_content = zeroblob(131073);",
            "invalid-utf8" => "UPDATE note_receipt_delivery SET receipt_content = X'80';",
            "wrong-id" => "UPDATE note_receipt_delivery SET receipt_content = CAST(replace(CAST(receipt_content AS TEXT), 'm1:00000001', 'm1:00000002') AS BLOB);",
            "null-preview" => """UPDATE note_receipt_delivery SET receipt_content = CAST(replace(CAST(receipt_content AS TEXT), '"preview":"saved note"', '"preview":null') AS BLOB);""",
            _ => "UPDATE note_receipt_delivery SET created_revision = created_revision - 1;",
        });
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner));
    }

    [Fact]
    public void ReceiptDelivery_StrictOpenRejectsBoundInputWithoutFrozenReceipt() {
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(99)));
        ActionReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        _ = fixture.Store.BindReceiptDelivery(pending.SourceActionAddress, pending.StateRevision, Address(100), RenderReceiptObservation(pending));
        fixture.DisposeStore();
        ExecuteSql(Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName),
            "UPDATE note_receipt_delivery SET bound_input = CAST('not a structured receipt Observation' AS BLOB);");
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner));
    }

    [Fact]
    public void ReceiptDelivery_FenceHeavyExactTextStoresOnlyShortFrozenPreview() {
        using var fixture = new ReadyStore();
        string exactText = new string('~', 64 * 1024);
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(98), exactText));
        ActionReceiptDeliverySnapshot pending = fixture.Store.ReadPendingReceiptDelivery()!;
        NoteReceiptBatch frozen = Assert.IsType<NoteReceiptBatch>(pending.FrozenBatch);
        Assert.Equal(new string('~', 32) + " …（省略）… " + new string('~', 16), Assert.Single(frozen.Items).Preview);
        Assert.Equal(exactText, Assert.Single(fixture.Store.ReadCaptureExact(Address(98))!.Notes).ExactText);
        SessionInputContent input = RenderReceiptObservation(pending);
        Assert.DoesNotContain(exactText, Encoding.UTF8.GetString(input.ToUtf8Json()), StringComparison.Ordinal);
        Assert.True(input.ToUtf8Json().Length < 4096);
    }

    private static CharacterMemorySettleRequest PrepareReceiptBatch(ReadyStore fixture, string source, string text = "saved note") {
        CharacterMemoryCaptureSnapshot captured = fixture.Store.CaptureNew(Capture(source, [text])).Capture!;
        _ = fixture.Store.PlanApply(new CharacterMemoryPlanRequest(source, captured.ExtractionCommitment, State('p'), State('t'), ["m1:00000001"]));
        return new(source, captured.ExtractionCommitment, State('t'));
    }

    private static SessionInputContent RenderReceiptObservation(ActionReceiptDeliverySnapshot receipt, bool compact = false) =>
        GalateaObservationContent.Create(new GalateaFreshInput.PlayerAction("next", GalateaDelegateTestConfiguration.PlayerSender),
            new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero), new GalateaSenderSnapshot("character", "alice", "Alice"),
            [new PlayerTurnNotice.ActionReceipt(compact ? receipt.FrozenBatch!.Compact() : receipt.FrozenBatch!)], connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"));
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

    internal static void WriteFrozenNotice(string directory, CharacterNoteReceiptDeliverySnapshot historical)
        => LegacyCharacterMemoryReceiptFixture.Install(
            Path.Combine(directory, CharacterMemorySqliteStore.DatabaseFileName), historical, 4);

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
        _ = CharacterMemorySqliteStore.UpgradeExisting(fixture.Path, Owner(), apply: true);
        using CharacterMemorySqliteStore migrated = CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner());
        Assert.Equal(5, ReadUserVersion(fixture.DatabasePath));
        Assert.Null(migrated.ReadPendingReceiptDelivery());
    }

    [Fact]
    public void V2ReceiptMigration_PreservesOldAuthorityAndDoesNotBackfillApplied() {
        using var fixture = V1Store.Create(valid: true);
        LeaveExactV2(fixture);
        _ = CharacterMemorySqliteStore.UpgradeExisting(fixture.Path, Owner(), apply: true);
        using CharacterMemorySqliteStore migrated = CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner());
        Assert.Equal(5, ReadUserVersion(fixture.DatabasePath));
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
        _ = CharacterMemorySqliteStore.UpgradeExisting(fixture.Path, Owner(), apply: true,
            hooks: new CharacterMemoryStoreTestHooks(AfterCommitBeforeReturn: operation => {
                if (operation == "migrate-character-memory-v2-to-v3" && Interlocked.Exchange(ref fired, 1) == 0) {
                    throw new IOException("simulated receipt migration response loss");
                }
            }));
        using CharacterMemorySqliteStore migrated = CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner());
        Assert.Equal(1, fired);
        Assert.Equal(5, ReadUserVersion(fixture.DatabasePath));
        Assert.Null(migrated.ReadPendingReceiptDelivery());
    }

    [Fact]
    public void V2ReceiptMigration_RevalidatesAuthorityUnderWriteLock() {
        using var fixture = V1Store.Create(valid: true);
        LeaveExactV2(fixture);
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.UpgradeExisting(fixture.Path, Owner(), apply: true,
            new CharacterMemoryStoreTestHooks(AfterValidationBeforeTransaction: operation => {
                if (operation == "migrate-character-memory-v2-to-v3") {
                    ExecuteSql(fixture.DatabasePath, "UPDATE character_memory_meta SET store_revision = store_revision + 1;");
                }
            })));
        Assert.Equal(2, ReadUserVersion(fixture.DatabasePath));
        Assert.False(TableExists(fixture.DatabasePath, "note_receipt_delivery"));
    }

    private static void LeaveExactV2(V1Store fixture) {
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.UpgradeExisting(fixture.Path, Owner(), apply: true,
            new CharacterMemoryStoreTestHooks(BeforeCommit: operation => {
                if (operation == "migrate-character-memory-v2-to-v3") {
                    throw new IOException("stop before V3 commit");
                }
            })));
        Assert.Equal(2, ReadUserVersion(fixture.DatabasePath));
        Assert.False(TableExists(fixture.DatabasePath, "note_receipt_delivery"));
    }
}
