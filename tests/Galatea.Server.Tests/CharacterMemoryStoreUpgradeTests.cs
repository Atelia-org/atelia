using System.Security.Cryptography;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.SessionJournal;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed partial class CharacterMemorySqliteStoreTestsV2 {
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OperatorUpgradePreviewsWithoutWritingAndBacksUpOriginalV1OrV2(int version) {
        using var fixture = V1Store.Create(valid: true);
        if (version == 2) { LeaveExactV2(fixture); }
        byte[] original = File.ReadAllBytes(fixture.DatabasePath);
        string[] notes = MemoryUpgradeTestInspection.ReadNotes(fixture.DatabasePath);
        var dryRun = CharacterMemorySqliteStore.UpgradeExisting(fixture.Path, Owner(), apply: false);
        Assert.Equal("DryRunReady", dryRun.Outcome);
        Assert.Equal(version, dryRun.SourceVersion);
        Assert.Null(dryRun.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Empty(MemoryUpgradeTestInspection.Backups(fixture.Path));
        var applied = CharacterMemorySqliteStore.UpgradeExisting(fixture.Path, Owner(), apply: true);
        Assert.Equal("Upgraded", applied.Outcome);
        string backup = Assert.IsType<string>(applied.BackupPath);
        Assert.Equal(version, MemoryUpgradeTestInspection.Version(backup));
        Assert.Equal(notes, MemoryUpgradeTestInspection.ReadNotes(backup));
        Assert.Equal(notes, MemoryUpgradeTestInspection.ReadNotes(fixture.DatabasePath));
        using (var reopened = CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner())) {
            Assert.Null(reopened.ReadReceiptDeliveryExact(Address(60))); // Historical Applied is not a notification.
        }
        var repeated = CharacterMemorySqliteStore.UpgradeExisting(fixture.Path, Owner(), apply: true);
        Assert.Equal("AlreadyCurrent", repeated.Outcome);
        Assert.Null(repeated.BackupPath);
        Assert.Single(MemoryUpgradeTestInspection.Backups(fixture.Path));
    }

    [Fact]
    public void OperatorUpgradeFailureRetainsCommittedIntermediateVersionAndOriginalBackup() {
        using var fixture = V1Store.Create(valid: true);
        string[] notes = MemoryUpgradeTestInspection.ReadNotes(fixture.DatabasePath);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.UpgradeExisting(
            fixture.Path, Owner(), apply: true, new CharacterMemoryStoreTestHooks(
                AfterValidationBeforeTransaction: operation => {
                    if (operation == "migrate-character-memory-v2-to-v3") { throw new IOException("private-note-must-not-be-printed"); }
                })));
        string originalBackup = Assert.Single(MemoryUpgradeTestInspection.Backups(fixture.Path));
        Assert.Contains(originalBackup, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-note-must-not-be-printed", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, MemoryUpgradeTestInspection.Version(originalBackup));
        Assert.Equal(2, MemoryUpgradeTestInspection.Version(fixture.DatabasePath));
        Assert.Equal(notes, MemoryUpgradeTestInspection.ReadNotes(originalBackup));
        Assert.Equal(notes, MemoryUpgradeTestInspection.ReadNotes(fixture.DatabasePath));
        var resumed = CharacterMemorySqliteStore.UpgradeExisting(fixture.Path, Owner(), apply: true);
        Assert.Equal("Upgraded", resumed.Outcome);
        Assert.Equal(2, resumed.SourceVersion);
        Assert.Equal(2, MemoryUpgradeTestInspection.Version(resumed.BackupPath!));
        Assert.Equal(2, MemoryUpgradeTestInspection.Backups(fixture.Path).Length);
    }
}

public sealed partial class CharacterMemorySqliteStoreTests {
    [Theory]
    [InlineData("Pending")]
    [InlineData("ObservationBound")]
    [InlineData("Delivered")]
    public void OperatorUpgradePreservesOriginalV3ReceiptStatesAndBackup(string state) {
        using var fixture = new ReadyStore();
        CharacterNoteReceiptDeliverySnapshot old = PrepareOperatorV3Fixture(fixture, state);
        string database = Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName);
        byte[] original = File.ReadAllBytes(database);
        var preview = CharacterMemorySqliteStore.UpgradeExisting(fixture.DirectoryPath, fixture.Owner, apply: false);
        Assert.Equal("DryRunReady", preview.Outcome);
        Assert.Equal(original, File.ReadAllBytes(database));
        Assert.Empty(MemoryUpgradeTestInspection.Backups(fixture.DirectoryPath));
        var applied = CharacterMemorySqliteStore.UpgradeExisting(fixture.DirectoryPath, fixture.Owner, apply: true);
        string backup = Assert.IsType<string>(applied.BackupPath);
        Assert.Equal(3, MemoryUpgradeTestInspection.Version(backup));
        Assert.Equal(MemoryUpgradeTestInspection.ReadLegacyReceiptCells(backup),
            MemoryUpgradeTestInspection.ReadLegacyReceiptCells(database));
        using var reopened = CharacterMemorySqliteStore.OpenExisting(fixture.DirectoryPath, fixture.Owner);
        Assert.Equal(old, reopened.ReadReceiptDeliveryExact(old.SourceActionAddress));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OperatorUpgradeBackupBoundaryFailureNeverChangesSource(bool afterBackup) {
        using var fixture = new ReadyStore();
        _ = PrepareOperatorV3Fixture(fixture, "Pending");
        string database = Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName);
        byte[] original = File.ReadAllBytes(database);
        Action<string> fail = _ => throw new IOException("private-note-must-not-be-printed");
        var hooks = afterBackup ? new CharacterMemoryStoreTestHooks(AfterUpgradeBackup: fail)
            : new CharacterMemoryStoreTestHooks(BeforeUpgradeBackup: fail);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            CharacterMemorySqliteStore.UpgradeExisting(fixture.DirectoryPath, fixture.Owner, apply: true, hooks));
        Assert.Equal(original, File.ReadAllBytes(database));
        Assert.DoesNotContain("private-note-must-not-be-printed", error.Message, StringComparison.Ordinal);
        if (afterBackup) {
            string backup = Assert.Single(MemoryUpgradeTestInspection.Backups(fixture.DirectoryPath));
            Assert.Contains(backup, error.Message, StringComparison.Ordinal);
            Assert.Contains("validated=True", error.Message, StringComparison.Ordinal);
            Assert.Equal(3, MemoryUpgradeTestInspection.Version(backup));
            Assert.Equal(MemoryUpgradeTestInspection.ReadLegacyReceiptCells(database), MemoryUpgradeTestInspection.ReadLegacyReceiptCells(backup));
        }
        else { Assert.Empty(MemoryUpgradeTestInspection.Backups(fixture.DirectoryPath)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OperatorUpgradeMigrationCommitBoundariesRetainBackupAndClassifyActualState(bool afterCommit) {
        using var fixture = new ReadyStore();
        _ = PrepareOperatorV3Fixture(fixture, "ObservationBound");
        string database = Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName);
        Action<string> fail = operation => {
            if (operation == "migrate-character-memory-v3-to-v4") { throw new IOException("migration boundary"); }
        };
        var hooks = afterCommit ? new CharacterMemoryStoreTestHooks(AfterCommitBeforeReturn: fail)
            : new CharacterMemoryStoreTestHooks(BeforeCommit: fail);
        if (afterCommit) {
            var result = CharacterMemorySqliteStore.UpgradeExisting(fixture.DirectoryPath, fixture.Owner, apply: true, hooks);
            Assert.Equal("Upgraded", result.Outcome);
            Assert.Equal(4, MemoryUpgradeTestInspection.Version(database));
        }
        else {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                CharacterMemorySqliteStore.UpgradeExisting(fixture.DirectoryPath, fixture.Owner, apply: true, hooks));
            Assert.Contains("validated=True", error.Message, StringComparison.Ordinal);
            Assert.Equal(3, MemoryUpgradeTestInspection.Version(database));
        }
        string backup = Assert.Single(MemoryUpgradeTestInspection.Backups(fixture.DirectoryPath));
        Assert.Equal(3, MemoryUpgradeTestInspection.Version(backup));
        Assert.Equal(MemoryUpgradeTestInspection.ReadLegacyReceiptCells(backup), MemoryUpgradeTestInspection.ReadLegacyReceiptCells(database));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OperatorUpgradeRevalidatesAuthorityBeforeBackupAndBeforeMigration(bool afterBackup) {
        using var fixture = new ReadyStore();
        _ = PrepareOperatorV3Fixture(fixture, "Pending");
        string database = Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName);
        void Alter() => ExecuteSql(database, "UPDATE character_memory_meta SET store_revision = store_revision + 1;");
        var hooks = afterBackup ? new CharacterMemoryStoreTestHooks(AfterUpgradeBackup: _ => Alter())
            : new CharacterMemoryStoreTestHooks(AfterValidationBeforeTransaction: operation => {
                if (operation == "upgrade-character-memory-v3-to-v4") { Alter(); }
            });
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.UpgradeExisting(fixture.DirectoryPath, fixture.Owner, apply: true, hooks));
        Assert.Equal(3, MemoryUpgradeTestInspection.Version(database));
        Assert.Equal(afterBackup ? 1 : 0, MemoryUpgradeTestInspection.Backups(fixture.DirectoryPath).Length);
    }

    [Fact]
    public void OperatorUpgradeRejectsLiveLockWrongOwnerUnknownVersionAndMissingStore() {
        using var fixture = new ReadyStore();
        Assert.ThrowsAny<IOException>(() => CharacterMemorySqliteStore.UpgradeExisting(fixture.DirectoryPath, fixture.Owner, apply: true));
        _ = PrepareOperatorV3Fixture(fixture, "Pending");
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.UpgradeExisting(fixture.DirectoryPath,
            fixture.Owner with { CharacterId = "wrong-owner" }, apply: true));
        string database = Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName);
        ExecuteSql(database, "PRAGMA user_version = 999;");
        byte[] original = File.ReadAllBytes(database);
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.UpgradeExisting(fixture.DirectoryPath, fixture.Owner, apply: true));
        Assert.Equal(original, File.ReadAllBytes(database));
        Assert.Empty(MemoryUpgradeTestInspection.Backups(fixture.DirectoryPath));
        string absent = Path.Combine(fixture.DirectoryPath, "absent");
        Assert.Throws<DirectoryNotFoundException>(() => CharacterMemorySqliteStore.UpgradeExisting(absent, fixture.Owner, apply: true));
        Assert.False(Directory.Exists(absent));
    }

    private static CharacterNoteReceiptDeliverySnapshot PrepareOperatorV3Fixture(ReadyStore fixture, string state) {
        _ = fixture.Store.SettleApplied(PrepareReceiptBatch(fixture, Address(160)));
        CharacterNoteReceiptDeliverySnapshot receipt = fixture.Store.ReadPendingReceiptDelivery()!;
        string body = HistoricalNoteReceiptFixture.OldWording(CharacterNoteSaveReceipt.CreateDurable(receipt.Facts!.Memos).Notice.Body);
        if (state != "Pending") {
            receipt = fixture.Store.BindReceiptDelivery(receipt.SourceActionAddress, receipt.StateRevision, Address(161), RenderReceiptObservation(receipt));
        }
        if (state == "Delivered") { receipt = fixture.Store.CompleteReceiptDelivery(receipt.SourceActionAddress, receipt.StateRevision, Address(162)); }
        var old = receipt with {
            Facts = null, BoundInput = null, NoticeBody = body,
            RenderedObservation = state == "ObservationBound"
                ? PlayerTurnObservationEnvelope.Wrap(new PlayerTurnObservation("old turn", notices: [new PlayerTurnNotice.NoteSaveReceipt(body)])) : null
        };
        fixture.DisposeStore();
        HistoricalNoteReceiptFixture.WriteFrozenNotice(fixture.DirectoryPath, old);
        DowngradeSyntheticReceiptStoreToV3(Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName));
        return old;
    }

    [Fact]
    public void OperatorUpgradeRejectsSymlinkedDatabaseBeforeCreatingBackup() {
        using var fixture = new ReadyStore();
        _ = PrepareOperatorV3Fixture(fixture, "Pending");
        string database = Path.Combine(fixture.DirectoryPath, CharacterMemorySqliteStore.DatabaseFileName);
        string actual = database + ".actual";
        File.Move(database, actual);
        byte[] original = File.ReadAllBytes(actual);
        File.CreateSymbolicLink(database, actual);
        Assert.Throws<InvalidDataException>(() => CharacterMemorySqliteStore.UpgradeExisting(fixture.DirectoryPath, fixture.Owner, apply: true));
        Assert.Equal(original, File.ReadAllBytes(actual));
        Assert.Empty(MemoryUpgradeTestInspection.Backups(fixture.DirectoryPath));
    }
}

public sealed class GalateaCharacterMemoryStoreUpgradeTests {
    [Theory]
    [InlineData(null)]
    [InlineData("relative-config.json")]
    public void MissingOrRelativeConfigIsRejectedBeforeLoading(string? config) {
        string[] args = config is null
            ? ["operator", GalateaCharacterMemoryStoreUpgrade.CommandName, "--character", "alice"]
            : ["operator", GalateaCharacterMemoryStoreUpgrade.CommandName, "--config", config, "--character", "alice"];
        var error = new StringWriter();
        Assert.Equal(2, GalateaCharacterMemoryStoreUpgrade.Run(args, new StringWriter(), error));
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("--user")]
    [InlineData("--unknown")]
    [InlineData("--apply")]
    public void InvalidArgumentsFailBeforeConfigOrStoreAccess(string extra) {
        string absent = Path.Combine(Path.GetTempPath(), "memory-upgrade-absent-" + Guid.NewGuid().ToString("N"), "config.json");
        var output = new StringWriter();
        var error = new StringWriter();
        int result = GalateaCharacterMemoryStoreUpgrade.Run(["operator", GalateaCharacterMemoryStoreUpgrade.CommandName,
            "--config", absent, "--character", "alice", "--apply", extra], output, error);
        Assert.Equal(2, result);
        Assert.Empty(output.ToString());
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.GetDirectoryName(absent)));
    }

    [Fact]
    public async Task CommandReadsCurrentConfigAndNeverCreatesHostOrMissingMemoryStore() {
        var factory = new RejectProvider();
        await using var host = GalateaTestHost.Create(factory, DisabledGalateaUserMessageNormalizer.Instance);
        GalateaCharacterConfig character = GalateaConfigLoader.Load(host.ConfigPath).Characters.Single(c => c.CharacterId == "alice");
        Assert.False(Directory.Exists(character.CharacterMemoryStateDir));
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(2, GalateaCharacterMemoryStoreUpgrade.Run(["operator", GalateaCharacterMemoryStoreUpgrade.CommandName,
            "--config", host.ConfigPath, "--character", "alice"], output, error));
        Assert.False(Directory.Exists(character.CharacterMemoryStateDir));
        var owner = new CharacterMemoryStoreOwner(character.CharacterId,
            CharacterMemorySessionComposition.CreateSessionRepositoryId(character.SessionDir));
        byte[] configBefore = File.ReadAllBytes(host.ConfigPath);
        Directory.CreateDirectory(Path.GetDirectoryName(character.CharacterMemoryStateDir)!);
        using (SessionJournalEngine journal = SessionJournalEngine.OpenReadOnly(character.SessionDir)) {
            using var store = CharacterMemorySqliteStore.CreateNew(character.CharacterMemoryStateDir, owner,
                new CharacterMemoryStoreBaseline(journal.ReadView.ReadPhysicalAppendFrontier(), EventAddressTextCodec.FormatNullable(journal.ReadCurrentHead())),
                CharacterNoteDefaultPodV1.EmptyStateIdentity);
        }
        output.GetStringBuilder().Clear(); error.GetStringBuilder().Clear();
        Assert.Equal(0, GalateaCharacterMemoryStoreUpgrade.Run(["operator", GalateaCharacterMemoryStoreUpgrade.CommandName,
            "--config", host.ConfigPath, "--character", "alice", "--apply"], output, error));
        Assert.Contains("outcome=AlreadyCurrent", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
        Assert.Equal(configBefore, File.ReadAllBytes(host.ConfigPath));
        Assert.Empty(MemoryUpgradeTestInspection.Backups(character.CharacterMemoryStateDir));
        Assert.Equal(0, factory.Calls);
    }

    private sealed class RejectProvider : ICompletionClientFactory {
        internal int Calls { get; private set; }
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Calls++;
            throw new InvalidOperationException("The offline operator must not instantiate a provider.");
        }
    }
}

internal static class MemoryUpgradeTestInspection {
    internal static string[] Backups(string directory) => Directory.GetFiles(directory, CharacterMemorySqliteStore.DatabaseFileName + ".v*-backup-*.sqlite3");
    internal static int Version(string database) {
        using var connection = Open(database);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
    internal static string[] ReadNotes(string database) => ReadCells(database,
        "SELECT source_action_address, artifact_ordinal, hex(CAST(exact_text AS BLOB)), memo_id FROM character_note ORDER BY source_action_address, artifact_ordinal;");
    internal static string[] ReadLegacyReceiptCells(string database) => ReadCells(database,
        "SELECT source_action_address, state, hex(CAST(notice_body AS BLOB)), created_revision, state_revision, expected_session_head, rendered_observation, observation_address FROM note_receipt_delivery ORDER BY source_action_address;");
    private static string[] ReadCells(string database, string sql) {
        using var connection = Open(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) {
            for (int i = 0; i < reader.FieldCount; i++) { values.Add(reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!); }
        }
        return values.ToArray();
    }
    private static SqliteConnection Open(string database) {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }
}
