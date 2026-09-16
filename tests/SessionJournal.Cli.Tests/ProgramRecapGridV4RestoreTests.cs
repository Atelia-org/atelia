using System.Globalization;
using System.Text.Json;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed partial class ProgramRecapGridCommandTests {
    [Fact]
    public void RestoreStoreV4UsesExplicitBackupAndExactPartialProof() {
        _ = CreateV4PartialFixture(activate: true);
        Dictionary<string, byte[]> rawBefore = SnapshotRawAuthority();
        Dictionary<string, byte[]> timelineBefore = SnapshotDirectory(
            Path.Combine(_root, "derived", "history-timeline"));
        Dictionary<string, byte[]> controlBefore = SnapshotDirectory(
            Path.Combine(_root, "control", "recap-grid"));
        (int upgradeCode, JsonElement upgraded) = RunGridCaptured(
            "upgrade-store-v5", "--input", _root, "--apply");
        Assert.True(upgradeCode == 0, upgraded.GetRawText());
        string backup = upgraded.GetProperty("detail")
            .GetProperty("BackupPath").GetString()!;

        (int prepareCode, JsonElement prepared) = RunGridCaptured(
            "prepare-restore-store-v4", "--input", _root,
            "--backup", backup);
        Assert.True(prepareCode == 0, prepared.GetRawText());
        Assert.Equal("prepared", prepared.GetProperty("status").GetString());
        JsonElement detail = prepared.GetProperty("detail");
        JsonElement active = detail.GetProperty("Active")
            .GetProperty("Witness");
        JsonElement backupWitness = detail.GetProperty("Backup")
            .GetProperty("Witness");

        (int restoreCode, JsonElement restored) = RunGridCaptured(
            "restore-store-v4", "--input", _root,
            "--backup", backup,
            "--confirm-active-length", active.GetProperty("Length")
                .GetInt64().ToString(CultureInfo.InvariantCulture),
            "--confirm-active-sha256", active.GetProperty("Sha256")
                .GetString()!,
            "--confirm-backup-length", backupWitness.GetProperty("Length")
                .GetInt64().ToString(CultureInfo.InvariantCulture),
            "--confirm-backup-sha256", backupWitness.GetProperty("Sha256")
                .GetString()!);

        Assert.True(restoreCode == 0, restored.GetRawText());
        Assert.Equal("restored", restored.GetProperty("status").GetString());
        Assert.Equal("run-upgrade-store-v5-explicitly",
            restored.GetProperty("detail").GetProperty("nextAction")
                .GetString());
        AssertSnapshotEqual(rawBefore, SnapshotRawAuthority());
        AssertSnapshotEqual(timelineBefore, SnapshotDirectory(
            Path.Combine(_root, "derived", "history-timeline")));
        AssertSnapshotEqual(controlBefore, SnapshotDirectory(
            Path.Combine(_root, "control", "recap-grid")));
        Assert.IsType<RecapGridStoreReaderOpenResult.UnsupportedSchema>(
            RecapGridStoreFactory.OpenReader(_root));

        (int repeatedCode, JsonElement repeated) = RunGridCaptured(
            "restore-store-v4", "--input", _root,
            "--backup", backup,
            "--confirm-active-length", active.GetProperty("Length")
                .GetInt64().ToString(CultureInfo.InvariantCulture),
            "--confirm-active-sha256", active.GetProperty("Sha256")
                .GetString()!,
            "--confirm-backup-length", backupWitness.GetProperty("Length")
                .GetInt64().ToString(CultureInfo.InvariantCulture),
            "--confirm-backup-sha256", backupWitness.GetProperty("Sha256")
                .GetString()!);
        Assert.True(repeatedCode == 0, repeated.GetRawText());
        Assert.Equal("already-restored",
            repeated.GetProperty("status").GetString());
    }

    [Fact]
    public void RestoreStoreV4RequiresBothPhysicalWitnesses() {
        _ = CreateV4PartialFixture(activate: false);
        (int upgradeCode, JsonElement upgraded) = RunGridCaptured(
            "upgrade-store-v5", "--input", _root, "--apply");
        Assert.True(upgradeCode == 0, upgraded.GetRawText());
        string backup = upgraded.GetProperty("detail")
            .GetProperty("BackupPath").GetString()!;

        Assert.Equal(1, Run(
            "restore-store-v4", "--input", _root, "--backup", backup));
    }
}
