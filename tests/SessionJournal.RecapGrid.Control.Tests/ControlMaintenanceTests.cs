using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Control.Tests;

public sealed partial class ControlVerticalTests {
    [Fact]
    public async Task ReaderSeesOldThenNewAcrossAtomicPublish() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef created = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hooks = new ControlPersistenceTestHooks(
            BeforeStatePublish: () => {
                entered.Set();
                release.Wait();
            }
        );
        using RecapGridControlHandle handle = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.OpenForTest(
            path,
            journal.BranchRefId,
            values.Admission,
            hooks
        )).Handle;
        Task<RecapGridControlPutResult> write = Task.Run(() =>
            handle.Coordinator.PutFamilyDefinition(
                created,
                values.Family
            )
        );
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            created,
            Assert.IsType<RecapGridControlSnapshotResult.Available>(
                handle.Reader.ReadSnapshot()
            ).Snapshot.Head
        );
        release.Set();
        ControlHeadRef stored = Assert.IsType<
            RecapGridControlPutResult.Stored
        >(await write).Head;
        Assert.Equal(
            stored,
            Assert.IsType<RecapGridControlSnapshotResult.Available>(
                handle.Reader.ReadSnapshot()
            ).Snapshot.Head
        );
    }

    [Fact]
    public void MaintenanceIsNoCreateAndNormalHandleBlocksExclusiveActions() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        string controlRoot = Path.Combine(path, "control");
        Assert.IsType<RecapGridControlInspectResult.Absent>(
            RecapGridControlMaintenance.Inspect(path, journal.BranchRefId)
        );
        Assert.IsType<RecapGridControlExportResult.Absent>(
            RecapGridControlMaintenance.Export(path, journal.BranchRefId)
        );
        Assert.False(Directory.Exists(controlRoot));

        ControlHeadRef created = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        RecapGridControlHandle handle = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            path,
            journal.BranchRefId,
            values.Admission
        )).Handle;
        string backup = Path.Combine(path, "control-backup");
        Assert.IsType<RecapGridControlBackupResult.Busy>(
            RecapGridControlMaintenance.Backup(
                path,
                journal.BranchRefId,
                created,
                backup
            )
        );
        Assert.IsType<RecapGridControlAdminResult.Busy>(
            RecapGridControlMaintenance.Reinitialize(
                path,
                journal.BranchRefId,
                created
            )
        );
        handle.Dispose();

        Assert.IsType<RecapGridControlBackupResult.Created>(
            RecapGridControlMaintenance.Backup(
                path,
                journal.BranchRefId,
                created,
                backup
            )
        );
    }

    [Fact]
    public void BackupRestoreAndReinitializeInstallFreshInstances() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef created = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        ControlHeadRef backedUpHead;
        using (RecapGridControlHandle handle = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   values.Admission
               )).Handle) {
            backedUpHead = Assert.IsType<
                RecapGridControlPutResult.Stored
            >(handle.Coordinator.PutFamilyDefinition(
                created,
                values.Family
            )).Head;
        }
        string backup = Path.Combine(path, "control-backup");
        RecapGridControlBackupResult.Created backedUp = Assert.IsType<
            RecapGridControlBackupResult.Created
        >(RecapGridControlMaintenance.Backup(
            path,
            journal.BranchRefId,
            backedUpHead,
            backup
        ));
        Assert.Equal(backedUpHead, backedUp.Manifest.Head);

        FamilyDefinition other = FamilyDefinition.Create(
            "A second family.",
            values.Family.OrderedTools,
            values.Family.OutputProtocol,
            values.Family.InputRenderingProtocol
        );
        var expandedAdmission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [values.Family.Digest, other.Digest],
            [values.Definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 0,
            maximumProjectedCalls: 0
        );
        ControlHeadRef changed;
        using (RecapGridControlHandle handle = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   expandedAdmission
               )).Handle) {
            changed = Assert.IsType<RecapGridControlPutResult.Stored>(
                handle.Coordinator.PutFamilyDefinition(
                    backedUpHead,
                    other
                )
            ).Head;
        }

        ControlHeadRef restored = Assert.IsType<
            RecapGridControlAdminResult.Applied
        >(RecapGridControlMaintenance.Restore(
            path,
            journal.BranchRefId,
            changed,
            backup
        )).Head;
        Assert.NotEqual(changed.InstanceId, restored.InstanceId);
        Assert.Equal(changed.Generation + 1, restored.Generation);
        RecapGridControlInspectResult.Available restoredSnapshot =
            Assert.IsType<RecapGridControlInspectResult.Available>(
                RecapGridControlMaintenance.Verify(
                    path,
                    journal.BranchRefId
                )
            );
        Assert.Single(restoredSnapshot.Snapshot.Families);

        using (RecapGridControlHandle handle = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   expandedAdmission
               )).Handle) {
            Assert.IsType<RecapGridControlPutResult.StaleControlHead>(
                handle.Coordinator.PutFamilyDefinition(changed, other)
            );
        }
        ControlHeadRef reinitialized = Assert.IsType<
            RecapGridControlAdminResult.Applied
        >(RecapGridControlMaintenance.Reinitialize(
            path,
            journal.BranchRefId,
            restored
        )).Head;
        Assert.NotEqual(restored.InstanceId, reinitialized.InstanceId);
        Assert.Equal(restored.Generation + 1, reinitialized.Generation);
        Assert.Empty(Assert.IsType<
            RecapGridControlInspectResult.Available
        >(RecapGridControlMaintenance.Inspect(
            path,
            journal.BranchRefId
        )).Snapshot.Families);
    }

    [Fact]
    public void CorruptCurrentStateCannotBeOnlineRestoredOrReinitialized() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef created = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        string backup = Path.Combine(path, "control-backup");
        Assert.IsType<RecapGridControlBackupResult.Created>(
            RecapGridControlMaintenance.Backup(
                path,
                journal.BranchRefId,
                created,
                backup
            )
        );
        File.WriteAllText(ControlStatePath(path, journal.BranchRefId, created),
            "{corrupt");
        Assert.IsType<RecapGridControlAdminResult.Invalid>(
            RecapGridControlMaintenance.Restore(
                path,
                journal.BranchRefId,
                created,
                backup
            )
        );
        Assert.IsType<RecapGridControlAdminResult.Invalid>(
            RecapGridControlMaintenance.Reinitialize(
                path,
                journal.BranchRefId,
                created
            )
        );
    }

    [Fact]
    public void NewTimelineHasNoFallbackToOldControlState() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        _ = Assert.IsType<RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(
                path,
                journal.BranchRefId,
                values.Admission
            )
        );
        ActiveTimelineLocator locator;
        using (HistoryTimelineReaderHandle reader = Assert.IsType<
               HistoryTimelineReaderOpenResult.Opened
               >(HistoryTimelineMaintenance.OpenReader(
                   path,
                   journal.BranchRefId
               )).Handle) {
            locator = reader.Locator;
        }
        Assert.IsType<HistoryTimelineAbandonResult.Abandoned>(
            HistoryTimelineMaintenance.Abandon(
                path,
                journal.BranchRefId,
                locator,
                new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    new HistoryLoadUnit(1),
                    maxRawEvents: 8,
                    maxRenderedBytes: 1024 * 1024
                ),
                _estimator
            )
        );
        Assert.IsType<RecapGridControlOpenResult.Absent>(
            RecapGridControlFactory.Open(
                path,
                journal.BranchRefId,
                values.Admission
            )
        );
    }

    [Fact]
    public void BackupAndGridRootsAreInertToNormalControlDiscovery() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef created = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        string backup = Path.Combine(path, "control-backup");
        Assert.IsType<RecapGridControlBackupResult.Created>(
            RecapGridControlMaintenance.Backup(
                path,
                journal.BranchRefId,
                created,
                backup
            )
        );
        File.WriteAllText(Path.Combine(backup, "control.json"), "{broken");
        string gridRoot = Path.Combine(path, "derived", "recap-grid");
        Directory.CreateDirectory(gridRoot);
        File.WriteAllText(Path.Combine(gridRoot, "not-authority"), "x");
        Directory.Delete(gridRoot, recursive: true);

        Assert.Equal(
            created,
            Assert.IsType<RecapGridControlInspectResult.Available>(
                RecapGridControlMaintenance.Inspect(
                    path,
                    journal.BranchRefId
                )
            ).Snapshot.Head
        );
    }

    private static string ControlStatePath(
        string repositoryPath,
        RefId refId,
        ControlHeadRef head
    ) => Path.Combine(
        repositoryPath,
        "control",
        "recap-grid",
        "v1",
        "refs",
        refId.ToHexString(),
        "timelines",
        head.TimelineId.Value,
        "control.json"
    );
}
