using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Store.Tests;

public sealed class StoreUpgradeCrashRecoveryTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-grid-v4-upgrade-crash-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, "after-backup-durable")]
    [InlineData(false, "after-temp-verified")]
    [InlineData(false, "after-replace-before-directory-fsync")]
    [InlineData(false, "after-directory-fsync-before-verify")]
    [InlineData(false, "after-verify")]
    [InlineData(true, "after-backup-durable")]
    [InlineData(true, "after-temp-verified")]
    [InlineData(true, "after-replace-before-directory-fsync")]
    [InlineData(true, "after-directory-fsync-before-verify")]
    [InlineData(true, "after-verify")]
    public void V4UpgradeCrashHasExactPhaseOutcomeAndReopensWithoutOutsideWrites(
        bool partial,
        string failpoint
    ) {
        string repository = Path.Combine(
            _root,
            partial ? "partial" : "full",
            failpoint);
        V4UpgradeCrashFixture fixture = V4UpgradeCrashFixture.Create(
            repository, partial);
        string? proofBundle = partial ? WriteProofBundle(fixture) : null;
        IReadOnlyDictionary<string, byte[]> outsideBefore =
            SnapshotOutsideGridRoot(repository);
        AssertAuthorityStoresPresent(outsideBefore);

        RunCrash(failpoint, repository, proofBundle);

        AssertNoActiveSidecars(fixture.DatabasePath);
        AssertOutsideUnchanged(repository, outsideBefore);
        string backup = Assert.Single(Directory.GetFiles(
            new StorePaths(repository).RootPath,
            "grid.sqlite.v4-backup-*.sqlite"));
        Assert.Equal(fixture.OriginalDatabase, File.ReadAllBytes(backup));
        AssertStrictV4(backup);

        bool preReplace = failpoint is "after-backup-durable"
            or "after-temp-verified";
        string[] orphanTemporaries = Directory.GetFiles(
            new StorePaths(repository).RootPath,
            ".grid.upgrade-v5.*.sqlite");
        if (preReplace) {
            Assert.Equal(fixture.OriginalDatabase,
                File.ReadAllBytes(fixture.DatabasePath));
            AssertStrictV4(fixture.DatabasePath);
            if (failpoint == "after-temp-verified") {
                string orphan = Assert.Single(orphanTemporaries);
                AssertStrictV5(repository, orphan);
            }
            else {
                Assert.Empty(orphanTemporaries);
            }

            RecapGridStoreUpgradeResult retry = partial
                ? RecapGridStoreMaintenance.UpgradeV4(
                    repository, apply: true, () => [fixture.ExpectedWork])
                : RecapGridStoreMaintenance.UpgradeV4(
                    repository, apply: true);
            Assert.IsType<RecapGridStoreUpgradeResult.Upgraded>(retry);
            if (failpoint == "after-temp-verified") {
                Assert.Equal(orphanTemporaries,
                    Directory.GetFiles(new StorePaths(repository).RootPath,
                        ".grid.upgrade-v5.*.sqlite"));
                AssertStrictV5(repository, Assert.Single(orphanTemporaries));
            }
        }
        else {
            Assert.Empty(orphanTemporaries);
            AssertStrictV5(repository, fixture.DatabasePath);
            Assert.IsType<RecapGridStoreUpgradeResult.AlreadyCurrent>(
                RecapGridStoreMaintenance.UpgradeV4(
                    repository, apply: true));
        }

        Assert.IsType<RecapGridStoreVerifyResult.Healthy>(
            RecapGridStoreMaintenance.Verify(repository));
        AssertPreserved(fixture);
        AssertNoActiveSidecars(fixture.DatabasePath);
        AssertOutsideUnchanged(repository, outsideBefore);
        foreach (string durableBackup in Directory.GetFiles(
                     new StorePaths(repository).RootPath,
                     "grid.sqlite.v4-backup-*.sqlite")) {
            Assert.Equal(fixture.OriginalDatabase,
                File.ReadAllBytes(durableBackup));
            AssertStrictV4(durableBackup);
        }
    }

    private string WriteProofBundle(V4UpgradeCrashFixture fixture) {
        string directory = Path.Combine(_root, "proofs");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory,
            Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new[] {
            Convert.ToBase64String(fixture.ExpectedWork.ToCanonicalBytes())
        }));
        Assert.False(Path.GetFullPath(path).StartsWith(
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(fixture.Repository))
                + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
        return path;
    }

    private static void RunCrash(
        string failpoint,
        string repository,
        string? proofBundle
    ) {
        string configuration = Directory.GetParent(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
        )?.Name ?? "Debug";
        string harness = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "SessionJournal.RecapGrid.Store.CrashHarness",
            "bin",
            configuration,
            "net10.0",
            "Atelia.SessionJournal.RecapGrid.Store.CrashHarness.dll"));
        Assert.True(File.Exists(harness), harness);
        var start = new ProcessStartInfo("dotnet") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repository
        };
        start.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        start.Environment.Remove("COMPlus_DbgMiniDumpName");
        start.Environment.Remove("DOTNET_DbgMiniDumpName");
        start.ArgumentList.Add(harness);
        start.ArgumentList.Add("upgrade-v4");
        start.ArgumentList.Add(failpoint);
        start.ArgumentList.Add(repository);
        if (proofBundle is not null) {
            start.ArgumentList.Add(proofBundle);
        }
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Crash harness could not be started.");
        Assert.True(process.WaitForExit(milliseconds: 30_000),
            "Crash harness did not terminate.");
        string standardError = process.StandardError.ReadToEnd();
        Assert.NotEqual(0, process.ExitCode);
        Assert.True(standardError.Contains($"upgrade-v4/{failpoint}",
            StringComparison.Ordinal), standardError);
    }

    private static void AssertPreserved(V4UpgradeCrashFixture fixture) {
        using RecapGridStoreReaderHandle reader = Assert.IsType<
            RecapGridStoreReaderOpenResult.Opened>(
            RecapGridStoreFactory.OpenReader(fixture.Repository)).Handle;
        RowWork work = Assert.IsType<RecapGridStoreReadResult<RowWork>.Found>(
            reader.Reader.ReadRowWork(fixture.ExpectedWork.Key)).Value;
        Assert.Equal(fixture.ExpectedWork.ToCanonicalBytes(),
            work.ToCanonicalBytes());
        Assert.Equal(fixture.ExpectedWork.ProducerTarget.ToCanonicalBytes(),
            work.ProducerTarget.ToCanonicalBytes());
        Assert.Equal(fixture.ExpectedPriorRow.HistoryRowId,
            work.PreviousHistoryRowId);
        Assert.Equal(fixture.ExpectedPriorRow.Id,
            work.PreviousRowResultId);

        RecapCellArtifact cell = Assert.IsType<
            RecapGridStoreReadResult<RecapCellArtifact>.Found>(
            reader.Reader.ReadCell(fixture.ExpectedCell.Id)).Value;
        Assert.Equal(fixture.ExpectedCell.Id, cell.Id);
        Assert.Equal(fixture.ExpectedCell.Content, cell.Content);
        Assert.Equal(fixture.ExpectedCell.DefinitionDigest,
            cell.DefinitionDigest);
        Assert.Equal(fixture.ExpectedCell.Outcome, cell.Outcome);
        Assert.Equal(fixture.ExpectedCell.Slot.RecipeDigest,
            cell.Slot.RecipeDigest);
        Assert.Equal(fixture.ExpectedCell.Slot.HistoryRowId,
            cell.Slot.HistoryRowId);
        Assert.Equal(work.WorkId, cell.Slot.WorkId);
        Assert.Equal(Assert.Single(work.OrderedAssignments).LogicalColumnId,
            cell.Slot.LogicalColumnId);

        RecapRowView prior = Assert.IsType<
            RecapGridStoreReadResult<RecapRowView>.Found>(
            reader.Reader.ReadView(fixture.ExpectedPriorRow.Id)).Value;
        Assert.Equal(fixture.ExpectedPriorRow.Id, prior.Id);
        Assert.Equal(fixture.ExpectedPriorRow.OrderedCells,
            prior.OrderedCells);
        if (fixture.ExpectedTerminalRow is { } expectedTerminal) {
            RecapRowView terminal = Assert.IsType<
                RecapGridStoreReadResult<RecapRowView>.Found>(
                reader.Reader.ReadView(expectedTerminal.Id)).Value;
            Assert.Equal(expectedTerminal.Id, terminal.Id);
            Assert.Equal(expectedTerminal.OrderedCells, terminal.OrderedCells);
            Assert.Equal(fixture.ExpectedPriorRow.HistoryRowId,
                terminal.PreviousHistoryRowId);
            Assert.Equal(fixture.ExpectedPriorRow.Id,
                terminal.PreviousRowResultId);
        }
    }

    private static void AssertStrictV4(string path) {
        using var connection = new SqliteConnection(
            $"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        Assert.Equal(SqliteRecapGridStore.ApplicationId,
            ScalarInt(connection, "PRAGMA application_id;"));
        Assert.Equal(4, ScalarInt(connection, "PRAGMA user_version;"));
        Assert.Equal(4, ScalarInt(connection,
            "SELECT schema_version FROM store_metadata WHERE singleton=1;"));
        Assert.Equal("ok", ScalarString(connection,
            "PRAGMA integrity_check;"));
        using SqliteCommand foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader violations = foreignKeys.ExecuteReader();
        Assert.False(violations.Read());
    }

    private static void AssertStrictV5(string repository, string path) {
        var paths = new StorePaths(repository);
        RecapGridStoreInfo info = new SqliteRecapGridStore(
            paths.WithDatabasePathForVerification(path),
            StoreStorageLimits.Production,
            readOnly: true).VerifyFully();
        Assert.Equal(SqliteRecapGridStore.SchemaVersion,
            info.Identity.SchemaVersion);
    }

    private static int ScalarInt(SqliteConnection connection, string sql) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ScalarString(
        SqliteConnection connection,
        string sql
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static void AssertNoActiveSidecars(string active) {
        Assert.False(File.Exists(active + "-journal"));
        Assert.False(File.Exists(active + "-wal"));
        Assert.False(File.Exists(active + "-shm"));
    }

    private static IReadOnlyDictionary<string, byte[]>
        SnapshotOutsideGridRoot(string repository) {
        string gridRoot = Path.GetFullPath(Path.Combine(
            repository, "derived", "recap-grid", "v1"));
        return Directory.EnumerateFiles(repository, "*",
                SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(path => !path.StartsWith(
                gridRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(repository, path)
                    .Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);
    }

    private static void AssertAuthorityStoresPresent(
        IReadOnlyDictionary<string, byte[]> snapshot
    ) {
        Assert.Contains(snapshot.Keys,
            static path => !path.StartsWith("control/", StringComparison.Ordinal)
                && !path.StartsWith("derived/", StringComparison.Ordinal));
        Assert.Contains(snapshot.Keys,
            static path => path.StartsWith("derived/history-timeline/",
                StringComparison.Ordinal));
        Assert.Contains(snapshot.Keys,
            static path => path.EndsWith("/cadence/cadence.json",
                StringComparison.Ordinal));
        Assert.Contains(snapshot.Keys,
            static path => path.StartsWith("control/recap-grid/",
                StringComparison.Ordinal)
                && path.EndsWith("/control.json", StringComparison.Ordinal));
    }

    private static void AssertOutsideUnchanged(
        string repository,
        IReadOnlyDictionary<string, byte[]> before
    ) {
        IReadOnlyDictionary<string, byte[]> after =
            SnapshotOutsideGridRoot(repository);
        Assert.Equal(before.Keys, after.Keys);
        foreach ((string path, byte[] bytes) in before) {
            Assert.Equal(bytes, after[path]);
        }
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }
}
