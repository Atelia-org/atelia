using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Control.Tests;

public sealed partial class ControlVerticalTests {
    [Fact]
    public void StatePublishFailuresReportExactCommitSettlement() {
        string createPath = NewPath();
        using SessionJournalEngine createJournal = CreateTimeline(createPath);
        Values createValues = ValuesFor(createPath, createJournal);
        var afterPublish = new ControlPersistenceTestHooks(
            AfterStatePublish: static _ => throw new IOException(
                "injected after publish"
            )
        );
        RecapGridControlCreateResult.CommitIndeterminate create =
            Assert.IsType<RecapGridControlCreateResult.CommitIndeterminate>(
                RecapGridControlFactory.CreateForTest(
                    createPath,
                    createJournal.BranchRefId,
                    createValues.Admission,
                    afterPublish
                )
            );
        Assert.Equal(create.Intended, create.Observed);
        using (RecapGridControlReaderHandle reader = Assert.IsType<
               RecapGridControlReaderOpenResult.Opened
               >(RecapGridControlFactory.OpenReader(
                   createPath,
                   createJournal.BranchRefId
               )).Handle) {
            Assert.Equal(
                create.Intended,
                Assert.IsType<RecapGridControlSnapshotResult.Available>(
                    reader.Reader.ReadSnapshot()
                ).Snapshot.Head
            );
        }

        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        using (RecapGridControlHandle handle = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.OpenForTest(
                   path,
                   journal.BranchRefId,
                   values.Admission,
                   afterPublish
               )).Handle) {
            RecapGridControlPutResult.CommitIndeterminate put =
                Assert.IsType<RecapGridControlPutResult.CommitIndeterminate>(
                    handle.Coordinator.PutFamilyDefinition(
                        initial,
                        values.Family
                    )
                );
            Assert.Equal(put.Intended, put.Observed);
        }

        ControlHeadRef putHead = Assert.IsType<
            RecapGridControlInspectResult.Available
        >(RecapGridControlMaintenance.Verify(
            path,
            journal.BranchRefId
        )).Snapshot.Head;
        ControlHeadRef recipeHead;
        using (RecapGridControlHandle normal = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   values.Admission
               )).Handle) {
            ControlHeadRef definition = Assert.IsType<
                RecapGridControlPutResult.Stored
            >(normal.Coordinator.PutMaintainerDefinition(
                putHead,
                values.Definition
            )).Head;
            recipeHead = Assert.IsType<RecapGridControlPutResult.Stored>(
                normal.Coordinator.PutBuildRecipe(
                    definition,
                    values.TimelineHead,
                    values.Recipe,
                    null
                )
            ).Head;
        }
        using (RecapGridControlHandle activating = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.OpenForTest(
                   path,
                   journal.BranchRefId,
                   values.Admission,
                   afterPublish
               )).Handle) {
            RecapGridControlActivateResult.CommitIndeterminate activation =
                Assert.IsType<
                    RecapGridControlActivateResult.CommitIndeterminate
                >(activating.Coordinator.CompareExchangeActiveRecipe(
                    recipeHead,
                    values.TimelineHead,
                    values.Recipe.Digest,
                    RecapGridControlActivationPurpose.Direct
                ));
            Assert.Equal(activation.Intended, activation.Observed);
        }
    }

    [Fact]
    public void PrePublishFailureLeavesCanonicalStateExact() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        string statePath = ControlStatePath(
            path,
            journal.BranchRefId,
            initial
        );
        byte[] before = File.ReadAllBytes(statePath);
        var beforePublish = new ControlPersistenceTestHooks(
            BeforeStatePublish: static () => throw new IOException(
                "injected before publish"
            )
        );
        using RecapGridControlHandle handle = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.OpenForTest(
            path,
            journal.BranchRefId,
            values.Admission,
            beforePublish
        )).Handle;
        Assert.IsType<RecapGridControlPutResult.Invalid>(
            handle.Coordinator.PutFamilyDefinition(initial, values.Family)
        );
        Assert.Equal(before, File.ReadAllBytes(statePath));
        Assert.Equal(
            initial,
            Assert.IsType<RecapGridControlSnapshotResult.Available>(
                handle.Reader.ReadSnapshot()
            ).Snapshot.Head
        );
    }

    [Fact]
    public void MaintenancePublishFailuresReportObservedInstallations() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        var afterBackup = new ControlPersistenceTestHooks(
            AfterBackupPublish: static () => throw new IOException(
                "injected after backup publish"
            )
        );
        string backup = Path.Combine(path, "indeterminate-backup");
        RecapGridControlBackupResult.PublishIndeterminate backedUp =
            Assert.IsType<
                RecapGridControlBackupResult.PublishIndeterminate
            >(RecapGridControlMaintenance.BackupForTest(
                path,
                journal.BranchRefId,
                initial,
                backup,
                afterBackup
            ));
        Assert.NotNull(backedUp.Observed);
        Assert.Equal(
            backedUp.Intended.Head,
            backedUp.Observed!.Head
        );

        var afterState = new ControlPersistenceTestHooks(
            AfterStatePublish: static _ => throw new IOException(
                "injected after state publish"
            )
        );
        RecapGridControlAdminResult.CommitIndeterminate reinitialized =
            Assert.IsType<
                RecapGridControlAdminResult.CommitIndeterminate
            >(RecapGridControlMaintenance.ReinitializeForTest(
                path,
                journal.BranchRefId,
                initial,
                afterState
            ));
        Assert.Equal(initial.Generation + 1,
            reinitialized.Intended.Generation);
        Assert.Equal(reinitialized.Intended, reinitialized.Observed);

        RecapGridControlAdminResult.CommitIndeterminate restored =
            Assert.IsType<
                RecapGridControlAdminResult.CommitIndeterminate
            >(RecapGridControlMaintenance.RestoreForTest(
                path,
                journal.BranchRefId,
                reinitialized.Intended,
                backup,
                afterState
            ));
        Assert.Equal(
            reinitialized.Intended.Generation + 1,
            restored.Intended.Generation
        );
        Assert.Equal(restored.Intended, restored.Observed);
        Assert.NotEqual(
            reinitialized.Intended.InstanceId,
            restored.Intended.InstanceId
        );
    }

    [Fact]
    public void PublishedTemporaryPathIsNeverCleanedAfterItIsReoccupied() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateTimeline(path);
        Values values = ValuesFor(path, journal);
        ControlHeadRef initial = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            values.Admission
        )).Head;
        string? reoccupiedPath = null;
        byte[] marker = "test-owned-reoccupied-temporary"u8.ToArray();
        var afterPublish = new ControlPersistenceTestHooks(
            AfterStatePublish: temporary => {
                reoccupiedPath = temporary;
                File.WriteAllBytes(temporary, marker);
                throw new IOException("injected after reoccupying moved source");
            }
        );

        using RecapGridControlHandle handle = Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.OpenForTest(
            path,
            journal.BranchRefId,
            values.Admission,
            afterPublish
        )).Handle;
        RecapGridControlPutResult.CommitIndeterminate result = Assert.IsType<
            RecapGridControlPutResult.CommitIndeterminate
        >(handle.Coordinator.PutFamilyDefinition(initial, values.Family));

        Assert.Equal(result.Intended, result.Observed);
        Assert.NotNull(reoccupiedPath);
        Assert.Equal(marker, File.ReadAllBytes(reoccupiedPath));
    }
}
