using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Microsoft.Data.Sqlite;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using static Atelia.SessionJournal.HistoryTimeline.Tests.HistoryTimelineTestData;

namespace Atelia.SessionJournal.HistoryTimeline.Tests;

public sealed class HistoryTimelineDurableLedgerTests : IDisposable {
    private readonly List<string> _paths = [];
    private readonly O200kBaseHistoryUnitLoadEstimator _estimator =
        new();

    [Fact]
    public void CreateOpenReopen_BindsLocatorAndDisposalCapability() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineInitialPolicySpec spec = InitialPolicy();

        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            spec,
            _estimator
        ));
        Assert.Equal(journal.BranchRefId, created.Locator.RefId);
        Assert.Equal(0, created.Locator.Generation);
        Assert.Equal(
            created.Locator.ActiveTimelineId,
            created.InitialHead.TimelineId
        );

        HistoryTimelineCreateResult.AlreadyExists existing =
            Assert.IsType<HistoryTimelineCreateResult.AlreadyExists>(
                HistoryTimelineFactory.Create(
                    journal.ReadView,
                    new HistoryTimelineInitialPolicySpec(
                        "unknown-partitioner",
                        "unknown-estimator",
                        new HistoryLoadUnit(1),
                        maxRawEvents: 1,
                        maxRenderedBytes: 1
                    )
                )
            );
        Assert.Equal(created.Locator, existing.Locator);

        Assert.IsType<HistoryTimelineOpenResult.Invalid>(
            HistoryTimelineFactory.Open(journal.ReadView)
        );

        HistoryTimelineOpenResult.Opened opened = Assert.IsType<
            HistoryTimelineOpenResult.Opened
        >(HistoryTimelineFactory.Open(
            journal.ReadView,
            _estimator
        ));
        HistoryTimelineHandle handle = opened.Handle;
        TimelineHeadRef head = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(handle.Reader.ReadSnapshot()).Head;
        Assert.Equal(created.InitialHead, head);
        handle.Dispose();
        Assert.IsType<HistoryTimelineSnapshotResult.Invalid>(
            handle.Reader.ReadSnapshot()
        );
        Assert.IsType<HistoryTimelineSnapshotResult.Invalid>(
            handle.Coordinator.ReadSnapshot()
        );

        using HistoryTimelineHandle reopened = Assert.IsType<
            HistoryTimelineOpenResult.Opened
        >(HistoryTimelineFactory.Open(
            journal.ReadView,
            _estimator
        )).Handle;
        Assert.Equal(
            created.InitialHead,
            Assert.IsType<HistoryTimelineSnapshotResult.Available>(
                reopened.Reader.ReadSnapshot()
            ).Head
        );
    }

    [Fact]
    public void OpenDistinguishesAbsentLocatorUnsupportedSchemaAndMissingExactSlot() {
        string absentPath = NewPath();
        using (SessionJournalEngine absent = CreateJournal(absentPath)) {
            Assert.IsType<HistoryTimelineOpenResult.Absent>(
                HistoryTimelineFactory.Open(absent.ReadView, _estimator)
            );
        }

        string unsupportedPath = NewPath();
        using (SessionJournalEngine unsupported =
               CreateJournal(unsupportedPath)) {
            HistoryTimelineCreateResult.Created created = Assert.IsType<
                HistoryTimelineCreateResult.Created
            >(HistoryTimelineFactory.Create(
                unsupported.ReadView,
                InitialPolicy(),
                _estimator
            ));
            string databasePath = new HistoryTimelinePaths(
                unsupportedPath,
                unsupported.BranchRefId
            ).TimelineDatabasePath(created.Locator.ActiveTimelineId);
            ExecuteSql(databasePath, "PRAGMA user_version = 3;");

            HistoryTimelineOpenResult.UnsupportedSchema result =
                Assert.IsType<HistoryTimelineOpenResult.UnsupportedSchema>(
                    HistoryTimelineFactory.Open(
                        unsupported.ReadView,
                        _estimator
                    )
                );
            Assert.Equal(3, result.SchemaVersion);
            Assert.Equal(
                3,
                Assert.IsType<
                    HistoryTimelineReaderOpenResult.UnsupportedSchema
                >(HistoryTimelineMaintenance.OpenReader(
                    unsupportedPath,
                    unsupported.BranchRefId
                )).SchemaVersion
            );
            Assert.Equal(
                "TimelineStoreUnsupportedSchema",
                Assert.IsType<HistoryTimelineInspectResult.Invalid>(
                    HistoryTimelineMaintenance.Inspect(
                        unsupportedPath,
                        unsupported.BranchRefId
                    )
                ).Code
            );
            Assert.Equal(
                "TimelineStoreUnsupportedSchema",
                Assert.IsType<HistoryTimelineExportResult.Invalid>(
                    HistoryTimelineMaintenance.Export(
                        unsupportedPath,
                        unsupported.BranchRefId
                    )
                ).Code
            );
            Assert.Equal(
                "TimelineStoreUnsupportedSchema",
                Assert.IsType<HistoryTimelineInspectResult.Invalid>(
                    HistoryTimelineMaintenance.Verify(
                        unsupportedPath,
                        unsupported.BranchRefId
                    )
                ).Code
            );
        }

        string missingPath = NewPath();
        using (SessionJournalEngine missing = CreateJournal(missingPath)) {
            HistoryTimelineCreateResult.Created created = Assert.IsType<
                HistoryTimelineCreateResult.Created
            >(HistoryTimelineFactory.Create(
                missing.ReadView,
                InitialPolicy(),
                _estimator
            ));
            string databasePath = new HistoryTimelinePaths(
                missingPath,
                missing.BranchRefId
            ).TimelineDatabasePath(created.Locator.ActiveTimelineId);
            File.Delete(databasePath);

            HistoryTimelineOpenResult.Invalid result = Assert.IsType<
                HistoryTimelineOpenResult.Invalid
            >(HistoryTimelineFactory.Open(
                missing.ReadView,
                _estimator
            ));
            Assert.Equal("TimelineStoreSlotMissing", result.Code);
        }
    }

    [Fact]
    public void V1BytesAreInertAndNeverUsedAsAFactoryFallback() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        string legacyRoot = Path.Combine(
            path, "derived", "history-timeline", "v1");
        Directory.CreateDirectory(legacyRoot);
        string sentinel = Path.Combine(legacyRoot, "sentinel.bin");
        byte[] poison = [0, 255, 17, 33, 65, 0];
        File.WriteAllBytes(sentinel, poison);

        Assert.IsType<HistoryTimelineOpenResult.Absent>(
            HistoryTimelineFactory.Open(journal.ReadView, _estimator));
        Assert.Equal(poison, File.ReadAllBytes(sentinel));

        Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                journal.ReadView, InitialPolicy(), _estimator));
        Assert.Equal(poison, File.ReadAllBytes(sentinel));
        Assert.True(Directory.Exists(Path.Combine(
            path, "derived", "history-timeline", "v2")));
    }

    [Fact]
    public void OpenDoesNotMapMalformedLocatorParentToAbsent() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        var paths = new HistoryTimelinePaths(
            path,
            journal.BranchRefId
        );
        string refsPath = Path.Combine(paths.RootPath, "refs");
        Directory.CreateDirectory(paths.RootPath);
        File.WriteAllText(refsPath, "not-a-directory");

        Assert.IsType<HistoryTimelineOpenResult.Invalid>(
            HistoryTimelineFactory.Open(
                journal.ReadView,
                _estimator
            )
        );
    }

    [Fact]
    public void ReaderOpenRequiresActivePolicyAndSnapshotRetainsSchemaOutcome() {
        string missingPolicyPath = NewPath();
        using (SessionJournalEngine missingPolicy =
               CreateJournal(missingPolicyPath)) {
            HistoryTimelineCreateResult.Created created = Assert.IsType<
                HistoryTimelineCreateResult.Created
            >(HistoryTimelineFactory.Create(
                missingPolicy.ReadView,
                InitialPolicy(),
                _estimator
            ));
            string databasePath = new HistoryTimelinePaths(
                missingPolicyPath,
                missingPolicy.BranchRefId
            ).TimelineDatabasePath(created.Locator.ActiveTimelineId);
            ExecuteSql(
                databasePath,
                """
                DELETE FROM policies;
                """
            );
            HistoryTimelineReaderOpenResult.Invalid invalid = Assert.IsType<
                HistoryTimelineReaderOpenResult.Invalid
            >(HistoryTimelineMaintenance.OpenReader(
                missingPolicyPath,
                missingPolicy.BranchRefId
            ));
            Assert.Equal("PartitionPolicyUnavailable", invalid.Code);
        }

        string schemaPath = NewPath();
        using SessionJournalEngine schema = CreateJournal(schemaPath);
        HistoryTimelineCreateResult.Created schemaCreated = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            schema.ReadView,
            InitialPolicy(),
            _estimator
        ));
        using HistoryTimelineHandle handle = Open(schema);
        string schemaDatabase = new HistoryTimelinePaths(
            schemaPath,
            schema.BranchRefId
        ).TimelineDatabasePath(schemaCreated.Locator.ActiveTimelineId);
        ExecuteSql(schemaDatabase, "PRAGMA user_version = 3;");

        Assert.Equal(
            3,
            Assert.IsType<HistoryTimelineSnapshotResult.UnsupportedSchema>(
                handle.Reader.ReadSnapshot()
            ).SchemaVersion
        );
    }

    [Fact]
    public void CommitAndReopen_PreservesSelectedTrieAndPathPage() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        _ = journal.AppendObservation("first observation");
        _ = journal.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("first answer")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        _ = Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                journal.ReadView,
                InitialPolicy(),
                _estimator
            )
        );

        HistorySegmentDescriptor descriptor;
        TimelineHeadRef committed;
        using (HistoryTimelineHandle handle = Assert.IsType<
                   HistoryTimelineOpenResult.Opened
               >(HistoryTimelineFactory.Open(
                   journal.ReadView,
                   _estimator
               )).Handle) {
            TimelineHeadRef before = Assert.IsType<
                HistoryTimelineSnapshotResult.Available
            >(handle.Reader.ReadSnapshot()).Head;
            OnlineSelectedRawCapture capture = Assert.IsType<
                OnlineSelectedRawCaptureResult.Captured
            >(handle.Coordinator.CaptureOnline(
                before,
                journal.ReadView
            )).Capture;
            HistoryTimelinePlanResult.Selected selected = Assert.IsType<
                HistoryTimelinePlanResult.Selected
            >(handle.Coordinator.PlanNextRow(before, capture));
            descriptor = selected.Candidate.Proposal.Descriptor;
            committed = Assert.IsType<
                HistoryTimelineCommitResult.Committed
            >(handle.Coordinator.CommitRow(selected.Candidate)).Head;
        }

        using HistoryTimelineHandle reopened = Assert.IsType<
            HistoryTimelineOpenResult.Opened
        >(HistoryTimelineFactory.Open(
            journal.ReadView,
            _estimator
        )).Handle;
        Assert.Equal(
            committed,
            Assert.IsType<HistoryTimelineSnapshotResult.Available>(
                reopened.Reader.ReadSnapshot()
            ).Head
        );
        HistoryTimelineReaderRowResult.Selected selectedRow =
            Assert.IsType<HistoryTimelineReaderRowResult.Selected>(
                reopened.Reader.ReadSelectedRow(
                    committed,
                    descriptor.RowId
                )
            );
        Assert.Equal(descriptor, selectedRow.Row.Descriptor);
        Assert.IsType<HistoryTimelineReaderRowResult.Selected>(
            reopened.Reader.ValidateWitness(
                committed,
                selectedRow.Row.Witness
            )
        );
        HistoryTimelinePathPage page = Assert.IsType<
            HistoryTimelinePathPageResult.Page
        >(reopened.Reader.ReadSelectedPathPage(
            committed,
            maximumRows: 1
        )).Value;
        Assert.Single(page.Rows);
        Assert.Equal(descriptor, page.Rows[0].Descriptor);
        Assert.Null(page.Next);
    }

    [Fact]
    public void InspectAbsent_IsReadOnlyAndDoesNotCreateTimelineRoot() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        string timelineRoot = Path.Combine(
            path,
            "derived",
            "history-timeline"
        );

        Assert.IsType<HistoryTimelineInspectResult.Absent>(
            HistoryTimelineMaintenance.Inspect(
                path,
                journal.BranchRefId
            )
        );
        Assert.IsType<HistoryTimelineExportResult.Absent>(
            HistoryTimelineMaintenance.Export(
                path,
                journal.BranchRefId
            )
        );
        Assert.False(Directory.Exists(timelineRoot));
    }

    [Fact]
    public void ReaderInspectAndVerify_DoNotChangeCanonicalDatabaseBytes() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        string databasePath = new HistoryTimelinePaths(
            path,
            journal.BranchRefId
        ).TimelineDatabasePath(created.Locator.ActiveTimelineId);
        byte[] before = File.ReadAllBytes(databasePath);

        using (HistoryTimelineReaderHandle reader = Assert.IsType<
                   HistoryTimelineReaderOpenResult.Opened
               >(HistoryTimelineMaintenance.OpenReader(
                   path,
                   journal.BranchRefId
               )).Handle) {
            Assert.IsType<HistoryTimelineSnapshotResult.Available>(
                reader.Reader.ReadSnapshot()
            );
        }
        Assert.IsType<HistoryTimelineInspectResult.Available>(
            HistoryTimelineMaintenance.Inspect(
                path,
                journal.BranchRefId
            )
        );
        Assert.IsType<HistoryTimelineInspectResult.Available>(
            HistoryTimelineMaintenance.Verify(
                path,
                journal.BranchRefId
            )
        );
        HistoryTimelineExportPage export = Assert.IsType<
            HistoryTimelineExportResult.Page
        >(HistoryTimelineMaintenance.Export(
            path,
            journal.BranchRefId
        )).Value;
        Assert.Equal(created.InitialHead, export.Head);
        Assert.Empty(export.Path.Rows);
        Assert.Null(export.Path.Next);

        Assert.Equal(before, File.ReadAllBytes(databasePath));
        Assert.False(File.Exists(databasePath + "-journal"));
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-shm"));
    }

    [Fact]
    public void BackupRestoreAndAbandon_RespectLifetimeLeaseAndScope() {
        string path = NewPath();
        string backup = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        HistoryTimelineHandle handle = Assert.IsType<
            HistoryTimelineOpenResult.Opened
        >(HistoryTimelineFactory.Open(
            journal.ReadView,
            _estimator
        )).Handle;
        TimelineHeadRef head = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(handle.Reader.ReadSnapshot()).Head;
        var confirmation = new HistoryTimelineActiveConfirmation(
            created.Locator,
            head
        );

        HistoryTimelineBackupResult.Created backedUp = Assert.IsType<
            HistoryTimelineBackupResult.Created
        >(HistoryTimelineMaintenance.Backup(
            path,
            journal.BranchRefId,
            backup
        ));
        Assert.Equal(created.Locator, backedUp.Manifest.Locator);
        Assert.Equal(head, backedUp.Manifest.Head);
        Assert.IsType<HistoryTimelineRestoreResult.Busy>(
            HistoryTimelineMaintenance.Restore(
                path,
                journal.BranchRefId,
                confirmation,
                backup
            )
        );
        Assert.IsType<HistoryTimelineAbandonResult.Busy>(
            HistoryTimelineMaintenance.Abandon(
                path,
                journal.BranchRefId,
                created.Locator,
                InitialPolicy(),
                _estimator
            )
        );
        handle.Dispose();

        Assert.IsType<HistoryTimelineRestoreResult.Restored>(
            HistoryTimelineMaintenance.Restore(
                path,
                journal.BranchRefId,
                confirmation,
                backup
            )
        );
        HistoryTimelineAbandonResult.Abandoned abandoned =
            Assert.IsType<HistoryTimelineAbandonResult.Abandoned>(
                HistoryTimelineMaintenance.Abandon(
                    path,
                    journal.BranchRefId,
                    created.Locator,
                    InitialPolicy(),
                    _estimator
                )
            );
        Assert.Equal(1, abandoned.Locator.Generation);
        Assert.NotEqual(
            created.Locator.ActiveTimelineId,
            abandoned.Locator.ActiveTimelineId
        );
        Assert.IsType<HistoryTimelineRestoreResult
            .ConfirmationMismatch>(
            HistoryTimelineMaintenance.Restore(
                path,
                journal.BranchRefId,
                confirmation,
                backup
            )
        );
        Assert.IsType<HistoryTimelineInspectResult.Available>(
            HistoryTimelineMaintenance.Verify(
                path,
                journal.BranchRefId
            )
        );
    }

    [Fact]
    public async Task DisposeDrainsEnteredOperationBeforeAdminLeaseRelease() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        using var writerEntered = new ManualResetEventSlim(false);
        using var releaseWriter = new ManualResetEventSlim(false);
        using var closing = new ManualResetEventSlim(false);
        var hooks = new HistoryTimelinePersistenceTestHooks(
            AfterAppendWriterLockAcquired: () => {
                writerEntered.Set();
                releaseWriter.Wait();
            },
            AfterLifetimeClosing: closing.Set
        );
        HistoryTimelineHandle handle = Assert.IsType<
            HistoryTimelineOpenResult.Opened
        >(HistoryTimelineFactory.OpenForTest(
            journal.ReadView,
            HistoryTimelineStorageLimits.Production,
            hooks,
            _estimator
        )).Handle;
        _ = journal.AppendObservation("lifetime drain");
        TimelineHeadRef expected = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(handle.Reader.ReadSnapshot()).Head;
        OnlineSelectedRawCapture capture = Assert.IsType<
            OnlineSelectedRawCaptureResult.Captured
        >(handle.Coordinator.CaptureOnline(
            expected,
            journal.ReadView
        )).Capture;
        HistoryRowCommitCandidate candidate = Assert.IsType<
            HistoryTimelinePlanResult.Selected
        >(handle.Coordinator.PlanNextRow(expected, capture)).Candidate;

        Task<HistoryTimelineCommitResult> commit = Task.Run(
            () => handle.Coordinator.CommitRow(candidate)
        );
        Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(5)));
        Task disposing = Task.Run(handle.Dispose);
        Assert.True(closing.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(disposing.IsCompleted);
        Assert.IsType<HistoryTimelineAbandonResult.Busy>(
            HistoryTimelineMaintenance.Abandon(
                path,
                journal.BranchRefId,
                created.Locator,
                InitialPolicy(),
                _estimator
            )
        );

        releaseWriter.Set();
        Assert.IsType<HistoryTimelineCommitResult.Committed>(
            await commit
        );
        await disposing;
        Assert.IsType<HistoryTimelineAbandonResult.Abandoned>(
            HistoryTimelineMaintenance.Abandon(
                path,
                journal.BranchRefId,
                created.Locator,
                InitialPolicy(),
                _estimator
            )
        );
    }

    [Fact]
    public void SameExpectedPolicyCasAcrossHandles_HasOneWinner() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        using HistoryTimelineHandle first = Open(journal);
        using HistoryTimelineHandle second = Open(journal);
        TimelineHeadRef expected = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(first.Reader.ReadSnapshot()).Head;
        PartitionPolicyRevision policy = PartitionPolicyRevision.Create(
            created.Locator.ActiveTimelineId,
            HistoryPartitionAlgorithms
                .FirstReplaySafeBoundaryAtTargetV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            new HistoryLoadUnit(2),
            maxRawEvents: 8,
            maxRenderedBytes: 1024 * 1024
        );
        Assert.IsType<HistoryTimelinePolicyPutResult.Stored>(
            first.Coordinator.PutPolicy(policy)
        );

        HistoryTimelinePolicyCasResult.Applied applied = Assert.IsType<
            HistoryTimelinePolicyCasResult.Applied
        >(first.Coordinator.CompareExchangePolicy(
            expected,
            policy.PolicyDigest
        ));
        Assert.Equal(expected.Generation + 1, applied.Head.Generation);
        Assert.IsType<HistoryTimelinePolicyCasResult.StaleTimelineHead>(
            second.Coordinator.CompareExchangePolicy(
                expected,
                policy.PolicyDigest
            )
        );
    }

    [Fact]
    public void BackupManifestUsesCopiedDatabaseHead_WhenLiveHeadAdvances() {
        string path = NewPath();
        string backup = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        using HistoryTimelineHandle handle = Open(journal);
        TimelineHeadRef before = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(handle.Reader.ReadSnapshot()).Head;
        PartitionPolicyRevision nextPolicy = PartitionPolicyRevision.Create(
            created.Locator.ActiveTimelineId,
            HistoryPartitionAlgorithms
                .FirstReplaySafeBoundaryAtTargetV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            new HistoryLoadUnit(2),
            maxRawEvents: 8,
            maxRenderedBytes: 1024 * 1024
        );
        Assert.IsType<HistoryTimelinePolicyPutResult.Stored>(
            handle.Coordinator.PutPolicy(nextPolicy)
        );
        TimelineHeadRef? liveAfter = null;
        var hooks = new HistoryTimelinePersistenceTestHooks(
            AfterBackupCopyBeforeVerify: () => {
                liveAfter = Assert.IsType<
                    HistoryTimelinePolicyCasResult.Applied
                >(handle.Coordinator.CompareExchangePolicy(
                    before,
                    nextPolicy.PolicyDigest
                )).Head;
            }
        );

        HistoryTimelineBackupResult.Created result = Assert.IsType<
            HistoryTimelineBackupResult.Created
        >(HistoryTimelineMaintenance.BackupCore(
            path,
            journal.BranchRefId,
            backup,
            HistoryTimelineStorageLimits.Production,
            hooks
        ));

        Assert.Equal(before, result.Manifest.Head);
        Assert.NotNull(liveAfter);
        Assert.NotEqual(liveAfter, result.Manifest.Head);
    }

    [Fact]
    public void RestoreRepairsNonHeadCorruption_ButRejectsUnreadableHead() {
        string path = NewPath();
        string backup = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        _ = journal.AppendObservation("restore corruption fixture");
        _ = journal.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("fixture answer")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        TimelineHeadRef committed;
        using (HistoryTimelineHandle handle = Open(journal)) {
            committed = CommitNextRow(handle, journal);
        }
        var confirmation = new HistoryTimelineActiveConfirmation(
            created.Locator,
            committed
        );
        Assert.IsType<HistoryTimelineBackupResult.Created>(
            HistoryTimelineMaintenance.Backup(
                path,
                journal.BranchRefId,
                backup
            )
        );
        string databasePath = new HistoryTimelinePaths(
            path,
            journal.BranchRefId
        ).TimelineDatabasePath(created.Locator.ActiveTimelineId);
        ExecuteSql(
            databasePath,
            "UPDATE rows SET canonical = zeroblob(length(canonical)) WHERE row_id = (SELECT row_id FROM rows LIMIT 1);"
        );
        Assert.IsType<HistoryTimelineInspectResult.Invalid>(
            HistoryTimelineMaintenance.Verify(
                path,
                journal.BranchRefId
            )
        );

        Assert.IsType<HistoryTimelineRestoreResult.Restored>(
            HistoryTimelineMaintenance.Restore(
                path,
                journal.BranchRefId,
                confirmation,
                backup
            )
        );
        Assert.IsType<HistoryTimelineInspectResult.Available>(
            HistoryTimelineMaintenance.Verify(
                path,
                journal.BranchRefId
            )
        );

        ExecuteSql(
            databasePath,
            "UPDATE store_metadata SET head_canonical = zeroblob(length(head_canonical)) WHERE singleton = 1;"
        );
        Assert.IsType<HistoryTimelineRestoreResult.Invalid>(
            HistoryTimelineMaintenance.Restore(
                path,
                journal.BranchRefId,
                confirmation,
                backup
            )
        );
        Assert.IsType<HistoryTimelineAbandonResult.Abandoned>(
            HistoryTimelineMaintenance.Abandon(
                path,
                journal.BranchRefId,
                created.Locator,
                InitialPolicy(),
                _estimator
            )
        );
    }

    [Fact]
    public void RestoreVerifiesPrivateCopy_WhenPublishedSourceIsReplaced() {
        string path = NewPath();
        string backup = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        TimelineHeadRef head;
        using (HistoryTimelineHandle handle = Open(journal)) {
            _ = journal.AppendObservation("private restore copy");
            head = CommitNextRow(handle, journal);
        }
        Assert.IsType<HistoryTimelineBackupResult.Created>(
            HistoryTimelineMaintenance.Backup(
                path,
                journal.BranchRefId,
                backup
            )
        );
        string publishedBackup = Path.Combine(
            backup,
            "timeline.sqlite"
        );
        var hooks = new HistoryTimelinePersistenceTestHooks(
            AfterRestoreCopyBeforeVerify: () =>
                File.WriteAllBytes(publishedBackup, [1, 2, 3])
        );

        Assert.IsType<HistoryTimelineRestoreResult.Restored>(
            HistoryTimelineMaintenance.RestoreCore(
                path,
                journal.BranchRefId,
                new HistoryTimelineActiveConfirmation(
                    created.Locator,
                    head
                ),
                backup,
                HistoryTimelineStorageLimits.Production,
                hooks
            )
        );
        Assert.Equal(3, new FileInfo(publishedBackup).Length);
        Assert.IsType<HistoryTimelineInspectResult.Available>(
            HistoryTimelineMaintenance.Verify(
                path,
                journal.BranchRefId
            )
        );
    }

    [Theory]
    [InlineData("drop-foreign-key")]
    [InlineData("add-trigger")]
    public void StrictSchemaRejectsMissingOrUnexpectedObjects(
        string corruption
    ) {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        string databasePath = new HistoryTimelinePaths(
            path,
            journal.BranchRefId
        ).TimelineDatabasePath(created.Locator.ActiveTimelineId);
        ExecuteSql(
            databasePath,
            corruption == "drop-foreign-key"
                ? """
                    PRAGMA writable_schema = ON;
                    UPDATE sqlite_schema
                    SET sql = replace(
                        sql,
                        'FOREIGN KEY(previous_row_id) REFERENCES rows(row_id)',
                        'CHECK(previous_row_id IS NULL OR length(previous_row_id) = 64)'
                    )
                    WHERE type = 'table' AND name = 'rows';
                    PRAGMA schema_version = 2;
                    PRAGMA writable_schema = OFF;
                    """
                : """
                    CREATE TRIGGER unexpected_rows_trigger
                    AFTER INSERT ON rows BEGIN SELECT 1; END;
                    """
        );

        Assert.IsType<HistoryTimelineOpenResult.Invalid>(
            HistoryTimelineFactory.Open(
                journal.ReadView,
                _estimator
            )
        );
        Assert.IsType<HistoryTimelineInspectResult.Invalid>(
            HistoryTimelineMaintenance.Verify(
                path,
                journal.BranchRefId
            )
        );
    }

    [Fact]
    public void SqliteV2IdentityAndOrderedSchemaHaveIndependentFingerprint() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        string databasePath = new HistoryTimelinePaths(
            path,
            journal.BranchRefId
        ).TimelineDatabasePath(created.Locator.ActiveTimelineId);

        using SqliteConnection connection = OpenSqlite(databasePath);
        Assert.Equal(
            0x41544854L,
            ReadSqliteInt64(connection, "PRAGMA application_id;")
        );
        Assert.Equal(
            2L,
            ReadSqliteInt64(connection, "PRAGMA user_version;")
        );
        using SqliteCommand schema = connection.CreateCommand();
        schema.CommandText = """
            SELECT type, name, tbl_name, sql
            FROM sqlite_schema
            WHERE name NOT LIKE 'sqlite_%'
            ORDER BY type, name;
            """;
        using SqliteDataReader reader = schema.ExecuteReader();
        var identities = new List<string>();
        using IncrementalHash fingerprint = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        AppendHashField(
            fingerprint,
            "atelia.tests.history-timeline.sqlite-schema-fingerprint.v1"
        );
        while (reader.Read()) {
            string type = reader.GetString(0);
            string name = reader.GetString(1);
            string tableName = reader.GetString(2);
            string sql = reader.GetString(3);
            identities.Add($"{type}:{name}:{tableName}");
            AppendHashField(fingerprint, type);
            AppendHashField(fingerprint, name);
            AppendHashField(fingerprint, tableName);
            AppendHashField(fingerprint, sql);
        }

        string[] expectedIdentities = [
            "table:current_selected_path:current_selected_path",
            "table:current_selected_path_guard:"
                + "current_selected_path_guard",
            "table:current_selected_path_merkle:"
                + "current_selected_path_merkle",
            "table:policies:policies",
            "table:rows:rows",
            "table:store_metadata:store_metadata",
            "trigger:guard_selected_path_ad:current_selected_path",
            "trigger:guard_selected_path_ai:current_selected_path",
            "trigger:guard_selected_path_au:current_selected_path",
            "trigger:guard_selected_path_commitments_ad:"
                + "current_selected_path_merkle",
            "trigger:guard_selected_path_commitments_ai:"
                + "current_selected_path_merkle",
            "trigger:guard_selected_path_commitments_au:"
                + "current_selected_path_merkle"
        ];
        Assert.Equal(expectedIdentities, identities);
        string actualFingerprint = Convert.ToHexString(
            fingerprint.GetHashAndReset()
        ).ToLowerInvariant();
        Assert.Equal(
            "7fbd3b1ee14ecfa50cb5194ac5d9b3cda3e55edaed5131f6d72ad7fada321b11",
            actualFingerprint
        );
    }

    [Theory]
    [InlineData("application-id")]
    [InlineData("metadata-schema")]
    [InlineData("metadata-timeline")]
    [InlineData("metadata-ref")]
    [InlineData("head-digest")]
    [InlineData("head-scope-with-valid-digest")]
    public void V2CoreIdentityMetadataAndHeadMismatchBlockNormalOpen(
        string corruption
    ) {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        string databasePath = new HistoryTimelinePaths(
            path,
            journal.BranchRefId
        ).TimelineDatabasePath(created.Locator.ActiveTimelineId);

        switch (corruption) {
            case "application-id":
                ExecuteSql(
                    databasePath,
                    "PRAGMA application_id = 1096042581;"
                );
                break;
            case "metadata-schema":
                ExecuteSql(
                    databasePath,
                    """
                    UPDATE store_metadata
                    SET schema_version = 3
                    WHERE singleton = 1;
                    """
                );
                break;
            case "metadata-timeline":
                ExecuteSql(
                    databasePath,
                    """
                    UPDATE store_metadata
                    SET timeline_id = 'ffffffffffffffffffffffffffffffff'
                    WHERE singleton = 1;
                    """
                );
                break;
            case "metadata-ref":
                ExecuteSql(
                    databasePath,
                    """
                    UPDATE store_metadata
                    SET ref_id = 'ffffffffffffffff'
                    WHERE singleton = 1;
                    """
                );
                break;
            case "head-digest":
                ExecuteSql(
                    databasePath,
                    """
                    UPDATE store_metadata
                    SET head_sha256 =
                        'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff'
                    WHERE singleton = 1;
                    """
                );
                break;
            case "head-scope-with-valid-digest":
                ReplaceHeadWithForeignScope(
                    databasePath,
                    created.InitialHead
                );
                break;
            default:
                throw new InvalidOperationException(corruption);
        }

        HistoryTimelineOpenResult.Invalid opened = Assert.IsType<
            HistoryTimelineOpenResult.Invalid
        >(HistoryTimelineFactory.Open(
            journal.ReadView,
            _estimator
        ));
        Assert.Equal("TimelineStoreInvalid", opened.Code);
        Assert.IsType<HistoryTimelineInspectResult.Invalid>(
            HistoryTimelineMaintenance.Verify(
                path,
                journal.BranchRefId
            )
        );
    }

    [Theory]
    [InlineData("policy_count")]
    [InlineData("row_count")]
    public void StoredCountsAreFullVerifyEvidenceNotNormalOpenAuthority(
        string column
    ) {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        string databasePath = new HistoryTimelinePaths(
            path,
            journal.BranchRefId
        ).TimelineDatabasePath(created.Locator.ActiveTimelineId);
        ExecuteSql(
            databasePath,
            $"UPDATE store_metadata SET {column} = {column} + 1 WHERE singleton = 1;"
        );

        using (HistoryTimelineHandle handle = Assert.IsType<
                   HistoryTimelineOpenResult.Opened
               >(HistoryTimelineFactory.Open(
                   journal.ReadView,
                   _estimator
               )).Handle) {
            Assert.IsType<HistoryTimelineSnapshotResult.Available>(
                handle.Reader.ReadSnapshot()
            );
        }
        Assert.IsType<HistoryTimelineInspectResult.Invalid>(
            HistoryTimelineMaintenance.Verify(
                path,
                journal.BranchRefId
            )
        );
    }

    [Theory]
    [InlineData("predecessor")]
    [InlineData("historical-policy")]
    public void VerifyRejectsHistoricalReferenceOrphans(
        string corruption
    ) {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        string initialDigest;
        using (HistoryTimelineHandle handle = Open(journal)) {
            TimelineHeadRef empty = Assert.IsType<
                HistoryTimelineSnapshotResult.Available
            >(handle.Reader.ReadSnapshot()).Head;
            initialDigest = empty.ActivePartitionPolicyDigest;
            _ = journal.AppendObservation("reference row one");
            _ = CommitNextDescriptor(handle, journal);
            _ = journal.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text("reference row two")
                ]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            TimelineHeadRef two = CommitNextRow(handle, journal);
            if (corruption == "historical-policy") {
                PartitionPolicyRevision next = NextPolicy(
                    created.Locator.ActiveTimelineId
                );
                Assert.IsType<HistoryTimelinePolicyPutResult.Stored>(
                    handle.Coordinator.PutPolicy(next)
                );
                Assert.IsType<HistoryTimelinePolicyCasResult.Applied>(
                    handle.Coordinator.CompareExchangePolicy(
                        two,
                        next.PolicyDigest
                    )
                );
            }
        }
        string databasePath = new HistoryTimelinePaths(
            path,
            journal.BranchRefId
        ).TimelineDatabasePath(created.Locator.ActiveTimelineId);
        ExecuteSql(
            databasePath,
            corruption == "predecessor"
                ? """
                    PRAGMA foreign_keys = OFF;
                    UPDATE rows
                    SET previous_row_id =
                        'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff'
                    WHERE previous_row_id IS NOT NULL;
                    """
                : $"""
                    DELETE FROM policies
                    WHERE policy_digest = '{initialDigest}';
                    UPDATE store_metadata
                    SET policy_count = policy_count - 1
                    WHERE singleton = 1;
                    """
        );

        Assert.IsType<HistoryTimelineInspectResult.Invalid>(
            HistoryTimelineMaintenance.Verify(
                path,
                journal.BranchRefId
            )
        );
    }

    [Fact]
    public void ReadOnlyOpenRejectsNonDeleteJournalMode() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        string databasePath = new HistoryTimelinePaths(
            path,
            journal.BranchRefId
        ).TimelineDatabasePath(created.Locator.ActiveTimelineId);
        ExecuteSql(databasePath, "PRAGMA journal_mode = WAL;");

        Assert.IsType<HistoryTimelineReaderOpenResult.Invalid>(
            HistoryTimelineMaintenance.OpenReader(
                path,
                journal.BranchRefId
            )
        );
    }

    [Fact]
    public void CommitRawFenceRunsOnceInsideWriterLock() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        _ = journal.AppendObservation("raw fence fixture");
        _ = journal.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("answer")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        _ = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.CreateForTest(
            journal.ReadView,
            InitialPolicy(),
            HistoryTimelineStorageLimits.Production,
            _estimator
        ));
        bool writerLockAcquired = false;
        var hooks = new HistoryTimelinePersistenceTestHooks(
            AfterAppendWriterLockAcquired: () =>
                writerLockAcquired = true
        );
        using HistoryTimelineHandle handle = Assert.IsType<
            HistoryTimelineOpenResult.Opened
        >(HistoryTimelineFactory.OpenForTest(
            journal.ReadView,
            HistoryTimelineStorageLimits.Production,
            hooks,
            _estimator
        )).Handle;
        TimelineHeadRef expected = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(handle.Reader.ReadSnapshot()).Head;
        OnlineSelectedRawCapture capture = Assert.IsType<
            OnlineSelectedRawCaptureResult.Captured
        >(handle.Coordinator.CaptureOnline(
            expected,
            journal.ReadView
        )).Capture;
        HistoryTimelinePlanResult.Selected planned = Assert.IsType<
            HistoryTimelinePlanResult.Selected
        >(handle.Coordinator.PlanNextRow(expected, capture));
        var fence = new LockObservingRawFence(
            journal.ReadView,
            journal.BranchRefId,
            capture.CapturedHead,
            () => writerLockAcquired
        );
        var candidate = new HistoryRowCommitCandidate(
            planned.Candidate.Proposal,
            fence
        );

        Assert.IsType<HistoryTimelineCommitResult.Committed>(
            handle.Coordinator.CommitRow(candidate)
        );
        Assert.Equal(1, fence.CallCount);
        Assert.True(fence.WriterLockObserved);
    }

    [Fact]
    public void ReaderPagesNewestToOldestAcrossOpaqueCursor() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        _ = Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                journal.ReadView,
                InitialPolicy(),
                _estimator
            )
        );
        using HistoryTimelineHandle handle = Open(journal);
        var committedNewestLast = new List<HistoryRowId>();
        TimelineHeadRef head = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(handle.Reader.ReadSnapshot()).Head;
        for (int index = 0; index < 3; index++) {
            _ = journal.AppendObservation($"page observation {index}");
            _ = journal.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"page answer {index}")
                ]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            OnlineSelectedRawCapture capture = Assert.IsType<
                OnlineSelectedRawCaptureResult.Captured
            >(handle.Coordinator.CaptureOnline(
                head,
                journal.ReadView
            )).Capture;
            HistoryTimelinePlanResult.Selected selected = Assert.IsType<
                HistoryTimelinePlanResult.Selected
            >(handle.Coordinator.PlanNextRow(head, capture));
            committedNewestLast.Add(
                selected.Candidate.Proposal.Descriptor.RowId
            );
            head = Assert.IsType<HistoryTimelineCommitResult.Committed>(
                handle.Coordinator.CommitRow(selected.Candidate)
            ).Head;
        }

        var paged = new List<HistoryRowId>();
        HistoryTimelinePathCursor? cursor = null;
        HistoryTimelinePathCursor? firstContinuation = null;
        do {
            HistoryTimelinePathPage page = Assert.IsType<
                HistoryTimelinePathPageResult.Page
            >(handle.Reader.ReadSelectedPathPage(
                head,
                cursor,
                maximumRows: 1
            )).Value;
            paged.Add(Assert.Single(page.Rows).Descriptor.RowId);
            cursor = page.Next;
            firstContinuation ??= cursor;
        } while (cursor is not null);

        committedNewestLast.Reverse();
        Assert.Equal(committedNewestLast, paged);
        Assert.NotNull(firstContinuation);
        TimelineHeadRef advanced = Assert.IsType<
            HistoryTimelinePolicyCasResult.Applied
        >(handle.Coordinator.CompareExchangePolicy(
            head,
            head.ActivePartitionPolicyDigest
        )).Head;
        Assert.IsType<HistoryTimelinePathPageResult.StaleTimelineHead>(
            handle.Reader.ReadSelectedPathPage(
                advanced,
                firstContinuation,
                maximumRows: 1
            )
        );
    }

    [Fact]
    public void V2MutableSelectedPathCommitsAndVerifies65537Rows() {
        const int firstMilestone = 4_097;
        const int secondMilestone = firstMilestone * 2;
        const int rowCount = 65_537;
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        var policy = PartitionPolicyRevision.Create(
            created.Locator.ActiveTimelineId,
            HistoryPartitionAlgorithms
                .FirstReplaySafeBoundaryAtTargetV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            new HistoryLoadUnit(1),
            maxRawEvents: 8,
            maxRenderedBytes: 1024 * 1024
        );
        var ledger = OpenLedger(
            path,
            journal.BranchRefId,
            created.Locator
        );
        TimelineHeadRef head = ReadHead(ledger);
        string databasePath = new HistoryTimelinePaths(
            path,
            journal.BranchRefId
        ).TimelineDatabasePath(created.Locator.ActiveTimelineId);
        var elapsed = Stopwatch.StartNew();
        long firstBytes = 0;
        long secondBytes = 0;
        HistorySegmentDescriptor? predecessor = null;
        HistorySegmentDescriptor? root = null;
        HistorySegmentDescriptor? penultimate = null;
        SessionContextAnchorSetupReferences setups = Setups();
        for (int index = 0; index < rowCount; index++) {
            EventAddress start = Address((ulong)(1_000 + index));
            EventAddress end = Address((ulong)(1_001 + index));
            var point = new HistoryPartitionPoint(
                head.TimelineId,
                policy.PolicyDigest,
                start,
                end,
                setups,
                setups,
                index,
                index + 1,
                new HistoryLoadUnit(1),
                rawEventCount: 1,
                measuredRenderedUtf8Bytes: 1
            );
            HistorySegmentDescriptor descriptor =
                HistorySegmentDescriptorFactory.Create(
                    point,
                    new BoundHistorySegmentRange(
                        journal.BranchRefId,
                        start,
                        end,
                        setups,
                        setups,
                        index,
                        index + 1,
                        rawEventCount: 1,
                        new string('a', 64)
                    ),
                    policy,
                    predecessor
                );
            var fence = new FixedRawFence(
                journal.BranchRefId,
                end
            );
            var proposal = new HistoryRowProposal(
                head,
                end,
                descriptor
            );
            head = Assert.IsType<HistoryTimelineCommitResult.Committed>(
                ledger.CommitRow(new HistoryRowCommitCandidate(
                    proposal,
                    fence
                ))
            ).Head;
            root ??= descriptor;
            if (index == rowCount - 2) {
                penultimate = descriptor;
            }
            predecessor = descriptor;
            if (index + 1 == firstMilestone) {
                firstBytes = new FileInfo(databasePath).Length;
                Console.WriteLine(
                    $"C3D rows={firstMilestone} bytes={firstBytes} elapsedMs={elapsed.ElapsedMilliseconds}"
                );
            }
            else if (index + 1 == secondMilestone) {
                secondBytes = new FileInfo(databasePath).Length;
                Console.WriteLine(
                    $"C3D rows={secondMilestone} bytes={secondBytes} elapsedMs={elapsed.ElapsedMilliseconds}"
                );
            }
        }

        long finalBytes = new FileInfo(databasePath).Length;
        Console.WriteLine(
            $"C3D rows={rowCount} bytes={finalBytes} elapsedMs={elapsed.ElapsedMilliseconds}"
        );
        Assert.True(firstBytes > 0);
        Assert.InRange(secondBytes, firstBytes, checked(firstBytes * 3));
        Assert.InRange(finalBytes, secondBytes, checked(firstBytes * 20));
        Assert.Equal(rowCount, ReadStoredRowCount(databasePath));
        Assert.Equal(rowCount, head.SelectedPathCount);
        using (SqliteConnection countConnection = OpenSqlite(databasePath))
        using (SqliteCommand countCommand = countConnection.CreateCommand()) {
            countCommand.CommandText =
                "SELECT COUNT(*) FROM current_selected_path_merkle;";
            long commitmentNodes = Convert.ToInt64(
                countCommand.ExecuteScalar());
            Assert.InRange(
                commitmentNodes,
                (long)rowCount,
                checked((2L * rowCount) - 1));
        }
        Assert.Equal(predecessor!.RowId, head.HeadRowId);
        Assert.IsType<HistoryTimelineStoreReadResult<TimelineHeadRef>.Found>(
            ledger.VerifyFully()
        );

        long publicRows = 0;
        HistoryRowId? publicNewest = null;
        HistoryRowId? publicOldest = null;
        using (HistoryTimelineHandle reopened = Open(journal)) {
            Assert.Equal(head, Assert.IsType<
                HistoryTimelineSnapshotResult.Available
            >(reopened.Reader.ReadSnapshot()).Head);
            HistoryTimelinePathCursor? cursor = null;
            do {
                HistoryTimelinePathPage page = Assert.IsType<
                    HistoryTimelinePathPageResult.Page
                >(reopened.Reader.ReadSelectedPathPage(
                    head,
                    cursor)).Value;
                publicNewest ??= page.Rows[0].Descriptor.RowId;
                publicOldest = page.Rows[^1].Descriptor.RowId;
                publicRows = checked(publicRows + page.Rows.Count);
                cursor = page.Next;
            } while (cursor is not null);
        }
        Assert.Equal(rowCount, publicRows);
        Assert.Equal(predecessor.RowId, publicNewest);
        Assert.Equal(root!.RowId, publicOldest);

        Assert.NotNull(penultimate);
        TimelineHeadRef rewound = Assert.IsType<
            HistoryTimelineReconcileResult.Reconciled
        >(ledger.ReconcileSelectedPath(
            new HistoryTimelineReconcileCandidate(
                head,
                penultimate.RowId,
                new FixedRawFence(
                    journal.BranchRefId,
                    penultimate.EndInclusive)))).Head;
        EventAddress siblingStart = penultimate.EndInclusive;
        EventAddress siblingEnd = Address(9_000_001);
        var siblingPoint = new HistoryPartitionPoint(
            head.TimelineId,
            policy.PolicyDigest,
            siblingStart,
            siblingEnd,
            setups,
            setups,
            rowCount - 1,
            rowCount,
            new HistoryLoadUnit(1),
            rawEventCount: 1,
            measuredRenderedUtf8Bytes: 1);
        HistorySegmentDescriptor sibling =
            HistorySegmentDescriptorFactory.Create(
                siblingPoint,
                new BoundHistorySegmentRange(
                    journal.BranchRefId,
                    siblingStart,
                    siblingEnd,
                    setups,
                    setups,
                    rowCount - 1,
                    rowCount,
                    rawEventCount: 1,
                    new string('b', 64)),
                policy,
                penultimate);
        TimelineHeadRef siblingHead = Assert.IsType<
            HistoryTimelineCommitResult.Committed
        >(ledger.CommitRow(new HistoryRowCommitCandidate(
            new HistoryRowProposal(rewound, siblingEnd, sibling),
            new FixedRawFence(journal.BranchRefId, siblingEnd)))).Head;
        TimelineHeadRef common = Assert.IsType<
            HistoryTimelineReconcileResult.Reconciled
        >(ledger.ReconcileSelectedPath(
            new HistoryTimelineReconcileCandidate(
                siblingHead,
                penultimate.RowId,
                new FixedRawFence(
                    journal.BranchRefId,
                    penultimate.EndInclusive)))).Head;
        TimelineHeadRef reselected = Assert.IsType<
            HistoryTimelineCommitResult.Committed
        >(ledger.CommitRow(new HistoryRowCommitCandidate(
            new HistoryRowProposal(
                common,
                predecessor.EndInclusive,
                predecessor),
            new FixedRawFence(
                journal.BranchRefId,
                predecessor.EndInclusive)))).Head;
        Assert.Equal(predecessor.RowId, reselected.HeadRowId);
        Assert.Equal(rowCount + 1, ReadStoredRowCount(databasePath));

        string backup = NewPath();
        HistoryTimelineBackupResult.Created backedUp = Assert.IsType<
            HistoryTimelineBackupResult.Created
        >(HistoryTimelineMaintenance.Backup(
            path,
            journal.BranchRefId,
            backup));
        Assert.Equal(reselected, backedUp.Manifest.Head);
        Assert.IsType<HistoryTimelineRestoreResult.Restored>(
            HistoryTimelineMaintenance.Restore(
                path,
                journal.BranchRefId,
                new HistoryTimelineActiveConfirmation(
                    created.Locator,
                    reselected),
                backup));
        using HistoryTimelineHandle restored = Open(journal);
        Assert.Equal(reselected, Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(restored.Reader.ReadSnapshot()).Head);
        Assert.IsType<HistoryTimelineReaderRowResult.Selected>(
            restored.Reader.ReadSelectedRow(
                reselected,
                predecessor.RowId));
        Assert.IsType<HistoryTimelineReaderRowResult.NotOnSelectedPath>(
            restored.Reader.ReadSelectedRow(
                reselected,
                sibling.RowId));
    }

    [Fact]
    public void PathPageRowAndByteCapsAcceptExactAndRejectCapPlusOne() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        _ = journal.AppendObservation("path cap fixture");
        _ = journal.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("path cap answer")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        _ = Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                journal.ReadView,
                InitialPolicy(),
                _estimator
            )
        );
        int descriptorBytes;
        using (HistoryTimelineHandle handle = Open(journal)) {
            descriptorBytes = CommitNextDescriptor(handle, journal)
                .ToCanonicalBytes()
                .Length;
        }

        using (HistoryTimelineHandle exact = Assert.IsType<
                   HistoryTimelineOpenResult.Opened
               >(HistoryTimelineFactory.OpenForTest(
                   journal.ReadView,
                   HistoryTimelineStorageLimits.Production with {
                       MaximumPathPageUtf8Bytes = descriptorBytes
                   },
                   _estimator
               )).Handle) {
            TimelineHeadRef head = Assert.IsType<
                HistoryTimelineSnapshotResult.Available
            >(exact.Reader.ReadSnapshot()).Head;
            Assert.IsType<HistoryTimelinePathPageResult.Page>(
                exact.Reader.ReadSelectedPathPage(
                    head,
                    maximumRows: HistoryTimelineStoreLimits
                        .MaximumPathPageRows
                )
            );
            Assert.IsType<HistoryTimelinePathPageResult.Invalid>(
                exact.Reader.ReadSelectedPathPage(
                    head,
                    maximumRows: HistoryTimelineStoreLimits
                        .MaximumPathPageRows + 1
                )
            );
        }

        using HistoryTimelineHandle over = Assert.IsType<
            HistoryTimelineOpenResult.Opened
        >(HistoryTimelineFactory.OpenForTest(
            journal.ReadView,
            HistoryTimelineStorageLimits.Production with {
                MaximumPathPageUtf8Bytes = descriptorBytes - 1
            },
            _estimator
        )).Handle;
        TimelineHeadRef overHead = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(over.Reader.ReadSnapshot()).Head;
        HistoryTimelinePathPageResult.Invalid invalid = Assert.IsType<
            HistoryTimelinePathPageResult.Invalid
        >(over.Reader.ReadSelectedPathPage(overHead));
        Assert.Equal("PathPageByteLimitExceeded", invalid.Code);
    }

    [Theory]
    [InlineData("ordinal-gap")]
    [InlineData("wrong-end")]
    [InlineData("missing-middle")]
    public void VerifyRejectsCorruptCurrentSelectedPath(string corruption) {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        var rows = new List<HistorySegmentDescriptor>();
        using (HistoryTimelineHandle handle = Open(journal)) {
            for (int index = 0; index < 3; index++) {
                _ = journal.AppendObservation(
                    $"selected path recurrence observation {index}"
                );
                _ = journal.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text(
                            $"selected path recurrence answer {index}"
                        )
                    ]),
                    new CompletionDescriptor("import", "v1", "model-A")
                );
                rows.Add(CommitNextDescriptor(handle, journal));
            }
        }
        string databasePath = new HistoryTimelinePaths(
            path,
            journal.BranchRefId
        ).TimelineDatabasePath(created.Locator.ActiveTimelineId);
        string sql = corruption switch {
            "ordinal-gap" =>
                $"UPDATE current_selected_path SET ordinal = 10 WHERE row_id = '{rows[1].RowId.Value}';",
            "wrong-end" =>
                $"UPDATE current_selected_path SET end_address = randomblob(length(end_address)) WHERE row_id = '{rows[1].RowId.Value}';",
            "missing-middle" =>
                $"DELETE FROM current_selected_path WHERE row_id = '{rows[1].RowId.Value}';",
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        ExecuteSql(databasePath, sql);

        Assert.IsType<HistoryTimelineInspectResult.Invalid>(
            HistoryTimelineMaintenance.Verify(path, journal.BranchRefId)
        );
    }

    [Fact]
    public void SelectedReadsRejectCurrentPathTailBeyondWholeHead() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        HistorySegmentDescriptor a1;
        HistorySegmentDescriptor a2;
        TimelineHeadRef rewound;
        using (HistoryTimelineHandle handle = Open(journal)) {
            _ = journal.AppendObservation("selected path authority A1");
            a1 = CommitNextDescriptor(handle, journal);
            EventAddress a2Raw = journal.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text("selected path authority A2")
                ]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            a2 = CommitNextDescriptor(handle, journal);
            TimelineHeadRef a2Head = Assert.IsType<
                HistoryTimelineSnapshotResult.Available
            >(handle.Reader.ReadSnapshot()).Head;
            Assert.True(journal.MoveCurrentHeadForTest(
                a2Raw,
                a1.EndInclusive
            ));
            rewound = Assert.IsType<
                HistoryTimelineReconcileResult.Reconciled
            >(handle.Coordinator.ReconcileSelectedPath(
                a2Head,
                journal.ReadView
            )).Head;
        }
        string databasePath = new HistoryTimelinePaths(
            path,
            journal.BranchRefId
        ).TimelineDatabasePath(created.Locator.ActiveTimelineId);
        InsertCurrentPathRow(
            databasePath,
            ordinal: 1,
            a2.RowId,
            a2.EndInclusive
        );

        Assert.IsType<HistoryTimelineOpenResult.Invalid>(
            HistoryTimelineFactory.Open(journal.ReadView, _estimator));
        Assert.IsType<SelectedHistoryRowResult.Invalid>(OpenLedger(
            path,
            journal.BranchRefId,
            created.Locator
        ).ReadSelectedRow(rewound, a2.RowId));
        Assert.IsType<HistoryTimelineStorePathPageResult.Invalid>(OpenLedger(
            path,
            journal.BranchRefId,
            created.Locator
        ).ReadSelectedPathPage(
            rewound,
            startAt: null,
            HistoryTimelineStoreLimits.MaximumPathPageRows));
        Assert.IsType<HistoryTimelineBoundaryProbeOpenResult.Invalid>(
            OpenLedger(
                path,
                journal.BranchRefId,
                created.Locator
            ).OpenBoundaryProbe(rewound)
        );
        Assert.IsType<HistoryTimelineInspectResult.Invalid>(
            HistoryTimelineMaintenance.Verify(path, journal.BranchRefId)
        );
    }

    [Theory]
    [InlineData("middle-delete")]
    [InlineData("ordinal-swap")]
    [InlineData("off-branch-insert")]
    public void NormalSelectedOperationsLatchAnyPathMutationInvalid(
        string corruption
    ) {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                journal.ReadView, InitialPolicy(), _estimator));
        HistorySegmentDescriptor first;
        HistorySegmentDescriptor second;
        HistorySegmentDescriptor third;
        TimelineHeadRef head;
        using (HistoryTimelineHandle handle = Open(journal)) {
            _ = journal.AppendObservation("path commitment first");
            _ = journal.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("first")]),
                new CompletionDescriptor("import", "v1", "model-A"));
            first = CommitNextDescriptor(handle, journal);
            _ = journal.AppendObservation("path commitment second");
            _ = journal.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("second")]),
                new CompletionDescriptor("import", "v1", "model-A"));
            second = CommitNextDescriptor(handle, journal);
            _ = journal.AppendObservation("path commitment third");
            _ = journal.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("third")]),
                new CompletionDescriptor("import", "v1", "model-A"));
            third = CommitNextDescriptor(handle, journal);
            head = Assert.IsType<HistoryTimelineSnapshotResult.Available>(
                handle.Reader.ReadSnapshot()).Head;
        }
        string databasePath = new HistoryTimelinePaths(
            path, journal.BranchRefId).TimelineDatabasePath(
                created.Locator.ActiveTimelineId);
        string sql = corruption switch {
            "middle-delete" =>
                $"DELETE FROM current_selected_path WHERE row_id = '{second.RowId.Value}';",
            "ordinal-swap" => $"""
                UPDATE current_selected_path SET ordinal = 100
                WHERE row_id = '{first.RowId.Value}';
                UPDATE current_selected_path SET ordinal = 0
                WHERE row_id = '{second.RowId.Value}';
                UPDATE current_selected_path SET ordinal = 1
                WHERE row_id = '{first.RowId.Value}';
                """,
            "off-branch-insert" => $"""
                UPDATE current_selected_path
                SET row_id = '{first.RowId.Value}',
                    previous_row_id = NULL,
                    leaf_digest = '{new string('0', 64)}'
                WHERE row_id = '{second.RowId.Value}';
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        if (corruption == "off-branch-insert") {
            // Keep a unique row locator while making the selected assignment
            // disagree with its immutable predecessor recurrence.
            sql = $"""
                UPDATE current_selected_path
                SET previous_row_id = NULL,
                    leaf_digest = '{new string('0', 64)}'
                WHERE row_id = '{second.RowId.Value}';
                """;
        }
        ExecuteSql(databasePath, sql);

        Assert.IsType<HistoryTimelineOpenResult.Invalid>(
            HistoryTimelineFactory.Open(journal.ReadView, _estimator));
        Assert.IsType<SelectedHistoryRowResult.Invalid>(OpenLedger(
            path, journal.BranchRefId, created.Locator)
            .ReadSelectedRow(head, third.RowId));
        Assert.IsType<HistoryTimelineStorePathPageResult.Invalid>(OpenLedger(
            path, journal.BranchRefId, created.Locator)
            .ReadSelectedPathPage(
                head,
                startAt: null,
                HistoryTimelineStoreLimits.MaximumPathPageRows));
        var reconcileCandidate = new HistoryTimelineReconcileCandidate(
            head,
            third.RowId,
            new FixedRawFence(
                journal.BranchRefId,
                head.SelectedRawHeadAtCommit
                    ?? throw new InvalidDataException(
                        "A committed head requires a raw fence.")));
        Assert.IsType<HistoryTimelineReconcileResult.Invalid>(
            OpenLedger(path, journal.BranchRefId, created.Locator)
                .ReconcileSelectedPath(reconcileCandidate));
    }

    [Theory]
    [InlineData("middle-delete")]
    [InlineData("ordinal")]
    [InlineData("end")]
    [InlineData("leaf")]
    public void SelectedPathGuardBlocksEveryNormalReadAndWriterWithoutMutation(
        string corruption
    ) {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                journal.ReadView, InitialPolicy(), _estimator));
        HistorySegmentDescriptor first;
        HistorySegmentDescriptor second;
        HistorySegmentDescriptor third;
        TimelineHeadRef head;
        HistoryRowCommitCandidate appendCandidate;
        using (HistoryTimelineHandle handle = Open(journal)) {
            _ = journal.AppendObservation("guard first");
            _ = journal.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("first")]),
                new CompletionDescriptor("import", "v1", "model-A"));
            first = CommitNextDescriptor(handle, journal);
            _ = journal.AppendObservation("guard second");
            _ = journal.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("second")]),
                new CompletionDescriptor("import", "v1", "model-A"));
            second = CommitNextDescriptor(handle, journal);
            _ = journal.AppendObservation("guard third");
            _ = journal.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("third")]),
                new CompletionDescriptor("import", "v1", "model-A"));
            third = CommitNextDescriptor(handle, journal);
            head = Assert.IsType<HistoryTimelineSnapshotResult.Available>(
                handle.Reader.ReadSnapshot()).Head;

            _ = journal.AppendObservation("guard pending append");
            _ = journal.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("pending")]),
                new CompletionDescriptor("import", "v1", "model-A"));
            OnlineSelectedRawCapture capture = Assert.IsType<
                OnlineSelectedRawCaptureResult.Captured>(
                handle.Coordinator.CaptureOnline(
                    head,
                    journal.ReadView
                )).Capture;
            appendCandidate = Assert.IsType<
                HistoryTimelinePlanResult.Selected>(
                handle.Coordinator.PlanNextRow(head, capture)
            ).Candidate;
        }

        string databasePath = new HistoryTimelinePaths(
            path, journal.BranchRefId).TimelineDatabasePath(
                created.Locator.ActiveTimelineId);
        string sql = corruption switch {
            "middle-delete" => $"""
                DELETE FROM current_selected_path
                WHERE row_id = '{second.RowId.Value}';
                """,
            "ordinal" => $"""
                UPDATE current_selected_path
                SET ordinal = 100
                WHERE row_id = '{first.RowId.Value}';
                """,
            "end" => $"""
                UPDATE current_selected_path
                SET end_address = X'FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF'
                WHERE row_id = '{second.RowId.Value}';
                """,
            "leaf" => $"""
                UPDATE current_selected_path
                SET leaf_digest = '{new string('0', 64)}'
                WHERE row_id = '{second.RowId.Value}';
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        ExecuteSql(databasePath, sql);
        byte[] corruptedBytes = File.ReadAllBytes(databasePath);
        PartitionPolicyRevision next = NextPolicy(
            created.Locator.ActiveTimelineId);

        Assert.IsType<HistoryTimelineStoreReadResult<
            TimelineHeadRef>.Invalid>(OpenLedger(
                path, journal.BranchRefId, created.Locator).ReadSnapshot());
        Assert.IsType<HistoryTimelineStoreReadResult<
            PartitionPolicyRevision>.Invalid>(OpenLedger(
                path, journal.BranchRefId, created.Locator).ReadPolicy(
                    head.ActivePartitionPolicyDigest));
        Assert.IsType<HistoryTimelineStoreReadResult<
            HistorySegmentDescriptor>.Invalid>(OpenLedger(
                path, journal.BranchRefId, created.Locator).ReadRow(
                    third.RowId));
        Assert.IsType<HistoryTimelinePolicyPutResult.Invalid>(OpenLedger(
            path, journal.BranchRefId, created.Locator).PutPolicy(next));
        Assert.IsType<HistoryTimelinePolicyCasResult.Invalid>(OpenLedger(
            path, journal.BranchRefId, created.Locator)
            .CompareExchangePolicy(head, next.PolicyDigest));
        Assert.IsType<HistoryTimelineCommitResult.Invalid>(OpenLedger(
            path, journal.BranchRefId, created.Locator)
            .CommitRow(appendCandidate));

        Assert.Equal(corruptedBytes, File.ReadAllBytes(databasePath));
        Assert.False(File.Exists(databasePath + "-journal"));
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-shm"));
    }

    [Fact]
    public void VerifyKeysetPagesUsePrimaryKeyRangeSearches() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        string databasePath = new HistoryTimelinePaths(
            path,
            journal.BranchRefId
        ).TimelineDatabasePath(created.Locator.ActiveTimelineId);
        (string First, string Next, string Table, string Key)[] cases = [
            (
                SqliteHistoryTimelineLedger.VerifyRowsFirstPageSql,
                SqliteHistoryTimelineLedger.VerifyRowsNextPageSql,
                "rows",
                "row_id"
            )
        ];

        foreach ((string first, string next, string table, string key)
                 in cases) {
            Assert.DoesNotContain(
                "WHERE",
                first,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.Contains(
                $"WHERE {key} > $after",
                next,
                StringComparison.Ordinal
            );
            string detail = Assert.Single(ReadQueryPlan(
                databasePath,
                next,
                "0000000000000000000000000000000000000000000000000000000000000000"
            ));
            Assert.Contains(
                $"SEARCH {table} USING PRIMARY KEY ({key}>?)",
                detail,
                StringComparison.Ordinal
            );
        }
    }

    [Fact]
    public void NewRefGetsNewTimelineIdentityAndIndependentLocator() {
        string path = NewPath();
        RefId mainRef;
        EventAddress mainHead;
        TimelineId mainTimeline;
        using (SessionJournalEngine main = CreateJournal(path)) {
            mainRef = main.BranchRefId;
            mainHead = main.ReadView.ReadCurrentHead()!.Value;
            mainTimeline = Assert.IsType<
                HistoryTimelineCreateResult.Created
            >(HistoryTimelineFactory.Create(
                main.ReadView,
                InitialPolicy(),
                _estimator
            )).Locator.ActiveTimelineId;
        }
        RefId featureRef;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            featureRef = journal.ForkBranch(
                "feature",
                mainRef,
                mainHead
            ).Unwrap();
        }
        TimelineId featureTimeline;
        using (SessionJournalEngine feature =
               SessionJournalEngine.Open(path, "feature")) {
            featureTimeline = Assert.IsType<
                HistoryTimelineCreateResult.Created
            >(HistoryTimelineFactory.Create(
                feature.ReadView,
                InitialPolicy(),
                _estimator
            )).Locator.ActiveTimelineId;
        }

        Assert.NotEqual(mainRef, featureRef);
        Assert.NotEqual(mainTimeline, featureTimeline);
        Assert.Equal(
            mainTimeline,
            HistoryTimelineFactory.ReadLocator(
                new HistoryTimelinePaths(path, mainRef)
            ).ActiveTimelineId
        );
        Assert.Equal(
            featureTimeline,
            HistoryTimelineFactory.ReadLocator(
                new HistoryTimelinePaths(path, featureRef)
            ).ActiveTimelineId
        );
    }

    [Fact]
    public async Task ConcurrentCreateForSameRefHasOneIdentityWinner() {
        string path = NewPath();
        using (SessionJournalEngine created = CreateJournal(path)) { }
        using SessionJournalEngine first =
            SessionJournalEngine.OpenReadOnly(path);
        using SessionJournalEngine second =
            SessionJournalEngine.OpenReadOnly(path);

        Task<HistoryTimelineCreateResult> firstCreate = Task.Run(
            () => HistoryTimelineFactory.Create(
                first.ReadView,
                InitialPolicy(),
                _estimator
            )
        );
        Task<HistoryTimelineCreateResult> secondCreate = Task.Run(
            () => HistoryTimelineFactory.Create(
                second.ReadView,
                InitialPolicy(),
                _estimator
            )
        );
        HistoryTimelineCreateResult[] results = await Task.WhenAll(
            firstCreate,
            secondCreate
        );

        Assert.Single(results.OfType<
            HistoryTimelineCreateResult.Created
        >());
        Assert.All(
            results,
            result => Assert.True(
                result is HistoryTimelineCreateResult.Created
                    or HistoryTimelineCreateResult.AlreadyExists
                    or HistoryTimelineCreateResult.Busy
            )
        );
        Assert.IsType<HistoryTimelineCreateResult.AlreadyExists>(
            HistoryTimelineFactory.Create(
                first.ReadView,
                InitialPolicy(),
                _estimator
            )
        );
    }
    [Fact]
    public void V2RowsPoliciesAndRestoreHaveNoCodeOwnedLifetimeCap() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        TimelineHeadRef head;
        using (HistoryTimelineHandle handle = Open(journal)) {
            Assert.IsType<HistoryTimelinePolicyPutResult.Stored>(
                handle.Coordinator.PutPolicy(
                    NextPolicy(created.Locator.ActiveTimelineId)
                )
            );
            for (int index = 0; index < 3; index++) {
                _ = journal.AppendObservation($"uncapped row {index}");
                _ = journal.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"uncapped answer {index}")
                    ]),
                    new CompletionDescriptor("import", "v1", "model-A")
                );
                _ = CommitNextDescriptor(handle, journal);
            }
            head = Assert.IsType<
                HistoryTimelineSnapshotResult.Available
            >(handle.Reader.ReadSnapshot()).Head;
        }

        string backup = NewPath();
        HistoryTimelineBackupResult.Created backedUp = Assert.IsType<
            HistoryTimelineBackupResult.Created
        >(HistoryTimelineMaintenance.Backup(
            path,
            journal.BranchRefId,
            backup
        ));
        Assert.True(backedUp.Manifest.DatabaseBytes > 0);
        Assert.IsType<HistoryTimelineRestoreResult.Restored>(
            HistoryTimelineMaintenance.Restore(
                path,
                journal.BranchRefId,
                new HistoryTimelineActiveConfirmation(
                    created.Locator,
                    head
                ),
                backup
            )
        );
    }
    [Fact]
    public void DurableBranchReconcileReusesPreviouslyCommittedSnapshot() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        HistorySegmentDescriptor a1;
        HistorySegmentDescriptor a2;
        HistorySegmentDescriptor b2;
        TimelineHeadRef b2Head;
        EventAddress a2Raw;
        EventAddress b2Raw;
        using (HistoryTimelineHandle handle = Open(journal)) {
            _ = journal.AppendObservation("A1");
            a1 = CommitNextDescriptor(handle, journal);
            a2Raw = journal.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("A2")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            a2 = CommitNextDescriptor(handle, journal);
            TimelineHeadRef a2Head = Assert.IsType<
                HistoryTimelineSnapshotResult.Available
            >(handle.Reader.ReadSnapshot()).Head;

            Assert.True(journal.MoveCurrentHeadForTest(
                a2Raw,
                a1.EndInclusive
            ));
            TimelineHeadRef rewound = Assert.IsType<
                HistoryTimelineReconcileResult.Reconciled
            >(handle.Coordinator.ReconcileSelectedPath(
                a2Head,
                journal.ReadView
            )).Head;
            Assert.Equal(a1.RowId, rewound.HeadRowId);

            b2Raw = journal.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("B2")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            b2 = CommitNextDescriptor(handle, journal);
            b2Head = Assert.IsType<
                HistoryTimelineSnapshotResult.Available
            >(handle.Reader.ReadSnapshot()).Head;
        }

        string databasePath = new HistoryTimelinePaths(
            path,
            journal.BranchRefId
        ).TimelineDatabasePath(created.Locator.ActiveTimelineId);
        long beforeSwitch = ReadStoredRowCount(databasePath);
        Assert.True(journal.MoveCurrentHeadForTest(b2Raw, a2Raw));
        TimelineHeadRef restored;
        using (HistoryTimelineHandle replay = Open(journal)) {
            TimelineHeadRef common = Assert.IsType<
                HistoryTimelineReconcileResult.Reconciled
            >(replay.Coordinator.ReconcileSelectedPath(
                b2Head,
                journal.ReadView
            )).Head;
            Assert.Equal(a1.RowId, common.HeadRowId);
            OnlineSelectedRawCapture capture = Assert.IsType<
                OnlineSelectedRawCaptureResult.Captured
            >(replay.Coordinator.CaptureOnline(
                common,
                journal.ReadView
            )).Capture;
            HistoryRowCommitCandidate existingA2 = Assert.IsType<
                HistoryTimelinePlanResult.Selected
            >(replay.Coordinator.PlanNextRow(
                common,
                capture
            )).Candidate;
            Assert.Equal(a2, existingA2.Proposal.Descriptor);
            restored = Assert.IsType<
                HistoryTimelineCommitResult.Committed
            >(replay.Coordinator.CommitRow(existingA2)).Head;
        }

        Assert.Equal(a2.RowId, restored.HeadRowId);
        Assert.Equal(beforeSwitch, ReadStoredRowCount(databasePath));
        using HistoryTimelineHandle reopened = Open(journal);
        Assert.IsType<HistoryTimelineReaderRowResult.Selected>(
            reopened.Reader.ReadSelectedRow(restored, a1.RowId)
        );
        Assert.IsType<HistoryTimelineReaderRowResult.Selected>(
            reopened.Reader.ReadSelectedRow(restored, a2.RowId)
        );
        Assert.IsType<HistoryTimelineReaderRowResult.NotOnSelectedPath>(
            reopened.Reader.ReadSelectedRow(restored, b2.RowId)
        );
        SelectedHistoryBoundaryResult.Found exactBoundary = Assert.IsType<
            SelectedHistoryBoundaryResult.Found
        >(OpenLedger(path, journal.BranchRefId, created.Locator)
            .ReadSelectedRowAtBoundary(restored, a2.EndInclusive));
        Assert.Equal(a2.RowId, exactBoundary.Descriptor.RowId);
    }

    [Fact]
    public void OfflineReconcile_UsesOneBoundProbeAndAllowsPolicyWriterInterleave() {
        string path = NewPath();
        RefId refId;
        TimelineId timelineId;
        HistorySegmentDescriptor commonAncestor;
        TimelineHeadRef expected;
        using (SessionJournalEngine journal = CreateJournal(path)) {
            HistoryTimelineCreateResult.Created created = Assert.IsType<
                HistoryTimelineCreateResult.Created
            >(HistoryTimelineFactory.Create(
                journal.ReadView,
                InitialPolicy(),
                _estimator
            ));
            refId = journal.BranchRefId;
            timelineId = created.Locator.ActiveTimelineId;
            using HistoryTimelineHandle handle = Assert.IsType<
                HistoryTimelineOpenResult.Opened>(
                    HistoryTimelineFactory.OpenForTest(
                        journal.ReadView,
                        HistoryTimelineStorageLimits.Production,
                        new HistoryTimelinePersistenceTestHooks(
                            OnlineRawCaptureLimit: 3),
                        _estimator)).Handle;
            var rows = new List<HistorySegmentDescriptor>();
            for (int index = 0; index < 10; index++) {
                _ = journal.AppendObservation($"main-{index}");
                rows.Add(CommitNextDescriptor(handle, journal));
                _ = journal.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"main-action-{index}")
                    ]),
                    new CompletionDescriptor("import", "v1", "model-A")
                );
                rows.Add(CommitNextDescriptor(handle, journal));
            }
            commonAncestor = rows[11];
            EventAddress mainHead = journal.ReadCurrentHead()!.Value;
            Assert.True(journal.MoveCurrentHeadForTest(
                mainHead,
                commonAncestor.EndInclusive
            ));
            for (int index = 0; index < 16; index++) {
                _ = journal.AppendSystemPromptSetup($"fork-{index}");
            }
            expected = Assert.IsType<
                HistoryTimelineSnapshotResult.Available
            >(handle.Reader.ReadSnapshot()).Head;
            Assert.IsType<
                HistoryTimelineReconcileResult.OfflineBootstrapRequired
            >(handle.Coordinator.ReconcileSelectedPath(
                expected,
                journal.ReadView
            ));
        }

        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenAuditCursor(offline, pageSize: 3);
        HistoryTimelinePolicyPutResult? interleavedPut = null;
        int probeOpenCount = 0;
        int lookupQueryCount = 0;
        using HistoryTimelineHandle writer = Assert.IsType<
            HistoryTimelineOpenResult.Opened
        >(HistoryTimelineFactory.Open(
            offline.ReadView,
            _estimator
        )).Handle;
        var hooks = new HistoryTimelinePersistenceTestHooks(
            AfterBoundaryProbeOpened: () => probeOpenCount++,
            BeforeBoundaryProbeLookupQuery: () => {
                lookupQueryCount++;
                interleavedPut ??= writer.Coordinator.PutPolicy(
                    NextPolicy(timelineId)
                );
            }
        );
        using HistoryTimelineHandle reconcile = Assert.IsType<
            HistoryTimelineOpenResult.Opened
        >(HistoryTimelineFactory.OpenForTest(
            offline.ReadView,
            HistoryTimelineStorageLimits.Production,
            hooks,
            _estimator
        )).Handle;

        TimelineHeadRef reconciled = Assert.IsType<
            HistoryTimelineReconcileResult.Reconciled
        >(reconcile.Coordinator.ReconcileSelectedPathOffline(
            expected,
            cursor
        )).Head;

        Assert.Equal(commonAncestor.RowId, reconciled.HeadRowId);
        Assert.IsType<HistoryTimelinePolicyPutResult.Stored>(
            interleavedPut
        );
        Assert.Equal(1, probeOpenCount);
        Assert.InRange(
            lookupQueryCount,
            1,
            checked((int)((cursor.Authority.EventCount + 1)
                * (EventAddressCodec.EventAddressLength + 1)))
        );
    }

    [Fact]
    public async Task MixedWholeHeadRaceHasOneWinnerAndLosersDoNotInsert() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        using HistoryTimelineHandle policyHandle = Open(journal);
        _ = journal.AppendObservation("race base row");
        _ = CommitNextDescriptor(policyHandle, journal);
        TimelineHeadRef expected = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(policyHandle.Reader.ReadSnapshot()).Head;
        PartitionPolicyRevision nextPolicy = NextPolicy(
            created.Locator.ActiveTimelineId
        );
        Assert.IsType<HistoryTimelinePolicyPutResult.Stored>(
            policyHandle.Coordinator.PutPolicy(nextPolicy)
        );
        _ = journal.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("race append")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        using HistoryTimelineHandle appendHandle = Open(journal);
        OnlineSelectedRawCapture capture = Assert.IsType<
            OnlineSelectedRawCaptureResult.Captured
        >(appendHandle.Coordinator.CaptureOnline(
            expected,
            journal.ReadView
        )).Capture;
        HistoryRowCommitCandidate appendCandidate = Assert.IsType<
            HistoryTimelinePlanResult.Selected
        >(appendHandle.Coordinator.PlanNextRow(
            expected,
            capture
        )).Candidate;
        var reconcileFence = new FixedRawFence(
            journal.BranchRefId,
            capture.CapturedHead
        );
        var reconcileCandidate = new HistoryTimelineReconcileCandidate(
            expected,
            selectedRowId: null,
            reconcileFence
        );
        SqliteHistoryTimelineLedger reconcileLedger = OpenLedger(
            path,
            journal.BranchRefId,
            created.Locator
        );
        using var start = new ManualResetEventSlim(false);
        Task<HistoryTimelinePolicyCasResult> policyTask = Task.Run(() => {
            start.Wait();
            return policyHandle.Coordinator.CompareExchangePolicy(
                expected,
                nextPolicy.PolicyDigest
            );
        });
        Task<HistoryTimelineCommitResult> appendTask = Task.Run(() => {
            start.Wait();
            return appendHandle.Coordinator.CommitRow(
                appendCandidate
            );
        });
        Task<HistoryTimelineReconcileResult> reconcileTask = Task.Run(() => {
            start.Wait();
            return reconcileLedger.ReconcileSelectedPath(
                reconcileCandidate
            );
        });
        start.Set();
        await Task.WhenAll(policyTask, appendTask, reconcileTask);
        HistoryTimelinePolicyCasResult policyResult = await policyTask;
        HistoryTimelineCommitResult appendResult = await appendTask;
        HistoryTimelineReconcileResult reconcileResult =
            await reconcileTask;

        bool policyWon = policyResult
            is HistoryTimelinePolicyCasResult.Applied;
        bool appendWon = appendResult
            is HistoryTimelineCommitResult.Committed;
        bool reconcileWon = reconcileResult
            is HistoryTimelineReconcileResult.Reconciled;
        Assert.Equal(
            1,
            new[] { policyWon, appendWon, reconcileWon }
                .Count(static won => won)
        );
        Assert.True(policyWon
            || policyResult
                is HistoryTimelinePolicyCasResult.StaleTimelineHead);
        Assert.True(appendWon
            || appendResult
                is HistoryTimelineCommitResult.StaleTimelineHead);
        Assert.True(reconcileWon
            || reconcileResult
                is HistoryTimelineReconcileResult.StaleTimelineHead);
        TimelineHeadRef finalHead = ReadHead(OpenLedger(
            path,
            journal.BranchRefId,
            created.Locator
        ));
        Assert.Equal(expected.Generation + 1, finalHead.Generation);
        HistoryTimelineStoreReadResult<HistorySegmentDescriptor> appendRow =
            OpenLedger(path, journal.BranchRefId, created.Locator)
                .ReadRow(appendCandidate.Proposal.Descriptor.RowId);
        Assert.Equal(
            appendWon,
            appendRow is HistoryTimelineStoreReadResult<
                HistorySegmentDescriptor>.Found
        );
    }

    [Fact]
    public void SameEndRowsFromDifferentPoliciesRemainLegalButNotGlobalAuthority() {
        string path = NewPath();
        using SessionJournalEngine journal = CreateJournal(path);
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        using HistoryTimelineHandle handle = Open(journal);
        EventAddress rawEnd = journal.AppendObservation(
            "same-end fixture"
        );
        HistorySegmentDescriptor first = CommitNextDescriptor(
            handle,
            journal
        );
        TimelineHeadRef firstHead = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(handle.Reader.ReadSnapshot()).Head;
        Assert.True(journal.MoveCurrentHeadForTest(
            rawEnd,
            first.StartExclusive
        ));
        TimelineHeadRef empty = Assert.IsType<
            HistoryTimelineReconcileResult.Reconciled
        >(handle.Coordinator.ReconcileSelectedPath(
            firstHead,
            journal.ReadView
        )).Head;
        Assert.Null(empty.HeadRowId);
        PartitionPolicyRevision secondPolicy =
            PartitionPolicyRevision.Create(
                created.Locator.ActiveTimelineId,
                HistoryPartitionAlgorithms
                    .FirstReplaySafeBoundaryAtTargetV1,
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                new HistoryLoadUnit(1),
                maxRawEvents: 7,
                maxRenderedBytes: 1024 * 1024
            );
        Assert.IsType<HistoryTimelinePolicyPutResult.Stored>(
            handle.Coordinator.PutPolicy(secondPolicy)
        );
        TimelineHeadRef policyHead = Assert.IsType<
            HistoryTimelinePolicyCasResult.Applied
        >(handle.Coordinator.CompareExchangePolicy(
            empty,
            secondPolicy.PolicyDigest
        )).Head;
        Assert.True(journal.MoveCurrentHeadForTest(
            first.StartExclusive,
            rawEnd
        ));
        OnlineSelectedRawCapture capture = Assert.IsType<
            OnlineSelectedRawCaptureResult.Captured
        >(handle.Coordinator.CaptureOnline(
            policyHead,
            journal.ReadView
        )).Capture;
        HistoryRowCommitCandidate candidate = Assert.IsType<
            HistoryTimelinePlanResult.Selected
        >(handle.Coordinator.PlanNextRow(
            policyHead,
            capture
        )).Candidate;
        HistorySegmentDescriptor second = candidate.Proposal.Descriptor;
        TimelineHeadRef secondHead = Assert.IsType<
            HistoryTimelineCommitResult.Committed
        >(handle.Coordinator.CommitRow(candidate)).Head;

        Assert.Equal(first.EndInclusive, second.EndInclusive);
        Assert.NotEqual(first.RowId, second.RowId);
        Assert.NotEqual(
            first.PartitionPolicyDigestAtCreation,
            second.PartitionPolicyDigestAtCreation
        );
        Assert.IsType<HistoryTimelineReaderRowResult.NotOnSelectedPath>(
            handle.Reader.ReadSelectedRow(secondHead, first.RowId)
        );
        Assert.IsType<HistoryTimelineReaderRowResult.Selected>(
            handle.Reader.ReadSelectedRow(secondHead, second.RowId)
        );
        SelectedHistoryBoundaryResult.Found selectedBoundary =
            Assert.IsType<SelectedHistoryBoundaryResult.Found>(
                OpenLedger(path, journal.BranchRefId, created.Locator)
                    .ReadSelectedRowAtBoundary(
                        secondHead,
                        second.EndInclusive
                    )
            );
        Assert.Equal(second.RowId, selectedBoundary.Descriptor.RowId);
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static SessionJournalEngine CreateJournal(string path)
        => SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );

    private static HistoryTimelineInitialPolicySpec InitialPolicy()
        => new(
            HistoryPartitionAlgorithms
                .FirstReplaySafeBoundaryAtTargetV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            new HistoryLoadUnit(1),
            maxRawEvents: 8,
            maxRenderedBytes: 1024 * 1024
        );

    private HistoryTimelineHandle Open(SessionJournalEngine journal)
        => Assert.IsType<HistoryTimelineOpenResult.Opened>(
            HistoryTimelineFactory.Open(
                journal.ReadView,
                _estimator
            )
        ).Handle;

    private static PartitionPolicyRevision NextPolicy(
        TimelineId timelineId
    ) => PartitionPolicyRevision.Create(
        timelineId,
        HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
        new HistoryLoadUnit(2),
        maxRawEvents: 8,
        maxRenderedBytes: 1024 * 1024
    );

    private static TimelineHeadRef CommitNextRow(
        HistoryTimelineHandle handle,
        SessionJournalEngine journal
    ) {
        TimelineHeadRef before = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(handle.Reader.ReadSnapshot()).Head;
        OnlineSelectedRawCapture capture = Assert.IsType<
            OnlineSelectedRawCaptureResult.Captured
        >(handle.Coordinator.CaptureOnline(
            before,
            journal.ReadView
        )).Capture;
        HistoryTimelinePlanResult.Selected selected = Assert.IsType<
            HistoryTimelinePlanResult.Selected
        >(handle.Coordinator.PlanNextRow(before, capture));
        return Assert.IsType<HistoryTimelineCommitResult.Committed>(
            handle.Coordinator.CommitRow(selected.Candidate)
        ).Head;
    }

    private static HistorySegmentDescriptor CommitNextDescriptor(
        HistoryTimelineHandle handle,
        SessionJournalEngine journal
    ) {
        TimelineHeadRef before = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(handle.Reader.ReadSnapshot()).Head;
        OnlineSelectedRawCapture capture = Assert.IsType<
            OnlineSelectedRawCaptureResult.Captured
        >(handle.Coordinator.CaptureOnline(
            before,
            journal.ReadView
        )).Capture;
        HistoryTimelinePlanResult.Selected selected = Assert.IsType<
            HistoryTimelinePlanResult.Selected
        >(handle.Coordinator.PlanNextRow(before, capture));
        _ = Assert.IsType<HistoryTimelineCommitResult.Committed>(
            handle.Coordinator.CommitRow(selected.Candidate)
        );
        return selected.Candidate.Proposal.Descriptor;
    }

    private static SqliteHistoryTimelineLedger OpenLedger(
        string path,
        RefId refId,
        ActiveTimelineLocator locator
    ) => new(
        new HistoryTimelinePaths(path, refId).TimelineDatabasePath(
            locator.ActiveTimelineId
        ),
        locator.ActiveTimelineId,
        refId,
        HistoryTimelineStorageLimits.Production
    );

    private static TimelineHeadRef ReadHead(
        SqliteHistoryTimelineLedger ledger
    ) => ledger.ReadSnapshot() is HistoryTimelineStoreReadResult<
        TimelineHeadRef>.Found found
            ? found.Value
            : throw new InvalidDataException(
                "The durable Timeline head is unavailable."
            );

    private static long ReadStoredRowCount(string databasePath) {
        using SqliteConnection connection = OpenSqlite(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT row_count
            FROM store_metadata
            WHERE singleton = 1;
            """;
        return Assert.IsType<long>(command.ExecuteScalar());
    }

    private static long ReadSqliteInt64(
        SqliteConnection connection,
        string sql
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void ReplaceHeadWithForeignScope(
        string databasePath,
        TimelineHeadRef existing
    ) {
        var foreign = new TimelineHeadRef(
            new TimelineId("ffffffffffffffffffffffffffffffff"),
            existing.RefId,
            headRowId: null,
            existing.ActivePartitionPolicyDigest,
            selectedRawHeadAtCommit: null,
            selectedPathCount: 0,
            "d3676eac2e1782a9797549c8529f06c46d52fa3acaee3e0a68c21366875e8ea9",
            generation: 0
        );
        byte[] canonical = foreign.ToCanonicalBytes();
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        AppendHashField(hash, "atelia.history-timeline.head.v1");
        AppendHashField(hash, canonical);
        string digest = Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();

        using SqliteConnection connection = OpenSqlite(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE store_metadata
            SET head_canonical = $canonical,
                head_sha256 = $digest
            WHERE singleton = 1;
            """;
        command.Parameters.AddWithValue("$canonical", canonical);
        command.Parameters.AddWithValue("$digest", digest);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void AppendHashField(
        IncrementalHash hash,
        string value
    ) => AppendHashField(hash, Encoding.UTF8.GetBytes(value));

    private static void AppendHashField(
        IncrementalHash hash,
        ReadOnlySpan<byte> value
    ) {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static void ExecuteSql(string databasePath, string sql) {
        using SqliteConnection connection = OpenSqlite(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void InsertCurrentPathRow(
        string databasePath,
        long ordinal,
        HistoryRowId rowId,
        EventAddress endInclusive
    ) {
        byte[] endAddress = new byte[
            EventAddressCodec.EventAddressLength];
        EventAddressCodec.Encode(endInclusive, endAddress);
        using SqliteConnection connection = OpenSqlite(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO current_selected_path(
                ordinal, row_id, previous_row_id, end_address, leaf_digest
            ) VALUES ($ordinal, $row, NULL, $end, $leaf);
            """;
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$row", rowId.Value);
        command.Parameters.AddWithValue("$end", endAddress);
        command.Parameters.AddWithValue("$leaf", new string('0', 64));
        Assert.Equal(1, command.ExecuteNonQuery());
    }
    private static SqliteConnection OpenSqlite(string databasePath) {
        var builder = new SqliteConnectionStringBuilder {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 0
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 0;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static IReadOnlyList<string> ReadQueryPlan(
        string databasePath,
        string sql,
        string after
    ) {
        using SqliteConnection connection = OpenSqlite(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddWithValue("$after", after);
        var details = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) {
            details.Add(reader.GetString(3));
        }
        return details;
    }

    private static SessionSelectedLineageForwardCursor OpenAuditCursor(
        SessionJournalEngine offline,
        int pageSize
    ) {
        SessionSelectedLineageAuditSession audit =
            offline.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!audit.IsCaptureComplete) {
            pages.Add(audit.ReadNextPage(pageSize));
        }
        _ = audit.Complete();
        return offline.OpenSelectedLineageForwardCursor(
            new AuditPageSnapshot(audit.Capture, pages)
        );
    }

    private sealed class LockObservingRawFence(
        SessionJournalReadView readView,
        RefId refId,
        EventAddress capturedHead,
        Func<bool> writerLockProbe
    ) : IHistoryTimelineRawFence {
        public string CanonicalRepositoryPath { get; } =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(readView.Path));
        public int CallCount { get; private set; }
        public bool WriterLockObserved { get; private set; }
        public RefId RefId { get; } = refId;
        public EventAddress? CapturedHead { get; } = capturedHead;

        public EventAddress? ReadCurrentHead() {
            CallCount++;
            WriterLockObserved = writerLockProbe();
            return readView.ReadCurrentHead();
        }
    }

    private sealed class FixedRawFence(
        RefId refId,
        EventAddress capturedHead
    ) : IHistoryTimelineRawFence {
        public string CanonicalRepositoryPath { get; } =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath("."));
        public RefId RefId { get; } = refId;
        public EventAddress? CapturedHead { get; } = capturedHead;
        public EventAddress? ReadCurrentHead() => capturedHead;
    }

    private sealed class AuditPageSnapshot(
        SessionSelectedLineageAuditCapture capture,
        IReadOnlyList<SessionSelectedLineageAuditPage> pages
    ) : ISessionSelectedLineageAuditPageSnapshot {
        public SessionSelectedLineageAuditCapture Capture { get; }
            = capture;
        public long PageCount => pages.Count;

        public IEnumerable<SessionSelectedLineageAuditPage>
            ReadHeadToOldestPages() => pages;

        public IEnumerable<SessionSelectedLineageAuditPage>
            ReadOldestToHeadPages() => pages.Reverse();

        public void Dispose() { }
    }

    private string NewPath() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm")
                ? "/dev/shm"
                : Path.GetTempPath(),
            "atelia-history-timeline-durable-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }
}
