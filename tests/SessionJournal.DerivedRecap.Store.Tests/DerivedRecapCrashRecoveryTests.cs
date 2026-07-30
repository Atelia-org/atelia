using System.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapCrashRecoveryTests {
    [Theory]
    [InlineData("root-before-commit", false)]
    [InlineData("root-after-commit", true)]
    public async Task CreateCrashHasUnavailableOrCommittedRoot(
        string failpoint,
        bool rootCommitted
    ) {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string path = CreateRawRepository();
        try {
            await RunCrashHarnessAsync(path, "create", failpoint);

            using SessionJournalEngine engine =
                SessionJournalEngine.Open(path);
            DerivedRecapStore store = DerivedRecapStore.Open(
                path,
                engine.BranchRefId
            );
            DerivedRecapSelection selection =
                await store.SelectNthPreviousAsync(
                    engine.ReadCurrentLineageHeaders(),
                    0
                );
            if (rootCommitted) {
                Assert.IsType<DerivedRecapSelection.EmptyLineage>(
                    selection
                );
            }
            else {
                Assert.IsType<DerivedRecapSelection.StoreUnavailable>(
                    selection
                );
            }
        }
        finally {
            TryDelete(path);
        }
    }

    [Theory]
    [InlineData("publication-before-seal", false)]
    [InlineData("publication-sealed", false)]
    [InlineData("promotion-before", false)]
    [InlineData("promotion-after", true)]
    public async Task PublishCrashNeverExposesHalfMembership(
        string failpoint,
        bool published
    ) {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string path = await CreatePublishableBuildingAsync();
        try {
            await RunCrashHarnessAsync(path, "publish", failpoint);

            using SessionJournalEngine engine =
                SessionJournalEngine.Open(path);
            DerivedRecapStore store = DerivedRecapStore.Open(
                path,
                engine.BranchRefId
            );
            SessionCurrentLineageSnapshot lineage =
                engine.ReadCurrentLineageHeaders();
            DerivedRecapSelection selection =
                await store.SelectNthPreviousAsync(lineage, 0);
            if (published) {
                Assert.IsType<DerivedRecapSelection.Selected>(
                    selection
                );
                Assert.False(
                    Directory.Exists(
                        store.GetBuildingPathForTest(
                            lineage.CapturedHead
                        )
                    )
                );
            }
            else {
                Assert.IsType<DerivedRecapSelection.EmptyLineage>(
                    selection
                );
                Assert.True(
                    Directory.Exists(
                        store.GetBuildingPathForTest(
                            lineage.CapturedHead
                        )
                    )
                );
            }
        }
        finally {
            TryDelete(path);
        }
    }

    [Theory]
    [InlineData("reset-after-quarantine", false)]
    [InlineData("reset-after-new-root-commit", true)]
    public async Task ResetCrashHasUnavailableOrFreshCommittedRoot(
        string failpoint,
        bool newRootCommitted
    ) {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string path = CreateRawRepository();
        try {
            using (SessionJournalEngine engine =
                   SessionJournalEngine.Open(path)) {
                DerivedRecapStore store = DerivedRecapStore.Open(
                    path,
                    engine.BranchRefId
                );
                await store.CreateAsync();
            }

            await RunCrashHarnessAsync(path, "reset", failpoint);

            using SessionJournalEngine reopened =
                SessionJournalEngine.Open(path);
            DerivedRecapStore reopenedStore = DerivedRecapStore.Open(
                path,
                reopened.BranchRefId
            );
            DerivedRecapSelection selection =
                await reopenedStore.SelectNthPreviousAsync(
                    reopened.ReadCurrentLineageHeaders(),
                    0
                );
            if (newRootCommitted) {
                Assert.IsType<DerivedRecapSelection.EmptyLineage>(
                    selection
                );
            }
            else {
                Assert.IsType<DerivedRecapSelection.StoreUnavailable>(
                    selection
                );
            }
        }
        finally {
            TryDelete(path);
        }
    }

    [Theory]
    [InlineData("rolling-before-replace", false)]
    [InlineData("rolling-after-replace", true)]
    public async Task RollingCheckpointCrashExposesWholeOldOrNewFile(
        string failpoint,
        bool newCheckpointInstalled
    ) {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string path = await CreateRollingBuildingAsync();
        try {
            await RunCrashHarnessAsync(path, "rolling", failpoint);

            using SessionJournalEngine engine =
                SessionJournalEngine.Open(path);
            DerivedRecapStore store = DerivedRecapStore.Open(
                path,
                engine.BranchRefId
            );
            BuildingReadResult.Available building =
                Assert.IsType<BuildingReadResult.Available>(
                    await store.ReadBuildingAsync(
                        engine.ReadCurrentLineageHeaders()
                            .CapturedHead
                    )
                );
            RecapBlockPlan plan =
                building.Snapshot.Manifest.Blocks.Single();
            MaintainRecapBlockPlan maintain =
                Assert.IsType<MaintainRecapBlockPlan>(plan);
            BuildingBlockInspection inspection =
                await store.InspectBuildingBlockAsync(
                    building.Snapshot.Descriptor,
                    plan.RecapBlockId
                );
            RollingRecapCheckpointHealth.Healthy checkpoint =
                Assert.IsType<
                    RollingRecapCheckpointHealth.Healthy
                >(inspection.Checkpoint);
            Assert.Equal(
                newCheckpointInstalled
                    ? maintain.CatchUpThrough[^1]
                    : maintain.CatchUpThrough[0],
                checkpoint.Block.AbsorbedThrough
            );
            Assert.Equal(
                newCheckpointInstalled
                    ? "new checkpoint"
                    : "old checkpoint",
                checkpoint.Block.Content
            );
        }
        finally {
            TryDelete(path);
        }
    }

    [Theory]
    [InlineData("restore-final-after-replace")]
    [InlineData("restore-envelope-before-replace")]
    public async Task PublishedRestoreCrashBeforeEnvelopeRetainsExactMembershipAndPendingRepair(
        string failpoint
    ) {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string path =
            await CreateDamagedPublishedRestoreFixtureAsync();
        try {
            EventAddress anchor;
            await RunCrashHarnessAsync(
                path,
                "executor-restore",
                failpoint
            );
            Assert.Equal(1, CountMaintainerCalls(path));

            using (SessionJournalEngine engine =
                   SessionJournalEngine.Open(path)) {
                DerivedRecapStore store = DerivedRecapStore.Open(
                    path,
                    engine.BranchRefId
                );
                SessionCurrentLineageSnapshot lineage =
                    engine.ReadCurrentLineageHeaders();
                anchor = lineage.CapturedHead;
                DerivedRecapSelection
                    .ExactPublishedSetInvalid invalid =
                    Assert.IsType<
                        DerivedRecapSelection
                            .ExactPublishedSetInvalid
                    >(
                        await store.SelectNthPreviousAsync(
                            lineage,
                            0
                        )
                    );
                Assert.Equal(anchor, invalid.SetAdmissionAnchor);
                Assert.True(
                    Directory.Exists(
                        store.GetPublishedPathForTest(anchor)
                    )
                );
            }

            await RunCrashHarnessSuccessAsync(
                path,
                "executor-restore"
            );

            Assert.Equal(1, CountMaintainerCalls(path));
            await AssertSelectedMaterializesAsync(path, anchor);
        }
        finally {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task PublishedRestoreCrashAfterEnvelopeExposesCompleteSelection() {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string path =
            await CreateDamagedPublishedRestoreFixtureAsync();
        try {
            await RunCrashHarnessAsync(
                path,
                "executor-restore",
                "restore-envelope-after-replace"
            );

            Assert.Equal(1, CountMaintainerCalls(path));
            EventAddress anchor;
            using (SessionJournalEngine engine =
                   SessionJournalEngine.Open(path)) {
                anchor =
                    engine.ReadCurrentLineageHeaders().CapturedHead;
            }
            await AssertSelectedMaterializesAsync(path, anchor);
        }
        finally {
            TryDelete(path);
        }
    }

    private static string CreateRawRepository() {
        string path = NewPath();
        using SessionJournalEngine engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        );
        engine.AppendObservation("observation");
        _ = engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text("answer")
            ]),
            new CompletionDescriptor("import", "v1", "model-a")
        );
        return path;
    }

    private static async ValueTask<string>
        CreatePublishableBuildingAsync() {
        string path = CreateRawRepository();
        using SessionJournalEngine engine =
            SessionJournalEngine.Open(path);
        DerivedRecapStore store = DerivedRecapStore.Open(
            path,
            engine.BranchRefId
        );
        await store.CreateAsync();
        SessionCurrentLineageSnapshot lineage =
            engine.ReadCurrentLineageHeaders();
        EventAddress anchor = lineage.CapturedHead;
        var plan = new MaintainRecapBlockPlan(
            new RecapBlockId("roleplay.self"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "roleplay.self"
            ),
            "roleplay.autobiographical",
            new EmptyRecapMaintainSource(
                lineage.HeadToRoot[2].Address
            ),
            [anchor],
            EmptyRecapPriorContext.Instance
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                engine.BranchRefId,
                anchor,
                [plan]
            );
        await store.CreateBuildingAsync(manifest);
        await RecapStoreTestDriver.InstallFinalAsync(
            store,

            anchor,
            DerivedRecapCodec.CreateBlock(plan, anchor, "recap")
        );
        return path;
    }

    private static async ValueTask<string>
        CreateRollingBuildingAsync() {
        string path = CreateRawRepository();
        using SessionJournalEngine engine =
            SessionJournalEngine.Open(path);
        engine.AppendObservation("second observation");
        _ = engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text("second answer")
            ]),
            new CompletionDescriptor("import", "v1", "model-a")
        );
        DerivedRecapStore store = DerivedRecapStore.Open(
            path,
            engine.BranchRefId
        );
        await store.CreateAsync();
        SessionCurrentLineageSnapshot lineage =
            engine.ReadCurrentLineageHeaders();
        EventAddress target = lineage.CapturedHead;
        EventAddress firstEndpoint =
            lineage.HeadToRoot[2].Address;
        EventAddress replayStart =
            lineage.HeadToRoot[^1].Address;
        var plan = new MaintainRecapBlockPlan(
            new RecapBlockId("roleplay.self"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "roleplay.self"
            ),
            "roleplay.autobiographical",
            new EmptyRecapMaintainSource(replayStart),
            [firstEndpoint, target],
            EmptyRecapPriorContext.Instance
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                engine.BranchRefId,
                target,
                [plan]
            );
        CreateBuildingResult.Created created =
            Assert.IsType<CreateBuildingResult.Created>(
                await store.CreateBuildingAsync(manifest)
            );
        _ = Assert.IsType<CheckpointWriteResult.Updated>(
            await store.AdvanceRollingCheckpointAsync(
                created.Descriptor,
                plan.RecapBlockId,
                "missing",
                DerivedRecapCodec.CreateBlock(
                    plan,
                    firstEndpoint,
                    "old checkpoint"
                )
            )
        );
        return path;
    }

    private static async ValueTask<string>
        CreateDamagedPublishedRestoreFixtureAsync() {
        string path = await CreatePublishableBuildingAsync();
        using SessionJournalEngine engine =
            SessionJournalEngine.Open(path);
        DerivedRecapStore store = DerivedRecapStore.Open(
            path,
            engine.BranchRefId
        );
        EventAddress anchor =
            engine.ReadCurrentLineageHeaders().CapturedHead;
        _ = await new DerivedRecapPublisher(store, engine)
            .PublishAsync(anchor);
        string publishedPath =
            store.GetPublishedPathForTest(anchor);
        string blockPath = Assert.Single(
            Directory.GetFiles(
                Path.Combine(publishedPath, "blocks"),
                "*.json",
                SearchOption.TopDirectoryOnly
            )
        );
        await File.WriteAllTextAsync(blockPath, "damaged");
        string workPath = Path.Combine(
            publishedPath,
            "work",
            Path.GetFileName(blockPath)
        );
        if (File.Exists(workPath)) {
            File.Delete(workPath);
        }
        _ = Assert.IsType<
            DerivedRecapSelection.ExactPublishedSetInvalid
        >(
            await store.SelectNthPreviousAsync(
                engine.ReadCurrentLineageHeaders(),
                0
            )
        );
        return path;
    }

    private static async ValueTask
        AssertSelectedMaterializesAsync(
        string path,
        EventAddress expectedAnchor
    ) {
        using SessionJournalEngine engine =
            SessionJournalEngine.Open(path);
        DerivedRecapStore store = DerivedRecapStore.Open(
            path,
            engine.BranchRefId
        );
        var selected =
            Assert.IsType<DerivedRecapSelection.Selected>(
                await store.SelectNthPreviousAsync(
                    engine.ReadCurrentLineageHeaders(),
                    0
                )
            );
        Assert.Equal(
            expectedAnchor,
            selected.Descriptor.SetAdmissionAnchor
        );
        DerivedRecapMaterialization materialized =
            await store.MaterializeAsync(selected.Descriptor);
        Assert.Equal(
            "|roleplay.autobiographical:1",
            Assert.Single(materialized.Contributions).ExactText
        );
    }

    private static int CountMaintainerCalls(string path) {
        string logPath = Path.Combine(
            path,
            "recap-maintainer-calls.jsonl"
        );
        Assert.True(
            File.Exists(logPath),
            $"Maintainer call log is missing: {logPath}"
        );
        return File.ReadLines(logPath).Count();
    }

    private static async Task RunCrashHarnessAsync(
        string repositoryPath,
        string operation,
        string failpoint
    ) {
        string harnessPath = GetCrashHarnessPath();
        Assert.True(
            File.Exists(harnessPath),
            $"Crash harness was not built: {harnessPath}"
        );
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
        startInfo.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        startInfo.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Failed to start DerivedRecap crash harness."
            );
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync()
            .WaitAsync(TimeSpan.FromSeconds(30));
        string output = await stdout;
        string error = await stderr;
        Assert.NotEqual(
            0,
            process.ExitCode
        );
        Assert.NotEqual(
            3,
            process.ExitCode
        );
        Assert.Contains(
            $"Intentional DerivedRecap crash at '{failpoint}'",
            output + error,
            StringComparison.Ordinal
        );
    }

    private static async Task RunCrashHarnessSuccessAsync(
        string repositoryPath,
        string operation
    ) {
        string harnessPath = GetCrashHarnessPath();
        Assert.True(
            File.Exists(harnessPath),
            $"Crash harness was not built: {harnessPath}"
        );
        var startInfo = new ProcessStartInfo("dotnet") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repositoryPath
        };
        startInfo.ArgumentList.Add(harnessPath);
        startInfo.ArgumentList.Add(operation);
        startInfo.ArgumentList.Add("none");
        startInfo.ArgumentList.Add(repositoryPath);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Failed to start DerivedRecap crash harness."
            );
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync()
            .WaitAsync(TimeSpan.FromSeconds(30));
        string output = await stdout;
        string error = await stderr;
        Assert.True(
            process.ExitCode == 0,
            $"Crash harness failed with exit code "
            + $"{process.ExitCode}.{Environment.NewLine}"
            + output
            + error
        );
        Assert.Contains(
            "executor-result:Restored",
            output,
            StringComparison.Ordinal
        );
    }

    private static string GetCrashHarnessPath() {
        string repositoryRoot = FindRepositoryRoot();
        string configuration =
            Directory.GetParent(
                Path.TrimEndingDirectorySeparator(
                    AppContext.BaseDirectory
                )
            )?.Name ?? "Debug";
        return Path.Combine(
            repositoryRoot,
            "tests",
            "SessionJournal.DerivedRecap.Store.CrashHarness",
            "bin",
            configuration,
            "net10.0",
            "Atelia.SessionJournal.DerivedRecap.Store.CrashHarness.dll"
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

    private static string NewPath()
        => Path.Combine(
            Path.GetTempPath(),
            "atelia-derived-recap-crash-tests",
            Guid.NewGuid().ToString("N")
        );

    private static void TryDelete(string path) {
        try {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
        catch {
        }
    }
}
