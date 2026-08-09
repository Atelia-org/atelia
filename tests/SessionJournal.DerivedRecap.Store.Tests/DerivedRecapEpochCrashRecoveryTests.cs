using System.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapEpochCrashRecoveryTests : IDisposable {
    private const string RangeHash =
        "3333333333333333333333333333333333333333333333333333333333333333";
    private readonly List<string> _paths = [];

    [Theory]
    [InlineData("raw-head-recheck")]
    [InlineData("building-promotion")]
    public async Task BuildingInstallCrashLeavesNoPartialBuildingAndCanRetry(
        string failpoint
    ) {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        CrashFixture fixture = await CreateFixtureAsync(
            installBuilding: false,
            damageFinal: false
        );

        await RunCrashHarnessAsync(fixture.Path, "building", failpoint);

        DerivedRecapEpochStore reopened = DerivedRecapEpochStore.Open(
            fixture.Path,
            fixture.RefId
        );
        Assert.IsType<RecapEpochBuildingSelectionResult.Empty>(
            await reopened.SelectBuildingAsync()
        );
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            BuildingRoot(fixture)
        ));
        Assert.IsType<InstallRecapEpochBuildingResult.Installed>(
            await reopened.InstallBuildingAsync(
                fixture.Manifest,
                fixture.Input
            )
        );
    }

    [Theory]
    [InlineData("final-before-replace", false)]
    [InlineData("final-after-replace", true)]
    public async Task FinalReplaceCrashReopensAtOneAtomicVersion(
        string failpoint,
        bool replacementInstalled
    ) {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        CrashFixture fixture = await CreateFixtureAsync(
            installBuilding: true,
            damageFinal: true
        );

        await RunCrashHarnessAsync(fixture.Path, "final", failpoint);

        DerivedRecapEpochStore reopened = DerivedRecapEpochStore.Open(
            fixture.Path,
            fixture.RefId
        );
        RecapEpochStoreSnapshot snapshot = Assert.IsType<
            RecapEpochBuildingSelectionResult.Selected
        >(await reopened.SelectBuildingAsync()).Snapshot;
        RecapEpochBlockInspection block = Assert.Single(snapshot.Blocks);
        if (replacementInstalled) {
            Assert.Equal(
                "crash-harness-final",
                Assert.IsType<RecapEpochFinalHealth.Healthy>(block.Final)
                    .Block.Content
            );
        }
        else {
            Assert.IsType<RecapEpochFinalHealth.Damaged>(block.Final);
            Assert.IsType<WriteRecapEpochFinalResult.Installed>(
                await reopened.WriteFinalAsync(
                    block.WriteAuthority!,
                    DerivedRecapV8Codec.CreateFinalBlock(
                        snapshot.Manifest,
                        block.Definition,
                        "retry-final"
                    )
                )
            );
        }
    }

    [Theory]
    [InlineData("raw-head-recheck", false)]
    [InlineData("publication-install", false)]
    [InlineData("published-promotion", true)]
    public async Task PublicationCrashKeepsRetryableBuildingAndEnvelopeLast(
        string failpoint,
        bool sealInstalled
    ) {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        CrashFixture fixture = await CreateFixtureAsync(
            installBuilding: true,
            damageFinal: false
        );

        await RunCrashHarnessAsync(fixture.Path, "publish", failpoint);

        DerivedRecapEpochStore reopened = DerivedRecapEpochStore.Open(
            fixture.Path,
            fixture.RefId
        );
        RecapEpochStoreSnapshot building = Assert.IsType<
            RecapEpochBuildingSelectionResult.Selected
        >(await reopened.SelectBuildingAsync()).Snapshot;
        Assert.Equal(
            sealInstalled,
            File.Exists(Path.Combine(
                BuildingPath(fixture),
                "publication.json"
            ))
        );
        PublishedRecapEpochDescriptor published = Assert.IsType<
            PublishRecapEpochResult.Published
        >(await reopened.PublishBuildingAsync(building.Descriptor))
            .Descriptor;
        Assert.IsType<RecapEpochBuildingSelectionResult.Empty>(
            await reopened.SelectBuildingAsync()
        );
        Assert.Single(
            (await reopened.MaterializeAsync(published)).Contributions
        );
    }

    [Fact]
    public async Task ResetCrashAfterQuarantineRenameIsRecoveredOnReopen() {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string path = NewPath();
        RefId refId;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-a", "system-a", "surface-a")
        )) {
            refId = engine.BranchRefId;
            DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
                path,
                refId
            );
            await store.CreateAsync();
        }

        await RunCrashHarnessAsync(
            path,
            "reset",
            "reset-quarantine-renamed"
        );

        DerivedRecapEpochStore reopened = DerivedRecapEpochStore.Open(
            path,
            refId
        );
        await reopened.EnsureCreatedAsync();
        Assert.IsType<RecapEpochBuildingSelectionResult.Empty>(
            await reopened.SelectBuildingAsync()
        );
        string quarantineRoot = Path.Combine(
            path,
            "derived",
            "recap",
            "v8",
            "quarantine"
        );
        Assert.Empty(Directory.Exists(quarantineRoot)
            ? Directory.EnumerateFileSystemEntries(quarantineRoot)
            : []);
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup for test-owned repositories.
            }
        }
    }

    private async ValueTask<CrashFixture> CreateFixtureAsync(
        bool installBuilding,
        bool damageFinal
    ) {
        string path = NewPath();
        using SessionJournalEngine engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-a", "system-a", "surface-a")
        );
        for (int index = 0; index < 3; index++) {
            engine.AppendObservation($"observation-{index}");
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"action-{index}")
                ]),
                new CompletionDescriptor("import", "v1", "model-a")
            );
        }
        SessionCurrentLineageSnapshot lineage =
            engine.ReadCurrentLineageHeaders();
        EventAddress admission = lineage.HeadToRoot[0].Address;
        EventAddress start = lineage.HeadToRoot[2].Address;
        DerivedRecapEpochInput input = DerivedRecapV8Codec.CreateEpochInput(
            new RecapEpochBoundary(
                start,
                engine.ResolveContextAnchorSetupReferences(start)
            ),
            new RecapEpochBoundary(
                admission,
                engine.ResolveContextAnchorSetupReferences(admission)
            ),
            rawEventCount: 2,
            RangeHash,
            [new ObservationMessage("history")],
            RecapEpochPrevious.Empty.Instance
        );
        RecapEpochBlockDefinition definition = new(
            new RecapBlockId("crash"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "crash"
            ),
            "crash",
            RecapTestIdentity.CapabilityFingerprint,
            1024,
            0
        );
        DerivedRecapEpochManifest manifest =
            DerivedRecapV8Codec.CreateManifest(
                engine.BranchRefId,
                admission,
                input.PayloadSha256,
                [definition]
            );
        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            path,
            engine.BranchRefId
        );
        await store.CreateAsync();
        var fixture = new CrashFixture(
            path,
            engine.BranchRefId,
            admission,
            input,
            manifest
        );
        if (!installBuilding) {
            return fixture;
        }
        Assert.IsType<InstallRecapEpochBuildingResult.Installed>(
            await store.InstallBuildingAsync(manifest, input)
        );
        RecapEpochStoreSnapshot building = Assert.IsType<
            RecapEpochBuildingSelectionResult.Selected
        >(await store.SelectBuildingAsync()).Snapshot;
        Assert.IsType<WriteRecapEpochFinalResult.Installed>(
            await store.WriteFinalAsync(
                Assert.Single(building.Blocks).WriteAuthority!,
                DerivedRecapV8Codec.CreateFinalBlock(
                    manifest,
                    definition,
                    "original-final"
                )
            )
        );
        if (damageFinal) {
            await File.WriteAllTextAsync(
                Path.Combine(BuildingPath(fixture), "blocks", "crash.json"),
                "damaged"
            );
        }
        return fixture;
    }

    private static string BuildingRoot(CrashFixture fixture) => Path.Combine(
        fixture.Path,
        "derived",
        "recap",
        "v8",
        "refs",
        fixture.RefId.ToHexString(),
        "building"
    );

    private static string BuildingPath(CrashFixture fixture) => Path.Combine(
        BuildingRoot(fixture),
        EventAddressFileNameCodec.Format(fixture.Admission)
    );

    private async Task RunCrashHarnessAsync(
        string repositoryPath,
        string operation,
        string failpoint
    ) {
        string harnessPath = GetCrashHarnessPath();
        Assert.True(File.Exists(harnessPath));
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
        Assert.NotEqual(0, process.ExitCode);
        Assert.NotEqual(3, process.ExitCode);
        Assert.Contains(
            $"Intentional DerivedRecap v8 crash at '{failpoint}'",
            output + error,
            StringComparison.Ordinal
        );
    }

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-recap-v8-crash-tests",
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

    private sealed record CrashFixture(
        string Path,
        RefId RefId,
        EventAddress Admission,
        DerivedRecapEpochInput Input,
        DerivedRecapEpochManifest Manifest
    );
}
