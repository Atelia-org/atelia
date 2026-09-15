using Atelia.Galatea.Server.CharacterMemory;
using Atelia.SessionJournal;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed partial class CharacterMemorySqliteStoreTests {
    [Theory]
    [InlineData("Pending")]
    [InlineData("ObservationBound")]
    [InlineData("Delivered")]
    public void V3SemanticReceiptMigrationPreservesOldBytesAndRequiresExplicitUpgrade(string state) {
        CharacterNoteReceiptDeliveryState targetState = Enum.Parse<CharacterNoteReceiptDeliveryState>(state);
        using var fixture = new ReadyStore();
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(150)));
        CharacterNoteReceiptDeliverySnapshot receipt = fixture.Store.ReadPendingReceiptDelivery()!;
        string oldBody = HistoricalNoteReceiptFixture.OldWording(CharacterNoteSaveReceipt.CreateDurable(receipt.Facts!.Memos).Notice.Body);
        if (targetState != CharacterNoteReceiptDeliveryState.Pending) {
            receipt = fixture.Store.BindReceiptDelivery(receipt.SourceActionAddress, receipt.StateRevision, Address(151), RenderReceiptObservation(receipt));
        }
        if (targetState == CharacterNoteReceiptDeliveryState.Delivered) {
            receipt = fixture.Store.CompleteReceiptDelivery(receipt.SourceActionAddress, receipt.StateRevision, Address(152));
        }
        var old = receipt with {
            Facts = null, BoundInput = null, NoticeBody = oldBody,
            RenderedObservation = targetState == CharacterNoteReceiptDeliveryState.ObservationBound
                ? PlayerTurnObservationEnvelope.Wrap(new PlayerTurnObservation("historical turn", notices: [new PlayerTurnNotice.NoteSaveReceipt(oldBody)])) : null
        };
        CharacterMemoryStatusSnapshot status = fixture.Store.ReadStatusSnapshot();
        fixture.DisposeStore();
        HistoricalNoteReceiptFixture.WriteFrozenNotice(fixture.DirectoryPath, old);
        string database = Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName);
        DowngradeSyntheticReceiptStoreToV3(database);
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner));
        using var upgraded = CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner, upgradeLegacyFormat: true);
        Assert.Equal(old, upgraded.ReadReceiptDeliveryExact(old.SourceActionAddress));
        Assert.Equal(status, upgraded.ReadStatusSnapshot());
        Assert.Equal(4L, ReadVersion(database));
    }

    private static long ReadVersion(string database) {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadWrite;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }

    private static void DowngradeSyntheticReceiptStoreToV3(string database) {
        // Exact historical V3 receipt schema: fixtures carry the old contract explicitly.
        const string receiptSchema = """
        CREATE TABLE note_receipt_delivery (
            source_action_address TEXT NOT NULL PRIMARY KEY
                REFERENCES note_action_capture(source_action_address)
                ON DELETE RESTRICT,
            state TEXT NOT NULL CHECK(state IN (
                'Pending', 'ObservationBound', 'Delivered'
            )),
            notice_body TEXT NOT NULL CHECK(length(notice_body) > 0),
            created_revision INTEGER NOT NULL CHECK(created_revision >= 1),
            state_revision INTEGER NOT NULL CHECK(state_revision >= created_revision),
            expected_session_head TEXT NULL,
            rendered_observation TEXT NULL,
            observation_address TEXT NULL,
            CHECK(
                (state = 'Pending' AND expected_session_head IS NULL
                    AND rendered_observation IS NULL AND observation_address IS NULL)
                OR (state = 'ObservationBound' AND expected_session_head IS NOT NULL
                    AND rendered_observation IS NOT NULL AND observation_address IS NULL)
                OR (state = 'Delivered' AND expected_session_head IS NOT NULL
                    AND rendered_observation IS NULL AND observation_address IS NOT NULL)
            )
        ) STRICT
        """;
        ExecuteSql(database, """
            DROP INDEX ux_note_receipt_single_bound;
            DROP INDEX ix_note_receipt_pending_schedule;
            ALTER TABLE note_receipt_delivery RENAME TO receipt_v4_fixture;
            """ + receiptSchema + ";" + """
            INSERT INTO note_receipt_delivery SELECT source_action_address, state, notice_body,
                created_revision, state_revision, expected_session_head, rendered_observation,
                observation_address FROM receipt_v4_fixture;
            DROP TABLE receipt_v4_fixture;
            CREATE UNIQUE INDEX ux_note_receipt_single_bound
            ON note_receipt_delivery((1)) WHERE state = 'ObservationBound';
            CREATE INDEX ix_note_receipt_pending_schedule
            ON note_receipt_delivery(created_revision, source_action_address)
            WHERE state = 'Pending';
            PRAGMA writable_schema = ON;
            UPDATE sqlite_schema SET sql = replace(sql, 'schema_version = 4', 'schema_version = 3')
                WHERE type = 'table' AND name = 'character_memory_meta';
            PRAGMA writable_schema = OFF;
            PRAGMA schema_version = 400;
            PRAGMA ignore_check_constraints = ON;
            UPDATE character_memory_meta SET schema_version = 3;
            PRAGMA ignore_check_constraints = OFF;
            PRAGMA user_version = 3;
            """);
    }
}
