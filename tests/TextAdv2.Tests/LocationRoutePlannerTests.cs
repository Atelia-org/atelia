using System.Text.Json;
using Atelia.StateJournal;
using Atelia.TextAdv2.DevSupport;
using Atelia.TextAdv2.Observation;
using Atelia.TextAdv2.Routing;
using Atelia.TextAdv2.WorldTruth;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class LocationRoutePlannerTests {
    [Fact]
    public void PlanShortestRoute_VillageToAerie_FindsExpectedShortestPath() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);

            var plan = LocationRoutePlanner.PlanShortestRoute(
                world,
                TestWorldBuilder.LocationIds.Village,
                TestWorldBuilder.LocationIds.Aerie
            );

            Assert.Equal(RoutePlanStatus.Found, plan.Status);
            Assert.Equal(3, plan.StepCount);
            Assert.Equal(11, plan.TotalTravelCost);
            Assert.Equal("zero", plan.SearchStats.HeuristicName);
            Assert.Equal(0, plan.SearchStats.LandmarkCount);
            Assert.Collection(
                plan.Steps,
                step => {
                    Assert.Equal(TestWorldBuilder.PassageIds.VillageSquareRoad, step.PassageId);
                    Assert.Equal(TestWorldBuilder.LocationIds.Village, step.FromLocationId);
                    Assert.Equal(TestWorldBuilder.LocationIds.Square, step.ToLocationId);
                    Assert.Equal(1, step.TravelCost);
                    Assert.Equal(1, step.CumulativeTravelCost);
                },
                step => {
                    Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, step.PassageId);
                    Assert.Equal(TestWorldBuilder.LocationIds.Square, step.FromLocationId);
                    Assert.Equal(TestWorldBuilder.LocationIds.Ridge, step.ToLocationId);
                    Assert.Equal(5, step.TravelCost);
                    Assert.Equal(6, step.CumulativeTravelCost);
                },
                step => {
                    Assert.Equal(TestWorldBuilder.PassageIds.RidgeAerieWinch, step.PassageId);
                    Assert.Equal(TestWorldBuilder.LocationIds.Ridge, step.FromLocationId);
                    Assert.Equal(TestWorldBuilder.LocationIds.Aerie, step.ToLocationId);
                    Assert.Equal(5, step.TravelCost);
                    Assert.Equal(11, step.CumulativeTravelCost);
                }
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void PlanShortestRoute_WithLandmarkHeuristic_ReportsSearchStatsAndShrinksExpandedNodes() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = WorldState.Create(revision);

            var start = world.CreateLocation("start", "Start", "search stats start");
            var fast = world.CreateLocation("fast", "Fast", "short branch");
            var slow1 = world.CreateLocation("slow1", "Slow1", "slow branch 1");
            var slow2 = world.CreateLocation("slow2", "Slow2", "slow branch 2");
            var goal = world.CreateLocation("goal", "Goal", "search stats goal");

            world.CreatePassage("start-fast", start.Id, "fast", fast.Id, "back", TravelMode.Land, 1);
            world.CreatePassage("fast-goal", fast.Id, "finish", goal.Id, "return-fast", TravelMode.Land, 1);
            world.CreatePassage("start-slow1", start.Id, "slow", slow1.Id, "return-start", TravelMode.Land, 1);
            world.CreatePassage("slow1-slow2", slow1.Id, "slower", slow2.Id, "return-slow1", TravelMode.Land, 1);
            world.CreatePassage("slow2-goal", slow2.Id, "late-finish", goal.Id, "return-slow2", TravelMode.Land, 10);

            var zeroPlan = LocationRoutePlanner.PlanShortestRoute(world, start.Id, goal.Id);
            var heuristic = LocationLandmarkHeuristicSnapshot.Create(world, [goal.Id]);
            var landmarkPlan = LocationRoutePlanner.PlanShortestRoute(
                world,
                start.Id,
                goal.Id,
                new LocationRoutePlanningOptions(heuristic)
            );

            Assert.Equal(RoutePlanStatus.Found, zeroPlan.Status);
            Assert.Equal(RoutePlanStatus.Found, landmarkPlan.Status);
            Assert.Equal(2, zeroPlan.TotalTravelCost);
            Assert.Equal(2, landmarkPlan.TotalTravelCost);
            Assert.Equal("zero", zeroPlan.SearchStats.HeuristicName);
            Assert.Equal(0, zeroPlan.SearchStats.LandmarkCount);
            Assert.Equal("landmark", landmarkPlan.SearchStats.HeuristicName);
            Assert.Equal(1, landmarkPlan.SearchStats.LandmarkCount);
            Assert.True(
                landmarkPlan.SearchStats.ExpandedNodeCount < zeroPlan.SearchStats.ExpandedNodeCount,
                $"Expected landmark heuristic to expand fewer nodes than zero heuristic, but saw zero={zeroPlan.SearchStats.ExpandedNodeCount}, landmark={landmarkPlan.SearchStats.ExpandedNodeCount}."
            );
            Assert.True(
                landmarkPlan.SearchStats.RelaxedEdgeCount <= zeroPlan.SearchStats.RelaxedEdgeCount,
                $"Expected landmark heuristic to relax no more edges than zero heuristic, but saw zero={zeroPlan.SearchStats.RelaxedEdgeCount}, landmark={landmarkPlan.SearchStats.RelaxedEdgeCount}."
            );
            Assert.Collection(
                landmarkPlan.Steps,
                step => Assert.Equal("start-fast", step.PassageId),
                step => Assert.Equal("fast-goal", step.PassageId)
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void PlanShortestRoute_VillageToShrine_UsesPortalRoute() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);

            var plan = LocationRoutePlanner.PlanShortestRoute(
                world,
                TestWorldBuilder.LocationIds.Village,
                TestWorldBuilder.LocationIds.Shrine
            );

            Assert.Equal(RoutePlanStatus.Found, plan.Status);
            Assert.Equal(2, plan.StepCount);
            Assert.Equal(2, plan.TotalTravelCost);
            Assert.Collection(
                plan.Steps,
                step => Assert.Equal(TestWorldBuilder.PassageIds.VillageSquareRoad, step.PassageId),
                step => Assert.Equal(TestWorldBuilder.PassageIds.SquareShrineGate, step.PassageId)
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void PlanShortestRouteForActor_UsesActorCurrentLocationAsStart() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            TestWorldBuilder.PopulateSampleActors(world);

            var plan = LocationRoutePlanner.PlanShortestRouteForActor(
                world,
                TestWorldBuilder.ActorIds.Scout,
                TestWorldBuilder.LocationIds.Aerie
            );

            Assert.Equal(RoutePlanStatus.Found, plan.Status);
            Assert.Equal(TestWorldBuilder.LocationIds.Square, plan.FromLocationId);
            Assert.Equal(TestWorldBuilder.LocationIds.Aerie, plan.ToLocationId);
            Assert.Equal(2, plan.StepCount);
            Assert.Equal(10, plan.TotalTravelCost);
            Assert.Collection(
                plan.Steps,
                step => Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, step.PassageId),
                step => Assert.Equal(TestWorldBuilder.PassageIds.RidgeAerieWinch, step.PassageId)
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void LocationLandmarkHeuristicSnapshot_TargetLandmarkYieldsExactLowerBound() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            var heuristic = LocationLandmarkHeuristicSnapshot.Create(
                world,
                [TestWorldBuilder.LocationIds.Aerie, TestWorldBuilder.LocationIds.Shrine]
            );

            Assert.Equal(11, heuristic.EstimateRemainingCost(TestWorldBuilder.LocationIds.Village, TestWorldBuilder.LocationIds.Aerie));
            Assert.Equal(2, heuristic.EstimateRemainingCost(TestWorldBuilder.LocationIds.Village, TestWorldBuilder.LocationIds.Shrine));
            Assert.Equal(0, heuristic.EstimateRemainingCost(TestWorldBuilder.LocationIds.Delta, TestWorldBuilder.LocationIds.Harbor));
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void PlanShortestRoute_WithLandmarkHeuristic_FindsSameStableShortestPath() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            var heuristic = LocationLandmarkHeuristicSnapshot.Create(
                world,
                [TestWorldBuilder.LocationIds.Aerie, TestWorldBuilder.LocationIds.Shrine]
            );

            var plan = LocationRoutePlanner.PlanShortestRoute(
                world,
                TestWorldBuilder.LocationIds.Village,
                TestWorldBuilder.LocationIds.Aerie,
                new LocationRoutePlanningOptions(heuristic)
            );

            Assert.Equal(RoutePlanStatus.Found, plan.Status);
            Assert.Equal(3, plan.StepCount);
            Assert.Equal(11, plan.TotalTravelCost);
            Assert.Collection(
                plan.Steps,
                step => Assert.Equal(TestWorldBuilder.PassageIds.VillageSquareRoad, step.PassageId),
                step => Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, step.PassageId),
                step => Assert.Equal(TestWorldBuilder.PassageIds.RidgeAerieWinch, step.PassageId)
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void PlanShortestRoute_DeltaToHarbor_ReturnsUnreachable() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);

            var plan = LocationRoutePlanner.PlanShortestRoute(
                world,
                TestWorldBuilder.LocationIds.Delta,
                TestWorldBuilder.LocationIds.Harbor
            );

            Assert.Equal(RoutePlanStatus.Unreachable, plan.Status);
            Assert.Equal(0, plan.StepCount);
            Assert.Null(plan.TotalTravelCost);
            Assert.Empty(plan.Steps);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void PlanShortestRoute_FromEqualsTo_ReturnsAlreadyThere() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);

            var plan = LocationRoutePlanner.PlanShortestRoute(
                world,
                TestWorldBuilder.LocationIds.Shrine,
                TestWorldBuilder.LocationIds.Shrine
            );

            Assert.Equal(RoutePlanStatus.AlreadyThere, plan.Status);
            Assert.Equal(0, plan.StepCount);
            Assert.Equal(0, plan.TotalTravelCost);
            Assert.Empty(plan.Steps);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void PlanShortestRoute_OnEqualCostPaths_UsesStableTieBreak() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = WorldState.Create(revision);

            var start = world.CreateLocation("start", "Start", "tie break start");
            var alpha = world.CreateLocation("alpha", "Alpha", "first branch");
            var beta = world.CreateLocation("beta", "Beta", "second branch");
            var goal = world.CreateLocation("goal", "Goal", "finish");

            world.CreatePassage("start-alpha", start.Id, "alpha-path", alpha.Id, "back-to-start", TravelMode.Land, 1);
            world.CreatePassage("alpha-goal", alpha.Id, "finish", goal.Id, "back-to-alpha", TravelMode.Land, 1);
            world.CreatePassage("start-beta", start.Id, "beta-path", beta.Id, "back-to-start", TravelMode.Land, 1);
            world.CreatePassage("beta-goal", beta.Id, "finish", goal.Id, "back-to-beta", TravelMode.Land, 1);

            var plan = LocationRoutePlanner.PlanShortestRoute(world, start.Id, goal.Id);

            Assert.Equal(RoutePlanStatus.Found, plan.Status);
            Assert.Equal(2, plan.StepCount);
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

    [Fact]
    public void PassageMutation_WithNegativeTravelCost_FailsFastBeforePlanning() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = WorldState.Create(revision);

            var start = world.CreateLocation("start", "Start", "negative-cost start");
            var goal = world.CreateLocation("goal", "Goal", "negative-cost goal");
            var passage = world.CreatePassage("negative-edge", start.Id, "shortcut", goal.Id, "return", TravelMode.Land, 1);
            var exception = Assert.Throws<InvalidOperationException>(
                () => world.SetPassageDirectionTravelCostModifierFrom(passage.Id, start.Id, -2)
            );

            Assert.Contains("Negative travel cost", exception.Message, StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void LocationRoutePlanTextRenderer_RendersStableText() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);

            var found = LocationRoutePlanner.PlanShortestRoute(
                world,
                TestWorldBuilder.LocationIds.Village,
                TestWorldBuilder.LocationIds.Aerie
            );
            var alreadyThere = LocationRoutePlanner.PlanShortestRoute(
                world,
                TestWorldBuilder.LocationIds.Shrine,
                TestWorldBuilder.LocationIds.Shrine
            );
            var unreachable = LocationRoutePlanner.PlanShortestRoute(
                world,
                TestWorldBuilder.LocationIds.Delta,
                TestWorldBuilder.LocationIds.Harbor
            );

            Assert.Equal(Normalize(ExpectedFoundPlanText), Normalize(LocationRoutePlanTextRenderer.Render(found)));
            Assert.Equal(Normalize(ExpectedAlreadyTherePlanText), Normalize(LocationRoutePlanTextRenderer.Render(alreadyThere)));
            Assert.Equal(Normalize(ExpectedUnreachablePlanText), Normalize(LocationRoutePlanTextRenderer.Render(unreachable)));
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void TextAdv2Json_RoutePlanStatus_UsesCanonicalWireTokens() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            var plan = LocationRoutePlanner.PlanShortestRoute(
                world,
                TestWorldBuilder.LocationIds.Village,
                TestWorldBuilder.LocationIds.Aerie
            );
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            TextAdv2Json.AddHostConverters(options);

            var json = JsonSerializer.Serialize(plan, options);
            using var document = JsonDocument.Parse(json);
            var roundTrip = JsonSerializer.Deserialize<LocationRoutePlanObservation>(json, options);

            Assert.Equal("found", document.RootElement.GetProperty("status").GetString());
            Assert.NotNull(roundTrip);
            Assert.Equal(RoutePlanStatus.Found, roundTrip!.Status);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-route-planner-tests-{Guid.NewGuid():N}");

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private const string ExpectedFoundPlanText = """
ROUTE PLAN from=village (Village) to=aerie (Aerie) status=found
1. village --east/village-square-road--> square | land | cost=1 | total=1
2. square --north gate/square-ridge-trail--> ridge | land | cost=5 | total=6
3. ridge --cliff lift/ridge-aerie-winch--> aerie | air | cost=5 | total=11
summary: steps=3 | totalCost=11
search: heuristic=zero | landmarks=0 | expanded=5 | relaxed=4 | frontierPeak=2 | staleSkips=0
""";

    private const string ExpectedAlreadyTherePlanText = """
ROUTE PLAN from=shrine (Shrine) to=shrine (Shrine) status=already-there
<already at destination>
summary: steps=0 | totalCost=0
search: heuristic=zero | landmarks=0 | expanded=0 | relaxed=0 | frontierPeak=0 | staleSkips=0
""";

    private const string ExpectedUnreachablePlanText = """
ROUTE PLAN from=delta (Delta) to=harbor (Harbor) status=unreachable
<no route found>
summary: steps=0 | totalCost=<unreachable>
search: heuristic=zero | landmarks=0 | expanded=1 | relaxed=0 | frontierPeak=1 | staleSkips=0
""";
}
