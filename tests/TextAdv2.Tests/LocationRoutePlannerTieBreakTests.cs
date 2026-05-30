using Atelia.StateJournal;
using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class LocationRoutePlannerTests_TieBreak {
    [Fact]
    public void PlanShortestRoute_OnEqualCostPaths_DoesNotUseExitNameAsTieBreak() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = WorldState.Create(revision);

            var start = world.CreateLocation("start", "Start", "tie break start");
            var alpha = world.CreateLocation("alpha", "Alpha", "first branch");
            var beta = world.CreateLocation("beta", "Beta", "second branch");
            var goal = world.CreateLocation("goal", "Goal", "finish");

            world.CreatePassage("start-alpha", start.Id, "zzz detour", alpha.Id, "return-start", TravelMode.Land, 1);
            world.CreatePassage("alpha-goal", alpha.Id, "finish", goal.Id, "return-alpha", TravelMode.Land, 1);
            world.CreatePassage("start-beta", start.Id, "aaa shortcut", beta.Id, "return-start", TravelMode.Land, 1);
            world.CreatePassage("beta-goal", beta.Id, "finish", goal.Id, "return-beta", TravelMode.Land, 1);

            var plan = LocationRoutePlanner.PlanShortestRoute(world, start.Id, goal.Id);

            Assert.Equal(RoutePlanStatus.Found, plan.Status);
            Assert.Collection(
                plan.Steps,
                step => Assert.Equal("start-alpha", step.PassageId),
                step => Assert.Equal("alpha-goal", step.PassageId)
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-route-planner-tiebreak-tests-{Guid.NewGuid():N}");

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }
}
