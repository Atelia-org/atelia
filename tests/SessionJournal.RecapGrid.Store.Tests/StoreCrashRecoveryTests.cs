using System.Diagnostics;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Store.Tests;

public sealed class StoreCrashRecoveryTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-grid-store-crash-tests",
        Guid.NewGuid().ToString("N")
    );

    [Theory]
    [InlineData("cell", "before-begin", false)]
    [InlineData("cell", "before-commit", false)]
    [InlineData("cell", "after-commit", true)]
    [InlineData("row-view", "before-begin", false)]
    [InlineData("row-view", "before-commit", false)]
    [InlineData("row-view", "after-commit", true)]
    [InlineData("fulfilled", "before-begin", false)]
    [InlineData("fulfilled", "before-commit", false)]
    [InlineData("fulfilled", "after-commit", true)]
    public void CommitCrashRecoversOldOrNew(
        string operation,
        string failpoint,
        bool expectedPresent
    ) {
        string repository = Path.Combine(
            _root,
            $"{operation}-{failpoint}"
        );
        Directory.CreateDirectory(repository);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(repository)
        );
        RowBuildSpec spec = StoreFixture.Spec();
        FulfilledViewKey fulfilled = StoreFixture.Fulfilled(spec);
        if (operation is "row-view" or "fulfilled") {
            using RecapGridStoreHandle setup = Open(repository);
            RecapCellArtifact stored = StoreFixture.Put(setup, spec, "crash fixture answer");
            if (operation == "fulfilled") {
                Assert.IsType<RecapGridRowViewPutResult.Inserted>(
                    setup.Writer.PutRowView(spec, [stored])
                );
            }
        }

        RunCrash(operation, failpoint, repository);

        Assert.IsType<RecapGridStoreVerifyResult.Healthy>(
            RecapGridStoreMaintenance.Verify(repository)
        );
        using RecapGridStoreHandle reopened = Open(repository);
        bool present = operation switch {
            "cell" => reopened.Reader.TryReadCell(((RowBuildAssignment.Evaluate)spec.OrderedAssignments[0]).Slot)
                is RecapGridStoreReadResult<RecapCellArtifact>.Found,
            "row-view" => reopened.Reader.ReadViewAt(spec.Coordinate.AssignmentKey)
                is RecapGridStoreReadResult<RecapRowView>.Found,
            "fulfilled" => reopened.Reader.ReadFulfilled(fulfilled)
                is RecapGridStoreReadResult<RecapGridFulfilledView>.Found,
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(expectedPresent, present);
    }

    [Theory]
    [InlineData("before-publish", false)]
    [InlineData("after-publish", true)]
    public void ResetCrashRecoversOldOrEmptyValid(
        string failpoint,
        bool expectedEmpty
    ) {
        string repository = Path.Combine(_root, $"reset-{failpoint}");
        CreateAuthorityStores(repository);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(repository)
        );
        RowBuildSpec spec = StoreFixture.Spec();
        using (RecapGridStoreHandle setup = Open(repository)) {
            StoreFixture.Put(setup, spec, "crash fixture answer");
        }
        IReadOnlyDictionary<string, byte[]> outsideBefore =
            SnapshotOutsideGridRoot(repository);
        Assert.Contains(
            outsideBefore.Keys,
            static path => path.Contains("history-timeline", StringComparison.Ordinal)
        );
        Assert.Contains(
            outsideBefore.Keys,
            static path => path.Contains("control/recap-grid", StringComparison.Ordinal)
        );

        RunCrash("reset", failpoint, repository);

        RecapGridStoreInfo info = Assert.IsType<
            RecapGridStoreVerifyResult.Healthy
        >(RecapGridStoreMaintenance.Verify(repository)).Info;
        Assert.Equal(expectedEmpty ? 0 : 1, info.CellCount);
        IReadOnlyDictionary<string, byte[]> outsideAfter =
            SnapshotOutsideGridRoot(repository);
        Assert.Equal(outsideBefore.Keys, outsideAfter.Keys);
        foreach ((string path, byte[] bytes) in outsideBefore) {
            Assert.Equal(bytes, outsideAfter[path]);
        }
    }

    private static void RunCrash(
        string operation,
        string failpoint,
        string repository
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
            "Atelia.SessionJournal.RecapGrid.Store.CrashHarness.dll"
        ));
        Assert.True(File.Exists(harness), harness);
        var start = new ProcessStartInfo("dotnet") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repository
        };
        start.ArgumentList.Add(harness);
        start.ArgumentList.Add(operation);
        start.ArgumentList.Add(failpoint);
        start.ArgumentList.Add(repository);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Crash harness could not be started."
            );
        Assert.True(
            process.WaitForExit(milliseconds: 30_000),
            "Crash harness did not terminate."
        );
        string standardError = process.StandardError.ReadToEnd();
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains(
            $"{operation}/{failpoint}",
            standardError,
            StringComparison.Ordinal
        );
    }

    private static RecapGridStoreHandle Open(string repository)
        => Assert.IsType<RecapGridStoreOpenResult.Opened>(
            RecapGridStoreFactory.Open(repository)
        ).Handle;

    private static void CreateAuthorityStores(string repository) {
        var estimator = new O200kBaseHistoryUnitLoadEstimator();
        RefId refId;
        using (SessionJournalEngine journal = SessionJournalEngine.Create(
                   repository,
                   new SessionCreateOptions("model", "system", "surface")
               )) {
            refId = journal.BranchRefId;
            _ = Assert.IsType<HistoryTimelineCreateResult.Created>(
                HistoryTimelineFactory.Create(
                    journal.ReadView,
                    new HistoryTimelineInitialPolicySpec(
                        HistoryPartitionAlgorithms
                            .FirstReplaySafeBoundaryAtTargetV1,
                        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                        new HistoryLoadUnit(1),
                        maxRawEvents: 8,
                        maxRenderedBytes: 1024 * 1024
                    ),
                    estimator
                )
            );
        }
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.Create,
            [],
            [],
            [],
            ["case."],
            maximumBootstrapRows: 0,
            maximumProjectedCalls: 0
        );
        _ = Assert.IsType<RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(repository, refId, admission)
        );
    }

    private static IReadOnlyDictionary<string, byte[]>
        SnapshotOutsideGridRoot(string repository) {
        string gridRoot = Path.GetFullPath(Path.Combine(
            repository,
            "derived",
            "recap-grid",
            "v1"
        ));
        return Directory.EnumerateFiles(
                repository,
                "*",
                SearchOption.AllDirectories
            )
            .Select(Path.GetFullPath)
            .Where(path => !path.StartsWith(
                gridRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal
            ))
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(repository, path)
                    .Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal
            );
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }
}
