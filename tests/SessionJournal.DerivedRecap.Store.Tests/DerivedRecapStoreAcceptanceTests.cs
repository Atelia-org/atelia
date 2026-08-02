using System.Diagnostics;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapStoreAcceptanceTests {
    [Theory]
    [InlineData("building-manifest-installed", false)]
    [InlineData("building-promoted", true)]
    public async Task BuildingCreateCrashExposesNoManifestOrWholeBuilding(
        string failpoint,
        bool buildingVisible
    ) {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 3);
        EventAddress anchor = fixture.Lineage().CapturedHead;
        fixture.CloseEngine();

        await RunHarnessCrashAsync(fixture.Path, failpoint);
        fixture.ReopenEngine();

        BuildingReadResult read =
            await fixture.Store.ReadBuildingAsync(anchor);
        if (buildingVisible) {
            Assert.IsType<BuildingReadResult.Available>(read);
        }
        else {
            Assert.IsType<BuildingReadResult.Missing>(read);
        }
    }

    [Fact]
    public async Task DistinctSourcesShareOneFinalEnvelopeRecheck() {
        Action? replaceFirstSource = null;
        var hooks = new RecapStoreTestHooks(
            BeforeBuildingSourceFinalRecheck:
                () => replaceFirstSource?.Invoke()
        );
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                hooks,
                historyPairs: 6
            );
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress target = lineage.CurrentPrefix.HeadToOldest[0].Address;
        EventAddress secondSource =
            lineage.CurrentPrefix.HeadToOldest[2].Address;
        EventAddress firstSource =
            lineage.CurrentPrefix.HeadToOldest[4].Address;
        EventAddress replayStart =
            lineage.CurrentPrefix.HeadToOldest[^1].Address;
        PublishedRecapDescriptor first =
            await fixture.PublishAsync(
                firstSource,
                replayStart,
                blockId: "alpha",
                content: "alpha source"
            );
        PublishedRecapDescriptor second =
            await fixture.PublishAsync(
                secondSource,
                replayStart,
                blockId: "zeta",
                content: "zeta source"
            );

        RecapBlockPlan originalFirstPlan =
            fixture.CreateMaintainPlan(
                firstSource,
                replayStart,
                "alpha"
            );
        var changedFirstPlan = new MaintainRecapBlockPlan(
            originalFirstPlan.RecapBlockId,
            originalFirstPlan.Target,
            "changed-maintainer",
            ((MaintainRecapBlockPlan)originalFirstPlan)
                .MaintainerCapabilityFingerprint,
            ((MaintainRecapBlockPlan)originalFirstPlan).Source,
            ((MaintainRecapBlockPlan)originalFirstPlan).CatchUpThrough,
            EmptyRecapPriorContext.Instance
        );
        PublishedRecapSet changedFirst =
            DerivedRecapCodec.CreatePublication(
                DerivedRecapCodec.CreateManifest(
                    fixture.Engine.BranchRefId,
                    firstSource,
                    [changedFirstPlan]
                ),
                [
                    DerivedRecapCodec.CreateBlock(
                        originalFirstPlan,
                        firstSource,
                        "alpha source"
                    )
                ]
            );
        replaceFirstSource = () => File.WriteAllBytes(
            Path.Combine(
                fixture.Store.GetPublishedPathForTest(firstSource),
                "publication.json"
            ),
            DerivedRecapCodec.EncodePublication(changedFirst)
        );

        var alphaId = new RecapBlockId("alpha");
        var zetaId = new RecapBlockId("zeta");
        var alphaTarget = new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            alphaId.Value
        );
        var zetaTarget = new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            zetaId.Value
        );
        DerivedRecapFrozenInput alphaInput =
            DerivedRecapCodec.CreateFrozenInput(
                alphaId,
                alphaTarget,
                firstSource,
                "alpha source"
            );
        DerivedRecapFrozenInput zetaInput =
            DerivedRecapCodec.CreateFrozenInput(
                zetaId,
                zetaTarget,
                secondSource,
                "zeta source"
            );
        DerivedRecapSetManifest targetManifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                target,
                [
                    new InheritRecapBlockPlan(
                        alphaId,
                        alphaTarget,
                        firstSource,
                        first.EnvelopeSha256,
                        alphaInput.PayloadSha256
                    ),
                    new InheritRecapBlockPlan(
                        zetaId,
                        zetaTarget,
                        secondSource,
                        second.EnvelopeSha256,
                        zetaInput.PayloadSha256
                    )
                ]
            );

        CreateBuildingResult.SourceChanged changed =
            Assert.IsType<CreateBuildingResult.SourceChanged>(
                await fixture.Store.CreateBuildingAsync(
                    targetManifest
                )
            );
        Assert.Equal(first, changed.Source);
        Assert.IsType<BuildingReadResult.Missing>(
            await fixture.Store.ReadBuildingAsync(target)
        );
    }

    private static async Task RunHarnessCrashAsync(
        string repositoryPath,
        string failpoint
    ) {
        var startInfo = new ProcessStartInfo("dotnet") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repositoryPath
        };
        startInfo.ArgumentList.Add(GetCrashHarnessPath());
        startInfo.ArgumentList.Add("building-create");
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
            $"Intentional DerivedRecap crash at '{failpoint}'",
            output + error,
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
            "Could not locate the Atelia repository root."
        );
    }
}
