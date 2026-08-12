using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.HistoryTimeline.CrashHarness;

internal static class Program {
    private static readonly O200kBaseHistoryUnitLoadEstimator Estimator
        = new();

    public static int Main(string[] args) {
        if (args.Length is < 3 or > 4) {
            Console.Error.WriteLine(
                "usage: <create|put-policy|policy-cas|append|reconcile|abandon|backup|restore> <failpoint> <repository> [backup]"
            );
            return 2;
        }
        string operation = args[0];
        string failpoint = args[1];
        string repositoryPath = Path.GetFullPath(args[2]);
        string? backupPath = args.Length == 4
            ? Path.GetFullPath(args[3])
            : null;
        Action crash = () => Environment.FailFast(
            $"Intentional HistoryTimeline crash at '{failpoint}'."
        );
        HistoryTimelinePersistenceTestHooks hooks = Hooks(
            failpoint,
            crash
        );
        using SessionJournalEngine journal =
            SessionJournalEngine.Open(repositoryPath);

        switch (operation) {
            case "create":
                RequireReached(HistoryTimelineFactory.CreateForTest(
                    journal.ReadView,
                    InitialPolicy(),
                    HistoryTimelineStorageLimits.Production,
                    hooks,
                    Estimator
                ));
                break;
            case "put-policy":
                using (HistoryTimelineHandle handle = Open(journal, hooks)) {
                    RequireReached(handle.Coordinator.PutPolicy(
                        NextPolicy(handle.Locator.ActiveTimelineId)
                    ));
                }
                break;
            case "policy-cas":
                using (HistoryTimelineHandle handle = Open(journal, hooks)) {
                    TimelineHeadRef expected = ReadHead(handle.Reader);
                    RequireReached(
                        handle.Coordinator.CompareExchangePolicy(
                            expected,
                            NextPolicy(handle.Locator.ActiveTimelineId)
                                .PolicyDigest
                        )
                    );
                }
                break;
            case "append":
                using (HistoryTimelineHandle handle = Open(journal, hooks)) {
                    TimelineHeadRef expected = ReadHead(handle.Reader);
                    OnlineSelectedRawCapture capture = RequireCaptured(
                        handle.Coordinator.CaptureOnline(
                            expected,
                            journal.ReadView
                        )
                    );
                    PartitionPolicyRevision policy =
                        PartitionPolicyRevision.Create(
                            expected.TimelineId,
                            InitialPolicy().PartitionAlgorithmId,
                            InitialPolicy().HistoryLoadEstimatorId,
                            InitialPolicy().TargetHistoryLoad,
                            InitialPolicy().MaxRawEvents,
                            InitialPolicy().MaxRenderedBytes);
                    var authority =
                        new HistoryRecentReserveAuthorityToken();
                    var reserve = new HistoryRecentReservePolicy(
                        journal.ReadView.Path,
                        expected.RefId,
                        cadenceGeneration: 0,
                        new string('a', 64),
                        policy,
                        new HistoryLoadUnit(1),
                        authority);
                    HistoryRowCommitCandidate candidate = RequireSelected(
                        handle.Coordinator.PlanNextRow(
                            expected,
                            capture,
                            reserve
                        )
                    );
                    RequireReached(
                        handle.Coordinator.CommitRow(candidate)
                    );
                }
                break;
            case "reconcile":
                ReconcileToEmpty(journal, hooks);
                break;
            case "abandon":
                RequireReached(HistoryTimelineMaintenance.AbandonCore(
                    repositoryPath,
                    journal.BranchRefId,
                    ReadLocator(repositoryPath, journal.BranchRefId),
                    InitialPolicy(),
                    HistoryTimelineStorageLimits.Production,
                    hooks,
                    [Estimator]
                ));
                break;
            case "backup":
                RequireReached(HistoryTimelineMaintenance.BackupCore(
                    repositoryPath,
                    journal.BranchRefId,
                    RequireBackupPath(backupPath),
                    HistoryTimelineStorageLimits.Production,
                    hooks
                ));
                break;
            case "restore":
                ActiveTimelineLocator locator = ReadLocator(
                    repositoryPath,
                    journal.BranchRefId
                );
                var ledger = OpenLedger(
                    repositoryPath,
                    journal.BranchRefId,
                    locator,
                    hooks
                );
                TimelineHeadRef head = AssertFound(ledger.ReadSnapshot());
                RequireReached(HistoryTimelineMaintenance.RestoreCore(
                    repositoryPath,
                    journal.BranchRefId,
                    new HistoryTimelineActiveConfirmation(locator, head),
                    RequireBackupPath(backupPath),
                    HistoryTimelineStorageLimits.Production,
                    hooks
                ));
                break;
            default:
                Console.Error.WriteLine($"unknown operation '{operation}'");
                return 2;
        }

        Console.Error.WriteLine($"failpoint '{failpoint}' was not reached");
        return 3;
    }

    private static HistoryTimelineHandle Open(
        SessionJournalEngine journal,
        HistoryTimelinePersistenceTestHooks hooks
    ) => HistoryTimelineFactory.OpenForTest(
        journal.ReadView,
        HistoryTimelineStorageLimits.Production,
        hooks,
        Estimator
    ) is HistoryTimelineOpenResult.Opened opened
        ? opened.Handle
        : throw new InvalidDataException("Timeline open failed.");

    private static void ReconcileToEmpty(
        SessionJournalEngine journal,
        HistoryTimelinePersistenceTestHooks hooks
    ) {
        ActiveTimelineLocator locator = ReadLocator(
            journal.Path,
            journal.BranchRefId
        );
        SqliteHistoryTimelineLedger ledger = OpenLedger(
            journal.Path,
            journal.BranchRefId,
            locator,
            hooks
        );
        TimelineHeadRef expected = AssertFound(ledger.ReadSnapshot());
        EventAddress? rawHead = journal.ReadView.ReadCurrentHead();
        var fence = new HarnessRawFence(
            journal.ReadView,
            journal.BranchRefId,
            rawHead
        );
        RequireReached(ledger.ReconcileSelectedPath(
            new HistoryTimelineReconcileCandidate(
                expected,
                selectedRowId: null,
                fence
            )
        ));
    }

    private static SqliteHistoryTimelineLedger OpenLedger(
        string repositoryPath,
        RefId refId,
        ActiveTimelineLocator locator,
        HistoryTimelinePersistenceTestHooks hooks
    ) {
        var paths = new HistoryTimelinePaths(repositoryPath, refId);
        return new SqliteHistoryTimelineLedger(
            paths.TimelineDatabasePath(locator.ActiveTimelineId),
            locator.ActiveTimelineId,
            refId,
            HistoryTimelineStorageLimits.Production,
            hooks
        );
    }

    private static ActiveTimelineLocator ReadLocator(
        string repositoryPath,
        RefId refId
    ) => HistoryTimelineFactory.ReadLocator(
        new HistoryTimelinePaths(repositoryPath, refId)
    );

    private static TimelineHeadRef AssertFound(
        HistoryTimelineStoreReadResult<TimelineHeadRef> result
    ) => result is HistoryTimelineStoreReadResult<
        TimelineHeadRef>.Found found
            ? found.Value
            : throw new InvalidDataException(
                "Timeline head is unavailable."
            );

    private static TimelineHeadRef ReadHead(HistoryTimelineReader reader)
        => reader.ReadSnapshot()
            is HistoryTimelineSnapshotResult.Available available
                ? available.Head
                : throw new InvalidDataException(
                    "Timeline head is unavailable."
                );

    private static OnlineSelectedRawCapture RequireCaptured(
        OnlineSelectedRawCaptureResult result
    ) => result is OnlineSelectedRawCaptureResult.Captured captured
        ? captured.Capture
        : throw new InvalidDataException("Raw capture failed.");

    private static HistoryRowCommitCandidate RequireSelected(
        HistoryTimelinePlanResult result
    ) => result is HistoryTimelinePlanResult.Selected selected
        ? selected.Candidate
        : throw new InvalidDataException("Timeline plan did not select a row.");

    private static string RequireBackupPath(string? value)
        => value ?? throw new InvalidDataException(
            "The operation requires a backup path."
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

    private static HistoryTimelinePersistenceTestHooks Hooks(
        string failpoint,
        Action crash
    ) => new(
        BeforePutPolicyCommit:
            failpoint == "put-policy-before-commit" ? crash : null,
        AfterPutPolicyCommit:
            failpoint == "put-policy-after-commit" ? crash : null,
        BeforePolicyCasCommit:
            failpoint == "policy-cas-before-commit" ? crash : null,
        AfterPolicyCasCommit:
            failpoint == "policy-cas-after-commit" ? crash : null,
        BeforeAppendCommit:
            failpoint == "append-before-commit" ? crash : null,
        AfterAppendCommit:
            failpoint == "append-after-commit" ? crash : null,
        BeforeReconcileCommit:
            failpoint == "reconcile-before-commit" ? crash : null,
        AfterReconcileCommit:
            failpoint == "reconcile-after-commit" ? crash : null,
        BeforeLocatorCreatePublish:
            failpoint == "create-before-publish" ? crash : null,
        AfterLocatorCreatePublish:
            failpoint == "create-after-publish" ? crash : null,
        BeforeLocatorAbandonPublish:
            failpoint == "abandon-before-publish" ? crash : null,
        AfterLocatorAbandonPublish:
            failpoint == "abandon-after-publish" ? crash : null,
        BeforeBackupPublish:
            failpoint == "backup-before-publish" ? crash : null,
        AfterBackupPublish:
            failpoint == "backup-after-publish" ? crash : null,
        BeforeRestoreReplace:
            failpoint == "restore-before-replace" ? crash : null,
        AfterRestoreReplace:
            failpoint == "restore-after-replace" ? crash : null
    );

    private static void RequireReached(object result) {
        throw new InvalidDataException(
            $"Operation completed without reaching failpoint: {result}."
        );
    }

    private sealed class HarnessRawFence(
        SessionJournalReadView readView,
        RefId refId,
        EventAddress? capturedHead
    ) : IHistoryTimelineRawFence {
        public string CanonicalRepositoryPath { get; } =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(readView.Path));
        public RefId RefId { get; } = refId;
        public EventAddress? CapturedHead { get; } = capturedHead;
        public EventAddress? ReadCurrentHead()
            => readView.ReadCurrentHead();
    }
}
