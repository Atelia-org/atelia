using System.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline.Tests;

public sealed class HistoryTimelineCrashRecoveryTests : IDisposable {
    private readonly List<string> _paths = [];
    private readonly O200kBaseHistoryUnitLoadEstimator _estimator = new();

    [Theory]
    [InlineData("put-policy-before-commit", false)]
    [InlineData("put-policy-after-commit", true)]
    public async Task PutPolicyCrashReopensAtOldOrNew(
        string failpoint,
        bool installed
    ) {
        CrashFixture fixture = CreateFixture(withRawHistory: false);
        PartitionPolicyRevision policy = NextPolicy(
            fixture.Locator.ActiveTimelineId
        );

        await RunCrashHarnessAsync(
            fixture.Path,
            "put-policy",
            failpoint
        );

        HistoryTimelineStoreReadResult<PartitionPolicyRevision> read =
            OpenLedger(fixture).ReadPolicy(policy.PolicyDigest);
        Assert.Equal(
            installed,
            read is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Found
        );
        Assert.IsType<HistoryTimelineInspectResult.Available>(
            HistoryTimelineMaintenance.Verify(
                fixture.Path,
                fixture.RefId
            )
        );
    }

    [Theory]
    [InlineData("policy-cas-before-commit", false)]
    [InlineData("policy-cas-after-commit", true)]
    public async Task PolicyCasCrashReopensAtOldOrNewHead(
        string failpoint,
        bool installed
    ) {
        CrashFixture fixture = CreateFixture(withRawHistory: false);
        PartitionPolicyRevision policy = NextPolicy(
            fixture.Locator.ActiveTimelineId
        );
        Assert.IsType<HistoryTimelinePolicyPutResult.Stored>(
            OpenLedger(fixture).PutPolicy(policy)
        );

        await RunCrashHarnessAsync(
            fixture.Path,
            "policy-cas",
            failpoint
        );

        TimelineHeadRef head = ReadHead(fixture);
        Assert.Equal(
            installed ? policy.PolicyDigest : fixture.InitialHead
                .ActivePartitionPolicyDigest,
            head.ActivePartitionPolicyDigest
        );
        Assert.Equal(installed ? 1 : 0, head.Generation);
    }

    [Theory]
    [InlineData("append-before-commit", false)]
    [InlineData("append-after-commit", true)]
    public async Task AppendCrashReopensAtOldOrNewSelectedSnapshot(
        string failpoint,
        bool installed
    ) {
        CrashFixture fixture = CreateFixture(withRawHistory: true);

        await RunCrashHarnessAsync(
            fixture.Path,
            "append",
            failpoint
        );

        TimelineHeadRef head = ReadHead(fixture);
        Assert.Equal(installed, head.HeadRowId is not null);
        Assert.Equal(installed ? 1 : 0, head.Generation);
        Assert.IsType<HistoryTimelineInspectResult.Available>(
            HistoryTimelineMaintenance.Verify(
                fixture.Path,
                fixture.RefId
            )
        );
    }

    [Theory]
    [InlineData("reconcile-before-commit", false)]
    [InlineData("reconcile-after-commit", true)]
    public async Task ReconcileCrashSwitchesWholeSelectedSnapshot(
        string failpoint,
        bool installed
    ) {
        CrashFixture fixture = CreateFixture(withRawHistory: true);
        TimelineHeadRef committed = CommitOneRow(fixture);

        await RunCrashHarnessAsync(
            fixture.Path,
            "reconcile",
            failpoint
        );

        TimelineHeadRef head = ReadHead(fixture);
        Assert.Equal(installed ? null : committed.HeadRowId, head.HeadRowId);
        Assert.Equal(installed ? 2 : 1, head.Generation);
        Assert.IsType<HistoryTimelineInspectResult.Available>(
            HistoryTimelineMaintenance.Verify(
                fixture.Path,
                fixture.RefId
            )
        );
    }

    [Theory]
    [InlineData("create-before-publish", false)]
    [InlineData("create-after-publish", true)]
    public async Task LocatorCreateCrashPublishesAbsentOrExactWinner(
        string failpoint,
        bool installed
    ) {
        CrashFixture fixture = CreateFixture(
            withRawHistory: false,
            createTimeline: false
        );

        await RunCrashHarnessAsync(
            fixture.Path,
            "create",
            failpoint
        );

        Assert.True(File.Exists(new HistoryTimelinePaths(
            fixture.Path,
            fixture.RefId
        ).LockPath));

        HistoryTimelineInspectResult inspection =
            HistoryTimelineMaintenance.Inspect(
                fixture.Path,
                fixture.RefId
            );
        Assert.Equal(
            installed,
            inspection is HistoryTimelineInspectResult.Available
        );
        using SessionJournalEngine journal =
            SessionJournalEngine.Open(fixture.Path);
        HistoryTimelineCreateResult retry = HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        );
        Assert.Equal(
            installed,
            retry is HistoryTimelineCreateResult.AlreadyExists
        );
        Assert.True(
            installed
                || retry is HistoryTimelineCreateResult.Created
        );
    }

    [Theory]
    [InlineData("abandon-before-publish", false)]
    [InlineData("abandon-after-publish", true)]
    public async Task LocatorAbandonCrashPublishesOldOrNewIdentity(
        string failpoint,
        bool installed
    ) {
        CrashFixture fixture = CreateFixture(withRawHistory: false);

        await RunCrashHarnessAsync(
            fixture.Path,
            "abandon",
            failpoint
        );

        ActiveTimelineLocator locator = ReadLocator(fixture);
        Assert.Equal(installed ? 1 : 0, locator.Generation);
        Assert.Equal(
            installed,
            locator.ActiveTimelineId != fixture.Locator.ActiveTimelineId
        );
        Assert.IsType<HistoryTimelineInspectResult.Available>(
            HistoryTimelineMaintenance.Verify(
                fixture.Path,
                fixture.RefId
            )
        );
    }

    [Theory]
    [InlineData("backup-before-publish", false)]
    [InlineData("backup-after-publish", true)]
    public async Task BackupCrashPublishesAbsentOrCompleteDirectory(
        string failpoint,
        bool installed
    ) {
        CrashFixture fixture = CreateFixture(withRawHistory: false);
        string backup = NewPath();

        await RunCrashHarnessAsync(
            fixture.Path,
            "backup",
            failpoint,
            backup
        );

        Assert.Equal(installed, Directory.Exists(backup));
        if (!installed) {
            Assert.IsType<HistoryTimelineBackupResult.Created>(
                HistoryTimelineMaintenance.Backup(
                    fixture.Path,
                    fixture.RefId,
                    backup
                )
            );
        }
        Assert.True(File.Exists(Path.Combine(backup, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(backup, "timeline.sqlite")));
    }

    [Theory]
    [InlineData("restore-before-replace", false)]
    [InlineData("restore-after-replace", true)]
    public async Task RestoreCrashReopensAtOldOrBackupDatabase(
        string failpoint,
        bool installed
    ) {
        CrashFixture fixture = CreateFixture(withRawHistory: false);
        string backup = NewPath();
        Assert.IsType<HistoryTimelineBackupResult.Created>(
            HistoryTimelineMaintenance.Backup(
                fixture.Path,
                fixture.RefId,
                backup
            )
        );
        PartitionPolicyRevision extra = NextPolicy(
            fixture.Locator.ActiveTimelineId
        );
        Assert.IsType<HistoryTimelinePolicyPutResult.Stored>(
            OpenLedger(fixture).PutPolicy(extra)
        );

        await RunCrashHarnessAsync(
            fixture.Path,
            "restore",
            failpoint,
            backup
        );

        HistoryTimelineStoreReadResult<PartitionPolicyRevision> read =
            OpenLedger(fixture).ReadPolicy(extra.PolicyDigest);
        Assert.Equal(
            !installed,
            read is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Found
        );
        Assert.IsType<HistoryTimelineInspectResult.Available>(
            HistoryTimelineMaintenance.Verify(
                fixture.Path,
                fixture.RefId
            )
        );
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best effort for process-death fixture artifacts.
            }
        }
    }

    private CrashFixture CreateFixture(
        bool withRawHistory,
        bool createTimeline = true
    ) {
        string path = NewPath();
        using SessionJournalEngine journal = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        );
        if (withRawHistory) {
            _ = journal.AppendObservation("crash fixture observation");
            _ = journal.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text("crash fixture answer")
                ]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
        }
        if (!createTimeline) {
            return new CrashFixture(
                path,
                journal.BranchRefId,
                null!,
                null!
            );
        }
        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            InitialPolicy(),
            _estimator
        ));
        return new CrashFixture(
            path,
            journal.BranchRefId,
            created.Locator,
            created.InitialHead
        );
    }

    private TimelineHeadRef CommitOneRow(CrashFixture fixture) {
        using SessionJournalEngine journal =
            SessionJournalEngine.Open(fixture.Path);
        using HistoryTimelineHandle handle = Assert.IsType<
            HistoryTimelineOpenResult.Opened
        >(HistoryTimelineFactory.Open(
            journal.ReadView,
            _estimator
        )).Handle;
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

    private static HistoryTimelineInitialPolicySpec InitialPolicy()
        => new(
            HistoryPartitionAlgorithms
                .FirstReplaySafeBoundaryAtTargetV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            new HistoryLoadUnit(1),
            maxRawEvents: 8,
            maxRenderedBytes: 1024 * 1024
        );

    private static SqliteHistoryTimelineLedger OpenLedger(
        CrashFixture fixture
    ) => new(
        new HistoryTimelinePaths(fixture.Path, fixture.RefId)
            .TimelineDatabasePath(
                ReadLocator(fixture).ActiveTimelineId
            ),
        ReadLocator(fixture).ActiveTimelineId,
        fixture.RefId,
        HistoryTimelineStorageLimits.Production
    );

    private static ActiveTimelineLocator ReadLocator(CrashFixture fixture)
        => HistoryTimelineFactory.ReadLocator(
            new HistoryTimelinePaths(fixture.Path, fixture.RefId)
        );

    private static TimelineHeadRef ReadHead(CrashFixture fixture)
        => OpenLedger(fixture).ReadSnapshot()
            is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Found found
                ? found.Value
                : throw new InvalidDataException(
                    "Timeline head is unavailable after crash."
                );

    private async Task RunCrashHarnessAsync(
        string repositoryPath,
        string operation,
        string failpoint,
        string? backupPath = null
    ) {
        string harnessPath = GetCrashHarnessPath();
        Assert.True(File.Exists(harnessPath), harnessPath);
        var startInfo = new ProcessStartInfo("dotnet") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repositoryPath
        };
        startInfo.ArgumentList.Add(harnessPath);
        startInfo.ArgumentList.Add(operation);
        startInfo.ArgumentList.Add(failpoint);
        startInfo.ArgumentList.Add(repositoryPath);
        if (backupPath is not null) {
            startInfo.ArgumentList.Add(backupPath);
        }
        startInfo.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        startInfo.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Failed to start HistoryTimeline crash harness."
            );
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync()
            .WaitAsync(TimeSpan.FromSeconds(30));
        string output = await stdout;
        string error = await stderr;
        Assert.NotEqual(0, process.ExitCode);
        Assert.NotEqual(3, process.ExitCode);
        Assert.Contains(
            $"Intentional HistoryTimeline crash at '{failpoint}'",
            output + error,
            StringComparison.Ordinal
        );
    }

    private string NewPath() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm")
                ? "/dev/shm"
                : Path.GetTempPath(),
            "atelia-history-timeline-crash-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private static string GetCrashHarnessPath() {
        string repositoryRoot = FindRepositoryRoot();
        string configuration = Directory.GetParent(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
        )?.Name ?? "Debug";
        return Path.Combine(
            repositoryRoot,
            "tests",
            "SessionJournal.HistoryTimeline.CrashHarness",
            "bin",
            configuration,
            "net10.0",
            "Atelia.SessionJournal.HistoryTimeline.CrashHarness.dll"
        );
    }

    private static string FindRepositoryRoot() {
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor is not null) {
            if (File.Exists(Path.Combine(cursor.FullName, "Atelia.sln"))) {
                return cursor.FullName;
            }
            cursor = cursor.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate Atelia.sln from test output."
        );
    }

    private sealed record CrashFixture(
        string Path,
        RefId RefId,
        ActiveTimelineLocator Locator,
        TimelineHeadRef InitialHead
    );
}
