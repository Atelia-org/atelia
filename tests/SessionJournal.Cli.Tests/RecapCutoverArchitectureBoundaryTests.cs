using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class RecapCutoverArchitectureBoundaryTests {
    private static readonly string[] RemovedSymbols = [
        "InheritRecapBlockPlan",
        "CatchUpBoundaries",
        "RecapMaintainSource",
        "DerivedRecapFrozenInput",
        "MaxRouteEndpointsPerBlock",
        "MaxMaintainerCallsPerBuild",
        "ResumeSuffix"
    ];

    [Fact]
    public void ProductionSourcesContainOnlyDirectSharedEpochOwners() {
        string root = FindRepositoryRoot();
        string[] directories = [
            Path.Combine(root, "prototypes", "SessionJournal.DerivedRecap.Store"),
            Path.Combine(root, "prototypes", "SessionJournal.DerivedRecap.Planner"),
            Path.Combine(root, "prototypes", "SessionJournal.Cli"),
            Path.Combine(root, "prototypes", "Galatea")
        ];
        string production = string.Join(
            "\n",
            directories.SelectMany(directory =>
                Directory.EnumerateFiles(
                    directory,
                    "*.cs",
                    SearchOption.TopDirectoryOnly
                )
            ).Select(File.ReadAllText)
        );
        foreach (string removed in RemovedSymbols) {
            Assert.DoesNotContain(removed, production, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("DerivedRecapStore", production, StringComparison.Ordinal);
        Assert.DoesNotContain("DerivedRecapPlannerExecutor", production, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicOwnersExposeV8StoreAndTypedRebuildResult() {
        Assert.NotNull(typeof(DerivedRecapEpochStore));
        Assert.NotNull(typeof(DerivedRecapEpochCampaignExecutor));
        Assert.NotNull(typeof(DerivedRecapEpochOperationResult.FullRebuildRequired));
        Assert.Null(typeof(DerivedRecapEpochStore).Assembly.GetType(
            "Atelia.SessionJournal.DerivedRecap.Store.DerivedRecapStore"
        ));
    }

    private static string FindRepositoryRoot() {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null) {
            if (File.Exists(Path.Combine(current.FullName, "Atelia.sln"))) {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Atelia.sln was not found.");
    }
}
